using System;
using System.IO;
using System.Linq;
using ReExtractor.Core;
using ReeLib;
using ReeLib.Mdf;
using ReeLib.Mesh;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

string gameDir = @"E:\Steam\steamapps\common\OnimushaWotS_Demo";
string listFile = @"D:\OnimushaWotS_Demo_Tools\REasy_v0.7.3\resources\data\lists\ONIWOTS_STM.list";
string outDir = @"D:\texdump\mdf2_diag";
Directory.CreateDirectory(outDir);

var pak = new PakService();
pak.AddPaksFromGameDir(gameDir);
pak.LoadListFile(listFile);

// Test ch001_00_20 (the hair mesh that shows rainbow noise)
string meshPath = "natives/stm/art/model/character/ch0/ch001_00/20/ch001_00_20.mesh.251215606";
string mdfPath = "natives/stm/art/model/character/ch0/ch001_00/20/ch001_00_20.mdf2.50";

Console.WriteLine("=== MDF2 Diagnosis ===");

// 1. Parse mdf2 directly
using var mdfStream = pak.ReadFile(mdfPath);
if (mdfStream != null)
{
    var mdf = new MdfFile(new FileHandler(mdfStream, mdfPath));
    if (mdf.Read())
    {
        Console.WriteLine($"MDF2: {mdfPath}");
        Console.WriteLine($"  Materials: {mdf.Materials.Count}");
        for (var m = 0; m < mdf.Materials.Count; m++)
        {
            var mat = mdf.Materials[m];
            Console.WriteLine($"  [{m}] Name='{mat.Name}' Textures={mat.Textures?.Count ?? 0}");
            if (mat.Textures != null)
                foreach (var t in mat.Textures)
                    Console.WriteLine($"      texType='{t.texType}' path='{t.texPath}'");
        }
    }
    else Console.WriteLine("  MDF2 READ FAILED");
}
else Console.WriteLine("  MDF2 NOT FOUND IN PAK");

// 2. Parse mesh to get material names
using var meshStream = pak.ReadFile(meshPath);
if (meshStream != null)
{
    // Use ViewportDataLoader which internally calls MeshService
    Console.WriteLine($"\nMESH: {meshPath}");
    
    // 3. Load via ViewportDataLoader (same path as GUI) and inspect textures
    Console.WriteLine("\n=== ViewportMesh textures ===");
    var vm = ViewportDataLoader.LoadMesh(
        meshStream, meshPath,
        openResource: (path) =>
        {
            var s = pak.ReadFile(path);
            return s ?? null;
        });
    
    Console.WriteLine($"  MaterialNames in mesh: {vm.Textures.Length} texture slots used");
    for (var t = 0; t < vm.Textures.Length; t++)
    {
        var tex = vm.Textures[t];
        // Sample pixels from corners and center
        var p0 = tex.Pixels[0];
        var pC = tex.Pixels[tex.Width / 2 + (tex.Height / 2) * tex.Width];
        var pE = tex.Pixels[^1];
        Console.WriteLine($"  [{t}] '{tex.Name}' {tex.Width}x{tex.Height}");
        Console.WriteLine($"       px[0]=0x{p0:X8} px[center]=0x{pC:X8} px[last]=0x{pE:X8}");
        
        // Dump this texture's pixel array as raw for comparison with PNG
        var rawPath = System.IO.Path.Combine(outDir, $"vm_tex_{t}_{tex.Name}.raw");
        var bytes = new byte[tex.Pixels.Length * 4];
        System.Buffer.BlockCopy(tex.Pixels, 0, bytes, 0, bytes.Length);
        System.IO.File.WriteAllBytes(rawPath, bytes);
        
        // Also save as PNG via ImageSharp for visual comparison
        using var img = new Image<Bgra32>(tex.Width, tex.Height);
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < tex.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < tex.Width; x++)
                {
                    var p = tex.Pixels[y * tex.Width + x];
                    row[x] = new Bgra32(
                        (byte)((p >> 16) & 0xFF),  // R
                        (byte)((p >> 8) & 0xFF),   // G
                        (byte)(p & 0xFF),          // B
                        255);                       // A
                }
            }
        });
        using var fs = System.IO.File.Create(System.IO.Path.Combine(outDir, $"vm_tex_{t}_{tex.Name}.png"));
        img.Save(fs, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        Console.WriteLine($"       dumped to {rawPath} + .png");
    }
    
    // Check face-to-texture mapping
    Console.WriteLine("\n=== Face->Texture mapping (first 20 faces) ===");
    var texCounts = new System.Collections.Generic.Dictionary<int, int>();
    for (var f = 0; f < Math.Min(20, vm.FaceCount); f++)
    {
        var slot = vm.FaceTexture[f];
        Console.WriteLine($"  face[{f}] -> texSlot={slot} {(slot >= 0 && slot < vm.Textures.Length ? vm.Textures[slot].Name : "NONE")}");
        if (!texCounts.ContainsKey(slot)) texCounts[slot] = 0;
        texCounts[slot]++;
    }
    Console.WriteLine("\n  Texture slot usage (all faces):");
    foreach (var kv in texCounts.OrderBy(x => x.Key))
        Console.WriteLine($"    slot[{kv.Key}] = {kv.Value} faces");
}

Console.WriteLine("\nDone.");
