#:project ../src/ReExtractor.Core/ReExtractor.Core.csproj

using ReExtractor.Core;
using ReeLib;
using SixLabors.ImageSharp;

// Offline probe: decode albedo .tex straight from the PAK with the CURRENT TexService,
// so we can tell whether decode itself produces noise (vs. the GUI mesh-texture link).
var gameDir = @"E:\Steam\steamapps\common\OnimushaWotS_Demo";
var listPath = @"D:\OnimushaWotS_Demo_Tools\REasy_v0.7.3\resources\data\lists\ONIWOTS_STM.list";
var outDir = @"D:\texdump\probe";
Directory.CreateDirectory(outDir);

var pak = new PakService();
pak.AddPaksFromGameDir(gameDir);
pak.LoadListFile(listPath);
Console.WriteLine($"paks={pak.PakCount} knownPaths={pak.KnownPathCount}");

string[] bases =
[
    "natives/stm/art/model/common/textures/human/player/pl_common_body_mat_albd.tex.251111100",
    "natives/stm/streaming/art/model/common/textures/human/player/pl_common_body_mat_albd.tex.251111100",
    "natives/stm/art/model/character/ch0/ch001_00/00/textures/ch001_00_00_cloth_top_mat_albd.tex.251111100",
    "natives/stm/streaming/art/model/character/ch0/ch001_00/00/textures/ch001_00_00_cloth_top_mat_albd.tex.251111100",
    "natives/stm/art/model/character/ch0/ch001_00/00/textures/ch001_00_00_parts_mat_albd.tex.251111100",
    "natives/stm/streaming/art/model/character/ch0/ch001_00/00/textures/ch001_00_00_parts_mat_albd.tex.251111100",
];

foreach (var path in bases)
{
    Console.WriteLine("=== " + path);
    try
    {
        using var ms = pak.ReadFile(path);
        Console.WriteLine($"  bytes={ms.Length}");

        // raw header first (no decode)
        ms.Position = 0;
        var hdr = new TexFile(new FileHandler(ms, path));
        if (hdr.Read())
            Console.WriteLine($"  header: format={hdr.Header.format}({(int)hdr.Header.format}) {hdr.Header.width}x{hdr.Header.height} mips={hdr.Header.mipCount}");
        else
            Console.WriteLine("  header: TexFile.Read() FAILED");

        // decode with current TexService
        ms.Position = 0;
        var (img, d) = new TexService().DecodeToImageDiag(ms, path);
        var name = Path.GetFileName(path).Replace('.', '_') + ".png";
        var outPath = Path.Combine(outDir, name);
        using (img) img.SaveAsPng(outPath);
        Console.WriteLine($"  decode: branch={d.Branch}");
        Console.WriteLine($"  stats: {d.PixelStats}");
        Console.WriteLine($"  notes: {d.Notes}");
        Console.WriteLine($"  -> {outPath} ({d.Width}x{d.Height})");
    }
    catch (Exception ex)
    {
        Console.WriteLine("  ERROR: " + ex.Message);
    }
}
Console.WriteLine("DONE");
