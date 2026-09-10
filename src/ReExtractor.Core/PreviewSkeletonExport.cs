using System.Numerics;

namespace ReExtractor.Core;

/// <summary>Shares the preview driver's pose without discarding each part's inverse bind.</summary>
internal static class PreviewSkeletonExport
{
    public static ViewportMesh Merge(IReadOnlyList<ViewportMesh> meshes)
    {
        if (ViewportMesh.TryGetSkeletonUnionCompatibility(meshes, out _)) return ViewportMesh.Merge(meshes);
        var driver = meshes.OrderByDescending(m => m.DeformToBone.Length)
            .ThenByDescending(m => m.VertexCount).First();
        var driverNames = driver.Bones.Select((b, i) => (b.Name, i))
            .ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = meshes.SelectMany(m => m.Bones).Select(b => b.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Unique(string stem)
        {
            while (!used.Add(stem)) stem += "_";
            return stem;
        }
        var prepared = new List<ViewportMesh>();
        for (var part = 0; part < meshes.Count; part++)
        {
            var mesh = meshes[part];
            var bones = driver.Bones.ToList();
            var map = new int[mesh.Bones.Length];
            for (var b = 0; b < map.Length; b++)
            {
                var source = mesh.Bones[b];
                if (driverNames.TryGetValue(source.Name, out var common)) map[b] = common;
                else
                {
                    map[b] = bones.Count;
                    var name = Unique($"__part{part}_{source.Name}");
                    aliases[name] = source.Name;
                    bones.Add(new ViewportBone { Name = name, ParentIndex = -1,
                        LocalBind = source.LocalBind, InverseGlobalBind = source.InverseGlobalBind });
                }
            }
            for (var b = 0; b < map.Length; b++)
                if (map[b] >= driver.Bones.Length)
                    bones[map[b]].ParentIndex = mesh.Bones[b].ParentIndex >= 0
                        ? map[mesh.Bones[b].ParentIndex] : -1;
            var deform = new int[mesh.DeformToBone.Length];
            var globals = new Matrix4x4[bones.Count];
            var computed = new bool[bones.Count];
            Matrix4x4 Global(int index)
            {
                if (computed[index]) return globals[index];
                var bone = bones[index];
                globals[index] = bone.ParentIndex >= 0 ? bone.LocalBind * Global(bone.ParentIndex) : bone.LocalBind;
                computed[index] = true;
                return globals[index];
            }
            for (var j = 0; j < deform.Length; j++)
            {
                var source = mesh.DeformToBone[j];
                var name = Unique($"__skin{part}_{j}_{mesh.Bones[source].Name}");
                aliases[name] = "";
                deform[j] = bones.Count;
                if (!Matrix4x4.Invert(mesh.Bones[source].InverseGlobalBind, out var sourceBind) ||
                    !Matrix4x4.Invert(Global(map[source]), out var inverseParent))
                    throw new InvalidDataException($"无法导出不可逆的骨骼绑定：{mesh.Bones[source].Name}");
                var localBind = sourceBind * inverseParent;
                // These are affine transforms; inversion can introduce rounding in column 4.
                localBind.M14 = localBind.M24 = localBind.M34 = 0; localBind.M44 = 1;
                bones.Add(new ViewportBone { Name = name, ParentIndex = map[source],
                    // A consistent rest pose lets Blender retain the source inverse bind.
                    // Animation drives this passive joint to identity, matching preview sharing.
                    LocalBind = localBind,
                    InverseGlobalBind = mesh.Bones[source].InverseGlobalBind });
            }
            var copy = mesh.WithTransform(Matrix4x4.Identity);
            copy.Bones = bones.ToArray();
            copy.DeformToBone = deform;
            prepared.Add(copy);
        }
        var merged = ViewportMesh.Merge(prepared);
        merged.AnimationBoneAliases = aliases;
        return merged;
    }
}
