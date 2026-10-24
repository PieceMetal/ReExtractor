using System.Numerics;
using ReeLib;
using ReeLib.MotTree;

namespace ReExtractor.Core;

/// <summary>The authored Nero Blue Rose layer tree at a fixed, central aim input.
/// This adapter does not evaluate the game's procedural aiming or weapon logic.</summary>
internal static class Dmc5NeroMotionComposition
{
    internal const string Notice = "尼禄蓝玫瑰组合：正前方固定瞄准；未模拟游戏实时瞄准、武器控制和状态切换。";
    internal static bool AppliesTo(string path) => path.Replace('\\', '/').Equals(
        "natives/x64/animation/player/pl0000/motlist/pl0000_br.motlist.85", StringComparison.OrdinalIgnoreCase);

    internal sealed record Plan(int TreeIndex, int UpperIndex, int LowerIndex);

    internal static Plan[] Plans(MotlistFile list, string path)
    {
        if (!AppliesTo(path)) return [];
        var result = new List<Plan>();
        foreach (var (entry, index) in list.Motions.Select((m, i) => (m, i)))
        {
            if (entry.MotFile is not MotTreeFile tree || entry.flags != 0 || entry.jointMaskId != 0) continue;
            // Match the complete authored topology, not an ID/name guess. Unknown
            // tree shapes retain their existing unsupported behavior.
            if (tree.Nodes.Count != 7 || tree.Links.Count != 6) continue;
            var layers = tree.Nodes.Select((n, i) => (n, i)).Where(x => x.n.typeName == "LayerBlendNode").ToArray();
            var octs = tree.Nodes.Select((n, i) => (n, i)).Where(x => x.n.typeName == "OctSyncBlendNode").ToArray();
            if (layers.Length != 1 || octs.Length != 1) continue;
            var layer = layers[0]; var oct = octs[0];
            if (Parameter(layer.n, "BlendRate") is not float layerWeight || layerWeight != 1
                || Parameter(oct.n, "BlendRate") is not float aim || aim != 0
                || Parameter(oct.n, "CircledBlend") is not false) continue;
            var links = tree.Links.Where(l => l.type == MotionTreeLinkType.Motion).ToArray();
            int Input(int target, int pin)
            {
                var matches = links.Where(l => l.inputNodeIndex == target && l.inputPinNo == pin).ToArray();
                return matches.Length == 1 ? matches[0].outputNodeIndex : -1;
            }
            if (links.Length != 5 || links.Any(l => l.outputPinNo != 0) || Input(layer.i, 0) != oct.i) continue;
            int lower = Input(layer.i, 1), upper = Input(oct.i, 1);
            var sources = new[] { lower, upper, Input(oct.i, 0), Input(oct.i, 2) };
            if (sources.Distinct().Count() != 4 || sources.Any(i => i < 0 || i >= tree.Nodes.Count
                || tree.Nodes[i].typeName != "MotionNode")) continue;
            var variables = tree.Links.Where(l => l.type == MotionTreeLinkType.Param).ToArray();
            if (variables.Length != 1 || variables[0].inputNodeIndex != oct.i || variables[0].inputPinNo != 0
                || variables[0].outputNodeIndex < 0 || variables[0].outputNodeIndex >= tree.Nodes.Count
                || tree.Nodes[(int)variables[0].outputNodeIndex].typeName != "VariablesNode") continue;
            var bank = Parameter(tree.Nodes[lower], "BankID");
            if (bank is not uint bankId || sources.Any(i => !Equals(Parameter(tree.Nodes[i], "BankID"), bankId)
                || Parameter(tree.Nodes[i], "Always") is not false
                || Parameter(tree.Nodes[i], "Asset") is not "")) continue;
            int SourceIndex(int node)
            {
                var id = Parameter(tree.Nodes[node], "MotionID");
                var matches = list.Motions.Select((m, i) => (m, i)).Where(x => Equals((uint)x.m.motNumber, id)
                    && x.m.MotFile is MotFile && x.m.flags == 0 && x.m.jointMaskId == 0).ToArray();
                return matches.Length == 1 ? matches[0].i : -1;
            }
            var indices = sources.Select(SourceIndex).ToArray();
            if (indices.Any(i => i < 0)) continue;
            // This scoped bank is verified again against the loaded MOTBANK at
            // evaluation time; listings do not require archive access.
            if (bankId != 60) continue;
            result.Add(new(index, indices[1], indices[0]));
        }
        return result.ToArray();
    }

    private static object? Parameter(MotionTreeNode node, string name)
        => node.Parameters.SingleOrDefault(p => p.name == name)?.value;

    internal static Plan? Resolve(MotlistFile list, string path, int index)
    {
        var plans = Plans(list, path).Where(p => p.TreeIndex == index || p.LowerIndex == index).ToArray();
        if (plans.Length > 1) throw new InvalidDataException("尼禄动作组合来源不唯一，无法自动组合。");
        return plans.SingleOrDefault();
    }

    internal static AnimationClip Load(MotlistFile list, string path, Plan plan,
        IReadOnlyList<string>? boneNames, IReadOnlyList<ViewportMesh>? meshes, Func<string, Stream?>? resolver)
    {
        const string bankPath = "natives/x64/animation/player/pl0000/pl0000.motbank.1";
        using var bankStream = resolver?.Invoke(bankPath)
            ?? throw new FileNotFoundException("尼禄组合动作需要配套动作库，请加载完整游戏资源。", bankPath);
        using var bank = new MotbankFile(new FileHandler(bankStream, bankPath));
        if (!bank.Read() || bank.MotlistItems.Count(e => e.BankID == 60
            && ("natives/x64/" + e.Path.Replace('\\', '/').TrimStart('/')).Equals(path[..path.LastIndexOf('.')],
                StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("尼禄动作库与组合树的 Bank 60 引用不匹配。");

        var upperMot = (MotFile)list.Motions[plan.UpperIndex].MotFile!;
        var lowerMot = (MotFile)list.Motions[plan.LowerIndex].MotFile!;
        var fps = (int)lowerMot.Header.FrameRate;
        if (fps <= 0 || upperMot.Header.FrameRate != fps || upperMot.Header.frameCount != lowerMot.Header.frameCount)
            throw new NotSupportedException("尼禄组合轨道时钟不一致，暂不支持该资源变体。");
        boneNames ??= upperMot.Bones.Concat(lowerMot.Bones).Select(b => b.boneName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        AnimationClip Raw(int index)
        {
            // Reuse normal loading so per-part skeleton/helper handling agrees
            // with preview. The independent streams leave the caller untouched.
            using var stream = resolver(path) ?? throw new FileNotFoundException(path);
            return ViewportDataLoader.LoadAnimation(stream, path, index, boneNames, meshes, resolver,
                composeDmc5Motions: false);
        }
        var upper = Raw(plan.UpperIndex); var lower = Raw(plan.LowerIndex);
        var names = meshes is { Count: > 0 }
            ? AnimationSkeleton.PreviewBones(meshes).Select(b => b.Name).ToArray() : boneNames.ToArray();
        var duration = lowerMot.Header.frameCount / fps;
        var count = (int)MathF.Ceiling(lowerMot.Header.frameCount);
        var times = Enumerable.Range(0, count + 1).Select(i => Math.Min(i / (float)fps, duration)).ToArray();
        var result = new AnimationClip { Name = $"{list.Motions[plan.TreeIndex].MotFile!.Name}（正前方组合）",
            PoseAdaptation = Notice, Duration = duration, FrameRate = fps, FrameCount = count, Tracks = new() };
        for (var i = 0; i < names.Length; i++)
        {
            upper.NamedTracks.TryGetValue(names[i], out var first);
            lower.NamedTracks.TryGetValue(names[i], out var second);
            // LayerBlend input 1 overwrites input 0 only where it authors a
            // channel. Filling missing lower channels with bind would erase arms.
            var t = second?.Translations is { Length: > 0 } ? second : first;
            var r = second?.Rotations is { Length: > 0 } ? second : first;
            var s = second?.Scales is { Length: > 0 } ? second : first;
            var track = new BoneTrack();
            if (t?.Translations is { Length: > 0 } translations && t.TransTimes is { Length: > 0 } tt)
            { track.TransTimes = times; track.Translations = times.Select(time => Sample(translations, tt, time, Vector3.Lerp)).ToArray(); }
            if (r?.Rotations is { Length: > 0 } rotations && r.RotTimes is { Length: > 0 } rt)
            { track.RotTimes = times; track.Rotations = times.Select(time => Quaternion.Normalize(Sample(rotations, rt, time, Quaternion.Slerp))).ToArray(); }
            if (s?.Scales is { Length: > 0 } scales && s.ScaleTimes is { Length: > 0 } st)
            { track.ScaleTimes = times; track.Scales = times.Select(time => Sample(scales, st, time, Vector3.Lerp)).ToArray(); }
            if (track.Translations == null && track.Rotations == null && track.Scales == null) continue;
            result.Tracks[i] = track; result.NamedTracks[names[i]] = track;
        }
        return result;
    }

    private static T Sample<T>(T[] values, float[] times, float time, Func<T, T, float, T> lerp)
    {
        if (values.Length != times.Length) throw new InvalidDataException("动画采样时间与数值数量不一致。");
        if (time <= times[0]) return values[0];
        if (time >= times[^1]) return values[^1];
        var end = Array.BinarySearch(times, time);
        if (end >= 0) return values[end];
        end = ~end;
        return lerp(values[end - 1], values[end], (time - times[end - 1]) / (times[end] - times[end - 1]));
    }
}
