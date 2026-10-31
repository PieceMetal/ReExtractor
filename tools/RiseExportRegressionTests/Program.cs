using System.Numerics;
using System.Text.Json;
using ReExtractor.Core;
using SharpGLTF.Schema2;

// Integration test using real Rise data. No preview window, Blender or Unity is launched.
var output = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "artifacts/rise-export-regression");
var game = args.ElementAtOrDefault(1) ?? @"H:\BaiduNetdiskDownload\Monster.Hunter.Rise\Monster.Hunter.Rise";
var list = args.ElementAtOrDefault(2) ?? "artifacts/ReExtractor-v1.3.11-mhr-npc-animation-fix-win-x64/ReExtractor-tools/filelists/remote/MHR_STM_Release.list";
Directory.CreateDirectory(output);
var pak = new PakService();
pak.AddPaksFromGameDir(game);
pak.LoadListFile(list);
Stream? Open(string path) { try { return pak.ReadFile(path); } catch (FileNotFoundException) { return null; } }
ViewportMesh Load(string path) { using var stream = pak.ReadFile(path); return ViewportDataLoader.LoadMesh(stream, path, loadTextures: false); }
var unityAxes = args.ElementAtOrDefault(3) == "unity";
var exporter = new ViewportExportService(unityAxes);
var animation = new AnimationService(unityAxes);
const string motion = "natives/stm/player/mot/plw_dualblades_100.motlist.528";
var checks = 0;
var report = new List<object>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }

foreach (var gender in new[] { "m", "f" })
{
    var paths = new[] { "body", "arm", "helm", "leg", "wst" }
        .Select(part => $"natives/stm/player/mod/{gender}/pl004/{gender}_{part}004.mesh.2109148288").ToArray();
    var parts = paths.Select(Load).ToArray();
    var models = parts.Select(p => (p, (IReadOnlySet<int>)p.Groups.Where(g => g.DefaultVisible).Select(g => g.Key).ToHashSet())).ToArray();
    var snapshots = parts.Select(Skeleton).ToArray();
    var legacy = exporter.BuildMergedExportModel(models);
    Check(!legacy.Mesh.Bones.Any(b => b.Name == "Cog"), "Negative control no longer exercises truncated skeleton");

    // Export the model FIRST. No LoadAnimation call may precede this preparation.
    var cold = RisePlayerExportSkeleton.Build(paths, models, Open);
    var modelPath = exporter.ConvertToGlb(cold.Mesh, cold.VisibleGroups, Path.Combine(output, gender, "model", "model.glb"));
    var model = ModelRoot.Load(modelPath);
    foreach (var (name, parent) in new[] { ("Cog", "Root"), ("Waist_00", "Cog"), ("Spine_00", "Waist_00"), ("Spine_01", "Spine_00") })
        Check(model.LogicalNodes.Any(n => n.Name == name && n.VisualParent?.Name == parent), $"{gender}: missing {parent}->{name}");
    for (var i = 0; i < parts.Length; i++) Check(Skeleton(parts[i]) == snapshots[i], "Export mutated preview/source skeleton");
    var snapshot = Skeleton(cold.Mesh);
    var orderedParts = paths.Select((path, i) => (path, mesh: parts[i])).OrderBy(x => x.path, StringComparer.OrdinalIgnoreCase);
    Check(orderedParts.SelectMany(x => x.mesh.DeformToBone.Select(i => x.mesh.Bones[i].InverseGlobalBind)).SequenceEqual(
        cold.Mesh.DeformToBone.Select(i => cold.Mesh.Bones[i].InverseGlobalBind)), "Outfit source inverse binds changed");
    var clipFiles = new List<string>();
    foreach (var index in new[] { 0, 1, 7 })
    {
        using var stream = pak.ReadFile(motion);
        var file = animation.ConvertOneToGlbWithAnimation(cold.Mesh, stream, motion, index,
            Path.Combine(output, gender, "animation")).Single();
        clipFiles.Add(file);
        var actual = ModelRoot.Load(file);
        Check(SameSkeleton(model, actual), $"{gender}: model/animation {index} bind or hierarchy mismatch");
        Check(Skeleton(cold.Mesh) == snapshot, "Animation export mutated prepared skeleton");

        // Compare named transforms with the complete player rig, independently of equipment merging.
        var reference = Load($"natives/stm/player/mod/{gender}/bone/{gender}_shadow.mesh.2109148288");
        using var referenceStream = pak.ReadFile(motion);
        var referenceClip = ViewportDataLoader.LoadAnimation(referenceStream, motion, index, sceneMeshes: [reference]);
        var referenceFile = exporter.ConvertToAnimatedGlb(reference,
            reference.Groups.Select(g => g.Key).ToHashSet(), referenceClip,
            Path.Combine(output, gender, "reference", $"motion{index}.glb"));
        var expected = ModelRoot.Load(referenceFile);
        var names = expected.LogicalNodes.Where(n => n.Name != null && referenceClip.NamedTracks.ContainsKey(n.Name))
            .ToDictionary(n => n.Name);
        float maxError = 0;
        foreach (var time in new[] { 0f, referenceClip.Duration * .25f, referenceClip.Duration * .46f, referenceClip.Duration * .75f, referenceClip.Duration })
            foreach (var node in actual.LogicalNodes.Where(n => n.Name != null && names.ContainsKey(n.Name)))
                maxError = Math.Max(maxError, MatrixError(node.GetWorldMatrix(actual.LogicalAnimations[0], time),
                    names[node.Name].GetWorldMatrix(expected.LogicalAnimations[0], time)));
        Check(maxError < .0001f, $"{gender}: animation {index} differs from full rig by {maxError}");
        report.Add(new { gender, index, maxError, bones = cold.Mesh.Bones.Length });
        Console.WriteLine($"PASS {gender} cold model/animation {index}: max matrix error={maxError}");
    }

    // Loading a different preview clip must not change future model/animation exports.
    AnimationClip previewClip;
    using (var stream = pak.ReadFile(motion)) previewClip = ViewportDataLoader.LoadAnimation(stream, motion, 7, sceneMeshes: parts);
    var warm = RisePlayerExportSkeleton.Build(paths, models, Open);
    var currentFile = exporter.ConvertToAnimatedGlb(warm.Mesh, warm.VisibleGroups, previewClip,
        Path.Combine(output, gender, "current-preview", "motion7.glb"));
    var current = ModelRoot.Load(currentFile);
    var batchEquivalent = ModelRoot.Load(clipFiles[2]);
    Check(SameSkeleton(model, current), "Current-preview export skeleton differs");
    foreach (var t in new[] { 0f, previewClip.Duration * .46f, previewClip.Duration })
        for (int i = 0; i < current.LogicalNodes.Count; i++)
            Check(MatrixError(current.LogicalNodes[i].GetWorldMatrix(current.LogicalAnimations[0], t),
                batchEquivalent.LogicalNodes[i].GetWorldMatrix(batchEquivalent.LogicalAnimations[0], t)) < .0001f,
                "Current-preview export differs from direct animation");
    Check(snapshot == Skeleton(warm.Mesh), $"{gender}: export depends on preview history");
    using (var stream = pak.ReadFile(motion))
    {
        var file = animation.ConvertOneToGlbWithAnimation(warm.Mesh, stream, motion, 0, Path.Combine(output, gender, "warm")).Single();
        Check(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(clipFiles[0])), "Cold/warm animation GLBs differ");
    }
    var rebuilt = RisePlayerExportSkeleton.Build(paths, models, Open);
    Check(snapshot == Skeleton(rebuilt.Mesh), "Repeated preparation changed skeleton");
    var reversed = RisePlayerExportSkeleton.Build(paths.Reverse().ToArray(), models.Reverse().ToArray(), Open);
    Check(snapshot == Skeleton(reversed.Mesh), "Equipment loading order changed exported skeleton");

    // Exercise the actual batch API without preview preparation, not only its single-clip sibling.
    using (var stream = pak.ReadFile(motion))
    {
        var files = animation.ConvertAllToGlbWithAnimation(rebuilt.Mesh, stream, motion, Path.Combine(output, gender, "batch"));
        Check(files.Count >= 8, "Batch fixture did not contain the expected motions");
        foreach (var file in files)
            Check(SameSkeleton(model, ModelRoot.Load(file)), $"{gender}: batch hierarchy/bind differs: {file}");
        Console.WriteLine($"PASS {gender} direct batch export: {files.Count} clips share the model skeleton");
    }

    // Single-part export still needs the complete player rig and must preserve source binds.
    var solo = RisePlayerExportSkeleton.Build([paths[1]], [models[1]], Open);
    Check(solo.Mesh.Bones.Any(b => b.Name == "Cog"), "Single-part export lacks full rig");
    var rawArm = Load(paths[1]);
    Check(rawArm.DeformToBone.Select(i => rawArm.Bones[i].InverseGlobalBind).SequenceEqual(
        solo.Mesh.DeformToBone.Select(i => solo.Mesh.Bones[i].InverseGlobalBind)),
        "Source inverse binds changed");

    // Visibility must also survive the already-merged preview case.
    var raw = paths.Select(Load).ToArray();
    var hidden = raw.Select(p => (p, (IReadOnlySet<int>)new HashSet<int>())).ToArray();
    var empty = RisePlayerExportSkeleton.Build(paths, hidden, Open);
    Check(empty.Mesh.FaceCount == 0, "Hidden groups became visible");
    var selectedMergedGroups = raw.SelectMany(p => p.Groups).Select((g, i) => (g, i)).Where(x => x.g.DefaultVisible).Select(x => x.i).ToHashSet();
    var mergedPreview = RisePlayerExportSkeleton.Build(paths, [(legacy.Mesh, (IReadOnlySet<int>)selectedMergedGroups)], Open);
    Check(snapshot == Skeleton(mergedPreview.Mesh) && mergedPreview.Mesh.FaceCount == cold.Mesh.FaceCount, "Merged preview group remap failed");

    try { RisePlayerExportSkeleton.Build(paths, models, path => path.Contains("/bone/") ? null : Open(path)); throw new Exception("Missing rig silently exported"); }
    catch (FileNotFoundException) { checks++; }
}
Check(!RisePlayerExportSkeleton.Supports(["natives/stm/npc/mod/npc501_00.mesh.2109148288"]), "NPC incorrectly routed to player rig");
Check(!RisePlayerExportSkeleton.Supports(["natives/stm/player/mod/m/pl004/m_body004.mesh.230110883"]), "Other mesh format incorrectly routed to Rise");
File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { checks, samples = report }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RISE_EXPORT_REGRESSION PASS checks={checks}");

static string Skeleton(ViewportMesh mesh) => JsonSerializer.Serialize(new {
    bones = mesh.Bones.Select(b => new { b.Name, b.ParentIndex, b.LocalBind, b.InverseGlobalBind }),
    mesh.DeformToBone, aliases = mesh.AnimationBoneAliases
}, new JsonSerializerOptions { IncludeFields = true });

static bool SameSkeleton(ModelRoot a, ModelRoot b)
{
    if (a.LogicalNodes.Count != b.LogicalNodes.Count || a.LogicalSkins.Count != b.LogicalSkins.Count) return false;
    for (var i = 0; i < a.LogicalNodes.Count; i++)
    {
        var x = a.LogicalNodes[i]; var y = b.LogicalNodes[i];
        if (x.Name != y.Name || x.VisualParent?.Name != y.VisualParent?.Name || MatrixError(x.LocalMatrix, y.LocalMatrix) > .00001f) return false;
    }
    for (var i = 0; i < a.LogicalSkins.Count; i++)
        if (!a.LogicalSkins[i].Joints.Select(n => n.Name).SequenceEqual(b.LogicalSkins[i].Joints.Select(n => n.Name)) ||
            !a.LogicalSkins[i].InverseBindMatrices.SequenceEqual(b.LogicalSkins[i].InverseBindMatrices)) return false;
    return true;
}

static float MatrixError(Matrix4x4 a, Matrix4x4 b)
{
    var d = a - b;
    return new[] { d.M11,d.M12,d.M13,d.M14,d.M21,d.M22,d.M23,d.M24,d.M31,d.M32,d.M33,d.M34,d.M41,d.M42,d.M43,d.M44 }.Max(MathF.Abs);
}
