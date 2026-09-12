using ReExtractor.Core;
using ReeLib;
using ReeLib.Mdf;

const string stem = "natives/stm/art/model/character/ch0/ch002_00/00/ch002_00_00";
byte[] Material(string texture)
{
    using var stream = new MemoryStream();
    var mdf = new MdfFile(new FileHandler(stream, "fixture.mdf2.50"));
    var material = new MaterialData { Name = "body", MasterMaterial = "test.mmtr" };
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
    var refs = ViewportDataLoader.ListReferencedTexturePaths(stem + ".mesh.260209350", Open);
    if (refs.Count != 2 || refs.Any(p => !p.Contains("/streaming/")) || !refs.Any(p => p.Contains("normal.tex")))
        throw new Exception("Missing base/normal or full resolution resource: " + suffix);
    // An unsuffixed material must continue to take priority over new fallbacks.
    files[stem + ".mdf2.50"] = Material("test/original.tex");
    files["natives/stm/streaming/test/original.tex.251111100"] = [1];
    refs = ViewportDataLoader.ListReferencedTexturePaths(stem + ".mesh.260209350", Open);
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
