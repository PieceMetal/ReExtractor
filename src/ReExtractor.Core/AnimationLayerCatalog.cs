using System.Text.RegularExpressions;
using System.Text.Json;

namespace ReExtractor.Core;

public sealed record AnimationLayerCandidate(MotionInfo Motion, bool Confirmed, bool PreviewSupported);
public sealed record AnimationLayerProfile(int BaseMaskId, int AdditiveMaskId, string? JointMapPath = null);
public sealed record AnimationLayerPair(string Path, int BaseId, int AdditiveId, int BaseMaskId, int AdditiveMaskId, string? JointMapPath = null);

public static class AnimationLayerCatalog
{
    // Retained audit fixture for offline regression. Live previews pass the
    // definitions resolved from their own PakService, never this snapshot.
    private static readonly AnimationLayerPair[] ConfirmedPairs = ReadPairs();
    private static AnimationLayerPair[] ReadPairs()
    {
        using var stream = typeof(AnimationLayerCatalog).Assembly.GetManifestResourceStream("ReExtractor.AnimationEvidence.re4-preview-pairs.json")
            ?? throw new InvalidDataException("Animation pairing evidence is missing");
        return JsonSerializer.Deserialize<AnimationLayerPair[]>(stream) ?? [];
    }
    private static IEnumerable<AnimationLayerPair> PairsFor(string path, int basis, IReadOnlyList<AnimationLayerPair>? definitions) => (definitions ?? ConfirmedPairs).Where(p =>
        p.BaseId == basis && path.Replace('\\', '/').Equals(p.Path, StringComparison.OrdinalIgnoreCase));

    private static string Name(MotionInfo motion) => motion.DisplayName.Split('（')[0].Trim();
    public static bool IsAdditive(MotionInfo motion) => Regex.IsMatch(Name(motion), @"_add(?:_|$)", RegexOptions.IgnoreCase);

    public static bool IsAdditive(string path, MotionInfo motion, IReadOnlyList<AnimationLayerPair>? definitions = null)
    {
        if (definitions != null)
        {
            if (PairsFor(path, motion.MotionNumber, definitions).Any()) return false;
            if (definitions.Any(p => p.AdditiveId == motion.MotionNumber && p.Path.Equals(path.Replace('\\','/'), StringComparison.OrdinalIgnoreCase))) return true;
        }
        return AnimationUsageCatalog.Get(path, motion).RequiresRawInspection;
    }

    public static bool UsesReloadMask(string path, int basis, int overlay, IReadOnlyList<AnimationLayerPair>? definitions = null) => PairsFor(path, basis, definitions)
        .Any(p => p.AdditiveId == overlay && p.AdditiveMaskId == 14);

    // ch0a0z0_body: additive layer 3 precedes upper-body layer 4 (mask 3).
    // The upper-body FSM plays these base motions on layer 4 and their ADD on 3.
    // Mask changes can be inherited from a parent FSM node. Hold-end uses 10/11.
    public static AnimationLayerProfile? GetProfile(string path, int basis, int? overlay, IReadOnlyList<AnimationLayerPair>? definitions = null)
    {
        // The base state owns the layer order and masks. Choosing a different
        // candidate changes the ADD clip, not that state's composition pipeline.
        // Pair confirmation is separate (AnimationLayerCandidate.Confirmed).
        var candidates = PairsFor(path, basis, definitions).ToArray();
        var paired = candidates.FirstOrDefault(p => p.AdditiveId == overlay) ?? candidates.FirstOrDefault();
        return paired == null ? null : new(paired.BaseMaskId, paired.AdditiveMaskId, paired.JointMapPath);
    }

    public static IReadOnlyList<AnimationLayerCandidate> Find(string path, MotionInfo basis, IReadOnlyList<MotionInfo> motions, IReadOnlyList<AnimationLayerPair>? definitions = null)
    {
        if (IsAdditive(path, basis, definitions)) return Array.Empty<AnimationLayerCandidate>();
        var ids = PairsFor(path, basis.MotionNumber, definitions).Select(p => p.AdditiveId).ToHashSet();
        return motions.Where(m => IsAdditive(path, m, definitions))
            .Where(m => ids.Contains(m.MotionNumber))
            .Select(m => new AnimationLayerCandidate(m, true,
                GetProfile(path, basis.MotionNumber, m.MotionNumber, definitions) != null))
            .ToArray();
    }
}
