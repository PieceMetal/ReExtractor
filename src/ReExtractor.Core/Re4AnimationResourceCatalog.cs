using ReeLib;

namespace ReExtractor.Core;

/// <summary>Exact RE4 references and inherited motion sources for a loaded archive.
/// Resolution never falls back to a similarly named file or a global motion ID.</summary>
public sealed class Re4AnimationResourceCatalog(PakService pak)
{
    public sealed record Motion(int Index, int Id, string? Name, bool Embedded, bool Empty, int Flags, bool HasScale,
        float Duration, int FrameRate, bool IsNeutralDelta);
    public sealed record MotionList(string Path, string? BasePath, Motion[] Motions);
    private readonly string[] paths = pak.EnumerateFiles().Select(f => f.Path).ToArray();
    private readonly Dictionary<string, MotionList> lists = new(StringComparer.OrdinalIgnoreCase);

    public string Resource(string reference)
    {
        var prefix = reference.Replace('\\', '/');
        if (!prefix.StartsWith("natives/stm/", StringComparison.OrdinalIgnoreCase)) prefix = "natives/stm/" + prefix;
        var matches = paths.Where(p => p.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || (p.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(p[(prefix.Length + 1)..], out _))).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"资源引用无法唯一解析：{reference}");
    }

    public MotionList List(string path)
    {
        if (lists.TryGetValue(path, out var cached)) return cached;
        using var stream = pak.ReadFile(path);
        using var list = new MotlistFile(new FileHandler(stream, path));
        if (!list.Read()) throw new InvalidDataException("动作列表读取失败：" + path);
        var result = new MotionList(path, list.Header.BaseMotListPath, list.Motions.Select((m, i) => new Motion(i,
            m.motNumber, m.MotFile?.Name, m.MotFile is MotFile, m.MotFile == null, (int)m.flags,
            m.MotFile is MotFile mot && mot.BoneClips.Any(c => c.HasScale),
            m.MotFile is MotFile timed ? timed.Header.frameCount / Math.Max(1, (int)timed.Header.FrameRate) : 0,
            m.MotFile is MotFile rated ? (int)rated.Header.FrameRate : 60,
            m.MotFile is MotFile delta && delta.BoneClips.Count > 0 && delta.BoneClips.All(c => !c.HasScale
                && (!c.HasTranslation || (c.Translation?.translations ?? []).All(v => v.LengthSquared() <= 1e-12f))
                && (!c.HasRotation || (c.Rotation?.rotations ?? []).All(q => float.IsFinite(q.LengthSquared())
                    && q.LengthSquared() > 0 && Math.Abs(q.W) / q.Length() >= 1 - 1e-7f))))).ToArray());
        lists[path] = result;
        return result;
    }

    public ResolvedAnimationMotion ResolveMotion(string requestedPath, int id)
    {
        var requested = List(requestedPath).Motions.Single(m => m.Id == id);
        var visited = new List<string>();
        var current = requestedPath;
        while (true)
        {
            if (visited.Contains(current, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("动作继承存在循环：" + string.Join(" → ", visited.Append(current)));
            visited.Add(current);
            var list = List(current);
            var motion = list.Motions.Single(m => m.Id == id);
            if (motion.Embedded)
            {
                if (motion.Flags != 0) throw new InvalidDataException("动作引用使用了不支持的标志：" + current + "#" + id);
                return new(requestedPath, new(requested.Index,
                    $"{motion.Name}（编号 {id}{(visited.Count > 1 ? "，继承" : "")}）", id),
                    current, motion.Index, visited.ToArray(), motion.Duration, motion.FrameRate);
            }
            // Only an explicit empty slot can inherit; a tree/link is never
            // silently replaced by a same-ID animation in the parent list.
            // The audited RE4 lists encode unpopulated inherited slots with
            // the all-bits-set byte sentinel, not a mirrored/linked MOT.
            if (!motion.Empty || motion.Flags != byte.MaxValue || string.IsNullOrWhiteSpace(list.BasePath))
                throw new InvalidDataException("动作没有可读取的源轨道：" + current + "#" + id);
            current = Resource(list.BasePath);
        }
    }

    public static AnimationClip LoadPreview(PakService pak, string path, int index,
        ResolvedAnimationLayers? configuration, IReadOnlyList<string>? boneNames = null,
        IReadOnlyList<ViewportMesh>? meshes = null, MotionTreeSettings? treeSettings = null)
    {
        var source = configuration?.Sources?.SingleOrDefault(s => s.ListPath.Equals(path.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase) && s.Motion.SourceIndex == index);
        var sourcePath = source?.SourcePath ?? path;
        using var stream = pak.ReadFile(sourcePath);
        var clip = ViewportDataLoader.LoadAnimation(stream, sourcePath, source?.SourceIndex ?? index, boneNames, meshes,
            pak.ReadFile, treeSettings: treeSettings);
        // Fixed HOLD inputs often store just a static key at frame zero while
        // declaring a longer authored loop. Preserve that inspection window
        // for these resolved inputs, without changing generic/raw-all export.
        if (source != null && float.IsFinite(source.InspectionDuration) && source.InspectionDuration > clip.Duration)
        {
            clip.Duration = source.InspectionDuration;
            clip.FrameRate = Math.Max(clip.FrameRate, source.FrameRate);
            clip.FrameCount = (int)MathF.Ceiling(clip.Duration * clip.FrameRate);
        }
        return clip;
    }
}

public sealed record ResolvedAnimationMotion(string ListPath, MotionInfo Motion, string SourcePath,
    int SourceIndex, string[] ReferenceChain, float InspectionDuration, int FrameRate);
public sealed record ResolvedAnimationUsage(string Path, int Id, string Name, AnimationUsage Usage, bool IsNeutralDelta = false);
