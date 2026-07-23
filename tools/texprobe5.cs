#:project ../src/ReExtractor.Core/ReExtractor.Core.csproj

using System.Buffers.Binary;
using ReExtractor.Core;
using ReeLib;

// Inspect the DDS that TexFile.SaveAsDDS produces (DX10 header? fourcc?) and probe
// what System.Drawing.Bitmap (WIC) accepts vs rejects.
var gameDir = @"E:\Steam\steamapps\common\OnimushaWotS_Demo";
var listPath = @"D:\OnimushaWotS_Demo_Tools\REasy_v0.7.3\resources\data\lists\ONIWOTS_STM.list";
var outDir = @"D:\texdump\probe5";
Directory.CreateDirectory(outDir);

var pak = new PakService();
pak.AddPaksFromGameDir(gameDir);
pak.LoadListFile(listPath);

string[] paths =
[
    "natives/stm/art/model/common/textures/human/player/pl_common_body_mat_albd.tex.251111100",          // 256 stub BC7
    "natives/stm/streaming/art/model/common/textures/human/player/pl_common_body_mat_albd.tex.251111100", // 2048 streaming BC7
    "natives/stm/art/model/character/ch0/ch001_00/00/textures/ch001_00_00_cloth_top_mat_albd.tex.251111100", // BC1 control
];

foreach (var path in paths)
{
    Console.WriteLine("=== " + Path.GetFileName(path));
    using var ms = pak.ReadFile(path);
    ms.Position = 0;
    var tex = new ReeLib.TexFile(new FileHandler(ms, path));
    if (!tex.Read()) { Console.WriteLine("  tex read failed"); continue; }
    var ddsPath = Path.Combine(outDir, Path.GetFileName(path).Replace('.', '_') + ".dds");
    tex.SaveAsDDS(ddsPath);

    var bytes = File.ReadAllBytes(ddsPath);
    Console.WriteLine($"  dds bytes={bytes.Length}");
    if (bytes.Length >= 128)
    {
        var magic = System.Text.Encoding.ASCII.GetString(bytes, 0, 4);
        var size = BitConverter.ToInt32(bytes, 4);
        var height = BitConverter.ToInt32(bytes, 12);
        var width = BitConverter.ToInt32(bytes, 16);
        var mips = BitConverter.ToInt32(bytes, 28);
        var pfSize = BitConverter.ToInt32(bytes, 76);
        var pfFlags = BitConverter.ToInt32(bytes, 80);
        var fourcc = System.Text.Encoding.ASCII.GetString(bytes, 84, 4);
        var rgbBitCount = BitConverter.ToInt32(bytes, 88);
        Console.WriteLine($"  magic={magic} hdrSize={size} {width}x{height} mips={mips} pfSize={pfSize} pfFlags=0x{pfFlags:X} fourcc='{fourcc}' rgbBits={rgbBitCount}");
        if (fourcc == "DX10" && bytes.Length >= 148)
        {
            var dxgi = BitConverter.ToInt32(bytes, 128);
            var dim = BitConverter.ToInt32(bytes, 132);
            var misc = BitConverter.ToInt32(bytes, 136);
            var arrSize = BitConverter.ToInt32(bytes, 140);
            var misc2 = BitConverter.ToInt32(bytes, 144);
            Console.WriteLine($"  DX10: dxgiFormat={dxgi} dim={dim} misc=0x{misc:X} arraySize={arrSize} misc2=0x{misc2:X}");
        }
    }

    // WIC acceptance test
    try
    {
        using var bmp = new System.Drawing.Bitmap(ddsPath);
        Console.WriteLine($"  WIC: OK {bmp.Width}x{bmp.Height} pxfmt={bmp.PixelFormat}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  WIC: FAIL {ex.GetType().Name}: {ex.Message}");
    }
}
Console.WriteLine("DONE");
