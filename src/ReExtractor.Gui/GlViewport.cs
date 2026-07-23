using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using ReExtractor.Core;
using Silk.NET.OpenGL;

namespace ReExtractor.Gui;

/// <summary>
/// GPU-accelerated 3D viewport via Avalonia's OpenGlControlBase + Silk.NET.OpenGL.
/// CPU does skinning (parallel), GPU draws — full-screen without lag.
/// </summary>
public sealed class GlViewport : OpenGlControlBase
{
    private GL? _gl;

    private sealed class GlModel
    {
        public required ViewportMesh Mesh;
        public uint Vao, Vbo, Ebo;
        public int IndexCount;
        public (int Slot, int Start, int Count)[] Batches = []; // texture slot + index range
        public uint[] TexHandles = [];                          // parallel to Batches
        public float[] Interleaved = []; // pos+normal+uv per vertex, rebuilt on pose change
        public Vector3[] Posed = [];
        public Vector3[] PosedNormals = [];
        public Matrix4x4[] BoneGlobals = [];
        public Matrix4x4[] JointMats = [];
        public bool IsPrimary;
    }

    private GlModel? _primary;
    private readonly List<GlModel> _extras = new();
    private AnimationClip? _clip;

    private uint _meshProgram, _lineProgram;
    private int _uMvp, _uLight, _uHasTex, _uTex, _uTexDebug;
    private int _lMvp, _lColor;
    private uint _lineVao, _lineVbo;
    private readonly List<float> _lineVerts = new();
    private int _gridLineCount;

    // camera (same orbit math as the software viewport)
    private float _yaw = 0.7f, _pitch = 0.35f, _dist = 2.0f;
    private Vector3 _target = Vector3.Zero;
    private Avalonia.Point _lastPointer;
    private bool _orbiting, _panning;

    // playback
    private readonly DispatcherTimer _timer;
    private float _time;
    private bool _playing;
    private DateTime _lastTick = DateTime.UtcNow;

    private static readonly Vector3 LightDir = Vector3.Normalize(new(0.5f, -1f, 0.8f));

    public bool ShowSkeleton { get; set; } = true;

    /// <summary>
    /// TEX_DEBUG render mode for isolating texture-anomaly causes:
    /// 0 = normal (texture × light) | 1 = solid color, skip texture sampling |
    /// 2 = raw texture, no lighting | 3 = UV visualization (u→R, v→G).
    /// </summary>
    public int TexDebugMode { get; set; }

    public bool IsPlaying => _playing;
    public bool HasAnimation => _clip != null && _clip.Duration > 0;
    public bool HasMesh => _primary != null;
    public float Duration => _clip?.Duration ?? 0;
    public float CurrentTime => _time;
    public double CurrentFps { get; private set; }
    private DateTime _lastRenderAt = DateTime.UtcNow;
    public string StatusInfo { get; private set; } = "无模型";
    public int ExtraModelCount => _extras.Count;
    public event Action? StateChanged;

    /// <summary>Bone names of the currently loaded mesh (for remap of .mot tracks).</summary>
    public string[]? MeshBoneNames
    {
        get
        {
            if (_primary == null) return null;
            var bones = _primary.Mesh.Bones;
            var names = new string[bones.Length];
            for (var i = 0; i < names.Length; i++) names[i] = bones[i].Name;
            return names;
        }
    }

    public GlViewport()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
    }

    // ---------- public API (mirrors the software viewport) ----------

    public void SetMesh(ViewportMesh mesh)
    {
        _primary = CreateModel(mesh, isPrimary: true);
        _extras.Clear();
        _clip = null;
        StopPlayback();
        FrameCamera(mesh);
        StatusInfo = $"顶点 {mesh.VertexCount:N0} | 面 {mesh.FaceCount:N0} | 骨骼 {mesh.Bones.Length} | 贴图 {mesh.Textures.Length}";
        StateChanged?.Invoke();
        RequestNextFrameRendering();
    }

    public void AddMesh(ViewportMesh mesh, string name)
    {
        _extras.Add(CreateModel(mesh, isPrimary: false));
        StateChanged?.Invoke();
        RequestNextFrameRendering();
    }

    public void SetAnimation(AnimationClip? clip)
    {
        _clip = clip;
        _time = 0;
        if (clip != null && clip.Duration > 0) StartPlayback(); else StopPlayback();
        StateChanged?.Invoke();
    }

    public void TogglePlayback()
    {
        if (_playing) StopPlayback(); else if (HasAnimation) StartPlayback();
        StateChanged?.Invoke();
    }

    public void ScrubTo(float time)
    {
        if (_clip == null) return;
        _time = Math.Clamp(time, 0, _clip.Duration);
        if (_primary != null) EvaluatePose(_primary, _time);
        RequestNextFrameRendering();
    }

    public void Refresh() => RequestNextFrameRendering();

    private void StartPlayback()
    {
        _playing = true;
        _lastTick = DateTime.UtcNow;
        _timer.Start();
    }

    private void StopPlayback()
    {
        _playing = false;
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (float)(now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (_clip == null || _primary == null) { StopPlayback(); return; }
        _time += dt;
        if (_time > _clip.Duration) _time -= _clip.Duration;
        EvaluatePose(_primary, _time);
        RequestNextFrameRendering();
    }

    // ---------- model management ----------

    private GlModel CreateModel(ViewportMesh mesh, bool isPrimary)
    {
        var model = new GlModel
        {
            Mesh = mesh,
            IsPrimary = isPrimary,
            Posed = (Vector3[])mesh.Vertices.Clone(),
            PosedNormals = (Vector3[])mesh.Normals.Clone(),
            BoneGlobals = new Matrix4x4[Math.Max(1, mesh.Bones.Length)],
            JointMats = new Matrix4x4[Math.Max(1, mesh.DeformToBone.Length)],
            Interleaved = new float[mesh.VertexCount * 8],
        };
        ComputeBindBoneGlobals(model);
        RebuildInterleaved(model);

        // index buffer, grouped by texture slot
        var indices = new List<uint>(mesh.FaceCount * 3);
        var groups = new Dictionary<int, List<uint>>();
        for (var f = 0; f < mesh.FaceCount; f++)
        {
            var slot = mesh.FaceTexture[f];
            if (!groups.TryGetValue(slot, out var list)) groups[slot] = list = new List<uint>();
            var (a, b, c) = mesh.Faces[f];
            list.Add((uint)a); list.Add((uint)b); list.Add((uint)c);
        }
        var batches = new List<(int, int, int)>();
        foreach (var (slot, list) in groups)
        {
            var start = indices.Count;
            indices.AddRange(list);
            batches.Add((slot, start, list.Count));
        }
        model.Batches = batches.ToArray();
        model.IndexCount = indices.Count;

        // ALWAYS defer upload to the render thread (OnOpenGlInit / OnOpenGlRender).
        // GL context is bound to the render thread only; calling GL APIs from the UI thread
        // silently produces handle=0 or corrupts state under ANGLE.
        _pendingUpload.Add((model, indices.ToArray()));
        RequestNextFrameRendering(); // wake render loop so pending upload is processed promptly
        return model;
    }

    private void UploadAllTextures(GlModel model)
    {
        var handles = new uint[model.Batches.Length];
        for (var i = 0; i < model.Batches.Length; i++)
        {
            var slot = model.Batches[i].Slot;
            handles[i] = slot >= 0 && slot < model.Mesh.Textures.Length
                ? UploadTexture(model.Mesh.Textures[slot])
                : 0u;
        }
        model.TexHandles = handles;
    }

    private readonly List<(GlModel model, uint[] indices)> _pendingUpload = new();

    private uint UploadTexture(ViewportTexture tex)
    {
        if (_gl == null) return 0;
        var g = _gl;
        var handle = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, handle);

        // ViewportTexture.Pixels is packed uint: A<<24 | R<<16 | G<<8 | B (CPU BGRA/ARGB).
        // ANGLE ES 3.0 only guarantees RGBA byte upload. Convert to byte array: R,G,B,A per pixel.
        // Also flip rows: our pixels are top-down, GL expects bottom-up.
        var w = tex.Width; var h = tex.Height;
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var srcRow = y * w;
            var dstRow = (h - 1 - y) * w * 4;
            for (var x = 0; x < w; x++)
            {
                var p = tex.Pixels[srcRow + x];
                var off = dstRow + x * 4;
                rgba[off]     = (byte)((p >> 16) & 0xFF); // R
                rgba[off + 1] = (byte)((p >> 8)  & 0xFF); // G
                rgba[off + 2] = (byte)(p          & 0xFF); // B
                rgba[off + 3] = (byte)((p >> 24) & 0xFF); // A
            }
        }
        unsafe
        {
            fixed (byte* p = rgba)
            {
                g.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)w, (uint)h, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }
        }
        // hardware mipmapping: per-pixel LOD + trilinear filtering
        g.GenerateMipmap(TextureTarget.Texture2D);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        // anisotropic filtering smooths minified grain at glancing angles (EXT_texture_filter_anisotropic)
        g.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, 4f); // TEXTURE_MAX_ANISOTROPY_EXT
        return handle;
    }

    private void UploadGeometry(GlModel model, uint[] indices)
    {
        if (_gl == null) return;
        var g = _gl;
        model.Vao = g.GenVertexArray();
        model.Vbo = g.GenBuffer();
        model.Ebo = g.GenBuffer();

        g.BindVertexArray(model.Vao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, model.Vbo);
        unsafe
        {
            fixed (float* p = model.Interleaved)
                g.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(model.Interleaved.Length * 4), p, BufferUsageARB.DynamicDraw);
        }
        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.Ebo);
        unsafe
        {
            fixed (uint* p = indices)
                g.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * 4), p, BufferUsageARB.StaticDraw);
        }

        const int stride = 8 * 4;
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 12);
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 24);
        g.BindVertexArray(0);
    }

    private void RebuildInterleaved(GlModel model)
    {
        var data = model.Interleaved;
        var posed = model.Posed;
        var norms = model.PosedNormals;
        var uvs = model.Mesh.Uvs;
        for (var i = 0; i < posed.Length; i++)
        {
            var o = i * 8;
            data[o] = posed[i].X; data[o + 1] = posed[i].Y; data[o + 2] = posed[i].Z;
            data[o + 3] = norms[i].X; data[o + 4] = norms[i].Y; data[o + 5] = norms[i].Z;
            data[o + 6] = uvs[i].X; data[o + 7] = uvs[i].Y;
        }
    }

    private void UploadPose(GlModel model)
    {
        if (_gl == null || model.Vbo == 0) return;
        RebuildInterleaved(model);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, model.Vbo);
        unsafe
        {
            fixed (float* p = model.Interleaved)
                _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(model.Interleaved.Length * 4), p);
        }
    }

    // ---------- pose evaluation (same math as the software viewport) ----------

    private void ComputeBindBoneGlobals(GlModel model)
    {
        var bones = model.Mesh.Bones;
        if (bones.Length == 0) return;
        var computed = new bool[bones.Length];
        for (var b = 0; b < bones.Length; b++)
            ComputeGlobal(model, b, computed, -1);
    }

    private void EvaluatePose(GlModel model, float time)
    {
        var bones = model.Mesh.Bones;
        if (bones.Length == 0 || _clip == null) return;
        var computed = new bool[bones.Length];
        for (var b = 0; b < bones.Length; b++)
            ComputeGlobal(model, b, computed, time);

        var deform = model.Mesh.DeformToBone;
        for (var j = 0; j < deform.Length && j < model.JointMats.Length; j++)
        {
            var bone = deform[j];
            model.JointMats[j] = bones[bone].InverseGlobalBind * model.BoneGlobals[bone];
        }

        var verts = model.Mesh.Vertices;
        var norms = model.Mesh.Normals;
        var weights = model.Mesh.Weights;
        var jointMats = model.JointMats;
        var posed = model.Posed;
        var posedN = model.PosedNormals;
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
    }

    private void ComputeGlobal(GlModel model, int b, bool[] computed, float time)
    {
        if (computed[b]) return;
        var bones = model.Mesh.Bones;
        var local = bones[b].LocalBind;
        if (time >= 0 && _clip != null && _clip.Tracks.TryGetValue(b, out var track))
            local = EvaluateLocal(track, time, local);

        var parent = bones[b].ParentIndex;
        if (parent >= 0)
        {
            ComputeGlobal(model, parent, computed, time);
            model.BoneGlobals[b] = local * model.BoneGlobals[parent];
        }
        else
        {
            model.BoneGlobals[b] = local;
        }
        computed[b] = true;
    }

    private static Matrix4x4 EvaluateLocal(BoneTrack track, float time, Matrix4x4 bindLocal)
    {
        var pos = bindLocal.Translation;
        var rot = Quaternion.CreateFromRotationMatrix(bindLocal);
        if (track.Translations is { Length: > 0 } tr && track.TransTimes != null)
            pos = Sample(tr, track.TransTimes, time);
        if (track.Rotations is { Length: > 0 } ro && track.RotTimes != null)
            rot = Sample(ro, track.RotTimes, time);
        return Matrix4x4.CreateScale(Vector3.One) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
    }

    private static Vector3 Sample(Vector3[] values, float[] times, float t)
    {
        if (values.Length == 1 || t <= times[0]) return values[0];
        if (t >= times[^1]) return values[^1];
        var i = Array.BinarySearch(times, t);
        if (i >= 0) return values[i];
        i = ~i;
        var f = (t - times[i - 1]) / Math.Max(1e-6f, times[i] - times[i - 1]);
        return Vector3.Lerp(values[i - 1], values[i], f);
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

    private void FrameCamera(ViewportMesh mesh)
    {
        if (mesh.VertexCount == 0) return;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in mesh.Vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        // Target the center of the bounding box (AABB midpoint).
        // XZ center, Y at the vertical center — keeps the whole figure framed.
        _target = new Vector3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f);
        var extentH = MathF.Max(max.X - min.X, max.Z - min.Z); // horizontal
        var extentV = max.Y - min.Y;                            // vertical (height)
        // Use the larger of horizontal or vertical extent so the whole body fits with FOV=45°.
        var extent = MathF.Max(extentH, extentV);
        _dist = MathF.Max(0.5f, extent * 1.1f);  // tight framing: 1.1× at FOV 45°
        _yaw = 0f;      // face-on
        _pitch = 0.05f; // very slightly above horizontal
    }

    // ---------- OpenGL lifecycle ----------

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        var logPath = @"C:\Users\zzjhuang\WorkBuddy\RE引擎解包工具\gl_log.txt";
        try
        {
            System.IO.File.WriteAllText(logPath, $"Version={gl.Version}\n");
            InitGl(gl, logPath);
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(logPath, "INIT FAILED: " + ex + "\n");
        }
    }

    private void InitGl(GlInterface gl, string logPath)
    {
        _gl = GL.GetApi(gl.GetProcAddress);
        System.IO.File.AppendAllText(logPath, "GL.GetApi ok\n");

        _meshProgram = CreateProgram(MeshVert, MeshFrag);
        _uMvp = _gl.GetUniformLocation(_meshProgram, "uMVP");
        _uLight = _gl.GetUniformLocation(_meshProgram, "uLightDir");
        _uHasTex = _gl.GetUniformLocation(_meshProgram, "uHasTex");
        _uTex = _gl.GetUniformLocation(_meshProgram, "uTex");
        _uTexDebug = _gl.GetUniformLocation(_meshProgram, "uTexDebug");

        _lineProgram = CreateProgram(LineVert, LineFrag);
        _lMvp = _gl.GetUniformLocation(_lineProgram, "uMVP");
        _lColor = _gl.GetUniformLocation(_lineProgram, "uColor");

        _lineVao = _gl.GenVertexArray();
        _lineVbo = _gl.GenBuffer();

        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace); // double-sided, like the game

        BuildGridLines();

        foreach (var (model, indices) in _pendingUpload)
        {
            UploadAllTextures(model);
            UploadGeometry(model, indices);
        }
        _pendingUpload.Clear();
    }

    private IEnumerable<(uint, int)> _unusedMarker() { yield break; }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastRenderAt).TotalSeconds;
        _lastRenderAt = now;
        if (dt > 1e-4)
        {
            var fps = 1.0 / dt;
            CurrentFps = CurrentFps <= 0 ? fps : CurrentFps * 0.8 + fps * 0.2;
        }

        if (_gl == null) return;
        var g = _gl;

        // Process any geometry/texture uploads queued from the UI thread (SetMesh calls).
        // This must run on the render thread where the GL context is current.
        if (_pendingUpload.Count > 0)
        {
            foreach (var (model, indices) in _pendingUpload)
            {
                UploadAllTextures(model);
                UploadGeometry(model, indices);
            }
            _pendingUpload.Clear();
            // log state after first upload
            var logPath = @"C:\Users\zzjhuang\WorkBuddy\RE引擎解包工具\gl_log.txt";
            System.IO.File.AppendAllText(logPath,
                $"After upload: primary.Vao={_primary?.Vao} batches={_primary?.Batches.Length} indexCount={_primary?.IndexCount}\n");
        }

        var w = (int)Bounds.Width;
        var h = (int)Bounds.Height;
        var scale = (float)(VisualRoot?.RenderScaling ?? 1.0);
        g.Viewport(0, 0, (uint)(w * scale), (uint)(h * scale));
        g.ClearColor(0.12f, 0.12f, 0.13f, 1f);
        g.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        var aspect = h > 0 ? (float)w / h : 1f;
        var mvp = ComputeMvp(aspect);

        if (_primary != null)
        {
            if (_playing || _clip != null) UploadPose(_primary);
            DrawModel(g, _primary, mvp);
        }
        foreach (var extra in _extras)
            DrawModel(g, extra, mvp);

        DrawLines(g, mvp);

        if (_playing) RequestNextFrameRendering();
    }

    private Matrix4x4 ComputeMvp(float aspect)
    {
        var eye = _target + new Vector3(
            MathF.Cos(_pitch) * MathF.Sin(_yaw),
            MathF.Sin(_pitch),
            MathF.Cos(_pitch) * MathF.Cos(_yaw)) * _dist;
        var view = Matrix4x4.CreateLookAt(eye, _target, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, aspect, _dist * 0.01f, _dist * 20f);
        return view * proj;
    }

    private static bool _drawLogged;
    private void DrawModel(GL g, GlModel model, Matrix4x4 mvp)
    {
        if (!_drawLogged)
        {
            _drawLogged = true;
            var logPath = @"C:\Users\zzjhuang\WorkBuddy\RE引擎解包工具\gl_log.txt";
            System.IO.File.AppendAllText(logPath,
                $"DrawModel: Vao={model.Vao} batches={model.Batches.Length} indexCount={model.IndexCount}\n" +
                $"  mvp[0,0]={mvp.M11:F3} mvp[0,3]={mvp.M14:F3} mvp[3,3]={mvp.M44:F3}\n" +
                $"  target={_target} dist={_dist:F3} yaw={_yaw:F3} pitch={_pitch:F3}\n");
        }
        if (model.Vao == 0) return;
        g.UseProgram(_meshProgram);
        unsafe { g.UniformMatrix4(_uMvp, 1, true, (float*)&mvp); }
        g.Uniform3(_uLight, LightDir.X, LightDir.Y, LightDir.Z);
        g.Uniform1(_uTex, 0);
        g.Uniform1(_uTexDebug, TexDebugMode);
        g.BindVertexArray(model.Vao);
        for (var i = 0; i < model.Batches.Length; i++)
        {
            var tex = i < model.TexHandles.Length ? model.TexHandles[i] : 0u;
            var (_, start, count) = model.Batches[i];
            g.Uniform1(_uHasTex, tex != 0 ? 1 : 0);
            if (tex != 0)
            {
                g.ActiveTexture(TextureUnit.Texture0);
                g.BindTexture(TextureTarget.Texture2D, tex);
            }
            unsafe { g.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedInt, (void*)(start * 4)); }
            // check GL error once on first draw
            if (!_drawLogged) { _drawLogged = true; }
            var err = g.GetError();
            if (err != GLEnum.NoError)
            {
                var logPath = @"C:\Users\zzjhuang\WorkBuddy\RE引擎解包工具\gl_log.txt";
                System.IO.File.AppendAllText(logPath, $"DrawElements GL error: {err} (batch {i} start={start} count={count})\n");
            }
        }
        g.BindVertexArray(0);
    }

    // ---------- lines: grid + skeleton ----------

    private void BuildGridLines()
    {
        _lineVerts.Clear();
        const float extent = 4f, step = 0.5f;
        for (var v = -extent; v <= extent + 1e-4f; v += step)
        {
            AddLineVertex(new Vector3(v, 0, -extent)); AddLineVertex(new Vector3(v, 0, extent));
            AddLineVertex(new Vector3(-extent, 0, v)); AddLineVertex(new Vector3(extent, 0, v));
        }
        _gridLineCount = _lineVerts.Count / 3;
    }

    private void AddLineVertex(Vector3 v)
    {
        _lineVerts.Add(v.X); _lineVerts.Add(v.Y); _lineVerts.Add(v.Z);
    }

    private void DrawLines(GL g, Matrix4x4 mvp)
    {
        var total = _gridLineCount;
        var skeletonStart = _lineVerts.Count / 3;

        if (ShowSkeleton && _primary != null && _primary.Mesh.Bones.Length > 0)
        {
            var bones = _primary.Mesh.Bones;
            var globals = _primary.BoneGlobals;
            for (var bi = 0; bi < bones.Length; bi++)
            {
                var parent = bones[bi].ParentIndex;
                if (parent < 0 || parent >= globals.Length) continue;
                AddLineVertex(globals[parent].Translation);
                AddLineVertex(globals[bi].Translation);
            }
            total = _lineVerts.Count / 3;
        }

        if (total == 0) return;

        g.UseProgram(_lineProgram);
        unsafe { g.UniformMatrix4(_lMvp, 1, true, (float*)&mvp); }
        g.BindVertexArray(_lineVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _lineVbo);
        var floats = _lineVerts.ToArray();
        unsafe
        {
            fixed (float* p = floats)
                g.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(floats.Length * 4), p, BufferUsageARB.DynamicDraw);
        }
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);

        g.Uniform4(_lColor, 0.35f, 0.35f, 0.35f, 1f);
        g.DrawArrays(PrimitiveType.Lines, 0, (uint)_gridLineCount);
        if (total > _gridLineCount)
        {
            g.Uniform4(_lColor, 0.3f, 0.5f, 1f, 1f);
            g.DrawArrays(PrimitiveType.Lines, _gridLineCount, (uint)(total - _gridLineCount));
        }
        g.BindVertexArray(0);

        // reset skeleton verts for next frame (grid is persistent)
        _lineVerts.RemoveRange(_gridLineCount * 3, _lineVerts.Count - _gridLineCount * 3);
    }

    // ---------- shaders ----------

    private const string MeshVert = """
        #version 300 es
        precision highp float;
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec3 aNormal;
        layout(location=2) in vec2 aUV;
        uniform mat4 uMVP;
        out vec3 vNormal;
        out vec2 vUV;
        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);
            vNormal = aNormal;
            vUV = aUV;
        }
        """;

    private const string MeshFrag = """
        #version 300 es
        precision highp float;
        in vec3 vNormal;
        in vec2 vUV;
        uniform vec3 uLightDir;
        uniform sampler2D uTex;
        uniform int uHasTex;
        uniform int uTexDebug;
        out vec4 FragColor;
        void main() {
            float lum = 0.25 + 0.75 * abs(dot(normalize(vNormal), -uLightDir));
            if (uTexDebug == 1) { FragColor = vec4(vec3(0.82) * lum, 1.0); return; }
            if (uTexDebug == 3) { FragColor = vec4(fract(vUV), 0.0, 1.0); return; }
            vec3 base = uHasTex == 1 ? texture(uTex, vUV).rgb : vec3(0.8);
            if (uTexDebug == 2) { FragColor = vec4(base, 1.0); return; }
            FragColor = vec4(base * lum, 1.0);
        }
        """;

    private const string LineVert = """
        #version 300 es
        precision highp float;
        layout(location=0) in vec3 aPos;
        uniform mat4 uMVP;
        void main() { gl_Position = uMVP * vec4(aPos, 1.0); }
        """;

    private const string LineFrag = """
        #version 300 es
        precision highp float;
        uniform vec4 uColor;
        out vec4 FragColor;
        void main() { FragColor = uColor; }
        """;

    private uint CreateProgram(string vertSrc, string fragSrc)
    {
        var g = _gl!;
        var vs = CompileShader(ShaderType.VertexShader, vertSrc);
        var fs = CompileShader(ShaderType.FragmentShader, fragSrc);
        var prog = g.CreateProgram();
        g.AttachShader(prog, vs);
        g.AttachShader(prog, fs);
        g.LinkProgram(prog);
        g.GetProgram(prog, ProgramPropertyARB.LinkStatus, out var ok);
        if (ok == 0)
        {
            var log = g.GetProgramInfoLog(prog);
            throw new InvalidOperationException("Shader link failed: " + log);
        }
        g.DeleteShader(vs);
        g.DeleteShader(fs);
        return prog;
    }

    private uint CompileShader(ShaderType type, string src)
    {
        var g = _gl!;
        var shader = g.CreateShader(type);
        g.ShaderSource(shader, src);
        g.CompileShader(shader);
        g.GetShader(shader, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0)
        {
            var log = g.GetShaderInfoLog(shader);
            throw new InvalidOperationException($"Shader compile failed ({type}): {log}");
        }
        return shader;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _timer.Stop();
        _gl = null;
        base.OnOpenGlDeinit(gl);
    }

    // ---------- input (same orbit/pan/zoom) ----------

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus(); // ensure this control has keyboard/pointer focus for hit-testing
        var p = e.GetCurrentPoint(this);
        _lastPointer = p.Position;
        if (p.Properties.IsLeftButtonPressed) _orbiting = true;
        if (p.Properties.IsRightButtonPressed || p.Properties.IsMiddleButtonPressed) _panning = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetCurrentPoint(this);
        var pos = p.Position;
        var dx = (float)(pos.X - _lastPointer.X);
        var dy = (float)(pos.Y - _lastPointer.Y);
        _lastPointer = pos;

        if (_orbiting)
        {
            _yaw -= dx * 0.01f;
            _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.5f, 1.5f);
            RequestNextFrameRendering();
        }
        else if (_panning)
        {
            var view = Matrix4x4.CreateLookAt(
                _target + new Vector3(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw)) * _dist,
                _target, Vector3.UnitY);
            Matrix4x4.Invert(view, out var inv);
            var scale = _dist * 0.0018f;
            var right = Vector3.TransformNormal(Vector3.UnitX, inv);
            var up = Vector3.TransformNormal(Vector3.UnitY, inv);
            _target += (-right * dx + up * dy) * scale;
            RequestNextFrameRendering();
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _orbiting = false;
        _panning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(Avalonia.Input.PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _dist = Math.Clamp(_dist * (e.Delta.Y > 0 ? 0.9f : 1.1f), 0.05f, 1000f);
        RequestNextFrameRendering();
        e.Handled = true;
    }
}
