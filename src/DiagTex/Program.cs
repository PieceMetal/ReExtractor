using ReExtractor.Core;
using ReeLib;

string gameDir = @"E:\Steam\steamapps\common\OnimushaWotS_Demo";
string listFile = @"D:\OnimushaWotS_Demo_Tools\REasy_v0.7.3\resources\data\lists\ONIWOTS_STM.list";

var pak = new PakService();
pak.AddPaksFromGameDir(gameDir);
pak.LoadListFile(listFile);

// Test BC7 texture
var texPath = "natives/stm/art/model/common/textures/human/player/pl_common_body_mat_albd.tex.251111100";
using var s = pak.ReadFile(texPath);
var tex = new TexFile(new FileHandler(s, texPath));
tex.Read();

Console.WriteLine($"Format: {tex.Header.format} Size: {tex.Header.width}x{tex.Header.height}");

using var iter = tex.CreateIterator(0, 0);
var mip = new ReeLib.DDS.DDSFile.MipMapLevelData();
iter.Next(ref mip);
var raw = mip.data.ToArray();
Console.WriteLine($"Raw data: {raw.Length} bytes (expected {((256+3)/4)*((256+3)/4)*16} for BC7 256x256)");

// Dump first few BC7 blocks (each 16 bytes) with mode analysis
Console.WriteLine("\nFirst 10 BC7 blocks (mode in bits [0-1] of byte 0, extended modes):");
for (int i = 0; i < Math.Min(10, raw.Length / 16); i++)
{
    var block = raw.AsSpan(i * 16, 16);
    byte b0 = block[0];
    // BC7 mode extraction (simplified)
    var mode = b0 & 0x1F;
    // Check if any mode bits are set beyond valid range
    var modeStr = mode switch {
        0 => "Mode 0 (3-subset, 4bpp, color only)",
        1 => "Mode 1 (2-subset, 6bpp, color only)",
        2 => "Mode 2 (3-subset, 5bpp)",
        3 => "Mode 3 (2-subset, 7bpp)",
        4 => "Mode 4 (1-subset, 5bpp + 6alpha)",
        5 => "Mode 5 (1-subset, 7bpp + 8alpha, 2-part)",
        6 => "Mode 6 (1-subset, 8bpp + 7alpha)",
        <= 13 => $"Mode {mode}",
        _ => $"INVALID MODE {mode}!!!"
    };
    Console.WriteLine($"  Block[{i}] b0=0x{b0:X2} mode={mode} ({modeStr}) hex={BitConverter.ToString(block.ToArray())}");
}

// Also dump BC1 texture for comparison
Console.WriteLine("\n--- BC1 comparison ---");
var bc1Path = "natives/stm/art/model/character/ch0/ch001_00/20/textures/ch001_00_20_albd.tex.251111100";
using var s2 = pak.ReadFile(bc1Path);
var tex2 = new TexFile(new FileHandler(s2, bc1Path));
tex2.Read();
using var iter2 = tex2.CreateIterator(0, 0);
var mip2 = new ReeLib.DDS.DDSFile.MipMapLevelData();
iter2.Next(ref mip2);
var raw2 = mip2.data.ToArray();
Console.WriteLine($"BC1 Raw: {raw2.Length} bytes");
Console.WriteLine($"BC1 first block hex: {BitConverter.ToString(raw2[..Math.Min(16, raw2.Length)])}");
Console.WriteLine($"\nDone. Data looks {(raw.All(b => b == 0) ? "ALL ZERO (bad!)" : "non-zero (probably OK)")}");
