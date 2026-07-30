using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ReExtractor.Core;

namespace ReExtractor.Gui;

/// <summary>
/// Interactive software-rendered 3D viewport: orbit/pan/zoom + CPU-skinned animation playback.
/// No GPU dependencies — renders into a WriteableBitmap.
/// </summary>
public sealed class Viewport3D : Control
{
    private ViewportMesh? _mesh;
    private AnimationClip? _clip;

    // pose state
    private Vector3[]? _posed;      // world-space vertices (current pose)
    private Vector3[]? _posedNormals;
    private float[]? _intensities;  // per-vertex light intensity (gouraud)
    private Matrix4x4[]? _jointMats; // per deform-joint skin matrices
    private Matrix4x4[]? _boneGlobals; // per-bone world transforms (skeleton overlay)

    /// <summary>Noesis F6-style skeleton overlay toggle.</summary>
    public bool ShowSkeleton { get; set; } = true;

    /// <summary>
    /// TEX_DEBUG render mode for isolating texture-anomaly causes:
    /// 0 = normal (texture × light) | 1 = solid color, skip texture sampling entirely |
    /// 2 = raw texture, no lighting | 3 = UV visualization (u→R, v→G).
    /// </summary>
    public int TexDebugMode { get; set; }

    /// <summary>Two-sided by default: RE materials commonly use intentional two-sided cloth/hair.</summary>
    public bool BackfaceCulling { get; set; } = false;

    // camera
    private float _yaw = 0.7f, _pitch = 0.35f, _dist = 2.0f;
    private Vector3 _target = Vector3.Zero;
    private Point _lastPointer;
    private bool _orbiting, _panning;

    // playback
    private readonly DispatcherTimer _timer;
    private float _time;
    private bool _playing;
    private DateTime _lastTick = DateTime.UtcNow;

    // framebuffer
    private WriteableBitmap? _bmp;      // front (currently presented)
    private WriteableBitmap? _bmpBack;  // back (being filled — no lock contention with the compositor)
    private int _fbW, _fbH;
    /// <summary>Supersampling scale when inspecting a STATIC pose (3 = 9x SSAA — kills texture minification grain).</summary>
    public float IdleRenderScale { get; set; } = 3.0f;
    /// <summary>Render scale during ANIMATION playback or camera drag (1 = native — keeps the CPU rasterizer interactive).</summary>
    public float PlayRenderScale { get; set; } = 1.0f;
    /// <summary>Effective render scale: supersample a static pose, drop to native while animating or dragging (interactive refinement). The Image control downsamples to the view size.</summary>
    private float EffectiveScale => (_playing || _dragging) ? PlayRenderScale : IdleRenderScale;
    private bool _dragging;

    // multi-threaded rasterization: disjoint row bands into a shared framebuffer (no merge pass)
    private readonly int _threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    private static readonly Vector3 LightDir = Vector3.Normalize(new(0.5f, -1f, 0.8f));

    public Viewport3D()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        ClipToBounds = true;
    }

    /// <summary>The latest rendered frame; assign to an Image.Source (Image does the upscaling).</summary>
    public WriteableBitmap? Frame => _bmp;
    public event Action? FrameReady;

    public bool IsPlaying => _playing;
    public bool HasAnimation => _clip != null && _clip.Duration > 0;
    public bool HasMesh => _mesh != null;
    /// <summary>Bone names of the currently loaded mesh, used to remap .mot tracks (bone-hash) onto the skeleton.</summary>
    public string[]? MeshBoneNames
    {
        get
        {
            if (_mesh == null) return null;
            var names = new string[_mesh.Bones.Length];
            for (var i = 0; i < names.Length; i++) names[i] = _mesh.Bones[i].Name;
            return names;
        }
    }
    public string StatusInfo { get; private set; } = "无模型";
    public event Action? StateChanged;

    /// <summary>Smoothed frames-per-second of the render loop (meaningful during playback).</summary>
    public double CurrentFps { get; private set; }
    private DateTime _lastRenderAt = DateTime.UtcNow;

    // additional models — each gets its own independent skinning evaluation so they all animate.
    private sealed class ExtraModel
    {
        public required ViewportMesh Mesh;
        public required Vector3[] Posed;
        public required float[] Intensities;
        public required string Name;
        // per-extra skinning buffers (populated by AddMesh, updated by EvaluateExtraPose)
        public Matrix4x4[]? BoneGlobals;
        public Matrix4x4[]? JointMats;
        // tracks remapped: key = THIS extra's bone index, value = track from _clip matched by bone name
        public Dictionary<int, BoneTrack>? RemappedTracks;
    }
    private readonly List<ExtraModel> _extras = new();
    public int ExtraModelCount => _extras.Count;

    public void SetMesh(ViewportMesh mesh)
    {
        lock (_lifecycleGate)
        {
            // Stop and fully join the old worker BEFORE replacing mesh/pose arrays it may still read.
            StopPlayback();
            lock (_renderGate)
            {
                _mesh = mesh;
                _clip = null;
                _extras.Clear();
                _posed = new Vector3[mesh.VertexCount];
                _posedNormals = new Vector3[mesh.VertexCount];
                _intensities = new float[mesh.VertexCount];
                _boneGlobals = new Matrix4x4[Math.Max(1, mesh.Bones.Length)];
                _jointMats = new Matrix4x4[Math.Max(1, mesh.DeformToBone.Length)];
                ApplyBindPose();
                FrameCamera();
                StatusInfo = $"顶点 {mesh.VertexCount:N0} | 面 {mesh.FaceCount:N0} | 骨骼 {mesh.Bones.Length}";
            }
            RenderFrame();
        }
        StateChanged?.Invoke();
    }

    /// <summary>Append another model into the scene (rendered in bind pose, no animation).</summary>
    public void AddMesh(ViewportMesh mesh, string name)
    {
        var posed = (Vector3[])mesh.Vertices.Clone();
        var intensities = new float[mesh.VertexCount];
        var light = LightDir;
        Parallel.For(0, mesh.Normals.Length, i =>
        {
            intensities[i] = 0.25f + 0.75f * MathF.Abs(Vector3.Dot(mesh.Normals[i], light));
        });
        // pre-allocate skinning buffers so EvaluateExtraPose can animate this extra
        var boneGlobals = mesh.Bones.Length > 0 ? new Matrix4x4[mesh.Bones.Length] : null;
        var jointMats = mesh.DeformToBone.Length > 0 ? new Matrix4x4[mesh.DeformToBone.Length] : null;
        if (boneGlobals != null) ComputeBindGlobalsFor(mesh.Bones, boneGlobals);
        var renderNow = false;
        lock (_renderGate)
        {
            var extra = new ExtraModel { Mesh = mesh, Posed = posed, Intensities = intensities, Name = name,
                                           BoneGlobals = boneGlobals, JointMats = jointMats };
            if (_clip != null) RemapTracksForExtra(extra);
            _extras.Add(extra);
            if (!_playing) renderNow = true;
            else _poseDirty = true;
        }
        if (renderNow) RenderFrame();
        StateChanged?.Invoke();
    }

    public float Duration => _clip?.Duration ?? 0;
    public float CurrentTime => _time;

    /// <summary>Force a re-render (e.g. after toggling overlay options).</summary>
    public void Refresh()
    {
        if (_playing) { _poseDirty = true; return; }
        RenderFrame();
    }

    /// <summary>Scrub the timeline to a specific time (seconds) and render that frame.</summary>
    public void ScrubTo(float time)
    {
        var renderNow = false;
        lock (_renderGate)
        {
            if (_clip == null) return;
            _time = Math.Clamp(time, 0, _clip.Duration);
            if (_playing) _poseDirty = true;
            else { EvaluatePose(_time); renderNow = true; }
        }
        if (renderNow) RenderFrame();
    }

    public void SetAnimation(AnimationClip? clip)
    {
        lock (_lifecycleGate)
        {
            // ComboBox initialization and switching can call this repeatedly: fully join before restart.
            StopPlayback();
            lock (_renderGate)
            {
                _clip = clip;
                _time = 0;
                // remap animation tracks onto every extra's skeleton by bone name
                foreach (var ex in _extras) RemapTracksForExtra(ex);
                if (clip != null) EvaluatePose(0);
            }
            if (clip != null && clip.Duration > 0) StartPlayback();
            else if (clip != null) RenderFrame();
        }
        StateChanged?.Invoke();
    }

    public void TogglePlayback()
    {
        // stopping returns to a static frame — re-render at IdleRenderScale (supersampled) for full quality
        if (_playing) { StopPlayback(); RenderFrame(); } else if (HasAnimation) StartPlayback();
        StateChanged?.Invoke();
    }

    private readonly object _renderGate = new();
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private volatile bool _poseDirty;
    private volatile bool _frameReady;

    private void StartPlayback()
    {
        lock (_lifecycleGate)
        {
            // A second SetAnimation/SelectedIndex event must never leave the previous worker alive.
            StopPlayback();
            if (_clip == null || _clip.Duration <= 0) return;

            // mark playing BEFORE sizing the framebuffer so it uses PlayRenderScale (native speed)
            _playing = true;
            lock (_renderGate)
            {
                var w0 = (int)(Bounds.Width * EffectiveScale);
                var h0 = (int)(Bounds.Height * EffectiveScale);
                if (w0 >= 8 && h0 >= 8) EnsureFramebuffer(w0, h0);
            }

            var cts = new CancellationTokenSource();
            _workerCts = cts;
            _poseDirty = true;
            _frameReady = false;
            _lastTick = DateTime.UtcNow;
            _workerTask = Task.Run(() => RenderWorker(cts.Token)); // capture this worker's token
            _timer.Start();
        }
    }

    private void StopPlayback()
    {
        lock (_lifecycleGate)
        {
            _timer.Stop();
            _playing = false;
            var cts = _workerCts;
            var task = _workerTask;
            cts?.Cancel();
            if (task != null && !task.IsCompleted)
            {
                // Worker has no UI-thread await and observes cancellation both while rendering and waiting;
                // a real join is required before framebuffer/mesh state may be reused.
                try { task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
            _workerCts = null;
            _workerTask = null;
            cts?.Dispose();
            _poseDirty = false;
            _frameReady = false;
        }
    }

    /// <summary>Background render loop: skinning + rasterization off the UI thread.</summary>
    private void RenderWorker(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_poseDirty || _frameReady)
            {
                Thread.Sleep(1);
                continue;
            }

            _poseDirty = false;
            lock (_renderGate)
            {
                if (ct.IsCancellationRequested) break;
                EvaluatePose(_time);
                RasterizeIntoColor();
                _frameReady = true; // publish only after the complete frame is immutable
            }

            // Never overwrite a published software framebuffer until the UI has copied it.
            while (_frameReady && !ct.IsCancellationRequested)
                Thread.Sleep(1);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (float)(now - _lastTick).TotalSeconds;
        _lastTick = now;
        var shouldStop = false;
        var framePresented = false;

        lock (_renderGate)
        {
            if (_clip == null || _clip.Duration <= 0)
            {
                shouldStop = true;
            }
            else
            {
                _time += dt;
                if (_time > _clip.Duration) _time %= _clip.Duration;

                // Consume while producer is fenced out; acknowledge only after a successful copy/swap.
                if (_frameReady && PresentToBitmap())
                {
                    _frameReady = false;
                    framePresented = true;
                }
                if (!_poseDirty && !_frameReady) _poseDirty = true;
            }
        }

        if (shouldStop) { StopPlayback(); RenderFrame(); return; }
        if (framePresented) FrameReady?.Invoke(); // external callback outside _renderGate
    }

    // ---------- pose ----------

    private void ApplyBindPose()
    {
        if (_mesh == null || _posed == null) return;
        Array.Copy(_mesh.Vertices, _posed, _mesh.VertexCount);
        Array.Copy(_mesh.Normals, _posedNormals!, _mesh.VertexCount);
        ComputeBindBoneGlobals();
        ComputeIntensities();
    }

    private void ComputeBindBoneGlobals()
    {
        if (_mesh == null || _boneGlobals == null || _mesh.Bones.Length == 0) return;
        var computed = new bool[_mesh.Bones.Length];
        for (var b = 0; b < _mesh.Bones.Length; b++)
            ComputeGlobal(b, _mesh.Bones, _boneGlobals, computed, 0);
    }

    /// <summary>Compute bind-pose global transforms for an arbitrary bone list (used by AddMesh for extras).</summary>
    private void ComputeBindGlobalsFor(ViewportBone[] bones, Matrix4x4[] globals)
    {
        var computed = new bool[bones.Length];
        for (var b = 0; b < bones.Length; b++)
            ComputeGlobal(b, bones, globals, computed, 0);
    }

    private void ComputeIntensities()
    {
        if (_posedNormals == null || _intensities == null) return;
        var light = LightDir;
        Parallel.For(0, _posedNormals.Length, i =>
        {
            _intensities[i] = 0.25f + 0.75f * MathF.Abs(Vector3.Dot(_posedNormals[i], light));
        });
    }

    private void EvaluatePose(float time)
    {
        if (_mesh == null || _posed == null || _jointMats == null || _clip == null) return;
        var bones = _mesh.Bones;
        if (bones.Length == 0) { ApplyBindPose(); return; }

        // local pose: bind pose overridden by tracks
        var globals = _boneGlobals!;
        var computed = new bool[bones.Length];
        for (var b = 0; b < bones.Length; b++)
            ComputeGlobal(b, bones, globals, computed, time);

        for (var j = 0; j < _mesh.DeformToBone.Length && j < _jointMats.Length; j++)
        {
            var bone = _mesh.DeformToBone[j];
            _jointMats[j] = bones[bone].InverseGlobalBind * globals[bone];
        }

        var verts = _mesh.Vertices;
        var norms = _mesh.Normals;
        var weights = _mesh.Weights;
        var jointMats = _jointMats;
        var posed = _posed;
        var posedN = _posedNormals!;
        Parallel.For(0, verts.Length, i =>
        {
            var ws = weights[i];
            var v = verts[i];
            var n = norms[i];
            var acc = Vector3.Zero;
            var accN = Vector3.Zero;
            foreach (var (joint, w) in ws)
            {
                if (joint >= jointMats.Length) continue;
                acc += Vector3.Transform(v, jointMats[joint]) * w;
                accN += Vector3.TransformNormal(n, jointMats[joint]) * w;
            }
            posed[i] = acc;
            posedN[i] = accN.LengthSquared() < 1e-8f ? n : Vector3.Normalize(accN);
        });
        ComputeIntensities();
        // also animate all extra models (each gets independent skinning with shared clip)
        foreach (var ex in _extras) EvaluateExtraPose(ex, time);
    }

    /// <summary>Evaluate skinning pose for one extra model using the current _clip (shared animation).</summary>
    private void EvaluateExtraPose(ExtraModel extra, float time)
    {
        var mesh = extra.Mesh;
        var bones = mesh.Bones;
        if (bones.Length == 0 || extra.BoneGlobals == null || extra.JointMats == null) return;
        // compute animated global transforms for this extra's skeleton (using name-remapped tracks)
        var computed = new bool[bones.Length];
        for (var b = 0; b < bones.Length; b++)
            ComputeGlobal(b, bones, extra.BoneGlobals, computed, time, extra.RemappedTracks);
        // build joint matrices
        for (var j = 0; j < mesh.DeformToBone.Length && j < extra.JointMats.Length; j++)
        {
            var bone = mesh.DeformToBone[j];
            extra.JointMats[j] = bones[bone].InverseGlobalBind * extra.BoneGlobals[bone];
        }
        // skin vertices
        var verts = mesh.Vertices;
        var norms = mesh.Normals;
        var weights = mesh.Weights;
        var jointMats = extra.JointMats;
        var posed = extra.Posed;
        Parallel.For(0, verts.Length, i =>
        {
            var ws = weights[i];
            var v = verts[i];
            var n = norms[i];
            var acc = Vector3.Zero;
            var accN = Vector3.Zero;
            foreach (var (joint, w) in ws)
            {
                if (joint >= jointMats.Length) continue;
                acc += Vector3.Transform(v, jointMats[joint]) * w;
                accN += Vector3.TransformNormal(n, jointMats[joint]) * w;
            }
            posed[i] = acc;
            // normals
            extra.Intensities[i] = 0.25f + 0.75f * MathF.Abs(Vector3.Dot(
                accN.LengthSquared() < 1e-8f ? n : Vector3.Normalize(accN), LightDir));
        });
    }

    /// <summary>
    /// Remap _clip tracks onto an extra model's skeleton by matching bone names.
    /// This mirrors what LoadAnimation does at load time (boneHash → meshBoneIndex),
    /// but at runtime so extras with different bone ordering still get animated.
    /// </summary>
    private void RemapTracksForExtra(ExtraModel extra)
    {
        if (_clip == null || _mesh == null) { extra.RemappedTracks = null; return; }
        // build name → main-model-bone-index lookup from _clip.Tracks keys
        var mainBones = _mesh.Bones;
        var mainNameToTrackIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (idx, track) in _clip.Tracks)
            if (idx >= 0 && idx < mainBones.Length)
                mainNameToTrackIdx[mainBones[idx].Name] = idx;

        var extraBones = extra.Mesh.Bones;
        var remapped = new Dictionary<int, BoneTrack>();
        for (var eb = 0; eb < extraBones.Length; eb++)
        {
            if (mainNameToTrackIdx.TryGetValue(extraBones[eb].Name, out var mainIdx))
                if (_clip.Tracks.TryGetValue(mainIdx, out var track))
                    remapped[eb] = track;
        }
        extra.RemappedTracks = remapped;
    }

    private void ComputeGlobal(int b, ViewportBone[] bones, Matrix4x4[] globals, bool[] computed, float time, Dictionary<int, BoneTrack>? tracks = null)
    {
        if (computed[b]) return;
        var local = bones[b].LocalBind;
        var src = tracks ?? (_clip?.Tracks);
        if (src != null && src.TryGetValue(b, out var track))
            local = EvaluateLocal(track, time, local);

        var parent = bones[b].ParentIndex;
        if (parent >= 0)
        {
            ComputeGlobal(parent, bones, globals, computed, time, tracks);
            globals[b] = local * globals[parent];
        }
        else
        {
            globals[b] = local;
        }
        computed[b] = true;
    }

    private static Matrix4x4 EvaluateLocal(BoneTrack track, float time, Matrix4x4 bindLocal)
    {
        var pos = bindLocal.Translation;
        var rot = Quaternion.CreateFromRotationMatrix(bindLocal);
        var scale = Vector3.One;

        if (track.Translations is { Length: > 0 } tr && track.TransTimes != null)
            pos = Sample(tr, track.TransTimes, time);
        if (track.Rotations is { Length: > 0 } ro && track.RotTimes != null)
            rot = Sample(ro, track.RotTimes, time);

        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
    }

    private static Vector3 Sample(Vector3[] values, float[] times, float t)
    {
        if (values.Length == 1 || t <= times[0]) return values[0];
        if (t >= times[^1]) return values[^1];
        var i = Array.BinarySearch(times, t);
        if (i >= 0) return values[i];
        i = ~i;
        var (a, b) = (values[i - 1], values[i]);
        var f = (t - times[i - 1]) / Math.Max(1e-6f, times[i] - times[i - 1]);
        return Vector3.Lerp(a, b, f);
    }

    private static Quaternion Sample(Quaternion[] values, float[] times, float t)
    {
        if (values.Length == 1 || t <= times[0]) return values[0];
        if (t >= times[^1]) return values[^1];
        var i = Array.BinarySearch(times, t);
        if (i >= 0) return values[i];
        i = ~i;
        var f = (t - times[i - 1]) / Math.Max(1e-6f, times[i] - times[i - 1]);
        return Quaternion.Slerp(values[i - 1], values[i], f);
    }

    // ---------- camera ----------

    private void FrameCamera()
    {
        if (_mesh == null || _mesh.VertexCount == 0) return;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in _mesh.Vertices)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        _target = (min + max) * 0.5f;
        var extent = MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z));
        _dist = MathF.Max(0.3f, extent * 1.2f);
    }

    private (Matrix4x4 view, Matrix4x4 proj) CameraMatrices(float aspect)
    {
        var eye = _target + new Vector3(
            MathF.Cos(_pitch) * MathF.Sin(_yaw),
            MathF.Sin(_pitch),
            MathF.Cos(_pitch) * MathF.Cos(_yaw)) * _dist;
        var view = Matrix4x4.CreateLookAt(eye, _target, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, aspect, _dist * 0.01f, _dist * 20f);
        return (view, proj);
    }

    // ---------- rendering ----------

    // shared framebuffer — each thread owns disjoint row bands, no merge pass needed
    private uint[] _color = [];
    private float[] _depth = Array.Empty<float>();

    private WriteableBitmap? _retired; // front buffer retired on resize; disposed one recreate later (after it's off-screen)

    private void EnsureFramebuffer(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        if (_bmp == null || _fbW != w || _fbH != h)
        {
            _fbW = w; _fbH = h;
            // Don't dispose the currently-presented front buffer here — VpImage.Source may still be
            // showing it, and disposing it makes the compositor throw NRE in Image.Render. Retire the
            // front buffer and free the previously-retired one (which is guaranteed off-screen by now).
            _retired?.Dispose();
            _retired = _bmp;            // front: retire, dispose on the next recreate
            _bmpBack?.Dispose();        // back: never on screen -> safe to dispose now
            _bmp = new WriteableBitmap(new PixelSize(w, h), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _bmpBack = new WriteableBitmap(new PixelSize(w, h), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _color = new uint[w * h];
            _depth = new float[w * h];
        }
    }

    private void RenderFrame()
    {
        // UI-side full render path (non-playing: mesh load, drag, scrub). The same gate protects
        // framebuffer resize, rasterization and bitmap copy from every worker/UI entry point.
        var presented = false;
        lock (_renderGate)
        {
            var w = (int)(Bounds.Width * EffectiveScale);
            var h = (int)(Bounds.Height * EffectiveScale);
            if (w < 8 || h < 8) return;
            EnsureFramebuffer(w, h);
            if (_bmp == null) return;
            RasterizeIntoColor();
            presented = PresentToBitmap();
        }
        if (presented) FrameReady?.Invoke(); // never call host/UI code while holding the render lock
    }

    private void RasterizeIntoColor()
    {
        Array.Fill(_color, 0xFF1E1E1Eu);
        Array.Fill(_depth, float.MaxValue);
        if (_mesh != null && _posed != null && _mesh.FaceCount > 0)
            Rasterize();
        DrawGridOverlay();
        if (ShowSkeleton && _mesh != null && _boneGlobals != null && _mesh.Bones.Length > 0)
            DrawSkeletonOverlay();
        // DrawAxesHud(); // TEMP: disabled for debugging texture noise
    }

    private bool PresentToBitmap()
    {
        // FPS (EMA) — only ticks on actual presents
        var now = DateTime.UtcNow;
        var dt = (now - _lastRenderAt).TotalSeconds;
        _lastRenderAt = now;
        if (dt > 1e-4)
        {
            var fps = 1.0 / dt;
            CurrentFps = CurrentFps <= 0 ? fps : CurrentFps * 0.8 + fps * 0.2;
        }

        if (_bmp == null || _bmpBack == null) return false;
        // fill the BACK buffer (compositor is showing the front one — no contention), then swap
        using (var fb = _bmpBack.Lock())
        {
            unsafe
            {
                var pixels = (uint*)fb.Address;
                fixed (uint* src = _color)
                    Buffer.MemoryCopy(src, pixels, (long)_fbW * _fbH * 4, (long)_fbW * _fbH * 4);
            }
        }

        // DEBUG: dump first few frames to verify rasterizer output — DISABLED
        // (framebuffer confirmed correct; issue is in TexService BC7 decode, now fixed via WIC path)

        (_bmp, _bmpBack) = (_bmpBack, _bmp);
        return true;
    }

    private void Rasterize()
    {
        if (_mesh != null && _posed != null && _intensities != null)
            RasterizeMesh(_mesh, _posed, _intensities);
        foreach (var extra in _extras)
            RasterizeMesh(extra.Mesh, extra.Posed, extra.Intensities);
    }

    private void RasterizeMesh(ViewportMesh mesh, Vector3[] posed, float[] intensities)
    {
        var (view, proj) = CameraMatrices(_fbW / (float)_fbH);
        var vp = view * proj;

        // project all vertices (parallel)
        var vc = mesh.VertexCount;
        var sx = new float[vc];
        var sy = new float[vc];
        var sz = new float[vc];
        var fbw = _fbW; var fbh = _fbH;
        Parallel.For(0, vc, i =>
        {
            var c = Vector4.Transform(new Vector4(posed[i], 1f), vp);
            if (c.W <= 1e-6f) { sz[i] = float.NaN; return; }
            var inv = 1f / c.W;
            sx[i] = (c.X * inv * 0.5f + 0.5f) * fbw;
            sy[i] = (0.5f - c.Y * inv * 0.5f) * fbh;
            sz[i] = c.Z * inv;
        });

        // rasterize into a shared framebuffer, partitioned into disjoint row bands (no merge pass)
        var faces = mesh.Faces;
        var faceTex = mesh.FaceTexture;
        var textures = mesh.Textures;
        var uvs = mesh.Uvs;
        var threads = _threads;
        var color = _color;
        var depth = _depth;
        Parallel.For(0, threads, t =>
        {
            var yStart = t * fbh / threads;
            var yEnd = (t + 1) * fbh / threads;
            for (var f = 0; f < faces.Length; f++)
            {
                var (a, b, c) = faces[f];
                var za = sz[a]; var zb = sz[b]; var zc = sz[c];
                if (float.IsNaN(za) || float.IsNaN(zb) || float.IsNaN(zc)) continue;

                var texSlot = faceTex[f];
                var tex = texSlot >= 0 && texSlot < textures.Length ? textures[texSlot] : null;
                RasterTriangleInto(color, depth, yStart, yEnd,
                    sx[a], sy[a], za, intensities[a], uvs[a],
                    sx[b], sy[b], zb, intensities[b], uvs[b],
                    sx[c], sy[c], zc, intensities[c], uvs[c],
                    tex);
            }
        });
    }

    private void RasterTriangleInto(uint[] pixels, float[] depth, int bandStart, int bandEnd,
        float x0, float y0, float z0, float i0, Vector2 uv0,
        float x1, float y1, float z1, float i1, Vector2 uv1,
        float x2, float y2, float z2, float i2, Vector2 uv2,
        ViewportTexture? tex)
    {
        // cheap band rejection FIRST (each thread walks all faces — keep the miss cheap)
        var yMin = MathF.Min(y0, MathF.Min(y1, y2));
        var yMax = MathF.Max(y0, MathF.Max(y1, y2));
        if (yMax < bandStart || yMin >= bandEnd) return;

        var minX = (int)MathF.Max(0, MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2))));
        var maxX = (int)MathF.Min(_fbW - 1, MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2))));
        var minY = (int)MathF.Max(bandStart, MathF.Floor(yMin));
        var maxY = (int)MathF.Min(bandEnd - 1, MathF.Ceiling(yMax));
        if (minX > maxX || minY > maxY) return;

        var d = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
        if (MathF.Abs(d) < 1e-8f) return;
        if (BackfaceCulling && d < 0f) return; // backface cull (front faces keep d>0)
        var invD = 1f / d;

        var tw = tex?.Width ?? 0;
        var th = tex?.Height ?? 0;
        var tp = tex?.Pixels;
        var debugMode = TexDebugMode;

        // Trilinear mip selection (normal mode only): affine UV interpolation makes the UV
        // derivatives constant per triangle, so lambda is computed ONCE here. The texture's
        // native BC grain must be minification-filtered or it renders as per-pixel speckle.
        int mip0 = 0, mip1 = 0; float mipBlend = 0f;
        uint[][]? mips = null; int[]? mipW = null; int[]? mipH = null;
        if (debugMode == 0 && tex != null && tex.Mips.Length > 1)
        {
            var dw0dx = (y1 - y2) * invD; var dw1dx = (y2 - y0) * invD; var dw2dx = -(dw0dx + dw1dx);
            var dw0dy = (x2 - x1) * invD; var dw1dy = (x0 - x2) * invD; var dw2dy = -(dw0dy + dw1dy);
            var dudx = dw0dx * uv0.X + dw1dx * uv1.X + dw2dx * uv2.X;
            var dvdx = dw0dx * uv0.Y + dw1dx * uv1.Y + dw2dx * uv2.Y;
            var dudy = dw0dy * uv0.X + dw1dy * uv1.X + dw2dy * uv2.X;
            var dvdy = dw0dy * uv0.Y + dw1dy * uv1.Y + dw2dy * uv2.Y;
            var fx = MathF.Sqrt(dudx * tw * dudx * tw + dvdx * th * dvdx * th);
            var fy = MathF.Sqrt(dudy * tw * dudy * tw + dvdy * th * dvdy * th);
            var lambda = MathF.Log2(MathF.Max(1e-6f, MathF.Max(fx, fy)));
            var maxMip = tex.Mips.Length - 1;
            var mipF = Math.Clamp(lambda, 0f, maxMip);
            mip0 = (int)MathF.Floor(mipF);
            mip1 = Math.Min(mip0 + 1, maxMip);
            mipBlend = mipF - mip0;
            mips = tex.Mips; mipW = tex.MipW; mipH = tex.MipH;
        }

        for (var y = minY; y <= maxY; y++)
        {
            var row = y * _fbW;
            for (var x = minX; x <= maxX; x++)
            {
                var px = x + 0.5f; var py = y + 0.5f;
                var w0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) * invD;
                var w1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) * invD;
                var w2 = 1f - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;

                var z = w0 * z0 + w1 * z1 + w2 * z2;
                var idx = row + x;
                if (z >= depth[idx]) continue;
                depth[idx] = z;

                // gouraud: interpolated per-vertex intensity
                var lum = w0 * i0 + w1 * i1 + w2 * i2;

                uint color;
                if (debugMode == 1)
                {
                    // TEX_DEBUG 1: solid color, completely skip texture sampling — isolates geometry/framebuffer/depth
                    var shade = (uint)MathF.Min(255, 210 * lum);
                    color = 0xFF000000u | (shade << 16) | (shade << 8) | shade;
                }
                else if (debugMode == 3 && tp != null)
                {
                    // TEX_DEBUG 3: UV visualization (u→R, v→G) — isolates UV layout correctness
                    var du = w0 * uv0.X + w1 * uv1.X + w2 * uv2.X;
                    var dv = w0 * uv0.Y + w1 * uv1.Y + w2 * uv2.Y;
                    du -= MathF.Floor(du); dv -= MathF.Floor(dv);
                    color = 0xFF000000u | ((uint)(du * 255) << 16) | ((uint)(dv * 255) << 8);
                }
                else if (tp != null)
                {
                    var u = w0 * uv0.X + w1 * uv1.X + w2 * uv2.X;
                    var v = w0 * uv0.Y + w1 * uv1.Y + w2 * uv2.Y;
                    uint texel;
                    if (mips != null && mipW != null && mipH != null)
                    {
                        var t0 = SampleTexBilinear(mips[mip0], mipW[mip0], mipH[mip0], u, v);
                        texel = mip1 != mip0 && mipBlend > 0.001f
                            ? BlendTexel(t0, SampleTexBilinear(mips[mip1], mipW[mip1], mipH[mip1], u, v), mipBlend)
                            : t0;
                    }
                    else
                    {
                        texel = SampleTexBilinear(tp, tw, th, u, v);
                    }
                    // TEX_DEBUG 2: raw texture, no lighting — isolates decode correctness from shading
                    var lumT = debugMode == 2 ? 1f : lum;
                    var bB = (texel & 0xFF) * lumT;
                    var bG = ((texel >> 8) & 0xFF) * lumT;
                    var bR = ((texel >> 16) & 0xFF) * lumT;
                    color = 0xFF000000u
                        | ((uint)MathF.Min(255, bR) << 16)
                        | ((uint)MathF.Min(255, bG) << 8)
                        | (uint)MathF.Min(255, bB);
                }
                else
                {
                    var shade = (uint)MathF.Min(255, 210 * lum);
                    color = 0xFF000000u | (shade << 16) | (shade << 8) | shade;
                }
                pixels[idx] = color;
            }
        }
    }


    /// <summary>Blend two packed texels (trilinear cross-mip lerp). Packed layout: A | R<<16 | G<<8 | B.</summary>
    private static uint BlendTexel(uint a, uint b, float f)
    {
        var fb = 1f - f;
        var r = (uint)(((a >> 16) & 0xFF) * fb + ((b >> 16) & 0xFF) * f);
        var g = (uint)(((a >> 8) & 0xFF) * fb + ((b >> 8) & 0xFF) * f);
        var bl = (uint)((a & 0xFF) * fb + (b & 0xFF) * f);
        return 0xFF000000u | (r << 16) | (g << 8) | bl;
    }

    /// <summary>Bilinear texture sample with wrap. Returns packed texel (A | R<<16 | G<<8 | B).</summary>
    private static uint SampleTexBilinear(uint[] tp, int tw, int th, float u, float v)
    {
        u -= MathF.Floor(u);
        v -= MathF.Floor(v);
        var fx = u * tw - 0.5f;
        var fy = v * th - 0.5f;
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;
        x0 = ((x0 % tw) + tw) % tw;
        y0 = ((y0 % th) + th) % th;
        var x1 = (x0 + 1) % tw;
        var y1 = (y0 + 1) % th;
        var c00 = tp[y0 * tw + x0];
        var c10 = tp[y0 * tw + x1];
        var c01 = tp[y1 * tw + x0];
        var c11 = tp[y1 * tw + x1];
        var b = Chan(c00, c10, c01, c11, 0, tx, ty);
        var g = Chan(c00, c10, c01, c11, 8, tx, ty);
        var r = Chan(c00, c10, c01, c11, 16, tx, ty);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;

        static int Chan(uint c00, uint c10, uint c01, uint c11, int shift, float tx, float ty)
        {
            var b00 = (c00 >> shift) & 0xFF; var b10 = (c10 >> shift) & 0xFF;
            var b01 = (c01 >> shift) & 0xFF; var b11 = (c11 >> shift) & 0xFF;
            var top = b00 + (b10 - b00) * tx;
            var bot = b01 + (b11 - b01) * tx;
            return (int)(top + (bot - top) * ty);
        }
    }

    /// <summary>Ground grid at y=0 (drawn before skeleton, depth-tested against geometry).</summary>
    private void DrawGridOverlay()
    {
        var (view, proj) = CameraMatrices(_fbW / (float)_fbH);
        var vp = view * proj;
        var color = _color;
        var depth = _depth;
        const uint gridColor = 0xFF4A4A4Au;
        const float extent = 4f, step = 0.5f;

        for (var v = -extent; v <= extent + 1e-4f; v += step)
        {
            DrawGridLine(color, depth, vp, new Vector3(v, 0, -extent), new Vector3(v, 0, extent), gridColor);
            DrawGridLine(color, depth, vp, new Vector3(-extent, 0, v), new Vector3(extent, 0, v), gridColor);
        }
    }

    private void DrawGridLine(uint[] pixels, float[] depth, Matrix4x4 vp, Vector3 a, Vector3 b, uint color)
    {
        var p0 = Project(a, vp);
        var p1 = Project(b, vp);
        if (p0 == null || p1 == null) return;
        DrawLine3D(pixels, depth, p0.Value, p1.Value, color);
    }

    /// <summary>Bottom-left screen-space orientation gizmo (3ds Max style): XYZ tripod that rotates with the camera, always on top.</summary>
    private void DrawAxesHud()
    {
        var (view, _) = CameraMatrices(1f); // only the view rotation is needed
        // gizmo origin in the framebuffer (bottom-left corner with margin)
        var ox = 56f;
        var oy = _fbH - 56f;
        const float axisLen = 34f;
        // framebuffer pixel layout: A<<24 | R<<16 | G<<8 | B
        DrawHudAxis(view, ox, oy, axisLen, Vector3.UnitX, 0xFFFF0000u, 'X'); // X = red
        DrawHudAxis(view, ox, oy, axisLen, Vector3.UnitY, 0xFF00FF00u, 'Y'); // Y = green
        DrawHudAxis(view, ox, oy, axisLen, Vector3.UnitZ, 0xFF0000FFu, 'Z'); // Z = blue
    }

    private void DrawHudAxis(Matrix4x4 view, float ox, float oy, float len, Vector3 worldDir, uint color, char label)
    {
        var d = Vector3.TransformNormal(worldDir, view); // view-space direction (rotation only)
        // view space: X=right, Y=up. Screen Y is down, so flip Y.
        var ex = ox + d.X * len;
        var ey = oy - d.Y * len;
        DrawHudLine(ox, oy, ex, ey, color);
        // axis-end letter label, slightly beyond the tip
        var lx = ox + d.X * (len + 10f);
        var ly = oy - d.Y * (len + 10f);
        DrawHudChar(lx, ly, label, color);
    }

    private void DrawHudLine(float x0, float y0, float x1, float y1, uint color)
    {
        var steps = (int)MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0));
        if (steps < 1) steps = 1;
        for (var s = 0; s <= steps; s++)
        {
            var t = s / (float)steps;
            PlotThick((int)MathF.Round(x0 + (x1 - x0) * t), (int)MathF.Round(y0 + (y1 - y0) * t), color);
        }
    }

    /// <summary>Plot a 2px dot (always on top, no depth test).</summary>
    private void PlotThick(int x, int y, uint color)
    {
        for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                var px = x + dx; var py = y + dy;
                if (px < 0 || py < 0 || px >= _fbW || py >= _fbH) continue;
                _color[py * _fbW + px] = color;
            }
    }

    // tiny 5x5 pixel font for the axis labels (X / Y / Z)
    private static readonly string[] FontX = { "10001", "01010", "00100", "01010", "10001" };
    private static readonly string[] FontY = { "10001", "01010", "00100", "00100", "00100" };
    private static readonly string[] FontZ = { "11111", "00010", "00100", "01000", "11111" };

    private void DrawHudChar(float cx, float cy, char label, uint color)
    {
        var glyph = label == 'X' ? FontX : label == 'Y' ? FontY : FontZ;
        var x0 = (int)MathF.Round(cx) - 2;
        var y0 = (int)MathF.Round(cy) - 2;
        for (var r = 0; r < 5; r++)
            for (var c = 0; c < 5; c++)
                if (glyph[r][c] == '1')
                    PlotThick(x0 + c, y0 + r, color);
    }

    /// <summary>Draw bone-connection lines over the merged frame (Noesis F6 style).</summary>
    private void DrawSkeletonOverlay()
    {
        var mesh = _mesh!;
        var globals = _boneGlobals!;
        var (view, proj) = CameraMatrices(_fbW / (float)_fbH);
        var vp = view * proj;
        var color = _color;
        var depth = _depth;
        const uint lineColor = 0xFF4040FFu; // red-ish in BGRA

        for (var bi = 0; bi < mesh.Bones.Length; bi++)
        {
            var bone = mesh.Bones[bi];
            if (bone.ParentIndex < 0 || bone.ParentIndex >= globals.Length) continue;
            var p0 = Project(globals[bone.ParentIndex].Translation, vp);
            var p1 = Project(globals[bi].Translation, vp);
            if (p0 == null || p1 == null) continue;
            DrawLine3D(color, depth, p0.Value, p1.Value, lineColor);
        }
    }

    private Vector3? Project(Vector3 world, Matrix4x4 vp)
    {
        var c = Vector4.Transform(new Vector4(world, 1f), vp);
        if (c.W <= 1e-6f) return null;
        var inv = 1f / c.W;
        return new Vector3((c.X * inv * 0.5f + 0.5f) * _fbW, (0.5f - c.Y * inv * 0.5f) * _fbH, c.Z * inv);
    }

    private void DrawLine3D(uint[] pixels, float[] depth, Vector3 a, Vector3 b, uint color)
    {
        var steps = (int)MathF.Max(MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y));
        if (steps <= 0) return;
        for (var s = 0; s <= steps; s++)
        {
            var t = s / (float)steps;
            var x = (int)(a.X + (b.X - a.X) * t);
            var y = (int)(a.Y + (b.Y - a.Y) * t);
            var z = a.Z + (b.Z - a.Z) * t;
            if ((uint)x >= (uint)_fbW || (uint)y >= (uint)_fbH) continue;
            var idx = y * _fbW + x;
            if (z > depth[idx] + 0.001f) continue; // hidden behind geometry
            pixels[idx] = color;
            depth[idx] = z;
        }
    }

    // self-healing: rebuild framebuffer when bounds change (called from layout pass)
    public void EnsureSizeForCurrentBounds()
    {
        var w = (int)(Bounds.Width * EffectiveScale);
        var h = (int)(Bounds.Height * EffectiveScale);
        if (w >= 8 && h >= 8 && (w != _fbW || h != _fbH))
            RenderFrame();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        System.Diagnostics.Debug.WriteLine($"[vp] size changed to {e.NewSize.Width}x{e.NewSize.Height}");
        RenderFrame();
    }

    // ---------- input ----------

    /// <summary>Begin a camera drag (called by the host control that owns pointer input).</summary>
    public void BeginDrag(Avalonia.Point pos, bool orbit, bool pan)
    {
        _lastPointer = pos;
        _orbiting = orbit;
        _panning = pan;
        _dragging = true; // drop to PlayRenderScale while dragging for interactivity
    }

    /// <summary>Continue a camera drag.</summary>
    public void DragTo(Avalonia.Point pos)
    {
        var dx = (float)(pos.X - _lastPointer.X);
        var dy = (float)(pos.Y - _lastPointer.Y);
        _lastPointer = pos;
        var changed = false;
        var renderNow = false;

        lock (_renderGate)
        {
            if (_orbiting)
            {
                _yaw -= dx * 0.01f;
                _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.5f, 1.5f);
                changed = true;
            }
            else if (_panning)
            {
                var (view, _) = CameraMatrices(1f);
                Matrix4x4.Invert(view, out var inv);
                var scale = _dist * 0.0018f;
                var right = Vector3.TransformNormal(Vector3.UnitX, inv);
                var up = Vector3.TransformNormal(Vector3.UnitY, inv);
                _target += (-right * dx + up * dy) * scale;
                changed = true;
            }
            if (changed)
            {
                if (_playing) _poseDirty = true;
                else renderNow = true;
            }
        }
        if (renderNow) RenderFrame();
    }

    public void EndDrag()
    {
        _orbiting = false;
        _panning = false;
        _dragging = false;
        RenderFrame(); // re-render once at IdleRenderScale (supersampled) for a clean static image
    }

    public void Zoom(double delta)
    {
        var renderNow = false;
        lock (_renderGate)
        {
            _dist = Math.Clamp(_dist * (delta > 0 ? 0.9f : 1.1f), 0.05f, 1000f);
            if (_playing) _poseDirty = true;
            else renderNow = true;
        }
        if (renderNow) RenderFrame();
    }
}
