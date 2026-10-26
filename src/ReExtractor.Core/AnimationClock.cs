using ReeLib;

namespace ReExtractor.Core;

/// <summary>Authored hold/loop time is independent of the last changing key.</summary>
internal static class AnimationClock
{
    internal static float Duration(MotFile mot)
    {
        var frames = mot.Header.frameCount;
        if (!float.IsFinite(frames) || frames < 0)
            throw new InvalidDataException("动画声明的帧数无效：" + mot.Name);
        return frames / (mot.Header.FrameRate > 0 ? mot.Header.FrameRate : 60f);
    }

    internal static AnimationClip Preserve(AnimationClip clip, MotFile mot)
    {
        clip.Duration = Math.Max(clip.Duration, Duration(mot));
        clip.FrameCount = (int)MathF.Ceiling(clip.Duration * clip.FrameRate);
        // Add a held endpoint without changing source arrays. Exporters determine
        // duration from sampler keys, so metadata alone does not preserve a hold.
        foreach (var track in clip.Tracks.Values.Concat(clip.NamedTracks.Values).Distinct())
        {
            Extend(ref track.TransTimes, ref track.Translations, clip.Duration);
            Extend(ref track.RotTimes, ref track.Rotations, clip.Duration);
            Extend(ref track.ScaleTimes, ref track.Scales, clip.Duration);
        }
        return clip;
    }

    private static void Extend<T>(ref float[]? times, ref T[]? values, float duration)
    {
        if (times is not { Length: > 0 } || values is not { Length: > 0 }) return;
        if (times.Length != values.Length) throw new InvalidDataException("动画采样时间与数值数量不一致");
        if (times[^1] >= duration) return;
        times = [.. times, duration];
        values = [.. values, values[^1]];
    }

    internal static void Extend<T>(IDictionary<float, T> keys, float duration)
    {
        if (keys.Count == 0) return;
        var last = keys.Keys.Max();
        if (last < duration) keys[duration] = keys[last];
    }
}
