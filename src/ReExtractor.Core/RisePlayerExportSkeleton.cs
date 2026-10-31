using System.Text.RegularExpressions;

namespace ReExtractor.Core;

/// <summary>Builds Rise player exports from source binds, independently of preview history.</summary>
public static class RisePlayerExportSkeleton
{
    private static readonly Regex PlayerPath = new(
        @"^natives/stm/player/mod/(?<gender>[mf])/.+\.mesh\.2109148288$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool Supports(IReadOnlyList<string> paths)
        => paths.Count > 0 && paths.All(path => PlayerPath.IsMatch(path.Replace('\\', '/'))) &&
           paths.Select(path => PlayerPath.Match(path.Replace('\\', '/')).Groups["gender"].Value.ToLowerInvariant())
               .Distinct().Count() == 1;

    public static (ViewportMesh Mesh, IReadOnlySet<int> VisibleGroups) Build(
        IReadOnlyList<string> paths,
        IReadOnlyList<(ViewportMesh Mesh, IReadOnlySet<int> VisibleGroups)> previewModels,
        Func<string, Stream?> openResource,
        int lodIndex = 0,
        bool loadTextures = false,
        MaterialResolver? materialResolver = null)
    {
        if (!Supports(paths)) throw new ArgumentException("需要同一性别的怪猎崛起玩家装备", nameof(paths));
        if (previewModels.Count != paths.Count && previewModels.Count != 1)
            throw new InvalidDataException("源装备与预览可见分组不匹配，无法安全导出");

        var gender = PlayerPath.Match(paths[0].Replace('\\', '/')).Groups["gender"].Value.ToLowerInvariant();
        var referencePath = $"natives/stm/player/mod/{gender}/bone/{gender}_shadow.mesh.2109148288";
        using var referenceStream = openResource(referencePath)
            ?? throw new FileNotFoundException("导出需要完整的怪猎玩家骨架，请加载对应游戏资源：" + referencePath);
        var reference = ViewportDataLoader.LoadMesh(referenceStream, referencePath, 0, loadTextures: false);
        var models = new List<(ViewportMesh Mesh, IReadOnlySet<int> VisibleGroups)>();
        var groupOffset = 0;
        for (var i = 0; i < paths.Count; i++)
        {
            using var stream = openResource(paths[i]) ?? throw new FileNotFoundException(paths[i]);
            var mesh = ViewportDataLoader.LoadMesh(stream, paths[i], lodIndex, openResource, loadTextures, materialResolver);
            RestoreFromReference(mesh, reference);
            // A compatible preview may already have merged its groups into sequential keys.
            IReadOnlySet<int> visible = previewModels.Count == paths.Count
                ? previewModels[i].VisibleGroups
                : mesh.Groups.Where((group, index) => previewModels[0].VisibleGroups.Contains(groupOffset + index))
                    .Select(group => group.Key).ToHashSet();
            groupOffset += mesh.Groups.Length;
            models.Add((mesh, visible));
        }
        // Even a single part needs passive skin joints: restoring parents must not
        // let the GLB -> FBX bridge replace its original inverse bind matrices.
        // Stable part ordering also stabilizes the per-part joint names when the
        // same outfit is loaded in a different order before animation export.
        var ordered = models.Select((model, i) => (Path: paths[i].Replace('\\', '/'), Model: model))
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(item => item.Model).ToArray();
        var prepared = new ViewportExportService().BuildMergedExportModel(ordered, preservePartBindings: true);
        prepared.Mesh.HasFixedExportSkeleton = true;
        return prepared;
    }

    private static void RestoreFromReference(ViewportMesh mesh, ViewportMesh reference)
    {
        var original = mesh.Bones;
        var bones = original.ToList();
        var byName = bones.Select((bone, index) => (bone.Name, index))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);
        var referenceByName = reference.Bones.ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        int Ensure(string name)
        {
            if (byName.TryGetValue(name, out var existing)) return existing;
            var source = referenceByName[name];
            var parent = source.ParentIndex < 0 ? -1 : Ensure(reference.Bones[source.ParentIndex].Name);
            var index = bones.Count;
            bones.Add(new ViewportBone { Name = source.Name, ParentIndex = parent,
                LocalBind = source.LocalBind, InverseGlobalBind = source.InverseGlobalBind });
            byName.Add(name, index);
            return index;
        }

        // Include the same complete rest skeleton even when no motion has been selected.
        foreach (var bone in reference.Bones) Ensure(bone.Name);
        for (var i = 0; i < original.Length; i++)
        {
            var bone = original[i];
            if (!referenceByName.TryGetValue(bone.Name, out var source)) continue;
            var parentName = bone.ParentIndex < 0 ? null : original[bone.ParentIndex].Name;
            var sourceParent = source.ParentIndex < 0 ? null : reference.Bones[source.ParentIndex].Name;
            if (string.Equals(parentName, sourceParent, StringComparison.OrdinalIgnoreCase)) continue;
            // Only restore collapsed ancestor paths, never reinterpret a different authored rig.
            var ancestor = source.ParentIndex;
            while (ancestor >= 0 && !string.Equals(reference.Bones[ancestor].Name, parentName, StringComparison.OrdinalIgnoreCase))
                ancestor = reference.Bones[ancestor].ParentIndex;
            if (ancestor < 0 && parentName != null)
                throw new InvalidDataException($"装备骨骼 {bone.Name} 的父节点 {parentName} 与完整玩家骨架不兼容");
            bones[i] = new ViewportBone { Name = bone.Name,
                ParentIndex = sourceParent == null ? -1 : byName[sourceParent],
                LocalBind = source.LocalBind, InverseGlobalBind = bone.InverseGlobalBind };
        }
        mesh.Bones = bones.ToArray();
    }
}
