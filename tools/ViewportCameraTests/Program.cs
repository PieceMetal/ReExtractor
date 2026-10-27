using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using ReExtractor.Core;
using ReExtractor.Gui;

AppBuilder.Configure<Application>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var viewport = new GlViewport();
viewport.Measure(new Size(800, 600));
viewport.Arrange(new Rect(0, 0, 800, 600));

var mesh = MakeCube();
var clip = new AnimationClip
{
    Name = "root movement between range samples",
    Duration = 2,
    FrameRate = 60,
    FrameCount = 120,
    Tracks = new Dictionary<int, BoneTrack>
    {
        [0] = new BoneTrack
        {
            TransTimes = [0, 0.125f, 0.25f, 2],
            Translations = [Vector3.Zero, new Vector3(30, 0, 0), Vector3.Zero, Vector3.Zero],
        },
    },
};
viewport.SetMesh(mesh);
viewport.SetAnimation(clip);
viewport.PausePlayback();
CheckPose(viewport, 0);
viewport.ScrubTo(0.125f); // misses the initial five range samples
CheckPose(viewport, 0.125f);
viewport.FrameAll();
CheckPose(viewport, 0);
CheckPose(viewport, 0.125f);
viewport.Arrange(new Rect(0, 0, 400, 800));
CheckPose(viewport, 0, 0.5f);
CheckPose(viewport, 0.125f, 0.5f);
viewport.SetView(GlViewport.ViewPreset.Front);
CheckPose(viewport, 0, 0.5f);
CheckPose(viewport, 0.125f, 0.5f);
viewport.SetView(GlViewport.ViewPreset.Perspective);
viewport.Arrange(new Rect(0, 0, 800, 600));
var distanceField = typeof(GlViewport).GetField("_dist", BindingFlags.Instance | BindingFlags.NonPublic)!;
viewport.ZoomCamera(1);
var manualDistance = (float)distanceField.GetValue(viewport)!;
viewport.ScrubTo(0.125f);
if (MathF.Abs((float)distanceField.GetValue(viewport)! - manualDistance) > 1e-6f)
    throw new Exception("Playback overrode manual camera zoom");
Console.WriteLine("SYNTHETIC_CAMERA_PASS");

if (args.Length > 0)
{
    var game = args[0];
    var pak = new PakService();
    pak.AddPaksFromGameDir(game);
    pak.LoadListFile(Path.Combine(game, "OWOTS_STM_Release.list"));
    pak.EnumerateFiles();
    const string meshPath = "natives/stm/art/model/character/ch1/ch102/00/00/ch102_00_00.mesh.260209350";
    const string motionPath = "natives/stm/motion/enemy/em112/00/01/em112_00_01_attack/em112_00_01_attack.motlist.1036";
    using var meshStream = pak.ReadFile(meshPath);
    var realMesh = ViewportDataLoader.LoadMesh(meshStream, meshPath, 0, null, false);
    using var motionStream = pak.ReadFile(motionPath);
    var motions = ViewportDataLoader.ListMotions(motionStream, motionPath, pak.ReadFile);
    var chosen = motions.Single(m => m.MotionNumber == 59);
    using var clipStream = pak.ReadFile(motionPath);
    var realClip = ViewportDataLoader.LoadAnimation(clipStream, motionPath, chosen.SourceIndex,
        realMesh.Bones.Select(b => b.Name).ToArray(), [realMesh], pak.ReadFile);
    viewport.SetMesh(realMesh);
    var clock = Stopwatch.StartNew();
    viewport.SetAnimation(realClip);
    clock.Stop();
    viewport.PausePlayback();
    var initialTime = clock.ElapsedMilliseconds;
    for (var frame = 0; frame <= 60; frame++)
    {
        var t = realClip.Duration * frame / 60f;
        viewport.ScrubTo(t);
        CheckPose(viewport, t);
    }
    Console.WriteLine($"EM112_CAMERA_PASS vertices={realMesh.VertexCount} faces={realMesh.FaceCount} duration={realClip.Duration:F2}s setup={initialTime}ms");
}

static void CheckPose(GlViewport viewport, float time, float aspect = 800f / 600f)
{
    viewport.ScrubTo(time);
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    var mvp = (Matrix4x4)typeof(GlViewport).GetMethod("ComputeMvp", flags)!.Invoke(viewport, [aspect])!;
    var model = typeof(GlViewport).GetField("_primary", flags)!.GetValue(viewport)!;
    var posed = (Vector3[])model.GetType().GetField("Posed")!.GetValue(model)!;
    var mesh = (ViewportMesh)model.GetType().GetField("Mesh")!.GetValue(model)!;
    var minDepth = float.MaxValue;
    var maxDepth = float.MinValue;
    var maximumScreen = 0f;
    foreach (var (a, b, c) in mesh.Faces)
    foreach (var index in new[] { a, b, c })
    {
        var v = posed[index];
        var world = new Vector3(-v.X, v.Z, v.Y);
        var projected = Vector4.Transform(new Vector4(world, 1), mvp);
        var x = projected.X / projected.W;
        var y = projected.Y / projected.W;
        var z = projected.Z / projected.W;
        maximumScreen = MathF.Max(maximumScreen, MathF.Max(MathF.Abs(x), MathF.Abs(y)));
        minDepth = MathF.Min(minDepth, z);
        maxDepth = MathF.Max(maxDepth, z);
        if (!(projected.W > 0 && MathF.Abs(x) <= 1.001f && MathF.Abs(y) <= 1.001f && z >= -0.001f && z <= 1.001f))
            throw new Exception($"Clipped at t={time:F3}: screen=({x:F3},{y:F3}) depth={z:F3} world={world}");
    }
    if (time is 0 or 0.125f) Console.WriteLine($"POSE t={time:F3} maxScreen={maximumScreen:F3} depth={minDepth:F3}..{maxDepth:F3}");
}

static ViewportMesh MakeCube()
{
    var vertices = new[]
    {
        new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
        new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1),
    };
    var faces = new (int, int, int)[]
    {
        (0, 1, 2), (0, 2, 3), (4, 5, 6), (4, 6, 7),
        (0, 1, 5), (0, 5, 4), (1, 2, 6), (1, 6, 5),
        (2, 3, 7), (2, 7, 6), (3, 0, 4), (3, 4, 7),
    };
    return new ViewportMesh
    {
        Vertices = vertices,
        Normals = vertices.Select(Vector3.Normalize).ToArray(),
        Uvs = vertices.Select(_ => Vector2.Zero).ToArray(),
        Faces = faces,
        FaceTexture = faces.Select(_ => -1).ToArray(),
        Textures = [],
        Weights = vertices.Select(_ => new[] { (0, 1f) }).ToArray(),
        Bones = [new ViewportBone
        {
            Name = "root", ParentIndex = -1,
            LocalBind = Matrix4x4.Identity,
            InverseGlobalBind = Matrix4x4.Identity,
        }],
        DeformToBone = [0],
    };
}
