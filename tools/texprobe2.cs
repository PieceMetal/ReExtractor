#:project ../src/ReExtractor.Core/ReExtractor.Core.csproj

using ReExtractor.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// Replicates the GUI path EXACTLY: ViewportDataLoader.LoadMesh (mdf resolve + decode + uint packing),
// then dumps every ViewportTexture.Pixels buffer to PNG + face-texture-slot histogram.
var gameDir = @"E:\Steam\steamapps\common\OnimushaWotS_Demo";
var listPath = @"D:\OnimushaWotS_Demo_Tools\REasy_v0.7.3\resources\data\lists\ONIWOTS_STM.list";
var outDir = @"D:\texdump\probe2";
Directory.CreateDirectory(outDir);

var pak = new PakService();
pak.AddPaksFromGameDir(gameDir);
pak.LoadListFile(listPath);
Stream? Open(string p) { try { return pak.ReadFile(p); } catch { return null; } }

var meshPath = "natives/stm/art/model/character/ch0/ch001_00/00/ch001_00_00.mesh.251215606";
using var ms = pak.ReadFile(meshPath);
var vm = ViewportDataLoader.LoadMesh(ms, meshPath, 1, Open);
Console.WriteLine($"verts={vm.VertexCount} faces={vm.FaceCount} bones={vm.Bones.Length} textures={vm.Textures.Length}");

for (var i = 0; i < vm.Textures.Length; i++)
{
    var t = vm.Textures[i];
    var ok = t.Pixels.Length == t.Width * t.Height;
    Console.WriteLine($"tex[{i}] {t.Name} {t.Width}x{t.Height} px={t.Pixels.Length} expect={t.Width * t.Height} {(ok ? "ok" : "MISMATCH")}");
    if (!ok) continue;
    var img = new Image<Rgba32>(t.Width, t.Height);
    img.ProcessPixelRows(acc =>
    {
        for (var y = 0; y < t.Height; y++)
        {
            var row = acc.GetRowSpan(y);
            for (var x = 0; x < t.Width; x++)
            {
                var p = t.Pixels[y * t.Width + x];
                row[x] = new Rgba32((byte)(p >> 16), (byte)(p >> 8), (byte)p, 255);
            }
        }
    });
    var outPath = Path.Combine(outDir, $"tex{i}_{t.Name.Replace('.', '_')}.png");
    using (img) img.SaveAsPng(outPath);
    Console.WriteLine($"  -> {outPath}");
}

var hist = new SortedDictionary<int, int>();
foreach (var s in vm.FaceTexture) hist[s] = hist.GetValueOrDefault(s) + 1;
Console.WriteLine("face slots (slot:faces): " + string.Join(", ", hist.Select(kv => $"{kv.Key}:{kv.Value}")));
Console.WriteLine("DONE");
