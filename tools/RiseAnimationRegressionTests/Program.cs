using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ReExtractor.Core;
using ReExtractor.Gui;
using ReeLib;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
var gameDirectory = Option("--game-dir");
var listFile = Option("--list");
if (gameDirectory == null || listFile == null)
{
    Console.Error.WriteLine("usage: RiseAnimationRegressionTests --game-dir <Rise directory> --list <Rise list> [--out <directory>] [--unity] [--baseline]");
    return 2;
}
var output = Path.GetFullPath(Option("--out") ?? "artifacts/rise-animation-regression");
var unity = args.Contains("--unity");
ViewportExportService Exporter() => unity ? (ViewportExportService)Activator.CreateInstance(typeof(ViewportExportService), [true])! : new ViewportExportService();
Directory.CreateDirectory(output);
var pak = new PakService();
pak.AddPaksFromGameDir(gameDirectory);
pak.LoadListFile(listFile);
Stream? Open(string path) { try { return pak.ReadFile(path); } catch (FileNotFoundException) { return null; } }
ViewportMesh Load(string path) { using var s = pak.ReadFile(path); return ViewportDataLoader.LoadMesh(s, path, 0, Open, false); }
const string motionPath = "natives/stm/player/mot/plw_dualblades_100.motlist.528";
using var rawStream = pak.ReadFile(motionPath);
using var rawList = new MotlistFile(new FileHandler(rawStream, motionPath));
rawList.Read();
var motionIndex = rawList.Motions.FindIndex(m => m.MotFile?.Name == "plw_DualBlades_100_001_Loop");
if (motionIndex < 0) throw new Exception("Feedback motion not found");
var mot = (MotFile)rawList.Motions[motionIndex].MotFile!;
Console.WriteLine($"motion={motionIndex} {mot.Name} version={mot.Header.version} bones={mot.Bones.Count}");
var results = new List<object>();
var failures = new List<string>();
var strict = !args.Contains("--baseline");
foreach (var gender in new[] { "m", "f" })
{
    var labels = new[] { "body", "arm", "helm", "leg", "wst" };
    var parts = labels.Select(part => Load($"natives/stm/player/mod/{gender}/pl004/{gender}_{part}004.mesh.2109148288")).ToArray();
    var reference = Load($"natives/stm/player/mod/{gender}/bone/{gender}_shadow.mesh.2109148288");
    var original = parts.Select(mesh => mesh.Bones.Select(b => new { b.Name, parent = b.ParentIndex < 0 ? null : mesh.Bones[b.ParentIndex].Name,
        local = b.LocalBind.Translation, bind = Matrix4x4.Invert(b.InverseGlobalBind, out var global) ? global.Translation : Vector3.Zero }).ToArray()).ToArray();
    var inverseBinds = parts.Select(p => p.Bones.Select(b => b.InverseGlobalBind).ToArray()).ToArray();
    using var referenceStream = pak.ReadFile(motionPath);
    var referenceClip = ViewportDataLoader.LoadAnimation(referenceStream, motionPath, motionIndex, sceneMeshes: [reference]);
    using var motionStream = pak.ReadFile(motionPath);
    var clip = ViewportDataLoader.LoadAnimation(motionStream, motionPath, motionIndex, sceneMeshes: parts);
    var frames = new[] { 0, 50, 92, 100, 150, 200 };
    var samples = new List<object>();
    foreach (var frame in frames)
    {
        var poses = Evaluate(parts, clip, frame / 60f);
        var referencePose = Evaluate([reference], referenceClip, frame / 60f)[0];
        var errors = parts.Select((mesh, i) => mesh.Bones.Select((b, j) => (b, j))
            .Where(x => !x.b.IsMotionHelper && clip.NamedTracks.ContainsKey(x.b.Name))
            .Select(x => new { x.b.Name, error = reference.Bones.Select((b, j) => (b, j)).Where(r => r.b.Name == x.b.Name)
                .Select(r => Vector3.Distance(poses[i].Globals[x.j].Translation, referencePose.Globals[r.j].Translation)).DefaultIfEmpty().Max() })
            .OrderByDescending(x => x.error).Take(6).ToArray()).ToArray();
        Console.WriteLine($"{gender} frame={frame} maxNamedBoneError={errors.SelectMany(x => x).Max(x => x.error):F6}");
        if (errors.SelectMany(x => x).Max(x => x.error) > 0.0001f) failures.Add($"{gender} frame {frame} diverged from full player skeleton");
        samples.Add(new { frame, errors });
        if (frame == 92)
        {
            Exporter().ConvertToGlb(Static(parts, poses.Select(p => p.Vertices).ToArray()), new HashSet<int> { 0 }, Path.Combine(output, $"{gender}-frame92.glb"));
            File.WriteAllText(Path.Combine(output, $"{gender}-frame92.json"), JsonSerializer.Serialize(parts.Select((p, i) => new {
                label = labels[i], vertices = poses[i].Vertices.Select(v => new[] { v.X, v.Y, v.Z }),
                faces = p.Faces.Where((f, j) => p.FaceGroups.Length == 0 || p.Groups.Any(g => g.Key == p.FaceGroups[j] && g.DefaultVisible))
                    .Select(f => new[] { f.A, f.B, f.C }) }), new JsonSerializerOptions { WriteIndented = false }));
        }
    }
    for (var i = 0; i < parts.Length; i++)
        if (!inverseBinds[i].SequenceEqual(parts[i].Bones.Take(inverseBinds[i].Length).Select(b => b.InverseGlobalBind)))
            failures.Add($"{gender}/{labels[i]} source inverse bind was modified");
    var baselinePose = Evaluate(parts, clip, 92 / 60f);
    foreach (var order in new[] { new[] { 4, 3, 2, 1, 0 }, new[] { 2, 0, 1, 4, 3 }, new[] { 1, 2, 3, 0, 4 } })
    {
        var reordered = Evaluate(order.Select(i => parts[i]).ToArray(), clip, 92 / 60f);
        for (var i = 0; i < order.Length; i++)
            if (reordered[i].Vertices.Zip(baselinePose[order[i]].Vertices, Vector3.Distance).Max() > 0.00001f)
                failures.Add($"{gender} load order changed preview");
    }
    for (var i = 0; i < parts.Length; i++)
    {
        var single = Load($"natives/stm/player/mod/{gender}/pl004/{gender}_{labels[i]}004.mesh.2109148288");
        using var singleStream = pak.ReadFile(motionPath);
        var singleClip = ViewportDataLoader.LoadAnimation(singleStream, motionPath, motionIndex, sceneMeshes: [single]);
        var solo = Evaluate([single], singleClip, 92 / 60f)[0];
        if (solo.Vertices.Zip(baselinePose[i].Vertices, Vector3.Distance).Max() > 0.0001f)
            failures.Add($"{gender}/{labels[i]} standalone diverged from assembled preview");
    }
    var inspection = parts.Select((mesh, i) => new { label = labels[i], mesh.VertexCount, deform = mesh.DeformToBone.Length, original = original[i],
        motion = mot.Bones.Where(b => original[i].Any(o => o.Name == b.boneName)).Select(b => new { b.boneName, parent = b.Parent?.boneName, b.translation, b.quaternion }) }).ToArray();
    results.Add(new { gender, samples, inspection });
    var export = Exporter();
    var exportModels = parts.Select(p => (p, (IReadOnlySet<int>)p.Groups.Where(g => g.DefaultVisible).Select(g => g.Key).ToHashSet())).ToArray();
    export.ConvertMergedToAnimatedGlb(exportModels, clip, Path.Combine(output, $"{gender}-animated.glb"));
    var merged = export.BuildMergedExportModel(exportModels);
    export.ConvertToGlb(merged.Mesh, merged.VisibleGroups, Path.Combine(output, $"{gender}-model.glb"));
    using var exportMotion = pak.ReadFile(motionPath);
    var animationService = unity ? (AnimationService)Activator.CreateInstance(typeof(AnimationService), [true])! : new AnimationService();
    animationService.ConvertOneToGlbWithAnimation(merged.Mesh, exportMotion, motionPath, motionIndex, Path.Combine(output, gender + "-animation"));
    var checkedMotions = 0;
    foreach (var index in Enumerable.Range(0, rawList.Motions.Count).Where(i => rawList.Motions[i].MotFile is MotFile).Take(8))
    {
        using var nextStream = pak.ReadFile(motionPath);
        var next = ViewportDataLoader.LoadAnimation(nextStream, motionPath, index, sceneMeshes: parts);
        var full = Load($"natives/stm/player/mod/{gender}/bone/{gender}_shadow.mesh.2109148288");
        using var fullStream = pak.ReadFile(motionPath);
        var fullClip = ViewportDataLoader.LoadAnimation(fullStream, motionPath, index, sceneMeshes: [full]);
        foreach (var time in new[] { 0f, next.Duration * 0.46f, next.Duration })
        {
            var poses = Evaluate(parts, next, time);
            var expected = Evaluate([full], fullClip, time)[0];
            var indices = full.Bones.Select((b, i) => (b.Name, i)).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().i);
            for (var part = 0; part < parts.Length; part++)
                foreach (var bone in parts[part].DeformToBone.Distinct())
                    if (next.NamedTracks.ContainsKey(parts[part].Bones[bone].Name) && indices.TryGetValue(parts[part].Bones[bone].Name, out var referenceIndex) &&
                        Vector3.Distance(poses[part].Globals[bone].Translation, expected.Globals[referenceIndex].Translation) > 0.0001f)
                        failures.Add($"{gender}/{labels[part]} motion {index} disagreed after switching clips");
        }
        checkedMotions++;
    }
    Console.WriteLine($"{gender} motion-switch checks={checkedMotions}");
}
File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
foreach (var failure in failures.Distinct()) Console.WriteLine("FAIL " + failure);
Console.WriteLine($"RISE_ANIMATION_REGRESSION {(failures.Count == 0 ? "PASS" : "FAIL")} errors={failures.Count} strict={strict}");
return strict && failures.Count > 0 ? 1 : 0;

static (Vector3[] Vertices, Matrix4x4[] Globals)[] Evaluate(ViewportMesh[] meshes, AnimationClip clip, float time)
{
    var type = typeof(GlViewport);
    var modelType = type.GetNestedType("GlModel", BindingFlags.NonPublic)!;
    void Set(object o, string name, object value) => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(o, value);
    object Model(ViewportMesh mesh)
    {
        var model = Activator.CreateInstance(modelType, true)!;
        Set(model, "Mesh", mesh); Set(model, "BoneGlobals", new Matrix4x4[mesh.Bones.Length]);
        Set(model, "JointMats", new Matrix4x4[mesh.DeformToBone.Length]); Set(model, "Posed", new Vector3[mesh.VertexCount]);
        Set(model, "PosedNormals", new Vector3[mesh.VertexCount]);
        Set(model, "Tracks", mesh.Bones.Select((b, i) => (b, i)).Where(x => clip.NamedTracks.ContainsKey(x.b.Name)).ToDictionary(x => x.i, x => clip.NamedTracks[x.b.Name]));
        return model;
    }
    var viewport = RuntimeHelpers.GetUninitializedObject(type);
    var models = meshes.Select(Model).ToArray();
    Set(viewport, "_primary", models[0]); Set(viewport, "_clip", clip);
    var extras = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(modelType))!;
    foreach (var model in models.Skip(1)) extras.Add(model);
    Set(viewport, "_extras", extras);
    type.GetMethod("EvaluateAllPose", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewport, [time]);
    return models.Select(model => ((Vector3[])modelType.GetField("Posed")!.GetValue(model)!, (Matrix4x4[])modelType.GetField("BoneGlobals")!.GetValue(model)!)).ToArray();
}

static ViewportMesh Static(ViewportMesh[] parts, Vector3[][] posed)
{
    var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>(); var faces = new List<(int, int, int)>();
    for (var i = 0; i < parts.Length; i++)
    {
        var p = parts[i]; var offset = vertices.Count;
        vertices.AddRange(posed[i]); normals.AddRange(p.Normals); uvs.AddRange(p.Uvs);
        var visible = p.Groups.Where(g => g.DefaultVisible).Select(g => g.Key).ToHashSet();
        faces.AddRange(p.Faces.Where((f, index) => p.FaceGroups.Length == 0 || visible.Contains(p.FaceGroups[index])).Select(f => (f.A + offset, f.B + offset, f.C + offset)));
    }
    return new ViewportMesh { Vertices = vertices.ToArray(), Normals = normals.ToArray(), Uvs = uvs.ToArray(), Faces = faces.ToArray(),
        FaceTexture = Enumerable.Repeat(-1, faces.Count).ToArray(), FaceGroups = new int[faces.Count],
        Groups = [new ViewportGroup { Key = 0, Id = 0, Name = "Actual preview pose", Materials = [], FaceCount = faces.Count, DefaultVisible = true }],
        Textures = [], Weights = vertices.Select(_ => Array.Empty<(int, float)>()).ToArray(), Bones = [], DeformToBone = [] };
}
