#:project ../src/ReExtractor.Core/ReExtractor.Core.csproj

using System.Numerics;
using ReExtractor.Core;
using ReeLib;

// Hypothesis: LoadMesh keeps EVERY viscon MeshGroup; if groups contain co-planar duplicate
// geometry (alternate vis states), rendering all of them z-fights -> per-pixel speckle.
// This probe measures per-group composition + exact duplicate faces ACROSS groups.
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
Console.WriteLine($"LODs={meshData.LODs.Count} materials={mesh.MaterialNames.Count}");
for (var i = 0; i < mesh.MaterialNames.Count; i++) Console.WriteLine($"  mat[{i}] {mesh.MaterialNames[i]}");

for (var li = 0; li < Math.Min(2, meshData.LODs.Count); li++)
{
    var lod = meshData.LODs[li];
    Console.WriteLine($"--- LOD[{li}] groups={lod.MeshGroups.Count}");
    // face key -> set of group ids containing it
    var faceOwners = new Dictionary<ulong, HashSet<int>>();
    var groupFaces = new Dictionary<int, int>();
    var groupVerts = new Dictionary<int, int>();
    foreach (var group in lod.MeshGroups)
    {
        var gid = group.groupId;
        foreach (var sub in group.Submeshes)
        {
            var pos = sub.Positions;
            for (var i = 0; i + 2 < sub.indicesCount; i += 3)
            {
                int i0 = GetIdx(sub, i), i1 = GetIdx(sub, i + 1), i2 = GetIdx(sub, i + 2);
                if ((uint)i0 >= (uint)pos.Length || (uint)i1 >= (uint)pos.Length || (uint)i2 >= (uint)pos.Length) continue;
                // quantize positions to 1e-4 to make exact-key matching robust
                var q = new[]
                {
                    Quant(pos[i0]), Quant(pos[i1]), Quant(pos[i2])
                };
                Array.Sort(q);
                var key = ((ulong)q[0] << 42) | ((ulong)q[1] << 21) | (ulong)q[2];
                if (!faceOwners.TryGetValue(key, out var set)) faceOwners[key] = set = new HashSet<int>();
                set.Add(gid);
                groupFaces[gid] = groupFaces.GetValueOrDefault(gid) + 1;
            }
            groupVerts[gid] = groupVerts.GetValueOrDefault(gid) + pos.Length;
        }
    }
    Console.WriteLine("groupId | faces | verts (approx per group)");
    foreach (var gid in groupFaces.Keys.OrderBy(k => k))
        Console.WriteLine($"  {gid,3} | {groupFaces[gid],6} | {groupVerts[gid],6}");

    var dupAcrossGroups = faceOwners.Count(kv => kv.Value.Count > 1);
    var total = faceOwners.Count;
    Console.WriteLine($"unique face-position keys: {total}; keys present in >1 group: {dupAcrossGroups} ({(total > 0 ? 100.0 * dupAcrossGroups / total : 0):F1}%)");

    // which group pairs share the most duplicated faces?
    var pairCount = new Dictionary<(int, int), int>();
    foreach (var set in faceOwners.Values)
    {
        if (set.Count < 2) continue;
        var arr = set.OrderBy(x => x).ToArray();
        for (var a = 0; a < arr.Length; a++)
            for (var b = a + 1; b < arr.Length; b++)
                pairCount[(arr[a], arr[b])] = pairCount.GetValueOrDefault((arr[a], arr[b])) + 1;
    }
    Console.WriteLine("top duplicated group pairs:");
    foreach (var kv in pairCount.OrderByDescending(kv => kv.Value).Take(10))
        Console.WriteLine($"  group {kv.Key.Item1} x {kv.Key.Item2}: {kv.Value} shared faces");
}
Console.WriteLine("DONE");

static int GetIdx(ReeLib.Mesh.Submesh sub, int i)
    => sub.Buffer.IntegerFaces != null ? sub.IntegerIndices[i] : sub.Indices[i];

static int Quant(Vector3 v)
{
    // pack quantized xyz into 21 bits (7 bits each, 1cm grid around origin offset)
    int x = (int)MathF.Round(v.X * 100) + 64;
    int y = (int)MathF.Round(v.Y * 100) + 64;
    int z = (int)MathF.Round(v.Z * 100) + 64;
    x = Math.Clamp(x, 0, 127); y = Math.Clamp(y, 0, 127); z = Math.Clamp(z, 0, 127);
    return (x << 14) | (y << 7) | z;
}
