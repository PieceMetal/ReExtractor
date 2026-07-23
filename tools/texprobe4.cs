#:project ../src/ReExtractor.Core/ReExtractor.Core.csproj

using ReExtractor.Core;
using ReeLib;

// groupId -> materials used (helps decide which groups are alternate states of the same part)
var gameDir = @"E:\Steam\steamapps\common\OnimushaWotS_Demo";
var listPath = @"D:\OnimushaWotS_Demo_Tools\REasy_v0.7.3\resources\data\lists\ONIWOTS_STM.list";
var meshPath = "natives/stm/art/model/character/ch0/ch001_00/00/ch001_00_00.mesh.251215606";

var pak = new PakService();
pak.AddPaksFromGameDir(gameDir);
pak.LoadListFile(listPath);

using var ms = pak.ReadFile(meshPath);
var mesh = new MeshFile(new FileHandler(ms, meshPath));
if (!mesh.Read()) { Console.WriteLine("mesh parse FAILED"); return; }
var meshData = mesh.MeshData!;

var lod = meshData.LODs[Math.Min(1, meshData.LODs.Count - 1)];
Console.WriteLine($"LOD[1] groups={lod.MeshGroups.Count}");
foreach (var group in lod.MeshGroups.OrderBy(g => g.groupId))
{
    var mats = group.Submeshes.Select(s => s.materialIndex).Distinct().OrderBy(x => x)
        .Select(mi => mi < mesh.MaterialNames.Count ? $"{mi}:{mesh.MaterialNames[mi]}" : $"{mi}:?").ToList();
    var faces = group.Submeshes.Sum(s => s.indicesCount / 3);
    Console.WriteLine($"group {group.groupId,3} faces={faces,5} mats=[{string.Join(", ", mats)}]");
}
Console.WriteLine("DONE");
