using System.Text.Json;

namespace ReExtractor.Core;

public enum AnimationUsageKind { Unknown, AdditiveHint, Additive, Override, ContextDependent }
public sealed record AnimationUsage(AnimationUsageKind Kind, string[] Evidence)
{
    public bool RequiresRawInspection => Kind is AnimationUsageKind.Additive or AnimationUsageKind.AdditiveHint;
}

/// <summary>
/// Playback usage is distinct from clip naming, completeness and pairing.
/// Evidence is keyed by resource, motion ID AND name; bank IDs alone are not global IDs.
/// The registry records possible uses in the audited player context, not a full FSM emulator.
/// </summary>
public static class AnimationUsageCatalog
{
    private sealed record Entry(string Path, int Id, string Name, string Kind, string[] Evidence);
    private static readonly IReadOnlyDictionary<string, AnimationUsage> Entries = Load();
    private static string Key(string path, int id, string name) => $"{path.Replace('\\','/')}|{id}|{name}";
    private static IReadOnlyDictionary<string, AnimationUsage> Load()
    {
        using var stream = typeof(AnimationUsageCatalog).Assembly.GetManifestResourceStream("ReExtractor.AnimationEvidence.re4-player-usage.json")
            ?? throw new InvalidDataException("Animation usage evidence is missing");
        var rows = JsonSerializer.Deserialize<Entry[]>(stream) ?? [];
        return rows.ToDictionary(e => Key(e.Path, e.Id, e.Name),
            e => new AnimationUsage(Enum.Parse<AnimationUsageKind>(e.Kind), e.Evidence), StringComparer.OrdinalIgnoreCase);
    }
    public static AnimationUsage Get(string path, MotionInfo motion, IReadOnlyList<AnimationLayerPair>? definitions = null,
        IReadOnlyList<ResolvedAnimationUsage>? usages = null)
    {
        var name = motion.DisplayName.Split('（')[0].Trim();
        var liveUsage = usages?.SingleOrDefault(u => Key(u.Path, u.Id, u.Name).Equals(
            Key(path, motion.MotionNumber, name), StringComparison.OrdinalIgnoreCase));
        if (liveUsage != null) return liveUsage.Usage;
        if (definitions != null)
        {
            var uses = definitions.Where(p => p.Path.Equals(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                && p.AdditiveId == motion.MotionNumber).ToArray();
            if (uses.Length > 0)
                return new(AnimationUsageKind.Additive, uses.Select(p => p.Context ?? "Loaded game layer configuration").Distinct().ToArray());
        }
        if (Entries.TryGetValue(Key(path, motion.MotionNumber, name), out var usage)) return usage;
        // Retain the separately audited shoot variants. No extrapolation to other banks/lists.
        if (path.Replace('\\','/').Equals("natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663", StringComparison.OrdinalIgnoreCase)
            && motion.MotionNumber is 500 or 501 or 502 && name.EndsWith("_shoot", StringComparison.OrdinalIgnoreCase))
            return new(AnimationUsageKind.Additive, ["RE4 wp4000H shoot layer audit"]);
        if (AnimationLayerCatalog.IsAdditive(motion)) return new(AnimationUsageKind.AdditiveHint, ["Name contains _add; layer usage not verified"]);
        return new(AnimationUsageKind.Unknown, []);
    }
}
