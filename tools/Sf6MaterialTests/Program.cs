using ReExtractor.Core;
using ReeLib.Mdf;
using System.Numerics;

// Without arguments: synthetic tint checks. With game directory and list: real-asset regression.
var colors = typeof(ViewportTexture).Assembly.GetType("ReExtractor.Core.Sf6MaterialColors")!;
foreach (var (color, expected) in new[] { (new Vector4(.5f), 0x77806040u), (new Vector4(0, 0, 0, 1), 0x77000000u) })
{
    var material = new MaterialData { Name = "esf_Hair" };
    material.Textures.Add(new TexHeader { texType = "BaseAnisoShiftMap", texPath = "hair.tex" });
    material.Parameters.Add(new ParamHeader { paramName = "CustomizeColor_0", parameter = color });
    var texture = new ViewportTexture { Width = 1, Height = 1, Name = "hair", Pixels = [0x77806040u] };
    colors.GetMethod("Apply")!.Invoke(null, [texture, material, (Func<string, Stream?>)(_ => null)]);
    Check(texture.Pixels[0] == expected, "Tint preserves independent coverage");
}
if (args.Length == 0) return;
if (args.Length != 2) throw new ArgumentException("Expected game directory and resource list path");
var pak = new PakService();
foreach (var path in Directory.GetFiles(args[0], "*.pak", SearchOption.AllDirectories)
    .OrderBy(p => p.Contains("pdlc") ? 2 : p.Contains("dlc") ? 1 : 0)) pak.AddPak(path);
pak.LoadListFile(args[1]);
Console.WriteLine($"Indexed {pak.EnumerateFiles().Count} resources");
Stream? Open(string path) { try { return pak.ReadFile(path); } catch (FileNotFoundException) { return null; } }
ViewportMesh Load(string id, string costume, string part)
{
    var path = $"natives/stm/product/model/esf/{id}/{costume}/{part}/{id}_{costume}_{part}.mesh.230110883";
    using var stream = pak.ReadFile(path);
    return ViewportDataLoader.LoadMesh(stream, path, 0, Open, loadTextures: true);
}
ViewportTexture Texture(ViewportMesh mesh, string prefix) => mesh.Textures.Single(t => t.Name.StartsWith(prefix));
double Red(ViewportTexture texture) => texture.Pixels.Average(p => (p >> 16) & 255);
foreach (var id in new[] { "esf001", "esf009", "esf010" })
{
    var head = Load(id, "000", "00");
    Check(Red(Texture(head, "esf_Head00_")) > 40, $"{id} skin retains color");
    Check(!head.FaceTexture.Where((t, i) => t < 0 && !head.FaceExportHidden[i]).Any(), $"{id} no opaque untextured eye overlay");
    Check(head.Textures.Select((t, i) => (t, i)).Any(pair =>
        (pair.t.Name.StartsWith("esf_Eye00_") || pair.t.Name.StartsWith("esf_Eyes00_")) &&
        head.FaceTexture.Where((slot, face) => slot == pair.i && !head.FaceExportHidden[face]).Any()), $"{id} eyeball retained");
}
var ryu = Load("esf001", "001", "01");
var threads = Texture(ryu, "esf_Threads_").Pixels[0];
Check(((threads >> 16) & 255) > ((threads >> 8) & 255) && ((threads >> 16) & 255) < 100, "Ryu threads are dark red");
Check(!ryu.FaceTexture.Where((t, i) => t < 0 && !ryu.FaceExportHidden[i]).Any(), "Ryu has no visible untextured cloth faces");
var ken = new[] { Load("esf010", "001", "00"), Load("esf010", "001", "01"), Load("esf010", "001", "02") };
var beard = Texture(ken[0], "esf_Hair02_");
Check(Red(beard) < 140 && beard.Width > 16 && beard.Pixels.Any(p => (p >> 24) == 0) && beard.Pixels.Any(p => (p >> 24) > 200), "Ken beard color and coverage");
var lashes = Texture(ken[0], "esf_Hair00_");
var hair = Texture(ken[2], "esf_Hair00_");
Check(lashes.Name != hair.Name, "Different head/hair palettes have distinct texture identities");
var merged = ViewportMesh.Merge(ken);
foreach (var original in new[] { lashes, hair, beard })
    Check(merged.Textures.Single(t => t.Name == original.Name).Pixels.AsSpan().SequenceEqual(original.Pixels), "Merge preserves baked material pixels");
static void Check(bool passed, string name)
{
    if (!passed) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
