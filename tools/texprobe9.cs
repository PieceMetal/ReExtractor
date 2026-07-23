#:project ../src/ReExtractor.Core/ReExtractor.Core.csproj

using System.Numerics;
using ReExtractor.Core;
using ReeLib;

// Bit-exact duplicate-face check across viscon groups (no quantization tolerance):
// decides whether rendering ALL groups is safe (no exact coplanar shells) or filtering is needed.
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

for (var li = 0; li < Math.Min(2, meshData.LODs.Count); li++)
{
    var lod = meshData.LODs[li];
    // face key: 3 sorted vertices, each vertex hashed by EXACT float bits of position
    var faceOwners = new Dictionary<string, int>();   // bit-exact key -> first group seen
    var pairCount = new Dictionary<(int, int), int>();
    var groupFaces = new Dictionary<int, int>();
    var exactDupTotal = 0;

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
                var keys = new[]
                {
                    BitKey(pos[i0]), BitKey(pos[i1]), BitKey(pos[i2])
                };
                Array.Sort(keys, StringComparer.Ordinal);
                var h = string.Join("|", keys);
                groupFaces[gid] = groupFaces.GetValueOrDefault(gid) + 1;
                if (faceOwners.TryGetValue(h, out var owner) && owner != gid)
                {
                    var pr = owner < gid ? (owner, (int)gid) : ((int)gid, owner);
                    pairCount[pr] = pairCount.GetValueOrDefault(pr) + 1;
                    exactDupTotal++;
                }
                else faceOwners[h] = gid;
            }
        }
    }
    Console.WriteLine($"--- LOD[{li}] exact-duplicate faces across different groups: {exactDupTotal}");
    foreach (var kv in pairCount.OrderByDescending(kv => kv.Value).Take(8))
        Console.WriteLine($"    group {kv.Key.Item1} x {kv.Key.Item2}: {kv.Value} exact-shared faces");
    Console.WriteLine("    groups: " + string.Join(", ", groupFaces.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}({kv.Value}f)")));
}
Console.WriteLine("DONE");

static int GetIdx(ReeLib.Mesh.Submesh sub, int i)
    => sub.Buffer.IntegerFaces != null ? sub.IntegerIndices[i] : sub.Indices[i];

static string BitKey(Vector3 v)
    => $"{BitConverter.SingleToUInt32Bits(v.X):X8},{BitConverter.SingleToUInt32Bits(v.Y):X8},{BitConverter.SingleToUInt32Bits(v.Z):X8}";
