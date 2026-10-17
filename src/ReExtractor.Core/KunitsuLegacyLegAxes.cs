using System.Numerics;
using ReeLib;

namespace ReExtractor.Core;

/// <summary>Transfer the legacy damage leg frames onto the current protagonist rig.</summary>
internal static class KunitsuLegacyLegAxes
{
    internal static bool IsDamageList(string path) => Path.GetFileName(path.Replace('\\', '/'))
        .Equals("ch0100_damage.motlist.854", StringComparison.OrdinalIgnoreCase);

    internal static AnimationClip Retarget(MotFile motion, int number, string path,
        AnimationClip clip, IReadOnlyList<string>? targetNames)
    {
        if ((int)motion.Header.version != 799 || number is < 0 or > 4 || !IsDamageList(path) ||
            !motion.Name.StartsWith("ch0100_Damage_", StringComparison.OrdinalIgnoreCase) || targetNames == null)
            return clip;
        var bases = new Dictionary<string, Quaternion>(StringComparer.OrdinalIgnoreCase);
        var halfTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        foreach (var (side, sign) in new[] { ("L", 1), ("R", -1) })
        {
            foreach (var joint in new[] { "Shin", "Foot", "Toe" })
                if (!clip.NamedTracks.TryGetValue(side + "_" + joint, out var track) ||
                    track.Rotations is not { Length: > 0 } || !targetNames.Contains(side + "_" + joint))
                    return clip;
            // Legacy shins contain an almost constant opposite Y quarter-turn:
            // q.Y ~= -sign*q.W, whereas the current rig uses an X knee hinge.
            // Require the measured signature as well as the resource identity.
            if (clip.NamedTracks[side + "_Shin"].Rotations!.Any(q =>
                MathF.Abs(q.Y + sign * q.W) > 0.12f * MathF.Sqrt(q.Y * q.Y + q.W * q.W)))
                return clip;
            bases[side + "_Shin"] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, sign * MathF.PI / 2);
            bases[side + "_Foot"] = halfTurn;
            bases[side + "_Toe"] = halfTurn;
        }
        var named = new Dictionary<string, BoneTrack>(clip.NamedTracks, StringComparer.OrdinalIgnoreCase);
        var indexed = new Dictionary<int, BoneTrack>(clip.Tracks);
        foreach (var (name, basis) in bases)
        {
            var parentName = name.EndsWith("_Foot", StringComparison.Ordinal) ? name[..^4] + "Shin"
                : name.EndsWith("_Toe", StringComparison.Ordinal) ? name[..^3] + "Foot" : "";
            var parentBasis = bases.GetValueOrDefault(parentName, Quaternion.Identity);
            var source = named[name];
            // q'local = inverse(Cparent) * qlocal * Cjoint. Propagate the
            // parent change: rotating only the foot would leave the calf/toe
            // frames inconsistent. Preserve target-rig offsets and every key time.
            var track = new BoneTrack {
                IsAdditive = source.IsAdditive, TransTimes = source.TransTimes, Translations = source.Translations,
                ScaleTimes = source.ScaleTimes, Scales = source.Scales, RotTimes = source.RotTimes,
                Rotations = source.Rotations!.Select(q => Quaternion.Normalize(
                    Quaternion.Inverse(parentBasis) * q * basis)).ToArray()
            };
            named[name] = track;
            for (var i = 0; i < targetNames.Count; i++)
                if (string.Equals(targetNames[i], name, StringComparison.OrdinalIgnoreCase)) indexed[i] = track;
        }
        return new AnimationClip { Name = clip.Name, Duration = clip.Duration, FrameRate = clip.FrameRate,
            FrameCount = clip.FrameCount, NamedTracks = named, Tracks = indexed,
            PoseAdaptation = "已转换旧受击的小腿、脚踝和脚趾坐标基准" };
    }
}
