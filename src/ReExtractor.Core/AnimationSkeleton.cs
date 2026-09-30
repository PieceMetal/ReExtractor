namespace ReExtractor.Core;

/// <summary>Choose an authoritative hierarchy for independently skinned character parts.</summary>
public static class AnimationSkeleton
{
    public static ViewportMesh? SelectDriver(IReadOnlyList<ViewportMesh> meshes)
    {
        // Face rigs can have more skin joints than a complete body. Prefer the
        // rig that supplies the missing parents of other parts' source roots.
        // Ignore appended MOT helpers: they do not repair a source bind hierarchy.
        int ParentCoverage(ViewportMesh candidate)
        {
            var score = 0;
            foreach (var part in meshes)
            {
                if (ReferenceEquals(part, candidate)) continue;
                foreach (var root in part.Bones)
                {
                    if (root.IsMotionHelper || (root.ParentIndex >= 0
                        && !part.Bones[root.ParentIndex].IsMotionHelper)) continue;
                    var counterpart = Array.Find(candidate.Bones, b => !b.IsMotionHelper
                        && b.Name.Equals(root.Name, StringComparison.OrdinalIgnoreCase));
                    if (counterpart?.ParentIndex >= 0 && !candidate.Bones[counterpart.ParentIndex].IsMotionHelper)
                        score++;
                }
            }
            return score;
        }
        return meshes.OrderByDescending(ParentCoverage)
            .ThenByDescending(m => m.DeformToBone.Length).ThenByDescending(m => m.VertexCount).FirstOrDefault();
    }

    public static ViewportBone[] PreviewBones(IReadOnlyList<ViewportMesh> meshes)
    {
        var driver = SelectDriver(meshes);
        if (driver == null) return [];
        var selected = new Dictionary<string, (ViewportMesh Mesh, ViewportBone Bone)>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in new[] { driver }.Concat(meshes.Where(m => !ReferenceEquals(m, driver))))
            foreach (var bone in mesh.Bones)
                if (!selected.TryGetValue(bone.Name, out var prior) || (prior.Bone.IsMotionHelper && !bone.IsMotionHelper))
                    selected[bone.Name] = (mesh, bone);
        var indices = selected.Keys.Select((name, i) => (name, i))
            .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);
        return selected.Values.Select(item => new ViewportBone {
            Name = item.Bone.Name, IsMotionHelper = item.Bone.IsMotionHelper,
            LocalBind = item.Bone.LocalBind, InverseGlobalBind = item.Bone.InverseGlobalBind,
            ParentIndex = item.Bone.ParentIndex >= 0
                ? indices[item.Mesh.Bones[item.Bone.ParentIndex].Name] : -1
        }).ToArray();
    }
}
