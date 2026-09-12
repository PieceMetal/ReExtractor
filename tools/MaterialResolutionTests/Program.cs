using ReExtractor.Core;
using ReeLib;
using ReeLib.Mdf;

const string stem = "natives/stm/art/model/character/ch0/ch002_00/00/ch002_00_00";
byte[] Material(string texture, string name = "body")
{
    using var stream = new MemoryStream();
    var mdf = new MdfFile(new FileHandler(stream, "fixture.mdf2.50"));
    var material = new MaterialData { Name = name, MasterMaterial = "test.mmtr" };
    material.Textures.Add(new TexHeader { texType = "BaseDielectricMap", texPath = texture });
    material.Textures.Add(new TexHeader { texType = "NormalRoughnessMap", texPath = "test/normal.tex" });
    mdf.Materials.Add(material);
    if (!mdf.Write()) throw new Exception("Cannot write MDF fixture");
    return stream.ToArray();
}

var count = 0;
foreach (var suffix in new[] { "", "_v00", "_event_00", "_c", "_c_event_00", "_d_event_00" })
{
    var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
    {
        [stem + suffix + ".mdf2.50"] = Material("test/body.tex"),
        ["natives/stm/streaming/test/body.tex.251111100"] = [1],
        ["natives/stm/streaming/test/normal.tex.251111100"] = [1],
    };
    Stream? Open(string path) => files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes) : null;
    var refs = ViewportDataLoader.ListReferencedTexturePaths(new MaterialResolver(files.Keys).Resolve(stem + ".mesh.260209350", ["body"], Open), Open);
    if (refs.Count != 2 || refs.Any(p => !p.Contains("/streaming/")) || !refs.Any(p => p.Contains("normal.tex")))
        throw new Exception("Missing base/normal or full resolution resource: " + suffix);
    // An unsuffixed material must continue to take priority over new fallbacks.
    files[stem + ".mdf2.50"] = Material("test/original.tex");
    files["natives/stm/streaming/test/original.tex.251111100"] = [1];
    refs = ViewportDataLoader.ListReferencedTexturePaths(new MaterialResolver(files.Keys).Resolve(stem + ".mesh.260209350", ["body"], Open), Open);
    if (!refs.Any(p => p.Contains("original.tex")) || refs.Any(p => p.Contains("body.tex")))
        throw new Exception("Default material priority changed");
    count++;
}
foreach (var path in new[] { "../escape.tex.1", "natives/stm/../../escape.tex.1" })
{
    try { TextureExportService.TextureExportPath(Path.GetTempPath(), path); }
    catch (InvalidDataException) { count++; continue; }
    throw new Exception("Accepted traversal path");
}
Console.WriteLine($"MATERIAL_RESOLUTION_PASS {count} cases; albedo + normal, streaming paths, default priority, safe output paths");

void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
var resources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
Stream? Read(string p) => resources.TryGetValue(p, out var bytes) ? new MemoryStream(bytes) : null;
const string modelPath = stem + ".mesh.260209350";
resources[stem + "_unexpected.mdf2.50"] = Material("test/body.tex");
var resolver = new MaterialResolver(resources.Keys);
Check(resolver.Resolve(modelPath, ["BODY"], Read).SelectedPath == stem + "_unexpected.mdf2.50", "unknown suffix + case insensitive names");
resources[stem + ".mdf2.51"] = [0, 1, 2];
resources[stem + ".mdf2.50"] = Material("wrong.tex", "unrelated");
resolver = new MaterialResolver(resources.Keys);
var resolution = resolver.Resolve(modelPath, ["body"], Read);
Check(resolution.SelectedPath == stem + "_unexpected.mdf2.50" && resolution.Diagnostics.Count >= 2, "corrupt and mismatched defaults do not hide a valid fallback");
resources[stem + "_second.mdf2.50"] = Material("second.tex");
resolver = new MaterialResolver(resources.Keys);
resolution = resolver.Resolve(modelPath, ["body"], Read);
Check(resolution.RequiresSelection && resolution.Candidates.Count == 2, "multiple valid candidates require choice");
try { ViewportDataLoader.ListReferencedTexturePaths(resolution, Read); throw new Exception("Ambiguous export accepted"); }
catch (MaterialSelectionRequiredException) { Console.WriteLine("PASS ambiguous export blocked"); }
resolver.Choose(resolution, stem + "_second.mdf2.50");
Check(resolver.Resolve(modelPath, ["body"], Read).SelectedPath == stem + "_second.mdf2.50", "explicit choice invalidates ambiguous cache");
try { resolver.Choose(resolution, "not-a-candidate.mdf2.50"); throw new Exception("Invalid choice accepted"); }
catch (InvalidDataException) { Console.WriteLine("PASS invalid choice rejected"); }
resources.Clear();
resources[stem + "_bad.mdf2.50"] = Material("wrong.tex", "other");
resolver = new MaterialResolver(resources.Keys);
Check(resolver.Resolve(modelPath, ["body"], Read).SelectedPath == null, "same ordinal but different material name rejected");
resources[stem[..stem.LastIndexOf('/')] + "/shared.mdf2.50"] = Material("shared.tex");
resolver = new MaterialResolver(resources.Keys);
Check(resolver.Resolve(modelPath, ["body"], Read).SelectedPath!.EndsWith("/shared.mdf2.50"), "same folder shared material validated");
resolver = new MaterialResolver(resources.Keys);
Check(resolver.Resolve(modelPath, ["body", "missing"], Read).SelectedPath == null, "partial name coverage rejected");
Check(resolver.Resolve(modelPath, ["body"], Read).SelectedPath != null, "cached result rechecks a changed material name set");
resources.Clear();
resources[stem[..stem.LastIndexOf('/')] + "/elsewhere/shared.mdf2.50"] = Material("shared.tex");
Check(new MaterialResolver(resources.Keys).Resolve(modelPath, ["body"], Read).Candidates.Count == 0, "discovery stays in the model folder");
var folderRoot = Path.Combine(Path.GetTempPath(), "ReExtractor-MaterialFolder-" + Guid.NewGuid().ToString("N"));
var physicalMdf = Path.Combine(folderRoot, (stem + "_folder.mdf2.50").Replace('/', Path.DirectorySeparatorChar));
Directory.CreateDirectory(Path.GetDirectoryName(physicalMdf)!);
File.WriteAllBytes(physicalMdf, Material("folder.tex"));
var folderPak = new PakService();
folderPak.AddFolder(folderRoot);
Stream? OpenFolder(string path) { try { return folderPak.ReadFile(path); } catch (FileNotFoundException) { return null; } }
Check(folderPak.CreateMaterialResolver().Resolve(modelPath, ["body"], OpenFolder).SelectedPath!.EndsWith("_folder.mdf2.50"), "extracted folder candidates discovered without a list file");

if (args.Length >= 2)
{
    var pak = new PakService();
    foreach (var file in Directory.GetFiles(args[0], "*.pak", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)) pak.AddPak(file);
    pak.LoadListFile(args[1]);
    var entries = pak.EnumerateFiles();
    Stream? Open(string p) { try { return pak.ReadFile(p); } catch (FileNotFoundException) { return null; } }
    var actual = pak.CreateMaterialResolver();
    var autoPath = entries.Single(e => e.Path.EndsWith("/ch024_00_20.mesh.260209350")).Path;
    var found = ViewportDataLoader.ResolveMaterial(autoPath, Open, actual);
    Check(found.SelectedPath!.EndsWith("_b.mdf2.51"), "real _b candidate discovered");
    using (var stream = pak.ReadFile(autoPath)) Check(ViewportDataLoader.LoadMesh(stream, autoPath, 1, Open, true, actual).Textures.Length == 2, "real _b preview loads two materials");
    Check(ViewportDataLoader.ListReferencedTexturePaths(autoPath, Open, actual).Count == 22, "real _b export uses same MDF with 22 texture references");
    var ambiguous = entries.Single(e => e.Path.EndsWith("/ch210_50_40.mesh.260209350")).Path;
    var choices = ViewportDataLoader.ResolveMaterial(ambiguous, Open, actual);
    Check(choices.RequiresSelection && choices.Candidates.Count == 3, "real _o1/_o2/_o3 remains ambiguous");
    var testOutput = Path.GetFullPath("artifacts/material_resolver/export");
    var blocked = ModelBatchExportService.Export(pak, [ambiguous], testOutput, Path.Combine(testOutput, "temp"),
        (_, _) => throw new Exception("Conversion must not start without a choice"));
    Check(blocked.OutputFiles.Count == 0 && blocked.Failures.Count == 1 && blocked.Failures[0].Contains("请先选择"), "batch export reports ambiguity before conversion");
    using (var stream = pak.ReadFile(ambiguous))
    {
        try { ViewportDataLoader.LoadMesh(stream, ambiguous, 1, Open, true, actual); throw new Exception("Ambiguous preview accepted"); }
        catch (MaterialSelectionRequiredException) { Console.WriteLine("PASS real ambiguous preview blocked"); }
    }
    actual.Choose(choices, choices.Candidates.Single(c => c.Path.EndsWith("_o2.mdf2.51")).Path);
    using (var stream = pak.ReadFile(ambiguous)) Check(ViewportDataLoader.LoadMesh(stream, ambiguous, 1, Open, true, actual).Textures.Length > 0, "real selected variant preview loads");
    Check(ViewportDataLoader.ListReferencedTexturePaths(ambiguous, Open, actual).Count > 0, "real selected variant texture export resolves");
    if (args.Length == 3)
    {
        var exported = ModelBatchExportService.Export(pak, [ambiguous], testOutput, Path.Combine(testOutput, "temp"), (input, output) =>
        {
            var start = new System.Diagnostics.ProcessStartInfo(args[2]) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--background", "--python-exit-code", "1", "--python", Path.GetFullPath("tools/export_models_fbx.py"), "--", input, output }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Blender conversion failed");
        }, materialResolver: actual);
        File.WriteAllText(Path.Combine(testOutput, "result.json"), System.Text.Json.JsonSerializer.Serialize(exported));
        var expectedUnsupported = new[] { "19_noise_100_MSK4", "tex_capcom_vectorfield_0003_MSK4" };
        Check(exported.OutputFiles.Count == 1 && exported.Failures.Count == 2 && expectedUnsupported.All(name =>
            exported.Failures.Any(f => f.Contains(name) && f.Contains("Depth > 1"))), "selected o2 FBX exported; only two known volume textures rejected");
        Check(Directory.GetFiles(Path.Combine(testOutput, "textures"), "*.png", SearchOption.AllDirectories).Length ==
            ViewportDataLoader.ListReferencedTexturePaths(ambiguous, Open, actual).Count - 2, "selected variant exports every supported related PNG");
    }
}
