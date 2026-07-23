#:project ../src/ReExtractor.Core/ReExtractor.Core.csproj

using ReExtractor.Core;
using ReeLib;

// Legacy DDS (fourcc DXTn, no DX10 header) + System.Drawing.Bitmap WIC acceptance test.
// Goal: find a WIC-decodable DDS form for BC7 (and confirm the pipeline for BC1).
var gameDir = @"E:\Steam\steamapps\common\OnimushaWotS_Demo";
var listPath = @"D:\OnimushaWotS_Demo_Tools\REasy_v0.7.3\resources\data\lists\ONIWOTS_STM.list";
var outDir = @"D:\texdump\probe6";
Directory.CreateDirectory(outDir);

var pak = new PakService();
pak.AddPaksFromGameDir(gameDir);
pak.LoadListFile(listPath);

// (path, dxgiFourCC) — fourcc 0 => keep DX10 header as-is (control)
(string path, string fourcc)[] cases =
[
    ("natives/stm/art/model/common/textures/human/player/pl_common_body_mat_albd.tex.251111100", "DX10"),
    ("natives/stm/art/model/common/textures/human/player/pl_common_body_mat_albd.tex.251111100", "BC7U"),
    ("natives/stm/art/model/common/textures/human/player/pl_common_body_mat_albd.tex.251111100", "DXT5"),
    ("natives/stm/art/model/common/textures/human/player/pl_common_body_mat_albd.tex.251111100", "ATI2"),
    ("natives/stm/streaming/art/model/common/textures/human/player/pl_common_body_mat_albd.tex.251111100", "BC7U"),
    ("natives/stm/art/model/character/ch0/ch001_00/00/textures/ch001_00_00_cloth_top_mat_albd.tex.251111100", "DXT1"),
];

foreach (var (path, cc) in cases)
{
    var tag = $"{Path.GetFileName(path)} [{cc}]";
    using var ms = pak.ReadFile(path);
    ms.Position = 0;
    var tex = new TexFile(new FileHandler(ms, path));
    if (!tex.Read()) { Console.WriteLine($"{tag}: tex read failed"); continue; }

    var ddsPath = Path.Combine(outDir, Path.GetFileName(path).Replace('.', '_') + "_" + cc + ".dds");
    tex.SaveAsDDS(ddsPath);
    var bytes = File.ReadAllBytes(ddsPath);

    if (cc != "DX10")
    {
        // rewrite: fourcc <- cc, strip the 20-byte DX10 extension header
        var ccBytes = System.Text.Encoding.ASCII.GetBytes(cc);
        Array.Copy(ccBytes, 0, bytes, 84, 4);
        var stripped = new byte[bytes.Length - 20];
        Array.Copy(bytes, 0, stripped, 0, 128);
        Array.Copy(bytes, 148, stripped, 128, bytes.Length - 148);
        bytes = stripped;
        var legacyPath = ddsPath.Replace("_" + cc + ".dds", "_" + cc + "_legacy.dds");
        File.WriteAllBytes(legacyPath, bytes);
        ddsPath = legacyPath;
    }

    try
    {
        using var bmp = new System.Drawing.Bitmap(ddsPath);
        var w = bmp.Width; var h = bmp.Height;
        // sample a few pixels to prove decode actually ran (not a lazy header read)
        var p1 = bmp.GetPixel(w / 2, h / 2);
        var p2 = bmp.GetPixel(w / 4, h / 3);
        Console.WriteLine($"{tag}: WIC OK {w}x{h} center=({p1.R},{p1.G},{p1.B},{p1.A}) q=({p2.R},{p2.G},{p2.B},{p2.A})");
        var pngPath = ddsPath + ".png";
        bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"   -> {pngPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{tag}: WIC FAIL {ex.GetType().Name}: {ex.Message}");
    }
}
Console.WriteLine("DONE");
