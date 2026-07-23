#:project ../src/ReExtractor.Core/ReExtractor.Core.csproj

using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using ReExtractor.Core;
using ReeLib;
using ReeLib.DDS;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// Verify: does BCnEncoder decode THIS game's BC7 correctly? (WIC path is dead — always throws.)
var gameDir = @"E:\Steam\steamapps\common\OnimushaWotS_Demo";
var listPath = @"D:\OnimushaWotS_Demo_Tools\REasy_v0.7.3\resources\data\lists\ONIWOTS_STM.list";
var outDir = @"D:\texdump\probe7";
Directory.CreateDirectory(outDir);

var pak = new PakService();
pak.AddPaksFromGameDir(gameDir);
pak.LoadListFile(listPath);

string[] paths =
[
    "natives/stm/art/model/common/textures/human/player/pl_common_body_mat_albd.tex.251111100",
    "natives/stm/streaming/art/model/common/textures/human/player/pl_common_body_mat_albd.tex.251111100",
    "natives/stm/art/model/common/textures/human/player/pl_common_hand_mat_albd.tex.251111100",
];

foreach (var path in paths)
{
    Console.WriteLine("=== " + Path.GetFileName(path));
    try
    {
        using var ms = pak.ReadFile(path);
        ms.Position = 0;
        var tex = new TexFile(new FileHandler(ms, path));
        if (!tex.Read()) { Console.WriteLine("  read failed"); continue; }
        Console.WriteLine($"  format={tex.Header.format}({(int)tex.Header.format}) {tex.Header.width}x{tex.Header.height} mips={tex.Header.mipCount}");

        using var iter = tex.CreateIterator(0, 0);
        var mip = new DDSFile.MipMapLevelData();
        if (!iter.Next(ref mip) || mip.data.IsEmpty) { Console.WriteLine("  no mip0"); continue; }
        var w = (int)mip.width; var h = (int)mip.height;
        var raw = new byte[mip.data.Length];
        mip.data.CopyTo(raw);
        Console.WriteLine($"  mip0: {w}x{h} rawBytes={raw.Length}");

        var decoder = new BcDecoder();
        var decoded = decoder.DecodeRaw2D(raw, w, h, CompressionFormat.Bc7);
        var pixels = new Rgba32[w * h];
        var span = decoded.Span;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = span[y, x];
                pixels[y * w + x] = new Rgba32(c.r, c.g, c.b, c.a);
            }
        var img = Image.LoadPixelData<Rgba32>(pixels, w, h);
        var outPath = Path.Combine(outDir, Path.GetFileName(path).Replace('.', '_') + "_bcn.png");
        using (img) img.SaveAsPng(outPath);
        var s0 = pixels[0]; var s1 = pixels[pixels.Length / 2]; var s2 = pixels[pixels.Length / 4];
        Console.WriteLine($"  px[0]=({s0.R},{s0.G},{s0.B},{s0.A}) px[mid]=({s1.R},{s1.G},{s1.B},{s1.A}) px[q]=({s2.R},{s2.G},{s2.B},{s2.A})");
        Console.WriteLine($"  -> {outPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine("  ERROR: " + ex.Message);
    }
}
Console.WriteLine("DONE");
