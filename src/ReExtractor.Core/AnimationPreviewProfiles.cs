using ReeLib;
using ReeLib.Common;

namespace ReExtractor.Core;

public static class AnimationPreviewProfiles
{
    public static IReadOnlyDictionary<string, PreviewJointWeight> ReadRe4ReloadMask(
        PakService pak, IReadOnlyList<ViewportBone> bones)
        => ReadRe4Mask(pak, bones, 14);

    public static IReadOnlyDictionary<string, PreviewJointWeight> ReadRe4Mask(
        PakService pak, IReadOnlyList<ViewportBone> bones, int maskId, string? jointMapPath = null)
    {
        var path = jointMapPath ?? "natives/stm/_chainsaw/appsystem/character/ch0common/jointmap/ch0commonjointmap.jmap.19";
        using var stream = pak.ReadFile(path);
        using var file = new JmapFile(new FileHandler(stream, path));
        file.Read();
        var group = file.MaskGroups.SingleOrDefault(g => g.groupId == maskId)
            ?? throw new InvalidDataException($"缺少 RE4 骨骼遮罩{maskId}，不能使用无遮罩叠加替代。");
        var entries = group.Masks.ToDictionary(m => m.jointHash);
        var result = new Dictionary<string, PreviewJointWeight>(StringComparer.OrdinalIgnoreCase);
        foreach (var bone in bones)
            if (entries.TryGetValue(MurMur3HashUtils.GetHash(bone.Name), out var entry))
                result[bone.Name] = new PreviewJointWeight(
                    (entry.mask & 1) != 0 ? entry.weight : 0,
                    (entry.mask & 2) != 0 ? entry.weight : 0);
        return result;
    }
}
