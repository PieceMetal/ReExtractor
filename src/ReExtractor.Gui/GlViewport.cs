using System;

using System.Collections.Generic;

using System.Linq;

using System.Numerics;

using System.Threading.Tasks;

using Avalonia.OpenGL;

using Avalonia.OpenGL.Controls;

using Avalonia.Threading;

using Avalonia.Input;

using ReExtractor.Core;

using Silk.NET.OpenGL;



namespace ReExtractor.Gui;



/// <summary>

/// GPU-accelerated 3D viewport via Avalonia's OpenGlControlBase + Silk.NET.OpenGL.

/// CPU does skinning (parallel), GPU draws — full-screen without lag.

/// </summary>

public sealed class GlViewport : OpenGlControlBase

{

    public sealed record ExportModel(ViewportMesh Mesh, IReadOnlySet<int> VisibleGroups);

    public enum ViewportRenderMode { Material, Textured, Solid, Wireframe, TexturedEdges }

    public enum ViewportProjection { Perspective, Orthographic }

    public enum ViewPreset { Perspective, Front, Back, Left, Right, Top, Bottom }



    private GL? _gl;



    private sealed class GlModel

    {

        public required ViewportMesh Mesh;

        public uint Vao, Vbo, Ebo, EdgeEbo, SelectionEbo;

        public int IndexCount;

        public int EdgeIndexCount;

        public int SelectionEdgeIndexCount;

        public (int Slot, bool AlphaCutout, int Start, int Count)[] Batches = []; // material state + index range

        public float[] Interleaved = []; // pos+normal+uv per vertex, rebuilt on pose change

        public Vector3[] Posed = [];

        public Vector3[] PosedNormals = [];

        public Matrix4x4[] BoneGlobals = [];

        public Matrix4x4[] JointMats = [];

        public Dictionary<int, BoneTrack>? Tracks;

        public HashSet<int> VisibleGroups = [];

        public uint[] TextureHandles = []; // indexed by Mesh.Textures slot

        public bool IsPrimary;

    }



    private GlModel? _primary;

    private int? _selectedPrimaryGroup;

    // -1 represents the virtual FBX Root used when the source skeleton starts at C_Hip.
    private int? _selectedPrimaryBone;

    private readonly List<GlModel> _extras = new();

    private readonly object _sceneLock = new();

    private AnimationClip? _clip;



    private uint _meshProgram, _lineProgram;

    private int _uMvp, _uLight, _uViewDir, _uHasTex, _uTex, _uRenderMode, _uAlphaCutout;

    private int _lMvp, _lColor;

    private uint _lineVao, _lineVbo;

    private readonly List<float> _lineVerts = new();

    private readonly object _lineVertsLock = new();

    private int _gridMinorCount, _gridLineCount;



    // camera (same orbit math as the software viewport)

    private const float DefaultYaw = -0.38f;
    private const float DefaultPitch = 0.35f;
    private float _yaw = DefaultYaw, _pitch = DefaultPitch, _dist = 3.0f;

    private Vector3 _target = Vector3.Zero;

    private Vector3 _boundsMin = new(-1), _boundsMax = new(1);

    private Avalonia.Point _lastPointer;

    private bool _orbiting, _panning;



    // playback

    private readonly DispatcherTimer _timer;

    private float _time;

    private bool _playing;

    private DateTime _lastTick = DateTime.UtcNow;



    private static readonly Vector3 LightDir = Vector3.Normalize(new(-0.45f, -0.75f, -0.55f));

    // RE mesh data is internally evaluated in its native basis. The viewport world is UE-style:
    // X right, Y forward, Z up. Convert at the render boundary so skinning/animation math stays
    // in the source basis and the displayed model stands upright in UE world space.
    private static readonly Matrix4x4 ReToUeWorld = new(
        -1, 0, 0, 0,
        0, 0, 1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);



    public bool ShowSkeleton { get; set; }

    public bool ShowGrid { get; set; } = true;

    public bool ShowAxes { get; set; } = true;

    public ViewportRenderMode RenderMode { get; set; } = ViewportRenderMode.Material;

    public ViewportProjection Projection { get; set; } = ViewportProjection.Perspective;

    public ViewPreset CurrentView { get; private set; } = ViewPreset.Perspective;

    public string ViewLabel => $"{CurrentViewName} · {(Projection == ViewportProjection.Perspective ? "透视" : "正交")} · {RenderModeName}";

    private string CurrentViewName => CurrentView switch

    {

        ViewPreset.Front => "前视图", ViewPreset.Back => "后视图", ViewPreset.Left => "左视图",

        ViewPreset.Right => "右视图", ViewPreset.Top => "顶视图", ViewPreset.Bottom => "底视图", _ => "用户视图"

    };

    private string RenderModeName => RenderMode switch

    {

        ViewportRenderMode.Material => "材质", ViewportRenderMode.Textured => "贴图",

        ViewportRenderMode.Solid => "实体", ViewportRenderMode.Wireframe => "线框", _ => "材质+边线"

    };



    /// <summary>

    /// Legacy diagnostic API retained for callers. The professional toolbar uses RenderMode.

    /// </summary>

    public int TexDebugMode { get; set; }



    public bool IsPlaying => _playing;

    public bool HasAnimation => _clip != null && _clip.Duration > 0;

    public bool HasMesh => _primary != null;

    public float Duration => _clip?.Duration ?? 0;

    public int AnimationFrameRate => _clip?.FrameRate ?? 60;

    public int AnimationFrameCount => _clip?.FrameCount ?? 0;

    public float CurrentTime => _time;

    public double CurrentFps { get; private set; }

    public string RenderDiagnostics { get; private set; } = "GL 等待首帧";

    private string _lineErrorStage = "NoError";

    private DateTime _lastRenderAt = DateTime.UtcNow;

    public string StatusInfo { get; private set; } = "无模型";

    public int ExtraModelCount => _extras.Count;

    public IReadOnlyList<ViewportGroup> PrimaryGroups => _primary?.Mesh.Groups ?? [];

    public IReadOnlySet<int> VisiblePrimaryGroups => _primary?.VisibleGroups ?? EmptyGroupSet;

    public ViewportBone[] PrimaryBones => _primary?.Mesh.Bones ?? [];

    public int[] PrimaryDeformBoneIndices => _primary?.Mesh.DeformToBone ?? [];

    public IReadOnlyList<ExportModel> ExportModels

    {

        get

        {

            if (_primary == null) return [];

            return new[] { _primary }.Concat(_extras)

                .Select(model => new ExportModel(model.Mesh, model.VisibleGroups.ToHashSet()))

                .ToArray();

        }

    }

    /// <summary>All currently loaded scene meshes, including merge extras.</summary>
    public IReadOnlyList<ViewportMesh> SceneMeshes
    {
        get
        {
            var result = new List<ViewportMesh>();
            if (_primary != null) result.Add(_primary.Mesh);
            result.AddRange(_extras.Select(extra => extra.Mesh));
            return result;
        }
    }

    private static readonly HashSet<int> EmptyGroupSet = [];

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

    /// <summary>
    /// Bone names used to decode a motlist.  Use the union of every loaded
    /// model part: MH Wilds splits the hunter skeleton across body, head,
    /// clothing and hand/finger parts.  Decoding against only the largest
    /// single part drops finger tracks when that part does not contain them.
    /// </summary>
    public string[] AllMeshBoneNames
    {
        get
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_primary != null)
            {
                foreach (var bone in _primary.Mesh.Bones)
                    names.Add(bone.Name);
            }
            foreach (var extra in _extras)
            {
                foreach (var bone in extra.Mesh.Bones)
                    names.Add(bone.Name);
            }
            return names.ToArray();
        }
    }



    public GlViewport()

    {

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

        _timer.Tick += OnTick;

    }



    // Re-render whenever the control resizes: the first frame can capture a not-yet-settled

    // Bounds (wrong aspect → tilted/clipped image); a resize-triggered redraw self-corrects it.

    protected override void OnSizeChanged(Avalonia.Controls.SizeChangedEventArgs e)

    {

        base.OnSizeChanged(e);

        RequestNextFrameRendering();

    }



    // ---------- public API (mirrors the software viewport) ----------



    public void SetMesh(ViewportMesh mesh)

    {

        _selectedPrimaryGroup = null;

        _selectedPrimaryBone = null;

        var primary = CreateModel(mesh, isPrimary: true);
        lock (_sceneLock)
        {
            _primary = primary;
            _extras.Clear();
        }

        _clip = null;

        StopPlayback();

        RecalculateVisibleBounds();

        _target = (_boundsMin + _boundsMax) * 0.5f;

        BuildGridLines();

        FitCamera();

        UpdateStatusInfo();

        StateChanged?.Invoke();

        RequestNextFrameRendering();

    }



    /// <summary>
    /// Replace the current scene with the same mesh layout after reloading textures.
    /// Animation clip, current time, playback state, and overlay topology are preserved.
    /// </summary>
    public void ReplaceSceneMeshes(IReadOnlyList<ViewportMesh> meshes, bool merge)
    {
        if (meshes.Count == 0) return;

        var clip = _clip;
        var time = clip == null ? 0f : Math.Clamp(_time, 0, clip.Duration);
        var wasPlaying = _playing;
        StopPlayback();

        var primaryMesh = merge && meshes.Count > 1
            ? ViewportMesh.Merge(meshes)
            : meshes[0];
        var primary = CreateModel(primaryMesh, isPrimary: true);
        var extras = new List<GlModel>();
        if (!merge)
        {
            for (var i = 1; i < meshes.Count; i++)
                extras.Add(CreateModel(meshes[i], isPrimary: false));
        }

        lock (_sceneLock)
        {
            _primary = primary;
            _extras.Clear();
            _extras.AddRange(extras);
        }

        _clip = clip;
        _time = time;
        _primary.Tracks = BuildTrackMap(_primary);
        foreach (var extra in _extras) extra.Tracks = BuildTrackMap(extra);
        if (_clip != null)
            EvaluateAllPose(_time);

        RecalculateVisibleBounds();
        _target = (_boundsMin + _boundsMax) * 0.5f;
        BuildGridLines();
        FitCamera();
        UpdateStatusInfo();
        if (wasPlaying && _clip != null && _clip.Duration > 0) StartPlayback();
        else RequestNextFrameRendering();
        StateChanged?.Invoke();
    }



    public void AddMesh(ViewportMesh mesh, string name)

    {

        var model = CreateModel(mesh, isPrimary: false);

        model.Tracks = BuildTrackMap(model);

        // Add first so a newly added, more complete skeleton can become the pose driver.
        // Right-click overlay order is arbitrary; the first selected part may be a small
        // arm/head mesh and must not drive a full-body animation.
        lock (_sceneLock)
            _extras.Add(model);

        if (_clip != null)
            EvaluateAllPose(_time);

        RecalculateVisibleBounds();

        BuildGridLines();

        FitCamera();

        StateChanged?.Invoke();

        RequestNextFrameRendering();

    }



    public void SetAnimation(AnimationClip? clip)

    {

        _clip = clip;

        _time = 0;

        EnsureAnimationBoneBuffers(_primary);
        foreach (var extra in _extras)
            EnsureAnimationBoneBuffers(extra);

        if (_primary != null) _primary.Tracks = BuildTrackMap(_primary);

        foreach (var extra in _extras) extra.Tracks = BuildTrackMap(extra);

        if (clip != null && clip.Duration > 0) PlayFromStart(); else StopPlayback();

        StateChanged?.Invoke();

    }

    private void EnsureAnimationBoneBuffers(GlModel? model)
    {
        if (model == null) return;
        if (model.BoneGlobals.Length < model.Mesh.Bones.Length)
            model.BoneGlobals = new Matrix4x4[Math.Max(1, model.Mesh.Bones.Length)];
        ComputeBindBoneGlobals(model);
    }



    private GlModel? GetPoseDriver()
    {
        GlModel? driver = _primary;
        foreach (var extra in _extras)
        {
            // Animation loading appends MOT-only helper bones to every part.  A partial
            // face/hair skeleton can therefore end up with more total bones than the
            // body and must not become the pose driver.  DeformToBone is the stable
            // source-skin count; use geometry only as a deterministic tie-breaker.
            if (driver == null ||
                extra.Mesh.DeformToBone.Length > driver.Mesh.DeformToBone.Length ||
                (extra.Mesh.DeformToBone.Length == driver.Mesh.DeformToBone.Length &&
                 extra.Mesh.VertexCount > driver.Mesh.VertexCount))
                driver = extra;
        }
        return driver;
    }

    private void EvaluateAllPose(float time)
    {
        var driver = GetPoseDriver();
        if (driver == null) return;

        EvaluatePose(driver, time);
        if (!ReferenceEquals(driver, _primary) && _primary != null)
            EvaluatePose(_primary, time);
        foreach (var extra in _extras)
            if (!ReferenceEquals(extra, driver)) EvaluatePose(extra, time);
    }



    /// <summary>Restart the current animation at frame zero and enter playback.</summary>
    public void PlayFromStart()
    {
        if (_clip == null || _clip.Duration <= 0) return;

        _time = 0;
        EvaluateAllPose(_time);
        StartPlayback();
        RequestNextFrameRendering();
    }



    public void TogglePlayback()
    {
        if (_playing) StopPlayback(); else if (HasAnimation) StartPlayback();
        StateChanged?.Invoke();
    }

    public void PausePlayback()
    {
        if (!_playing) return;
        StopPlayback();
        StateChanged?.Invoke();
    }



    public void ScrubTo(float time)

    {

        if (_clip == null) return;

        _time = Math.Clamp(time, 0, _clip.Duration);

        EvaluateAllPose(_time);

        RequestNextFrameRendering();

    }



    public void Refresh() => RequestNextFrameRendering();



    public void SetPrimaryGroupVisible(int key, bool visible)

    {

        if (_primary == null || !_primary.Mesh.Groups.Any(g => g.Key == key)) return;

        if (visible) _primary.VisibleGroups.Add(key); else _primary.VisibleGroups.Remove(key);

        QueueIndexUpdate(_primary);

        RecalculateVisibleBounds();

        UpdateStatusInfo();

        RequestNextFrameRendering();

        StateChanged?.Invoke();

    }



    public void ResetPrimaryGroupVisibility()

    {

        if (_primary == null) return;

        _primary.VisibleGroups = _primary.Mesh.Groups.Where(g => g.DefaultVisible).Select(g => g.Key).ToHashSet();

        QueueIndexUpdate(_primary);

        RecalculateVisibleBounds();

        UpdateStatusInfo();

        FrameAll();

    }



    public void SetAllPrimaryGroupsVisible(bool visible)

    {

        if (_primary == null) return;

        _primary.VisibleGroups = visible

            ? _primary.Mesh.Groups.Select(g => g.Key).ToHashSet()

            : [];

        QueueIndexUpdate(_primary);

        RecalculateVisibleBounds();

        UpdateStatusInfo();

        RequestNextFrameRendering();

        StateChanged?.Invoke();

    }



    public void IsolatePrimaryGroup(int key)

    {

        if (_primary == null || !_primary.Mesh.Groups.Any(g => g.Key == key)) return;

        _primary.VisibleGroups = [key];

        QueueIndexUpdate(_primary);

        RecalculateVisibleBounds();

        UpdateStatusInfo();

        FrameAll();

    }



    public void FramePrimaryGroup(int key)

    {

        if (_primary == null || !_primary.Mesh.Groups.Any(g => g.Key == key)) return;

        if (!TryGetPrimaryGroupBounds(key, out var min, out var max)) return;

        _target = (min + max) * 0.5f;

        FitCamera(min, max);

        RequestNextFrameRendering();

        StateChanged?.Invoke();

    }



    public void SelectPrimaryGroup(int? key)

    {

        if (_primary == null) return;

        _selectedPrimaryGroup = key.HasValue && _primary.Mesh.Groups.Any(g => g.Key == key.Value)

            ? key

            : null;

        QueueSelectionUpdate(_primary);

        RequestNextFrameRendering();

    }

    /// <summary>Select a source bone, or -1 for the virtual Root added by FBX export.</summary>
    public void SelectPrimaryBone(int? index)
    {
        if (_primary == null) return;
        _selectedPrimaryBone = index is -1 ||
            index.HasValue && index.Value >= 0 && index.Value < _primary.Mesh.Bones.Length
                ? index
                : null;
        if (_selectedPrimaryBone.HasValue) ShowSkeleton = true;
        RequestNextFrameRendering();
        StateChanged?.Invoke();
    }

    /// <summary>Keep the current viewing angle and move the camera target onto one bone.</summary>
    public void FramePrimaryBone(int index)
    {
        if (_primary == null) return;
        Vector3 point;
        if (index == -1)
        {
            point = Vector3.Zero;
        }
        else
        {
            if (index < 0 || index >= _primary.Mesh.Bones.Length || index >= _primary.BoneGlobals.Length) return;
            point = Vector3.Transform(_primary.BoneGlobals[index].Translation, ReToUeWorld);
        }

        _target = point;
        var sceneSize = MathF.Max(0.1f, (_boundsMax - _boundsMin).Length());
        _dist = MathF.Max(0.18f, sceneSize * 0.2f);
        RequestNextFrameRendering();
        StateChanged?.Invoke();
    }



    /// <summary>

    /// Camera input entry points used by the transparent Avalonia input surface hosted above

    /// the native OpenGL control.  OpenGlControlBase can be rendered through a native surface

    /// on Windows, so relying on its own routed pointer events is not reliable.

    /// </summary>

    public void BeginCameraDrag(Avalonia.Point position, bool orbit, bool pan)

    {

        _lastPointer = position;

        _orbiting = orbit;

        _panning = pan;

    }



    public void UpdateCameraDrag(Avalonia.Point position)

    {

        var dx = (float)(position.X - _lastPointer.X);

        var dy = (float)(position.Y - _lastPointer.Y);

        _lastPointer = position;



        if (_orbiting)

        {

            CurrentView = ViewPreset.Perspective;

            // Named direction views are orthographic, but as soon as the user starts a

            // free orbit this becomes the User view again. User view is always perspective;

            // never carry the named view's orthographic projection into an orbit.

            Projection = ViewportProjection.Perspective;

            _yaw += dx * 0.01f;

            _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.56f, 1.56f);

            RequestNextFrameRendering();

            StateChanged?.Invoke();

        }

        else if (_panning)

        {

            var view = Matrix4x4.CreateLookAt(GetEye(), _target, GetCameraUp());

            Matrix4x4.Invert(view, out var inv);

            var scale = _dist * 0.0018f;

            var right = Vector3.TransformNormal(Vector3.UnitX, inv);

            var up = Vector3.TransformNormal(Vector3.UnitY, inv);

            _target += (-right * dx + up * dy) * scale;

            RequestNextFrameRendering();

        }

    }



    public void EndCameraDrag()

    {

        _orbiting = false;

        _panning = false;

    }



    public void ZoomCamera(double wheelDelta)

    {

        if (wheelDelta == 0) return;

        // Exponential zoom stays smooth for high-resolution wheels and trackpads.

        var factor = MathF.Exp((float)-wheelDelta * 0.12f);

        _dist = Math.Clamp(_dist * factor, 0.05f, 1000f);

        RequestNextFrameRendering();

    }



    public bool HandleCameraKey(Key key)

    {

        switch (key)

        {

            case Key.F: FrameAll(); break;

            case Key.Z:

                if (_selectedPrimaryGroup.HasValue) FramePrimaryGroup(_selectedPrimaryGroup.Value);

                else FrameAll();

                break;

            case Key.G: ShowGrid = !ShowGrid; Refresh(); StateChanged?.Invoke(); break;

            case Key.B: ShowSkeleton = !ShowSkeleton; Refresh(); StateChanged?.Invoke(); break;

            case Key.D1: case Key.NumPad1: SetView(ViewPreset.Perspective); break;

            case Key.D2: case Key.NumPad2: SetView(ViewPreset.Front); break;

            case Key.D3: case Key.NumPad3: SetView(ViewPreset.Back); break;

            case Key.D4: case Key.NumPad4: SetView(ViewPreset.Left); break;

            case Key.D5: case Key.NumPad5: SetView(ViewPreset.Right); break;

            case Key.D6: case Key.NumPad6: SetView(ViewPreset.Top); break;

            case Key.D7: case Key.NumPad7: SetView(ViewPreset.Bottom); break;

            default: return false;

        }

        return true;

    }



    public void FrameAll()

    {

        // "Frame all" is also the orbit-target reset: undo any prior pan and make the

        // complete model bounds the camera's tracking point, so subsequent orbiting stays

        // centered on the asset just like Zoom Extents Selected in a DCC viewport.

        _target = (_boundsMin + _boundsMax) * 0.5f;

        FitCamera();

        RequestNextFrameRendering();

        StateChanged?.Invoke();

    }



    public void SetView(ViewPreset preset)

    {

        CurrentView = preset;

        switch (preset)

        {

            case ViewPreset.Front:  _yaw = 0f;                 _pitch = 0f; break;

            case ViewPreset.Back:   _yaw = MathF.PI;           _pitch = 0f; break;

            case ViewPreset.Left:   _yaw = -MathF.PI * 0.5f;   _pitch = 0f; break;

            case ViewPreset.Right:  _yaw = MathF.PI * 0.5f;    _pitch = 0f; break;

            case ViewPreset.Top:    _yaw = 0f;                 _pitch = MathF.PI * 0.5f; break;

            case ViewPreset.Bottom: _yaw = 0f;                 _pitch = -MathF.PI * 0.5f; break;

            default:                _yaw = DefaultYaw;         _pitch = DefaultPitch; break;

        }

        // Max-style named views are orthographic; User/Perspective is perspective.

        Projection = preset == ViewPreset.Perspective ? ViewportProjection.Perspective : ViewportProjection.Orthographic;

        FitCamera();

        RequestNextFrameRendering();

        StateChanged?.Invoke();

    }



    public void SetRenderMode(ViewportRenderMode mode)

    {

        RenderMode = mode;

        RequestNextFrameRendering();

        StateChanged?.Invoke();

    }



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

        if (_time >= _clip.Duration) _time %= _clip.Duration;

        EvaluateAllPose(_time);

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

            VisibleGroups = mesh.Groups.Where(g => g.DefaultVisible).Select(g => g.Key).ToHashSet(),

        };

        ComputeBindBoneGlobals(model);

        RebuildInterleaved(model);



        var (indices, edges) = BuildIndexData(model);



        // ALWAYS defer upload to the render thread (OnOpenGlInit / OnOpenGlRender).

        // GL context is bound to the render thread only; calling GL APIs from the UI thread

        // silently produces handle=0 or corrupts state under ANGLE.

        lock (_pendingLock)
            _pendingUpload.Add((model, indices, edges));

        RequestNextFrameRendering(); // wake render loop so pending upload is processed promptly

        return model;

    }



    private static bool IsFaceVisible(GlModel model, int face)

    {

        var mesh = model.Mesh;

        // Dedicated RE cornea/refraction/tear shaders have no faithful basic-material

        // fallback. Do not draw their geometry as an opaque white shell in the preview.

        if (face < mesh.FaceExportHidden.Length && mesh.FaceExportHidden[face]) return false;

        var faceGroups = mesh.FaceGroups;

        return faceGroups.Length != mesh.FaceCount || mesh.Groups.Length == 0 ||

               model.VisibleGroups.Contains(faceGroups[face]);

    }



    private static (uint[] Indices, uint[] Edges) BuildIndexData(GlModel model)

    {

        var mesh = model.Mesh;

        var indices = new List<uint>(mesh.FaceCount * 3);

        var groups = new Dictionary<(int Slot, bool AlphaCutout), List<uint>>();

        for (var f = 0; f < mesh.FaceCount; f++)

        {

            if (!IsFaceVisible(model, f)) continue;

            var slot = mesh.FaceTexture[f];

            var alphaCutout = f < mesh.FaceAlphaCutout.Length && mesh.FaceAlphaCutout[f];

            var batchKey = (slot, alphaCutout);

            if (!groups.TryGetValue(batchKey, out var list)) groups[batchKey] = list = new List<uint>();

            var (a, b, c) = mesh.Faces[f];

            list.Add((uint)a); list.Add((uint)b); list.Add((uint)c);

        }

        var batches = new List<(int, bool, int, int)>();

        foreach (var (key, list) in groups)

        {

            var start = indices.Count;

            indices.AddRange(list);

            batches.Add((key.Slot, key.AlphaCutout, start, list.Count));

        }

        model.Batches = batches.ToArray();

        model.IndexCount = indices.Count;



        var edgeSet = new HashSet<ulong>();

        var edgeIndices = new List<uint>(indices.Count * 2);

        static ulong EdgeKey(uint a, uint b)

        {

            if (a > b) (a, b) = (b, a);

            return ((ulong)a << 32) | b;

        }

        for (var i = 0; i + 2 < indices.Count; i += 3)

        {

            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];

            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })

                if (edgeSet.Add(EdgeKey(x, y))) { edgeIndices.Add(x); edgeIndices.Add(y); }

        }

        model.EdgeIndexCount = edgeIndices.Count;

        return (indices.ToArray(), edgeIndices.ToArray());

    }



    private void UploadAllTextures(GlModel model)

    {

        var handles = new uint[model.Mesh.Textures.Length];

        for (var i = 0; i < handles.Length; i++)

        {

            handles[i] = UploadTexture(model.Mesh.Textures[i]);

        }

        model.TextureHandles = handles;

    }



    private readonly List<(GlModel model, uint[] indices, uint[] edges)> _pendingUpload = new();

    private readonly Dictionary<GlModel, (uint[] indices, uint[] edges)> _pendingIndexUpdates = new();

    private readonly Dictionary<GlModel, uint[]> _pendingSelectionUpdates = new();

    private readonly object _pendingLock = new();



    private void QueueIndexUpdate(GlModel model)

    {

        var data = BuildIndexData(model);

        lock (_pendingLock)
        {
        if (model.Vao == 0)

        {

            _pendingUpload.RemoveAll(x => ReferenceEquals(x.model, model));

            _pendingUpload.Add((model, data.Indices, data.Edges));

        }

        else

        {

            _pendingIndexUpdates[model] = data;

        }
        }

    }



    private void QueueSelectionUpdate(GlModel model)

    {

        var edges = BuildSelectionEdges(model);
        lock (_pendingLock)
            _pendingSelectionUpdates[model] = edges;

    }

    private (GlModel model, uint[] indices, uint[] edges)[] TakePendingUploads()
    {
        lock (_pendingLock)
        {
            var pending = _pendingUpload.ToArray();
            _pendingUpload.Clear();
            return pending;
        }
    }

    private KeyValuePair<GlModel, (uint[] indices, uint[] edges)>[] TakePendingIndexUpdates()
    {
        lock (_pendingLock)
        {
            var pending = _pendingIndexUpdates.ToArray();
            _pendingIndexUpdates.Clear();
            return pending;
        }
    }

    private KeyValuePair<GlModel, uint[]>[] TakePendingSelectionUpdates()
    {
        lock (_pendingLock)
        {
            var pending = _pendingSelectionUpdates.ToArray();
            _pendingSelectionUpdates.Clear();
            return pending;
        }
    }

    private GlModel[] SnapshotSceneModels()
    {
        lock (_sceneLock)
        {
            if (_primary == null) return _extras.ToArray();
            return new[] { _primary }.Concat(_extras).ToArray();
        }
    }

    private void InvalidateGpuResourcesAndQueueScene()
    {
        var models = SnapshotSceneModels();
        lock (_pendingLock)
        {
            _pendingIndexUpdates.Clear();
            _pendingSelectionUpdates.Clear();

            foreach (var model in models)
            {
                model.Vao = 0;
                model.Vbo = 0;
                model.Ebo = 0;
                model.EdgeEbo = 0;
                model.SelectionEbo = 0;
                model.TextureHandles = [];
                model.SelectionEdgeIndexCount = 0;

                var data = BuildIndexData(model);
                _pendingUpload.RemoveAll(item => ReferenceEquals(item.model, model));
                _pendingUpload.Add((model, data.Indices, data.Edges));
            }
        }
    }



    private uint[] BuildSelectionEdges(GlModel model)

    {

        if (!model.IsPrimary || !_selectedPrimaryGroup.HasValue ||

            !model.VisibleGroups.Contains(_selectedPrimaryGroup.Value)) return [];

        var mesh = model.Mesh;

        var edges = new HashSet<ulong>();

        var result = new List<uint>();

        static ulong Key(uint a, uint b)

        {

            if (a > b) (a, b) = (b, a);

            return ((ulong)a << 32) | b;

        }

        for (var f = 0; f < mesh.FaceCount; f++)

        {

            if (f >= mesh.FaceGroups.Length || mesh.FaceGroups[f] != _selectedPrimaryGroup.Value) continue;

            var (ia, ib, ic) = mesh.Faces[f];

            var a = (uint)ia; var b = (uint)ib; var c = (uint)ic;

            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })

                if (edges.Add(Key(x, y))) { result.Add(x); result.Add(y); }

        }

        return result.ToArray();

    }



    private uint UploadTexture(ViewportTexture tex)

    {

        if (_gl == null) return 0;

        var g = _gl;

        var handle = g.GenTexture();

        g.BindTexture(TextureTarget.Texture2D, handle);



        // ViewportTexture.Pixels is packed uint: A<<24 | R<<16 | G<<8 | B (CPU BGRA/ARGB).

        // ANGLE ES 3.0 only guarantees RGBA byte upload. Convert to byte array: R,G,B,A per pixel.

        // Keep rows in the decoded image's top-down order. OpenGL's origin convention does

        // not require a CPU-side flip here: RE mesh UVs and the decoded .tex image already

        // agree in this orientation. Flipping the upload maps every UV island to the wrong

        // part of asymmetric atlases (the visible striped / scrambled-material bug).

        var w = tex.Width; var h = tex.Height;

        var rgba = new byte[w * h * 4];

        var src = tex.Pixels;

        // Parallel per-row conversion (was a single-threaded per-pixel loop = the slow part of model load).

        Parallel.For(0, h, y =>

        {

            var srcRow = y * w;

            var off = y * w * 4;

            for (var x = 0; x < w; x++)

            {

                var p = src[srcRow + x];

                rgba[off]     = (byte)(p >> 16); // R

                rgba[off + 1] = (byte)(p >> 8);  // G

                rgba[off + 2] = (byte)p;         // B

                rgba[off + 3] = (byte)(p >> 24); // A

                off += 4;

            }

        });

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



    private void UploadGeometry(GlModel model, uint[] indices, uint[] edgeIndices)

    {

        if (_gl == null) return;

        var g = _gl;

        model.Vao = g.GenVertexArray();

        model.Vbo = g.GenBuffer();

        model.Ebo = g.GenBuffer();

        model.EdgeEbo = g.GenBuffer();

        model.SelectionEbo = g.GenBuffer();



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



        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.EdgeEbo);

        unsafe

        {

            fixed (uint* p = edgeIndices)

                g.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(edgeIndices.Length * 4), p, BufferUsageARB.StaticDraw);

        }



        UploadSelectionData(model, BuildSelectionEdges(model));



        // VAO element binding must point back at the triangle buffer for normal draws.

        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.Ebo);



        const int stride = 8 * 4;

        g.EnableVertexAttribArray(0);

        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);

        g.EnableVertexAttribArray(1);

        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 12);

        g.EnableVertexAttribArray(2);

        g.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 24);

        g.BindVertexArray(0);

    }



    private void UploadIndexData(GlModel model, uint[] indices, uint[] edgeIndices)

    {

        if (_gl == null || model.Vao == 0) return;

        var g = _gl;

        g.BindVertexArray(model.Vao);

        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.Ebo);

        unsafe

        {

            fixed (uint* p = indices)

                g.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * 4), p, BufferUsageARB.StaticDraw);

        }

        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.EdgeEbo);

        unsafe

        {

            fixed (uint* p = edgeIndices)

                g.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(edgeIndices.Length * 4), p, BufferUsageARB.StaticDraw);

        }

        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.Ebo);

        g.BindVertexArray(0);

        QueueSelectionUpdate(model);

    }



    private void UploadSelectionData(GlModel model, uint[] edges)

    {

        if (_gl == null || model.SelectionEbo == 0) return;

        model.SelectionEdgeIndexCount = edges.Length;

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.SelectionEbo);

        unsafe

        {

            fixed (uint* p = edges)

                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(edges.Length * 4), p, BufferUsageARB.DynamicDraw);

        }

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
        var driver = GetPoseDriver();

        if (driver != null && !ReferenceEquals(model, driver))
        {
            var driverByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < driver.Mesh.Bones.Length; i++)
                driverByName.TryAdd(driver.Mesh.Bones[i].Name, i);

            for (var b = 0; b < bones.Length; b++)
                ComputeOverlayGlobal(model, b, computed, time, driver, driverByName);
        }
        else
        {
            for (var b = 0; b < bones.Length; b++)
                ComputeGlobal(model, b, computed, time);
        }



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



    private void ComputeOverlayGlobal(
        GlModel model,
        int b,
        bool[] computed,
        float time,
        GlModel primary,
        IReadOnlyDictionary<string, int> primaryByName)

    {
        if (computed[b]) return;

        var bone = model.Mesh.Bones[b];
        if (primaryByName.TryGetValue(bone.Name, out var primaryIndex))
        {
            // The primary model owns the authoritative animated hierarchy.  This is
            // what keeps a partial head/hair/body overlay on the same neck/root pose.
            model.BoneGlobals[b] = primary.BoneGlobals[primaryIndex];
            computed[b] = true;
            return;
        }

        var local = bone.LocalBind;
        if (model.Tracks != null && model.Tracks.TryGetValue(b, out var track))
            local = EvaluateLocal(track, time, local);

        var parent = bone.ParentIndex;
        if (parent >= 0)
        {
            ComputeOverlayGlobal(model, parent, computed, time, primary, primaryByName);
            model.BoneGlobals[b] = local * model.BoneGlobals[parent];
        }
        else
        {
            model.BoneGlobals[b] = local;
        }

        computed[b] = true;
    }



    private void ComputeGlobal(GlModel model, int b, bool[] computed, float time)

    {

        if (computed[b]) return;

        var bones = model.Mesh.Bones;

        var local = bones[b].LocalBind;

        if (time >= 0 && model.Tracks != null && model.Tracks.TryGetValue(b, out var track))

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



    private Dictionary<int, BoneTrack>? BuildTrackMap(GlModel model)

    {

        if (_clip == null || _primary == null) return null;

        if (_clip.NamedTracks.Count > 0)

        {

            var mapped = new Dictionary<int, BoneTrack>();

            for (var i = 0; i < model.Mesh.Bones.Length; i++)

                if (_clip.NamedTracks.TryGetValue(model.Mesh.Bones[i].Name, out var track)) mapped[i] = track;

            return mapped;

        }

        var primaryBones = _primary.Mesh.Bones;

        var byName = new Dictionary<string, BoneTrack>(StringComparer.OrdinalIgnoreCase);

        foreach (var (boneIndex, track) in _clip.Tracks)

            if ((uint)boneIndex < (uint)primaryBones.Length)

                byName[primaryBones[boneIndex].Name] = track;

        var result = new Dictionary<int, BoneTrack>();

        for (var i = 0; i < model.Mesh.Bones.Length; i++)

            if (byName.TryGetValue(model.Mesh.Bones[i].Name, out var track)) result[i] = track;

        return result;

    }



    private void FrameCamera()

    {

        RecalculateVisibleBounds();

        _target = (_boundsMin + _boundsMax) * 0.5f;
        CurrentView = ViewPreset.Perspective;

        Projection = ViewportProjection.Perspective;

        _yaw = DefaultYaw;

        _pitch = DefaultPitch;

        BuildGridLines();

        FitCamera();

    }



    private void RecalculateVisibleBounds()

    {

        var min = new Vector3(float.MaxValue);

        var max = new Vector3(float.MinValue);

        var found = false;

        void IncludeModel(GlModel model)

        {

            var mesh = model.Mesh;

            for (var f = 0; f < mesh.FaceCount; f++)

            {

                if (!IsFaceVisible(model, f)) continue;

                var (a, b, c) = mesh.Faces[f];

                foreach (var index in new[] { a, b, c })

                {

                    if ((uint)index >= (uint)mesh.Vertices.Length) continue;

                    var positions = model.Posed.Length == mesh.Vertices.Length ? model.Posed : mesh.Vertices;

                    var v = Vector3.Transform(positions[index], ReToUeWorld);

                    min = Vector3.Min(min, v);

                    max = Vector3.Max(max, v);

                    found = true;

                }

            }

        }

        if (_primary != null) IncludeModel(_primary);

        foreach (var extra in _extras) IncludeModel(extra);

        if (!found) { min = new Vector3(-0.5f); max = new Vector3(0.5f); }

        _boundsMin = min;

        _boundsMax = max;

        BuildGridLines();

    }



    private bool TryGetPrimaryGroupBounds(int key, out Vector3 min, out Vector3 max)

    {

        min = new Vector3(float.MaxValue);

        max = new Vector3(float.MinValue);

        if (_primary == null) return false;

        var mesh = _primary.Mesh;

        var positions = _primary.Posed.Length == mesh.Vertices.Length ? _primary.Posed : mesh.Vertices;

        var found = false;

        for (var f = 0; f < mesh.FaceCount; f++)

        {

            if (f >= mesh.FaceGroups.Length || mesh.FaceGroups[f] != key) continue;

            var (a, b, c) = mesh.Faces[f];

            foreach (var index in new[] { a, b, c })

            {

                if ((uint)index >= (uint)positions.Length) continue;

                var v = Vector3.Transform(positions[index], ReToUeWorld);

                min = Vector3.Min(min, v);

                max = Vector3.Max(max, v);

                found = true;

            }

        }

        return found;

    }



    private void UpdateStatusInfo()

    {

        if (_primary == null) { StatusInfo = "无模型"; return; }

        var mesh = _primary.Mesh;

        var visibleFaces = 0;

        for (var f = 0; f < mesh.FaceCount; f++) if (IsFaceVisible(_primary, f)) visibleFaces++;

        var groupInfo = mesh.Groups.Length > 0

            ? $" | 组 {_primary.VisibleGroups.Count}/{mesh.Groups.Length}"

            : "";

        StatusInfo = $"顶点 {mesh.VertexCount:N0} | 面 {visibleFaces:N0} | 骨骼 {mesh.Bones.Length} | 贴图 {mesh.Textures.Length}{groupInfo}";

    }



    private void FitCamera()

        => FitCamera(_boundsMin, _boundsMax);



    private void FitCamera(Vector3 boundsMin, Vector3 boundsMax)

    {

        var size = boundsMax - boundsMin;

        var radius = MathF.Max(0.05f, size.Length() * 0.5f);

        var aspect = Bounds.Height > 1 ? (float)(Bounds.Width / Bounds.Height) : 1.6f;

        var halfFov = MathF.PI / 8f;

        // Bounding-sphere fit is stable for any orbit angle and never clips tall or wide assets.

        var verticalFit = radius / MathF.Tan(halfFov);

        var horizontalFit = radius / MathF.Tan(MathF.Atan(MathF.Tan(halfFov) * MathF.Max(0.2f, aspect)));

        _dist = MathF.Max(0.1f, MathF.Max(verticalFit, horizontalFit) * 1.02f);

    }



    // ---------- OpenGL lifecycle ----------



    protected override void OnOpenGlInit(GlInterface gl)

    {

        base.OnOpenGlInit(gl);

        try

        {

            InitGl(gl);

        }

        catch (Exception ex)

        {

            StatusInfo = "图形视口初始化失败：" + ex.Message;

            RenderDiagnostics = StatusInfo;

            _gl = null;

            StateChanged?.Invoke();

        }

    }



    private void InitGl(GlInterface gl)

    {

        _gl = GL.GetApi(gl.GetProcAddress);



        _meshProgram = CreateProgram(MeshVert, MeshFrag);

        _uMvp = _gl.GetUniformLocation(_meshProgram, "uMVP");

        _uLight = _gl.GetUniformLocation(_meshProgram, "uLightDir");

        _uViewDir = _gl.GetUniformLocation(_meshProgram, "uViewDir");

        _uHasTex = _gl.GetUniformLocation(_meshProgram, "uHasTex");

        _uTex = _gl.GetUniformLocation(_meshProgram, "uTex");

        _uRenderMode = _gl.GetUniformLocation(_meshProgram, "uRenderMode");

        _uAlphaCutout = _gl.GetUniformLocation(_meshProgram, "uAlphaCutout");



        _lineProgram = CreateProgram(LineVert, LineFrag);

        _lMvp = _gl.GetUniformLocation(_lineProgram, "uMVP");

        _lColor = _gl.GetUniformLocation(_lineProgram, "uColor");



        _lineVao = _gl.GenVertexArray();

        _lineVbo = _gl.GenBuffer();



        _gl.Enable(EnableCap.DepthTest);

        _gl.Disable(EnableCap.CullFace); // double-sided, like the game



        BuildGridLines();



        foreach (var (model, indices, edges) in TakePendingUploads())

        {

            UploadAllTextures(model);

            UploadGeometry(model, indices, edges);

        }

    }



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

        while (g.GetError() != GLEnum.NoError) { }



        // Process any geometry/texture uploads queued from the UI thread (SetMesh calls).

        // This must run on the render thread where the GL context is current.

        foreach (var (model, indices, edges) in TakePendingUploads())
        {
            UploadAllTextures(model);
            UploadGeometry(model, indices, edges);
        }

        foreach (var (model, data) in TakePendingIndexUpdates())
            UploadIndexData(model, data.indices, data.edges);

        foreach (var (model, edges) in TakePendingSelectionUpdates())
            UploadSelectionData(model, edges);



        var w = (int)Bounds.Width;

        var h = (int)Bounds.Height;

        var scale = (float)(VisualRoot?.RenderScaling ?? 1.0);

        var pw = (int)(w * scale);

        var ph = (int)(h * scale);

        if (pw < 1 || ph < 1) return;

        g.Viewport(0, 0, (uint)pw, (uint)ph);

        g.ClearColor(0.105f, 0.115f, 0.13f, 1f);

        g.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));



        var aspect = h > 0 ? (float)w / h : 1f;

        var mvp = ComputeMvp(aspect);



        GlModel? primary;
        GlModel[] extras;
        lock (_sceneLock)
        {
            primary = _primary;
            extras = _extras.ToArray();
        }

        if (primary != null)
        {
            if (_playing || _clip != null) UploadPose(primary);
            DrawModel(g, primary, ReToUeWorld * mvp);
        }

        var modelError = g.GetError();

        foreach (var extra in extras)

        {

            if (_playing || _clip != null) UploadPose(extra);

            DrawModel(g, extra, ReToUeWorld * mvp);

        }



        DrawLines(g, mvp);

        var lineError = g.GetError();
        RenderDiagnostics = primary == null
            ? $"GL P={_lineProgram} VAO={_lineVao} VBO={_lineVbo} 模型=无 网格={_lineErrorStage}/{lineError}"
            : $"GL VAO={primary.Vao} 索引={primary.IndexCount:N0} 批次={primary.Batches.Length} 模型={modelError} 网格={_lineErrorStage}/{lineError}";



        if (_playing) RequestNextFrameRendering();

    }



    private Matrix4x4 ComputeMvp(float aspect)

    {

        var eye = GetEye();

        var view = Matrix4x4.CreateLookAt(eye, _target, GetCameraUp());

        var radius = MathF.Max(0.05f, (_boundsMax - _boundsMin).Length() * 0.5f);

        var near = MathF.Max(0.001f, _dist - radius * 2.2f);

        var far = MathF.Max(near + 1f, _dist + radius * 3f);

        Matrix4x4 proj;

        if (Projection == ViewportProjection.Orthographic)

        {

            var halfH = MathF.Max(0.05f, _dist * MathF.Tan(MathF.PI / 8f));

            proj = Matrix4x4.CreateOrthographic(halfH * 2f * MathF.Max(0.1f, aspect), halfH * 2f, near, far);

        }

        else

        {

            proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, MathF.Max(0.1f, aspect), near, far);

        }

        return view * proj;

    }



    private static int GetAlphaCutoutMode(ViewportTexture texture)
    {
        // Hair-card alpha is a coverage mask with a large population of mid-alpha
        // texels. A 0.25 threshold turns the beard into a grey dotted stencil at
        // distance; keep coverage down to 0.1 so mip-filtered strands remain visible.
        if (texture.Name.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
            texture.Name.Contains("eyebrow", StringComparison.OrdinalIgnoreCase) ||
            texture.Name.Contains("eyelash", StringComparison.OrdinalIgnoreCase) ||
            texture.Name.Contains("eyeduct", StringComparison.OrdinalIgnoreCase) ||
            texture.Name.Contains("eyeshadow", StringComparison.OrdinalIgnoreCase) ||
            texture.Name.Contains("beard", StringComparison.OrdinalIgnoreCase)) return 3;
        return 1;
    }

    private void DrawModel(GL g, GlModel model, Matrix4x4 mvp)

    {

        if (model.Vao == 0) return;

        var eye = GetEye();

        var viewDir = Vector3.Normalize(_target - eye);

        g.UseProgram(_meshProgram);

        // Matrix4x4 is stored row-major and System.Numerics composes row vectors (v * M).

        // OpenGL reads the same bytes as a column-major matrix, which already supplies M^T

        // for the shader's column-vector expression (M * v). Therefore transpose MUST be false.

        // OpenGL ES also requires transpose=false; true raises GL_INVALID_VALUE on ANGLE and

        // leaves the previous/identity uniform active, producing the tilted, clipped "broken camera".

        unsafe { g.UniformMatrix4(_uMvp, 1, false, (float*)&mvp); }

        g.Uniform3(_uLight, LightDir.X, LightDir.Y, LightDir.Z);

        g.Uniform3(_uViewDir, viewDir.X, viewDir.Y, viewDir.Z);

        g.Uniform1(_uTex, 0);

        var shaderMode = RenderMode switch

        {

            ViewportRenderMode.Textured => 1,

            ViewportRenderMode.Solid or ViewportRenderMode.Wireframe => 2,

            _ => 0

        };

        g.Uniform1(_uRenderMode, shaderMode);

        g.BindVertexArray(model.Vao);

        if (RenderMode != ViewportRenderMode.Wireframe)

        {

            g.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.Ebo);

            for (var i = 0; i < model.Batches.Length; i++)

            {

                var (slot, alphaCutout, start, count) = model.Batches[i];

                var tex = slot >= 0 && slot < model.TextureHandles.Length ? model.TextureHandles[slot] : 0u;

                g.Uniform1(_uHasTex, tex != 0 ? 1 : 0);

                var alphaMode = alphaCutout && tex != 0
                    ? GetAlphaCutoutMode(model.Mesh.Textures[slot])
                    : 0;
                g.Uniform1(_uAlphaCutout, alphaMode);

                if (tex != 0)

                {

                    g.ActiveTexture(TextureUnit.Texture0);

                    g.BindTexture(TextureTarget.Texture2D, tex);

                }

                unsafe { g.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedInt, (void*)(start * 4)); }

            }

        }

        g.BindVertexArray(0);



        if (RenderMode is ViewportRenderMode.Wireframe or ViewportRenderMode.TexturedEdges)

            DrawModelEdges(g, model, mvp);

        if (model.IsPrimary && model.SelectionEdgeIndexCount > 0)

            DrawSelectionEdges(g, model, mvp);

    }



    private Vector3 GetEye() => _target + new Vector3(

        MathF.Cos(_pitch) * MathF.Sin(_yaw),

        MathF.Cos(_pitch) * MathF.Cos(_yaw),

        MathF.Sin(_pitch)) * _dist;



    private Vector3 GetCameraUp() => MathF.Abs(MathF.Cos(_pitch)) < 0.01f

        ? (_pitch > 0 ? -Vector3.UnitY : Vector3.UnitY)

        : Vector3.UnitZ;



    /// <summary>Projects the fixed world X/Y/Z directions into viewport screen space.</summary>

    public Vector2[] GetWorldAxisScreenDirections()

    {

        var view = Matrix4x4.CreateLookAt(GetEye(), _target, GetCameraUp());

        return new[] { -Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }.Select(axis =>

        {

            var camera = Vector3.TransformNormal(axis, view);

            var screen = new Vector2(camera.X, -camera.Y);

            return screen.LengthSquared() > 1e-6f ? Vector2.Normalize(screen) : Vector2.Zero;

        }).ToArray();

    }



    private void DrawModelEdges(GL g, GlModel model, Matrix4x4 mvp)

    {

        if (model.EdgeEbo == 0 || model.EdgeIndexCount == 0) return;

        g.UseProgram(_lineProgram);

        unsafe { g.UniformMatrix4(_lMvp, 1, false, (float*)&mvp); }

        g.Uniform4(_lColor, 0.06f, 0.08f, 0.1f, 1f);

        g.BindVertexArray(model.Vao);

        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.EdgeEbo);

        unsafe { g.DrawElements(PrimitiveType.Lines, (uint)model.EdgeIndexCount, DrawElementsType.UnsignedInt, null); }

        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.Ebo);

        g.BindVertexArray(0);

    }



    private void DrawSelectionEdges(GL g, GlModel model, Matrix4x4 mvp)

    {

        g.UseProgram(_lineProgram);

        unsafe { g.UniformMatrix4(_lMvp, 1, false, (float*)&mvp); }

        g.Uniform4(_lColor, 1f, 0.55f, 0.08f, 1f);

        g.BindVertexArray(model.Vao);

        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.SelectionEbo);

        g.DepthFunc(DepthFunction.Lequal);

        g.LineWidth(2f);

        unsafe { g.DrawElements(PrimitiveType.Lines, (uint)model.SelectionEdgeIndexCount, DrawElementsType.UnsignedInt, null); }

        g.LineWidth(1f);

        g.DepthFunc(DepthFunction.Less);

        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.Ebo);

        g.BindVertexArray(0);

    }



    // ---------- lines: grid + skeleton ----------



    private void BuildGridLines()

    {

        lock (_lineVertsLock)
        {
        _lineVerts.Clear();

        var modelSize = MathF.Max(0.1f, MathF.Max(_boundsMax.X - _boundsMin.X, _boundsMax.Y - _boundsMin.Y));

        var exponent = MathF.Floor(MathF.Log10(modelSize));

        var step = MathF.Pow(10f, exponent - 1f);

        if (modelSize / step > 30f) step *= 2f;

        var extent = MathF.Ceiling(modelSize * 1.5f / (step * 10f)) * step * 10f;

        extent = MathF.Max(extent, step * 10f);

        var lineIndex = 0;

        for (var v = -extent; v <= extent + step * 0.1f; v += step)

        {

            AddLineVertex(new Vector3(v, -extent, 0)); AddLineVertex(new Vector3(v, extent, 0));

            AddLineVertex(new Vector3(-extent, v, 0)); AddLineVertex(new Vector3(extent, v, 0));

            lineIndex++;

        }

        _gridLineCount = _lineVerts.Count / 3;

        _gridMinorCount = _gridLineCount;



        // UE-facing origin triad. In this tool's target convention the character/world
        // forward axis is Y, so draw the forward ground axis as green Y and the side
        // ground axis as red X.

        AddLineVertex(Vector3.Zero); AddLineVertex(new Vector3(0, extent * 0.35f, 0));

        AddLineVertex(Vector3.Zero); AddLineVertex(new Vector3(-extent * 0.35f, 0, 0));

        AddLineVertex(Vector3.Zero); AddLineVertex(new Vector3(0, 0, extent * 0.35f));

        }
    }



    private void AddLineVertex(Vector3 v)

    {

        _lineVerts.Add(v.X); _lineVerts.Add(v.Y); _lineVerts.Add(v.Z);

    }



    private void DrawLines(GL g, Matrix4x4 mvp)

    {

        lock (_lineVertsLock)
        {
        _lineErrorStage = "NoError";
        var persistentCount = _gridLineCount + 6;
        var existingCount = _lineVerts.Count / 3;
        if (existingCount < persistentCount)
        {
            BuildGridLines();
            persistentCount = _gridLineCount + 6;
            existingCount = _lineVerts.Count / 3;
            if (existingCount < persistentCount) return;
        }

        var total = persistentCount;
        var skeletonStart = persistentCount;
        var skeletonCount = 0;
        var selectedBoneStart = persistentCount;
        var selectedBoneCount = 0;



        if (ShowSkeleton && _primary != null && _primary.Mesh.Bones.Length > 0)

        {

            var bones = _primary.Mesh.Bones;

            var globals = _primary.BoneGlobals;

            var selectedSegments = new List<(Vector3 A, Vector3 B)>();

            for (var bi = 0; bi < bones.Length; bi++)

            {

                var parent = bones[bi].ParentIndex;

                if (parent < 0 || parent >= globals.Length) continue;

                var a = Vector3.Transform(globals[parent].Translation, ReToUeWorld);

                var b = Vector3.Transform(globals[bi].Translation, ReToUeWorld);

                if (_selectedPrimaryBone.HasValue &&
                    (bi == _selectedPrimaryBone.Value || parent == _selectedPrimaryBone.Value))
                    selectedSegments.Add((a, b));
                else
                {
                    AddLineVertex(a);
                    AddLineVertex(b);
                }

            }

            skeletonCount = _lineVerts.Count / 3 - skeletonStart;
            selectedBoneStart = _lineVerts.Count / 3;

            if (_selectedPrimaryBone == -1)
            {
                foreach (var rootIndex in Enumerable.Range(0, bones.Length)
                             .Where(i => bones[i].ParentIndex < 0 || bones[i].ParentIndex >= bones.Length))
                    selectedSegments.Add((Vector3.Zero,
                        Vector3.Transform(globals[rootIndex].Translation, ReToUeWorld)));
            }

            foreach (var (a, b) in selectedSegments)
            {
                AddLineVertex(a);
                AddLineVertex(b);
            }

            if (_selectedPrimaryBone.HasValue)
            {
                var selected = _selectedPrimaryBone.Value;
                var point = selected == -1
                    ? Vector3.Zero
                    : selected >= 0 && selected < globals.Length
                        ? Vector3.Transform(globals[selected].Translation, ReToUeWorld)
                        : Vector3.Zero;
                var markerSize = MathF.Max(0.005f, (_boundsMax - _boundsMin).Length() * 0.012f);
                AddLineVertex(point - Vector3.UnitX * markerSize); AddLineVertex(point + Vector3.UnitX * markerSize);
                AddLineVertex(point - Vector3.UnitY * markerSize); AddLineVertex(point + Vector3.UnitY * markerSize);
                AddLineVertex(point - Vector3.UnitZ * markerSize); AddLineVertex(point + Vector3.UnitZ * markerSize);
            }

            selectedBoneCount = _lineVerts.Count / 3 - selectedBoneStart;
            total = _lineVerts.Count / 3;

        }



        if (total == 0) return;



        g.UseProgram(_lineProgram);
        CaptureLineError(g, "UseProgram");

        unsafe { g.UniformMatrix4(_lMvp, 1, false, (float*)&mvp); }
        CaptureLineError(g, "UniformMatrix4");

        g.BindVertexArray(_lineVao);
        CaptureLineError(g, "BindVertexArray");

        g.BindBuffer(BufferTargetARB.ArrayBuffer, _lineVbo);
        CaptureLineError(g, "BindBuffer");

        var floats = _lineVerts.ToArray();

        unsafe

        {

            fixed (float* p = floats)

                g.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(floats.Length * 4), p, BufferUsageARB.DynamicDraw);

        }
        CaptureLineError(g, "BufferData");

        g.EnableVertexAttribArray(0);
        CaptureLineError(g, "EnableVertexAttribArray");

        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
        CaptureLineError(g, "VertexAttribPointer");



        if (ShowGrid)

        {

            g.Uniform4(_lColor, 0.25f, 0.28f, 0.32f, 1f);

            g.DrawArrays(PrimitiveType.Lines, 0, (uint)_gridMinorCount);
            CaptureLineError(g, "DrawGrid");

        }

        if (ShowAxes)

        {
            g.LineWidth(3f);

            g.Uniform4(_lColor, 0.2f, 0.78f, 0.3f, 1f);  // Y green = forward

            g.DrawArrays(PrimitiveType.Lines, _gridLineCount, 2);

            g.Uniform4(_lColor, 0.9f, 0.18f, 0.18f, 1f); // X red = side

            g.DrawArrays(PrimitiveType.Lines, _gridLineCount + 2, 2);

            g.Uniform4(_lColor, 0.25f, 0.48f, 1f, 1f);   // Z blue

            g.DrawArrays(PrimitiveType.Lines, _gridLineCount + 4, 2);
            g.LineWidth(1f);
            CaptureLineError(g, "DrawAxes");

        }

        if (skeletonCount > 0)

        {

            g.Uniform4(_lColor, 0.25f, 0.65f, 1f, 1f);

            g.DrawArrays(PrimitiveType.Lines, skeletonStart, (uint)skeletonCount);
            CaptureLineError(g, "DrawSkeleton");

        }

        if (selectedBoneCount > 0)
        {
            g.LineWidth(4f);
            g.Uniform4(_lColor, 1f, 0.72f, 0.12f, 1f);
            g.DrawArrays(PrimitiveType.Lines, selectedBoneStart, (uint)selectedBoneCount);
            g.LineWidth(1f);
            CaptureLineError(g, "DrawSelectedBone");
        }

        g.BindVertexArray(0);



        // reset skeleton verts for next frame (grid is persistent)

        var keepFloats = persistentCount * 3;
        if (_lineVerts.Count > keepFloats)
            _lineVerts.RemoveRange(keepFloats, _lineVerts.Count - keepFloats);

    }
        }

    private void CaptureLineError(GL g, string stage)
    {
        var error = g.GetError();
        if (error != GLEnum.NoError && _lineErrorStage == "NoError")
            _lineErrorStage = $"{stage}:{error}";
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

        uniform vec3 uViewDir;

        uniform sampler2D uTex;

        uniform int uHasTex;

        uniform int uRenderMode;

        uniform int uAlphaCutout;

        out vec4 FragColor;

        void main() {

            vec3 n = normalize(vNormal);

            vec4 texel = (uHasTex == 1 && uRenderMode != 2) ? texture(uTex, vUV) : vec4(0.62, 0.66, 0.72, 1.0);

            if (uAlphaCutout > 0 && uRenderMode != 2 &&
                texel.a < (uAlphaCutout == 3 ? 0.1 : uAlphaCutout == 2 ? 0.25 : 0.5)) discard;

            vec3 baseSrgb = texel.rgb;

            if (uRenderMode == 1) { FragColor = vec4(baseSrgb, 1.0); return; }

            // ALBD textures are authored in sRGB. Decode before lighting and encode once

            // afterwards; treating encoded values as linear and then applying gamma again

            // washes dark cloth into the pale pink/white seen in the broken preview.

            vec3 baseLinear = pow(max(baseSrgb, vec3(0.0)), vec3(2.2));

            float key = max(0.0, dot(n, -uLightDir));

            float fill = max(0.0, dot(n, normalize(vec3(0.55, 0.15, -0.8))));

            float rim = pow(1.0 - abs(dot(n, normalize(-uViewDir))), 3.0);

            float light = 0.30 + key * 0.58 + fill * 0.12;

            vec3 colorLinear = baseLinear * light + baseLinear * rim * 0.08;

            vec3 colorSrgb = pow(clamp(colorLinear, 0.0, 1.0), vec3(1.0 / 2.2));

            FragColor = vec4(colorSrgb, 1.0);

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

        g.ShaderSource(shader, src.TrimStart());

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

        InvalidateGpuResourcesAndQueueScene();

        _meshProgram = 0;
        _lineProgram = 0;
        _lineVao = 0;
        _lineVbo = 0;

        _gl = null;

        base.OnOpenGlDeinit(gl);

    }



    // ---------- input (same orbit/pan/zoom) ----------



    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)

    {

        base.OnPointerPressed(e);

        Focus(); // ensure this control has keyboard/pointer focus for hit-testing

        var p = e.GetCurrentPoint(this);

        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        BeginCameraDrag(p.Position,

            p.Properties.IsLeftButtonPressed || (alt && p.Properties.IsMiddleButtonPressed),

            p.Properties.IsRightButtonPressed || (p.Properties.IsMiddleButtonPressed && !alt));

        e.Pointer.Capture(this);

        e.Handled = true;

    }



    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)

    {

        base.OnPointerMoved(e);

        UpdateCameraDrag(e.GetPosition(this));

        e.Handled = true;

    }



    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)

    {

        base.OnPointerReleased(e);

        EndCameraDrag();

        e.Pointer.Capture(null);

        e.Handled = true;

    }



    protected override void OnPointerWheelChanged(Avalonia.Input.PointerWheelEventArgs e)

    {

        base.OnPointerWheelChanged(e);

        ZoomCamera(e.Delta.Y);

        e.Handled = true;

    }



    protected override void OnKeyDown(KeyEventArgs e)

    {

        base.OnKeyDown(e);

        e.Handled = HandleCameraKey(e.Key);

    }

}
