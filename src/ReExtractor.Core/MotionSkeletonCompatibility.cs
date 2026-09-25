using ReeLib;
using ReeLib.Common;
using ReeLib.Mesh;
using ReeLib.Mot;

namespace ReExtractor.Core;

/// <summary>RE RT split body/face/weapon rigs can share names but use different local spaces.</summary>
internal static class MotionSkeletonCompatibility
{
    internal static void Validate(MotFile motion, ViewportMesh mesh)
        => Validate(motion, mesh.Bones.Where(b => !b.IsMotionHelper).Select(b =>
            (b.Name, Parent: b.ParentIndex >= 0 && b.ParentIndex < mesh.Bones.Length
                ? mesh.Bones[b.ParentIndex].Name : null)));

    internal static void Validate(MotFile motion, MeshBoneHierarchy? hierarchy)
    {
        if (hierarchy == null) return;
        Validate(motion, hierarchy.Bones.Select(b => (b.name,
            Parent: b.parentIndex >= 0 && b.parentIndex < hierarchy.Bones.Count && b.parentIndex != b.index
                ? hierarchy.Bones[b.parentIndex].name : null)));
    }

    private static void Validate(MotFile motion, IEnumerable<(string Name, string? Parent)> targetBones)
    {
        // Other MOT generations have separate retargeting conventions. This guard
        // covers the RE2/RE3 RT rigs verified against the original MOT bone table.
        if (motion.Header.version != MotVersion.RE_RT || motion.Bones.Count == 0) return;
        var target = targetBones.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Parent, StringComparer.OrdinalIgnoreCase);
        var animatedHashes = motion.BoneClips.Select(c => c.ClipHeader.boneHash).ToHashSet();
        var shared = motion.Bones.Where(b => target.ContainsKey(b.boneName) &&
            animatedHashes.Contains(b.boneHash != 0 ? b.boneHash : MurMur3HashUtils.GetHash(b.boneName))).ToArray();
        foreach (var bone in shared)
        {
            var sourceParent = bone.Parent?.boneName;
            var targetParent = target[bone.boneName];
            if (!string.Equals(sourceParent, targetParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"动画与模型骨架不兼容：{bone.boneName} 在动画中的父节点为 {sourceParent ?? "<根节点>"}，模型中为 {targetParent ?? "<根节点>"}。分离的面部动作需要配套身体姿态与骨架转换，暂不支持直接播放/导出。请使用匹配的身体动作。");
        }
        // A root-only track is valid when its authored skeleton still matches
        // the character (for example locomotion). A weapon rig sharing only the
        // generic name "root" is not evidence of character compatibility.
        if (shared.Length == 0 || (target.Count > 1 &&
            !motion.Bones.Any(b => b.Parent != null && target.ContainsKey(b.boneName))))
            throw new InvalidDataException("动画与模型只匹配根节点或没有匹配骨骼，无法确认是同一角色骨架。请为武器等独立动作加载对应模型。");
    }
}
