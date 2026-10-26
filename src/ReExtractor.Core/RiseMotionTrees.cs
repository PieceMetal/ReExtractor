using System.Numerics;
using ReeLib;
using ReeLib.MotTree;

namespace ReExtractor.Core;

public sealed record MotionTreeSource(string ListPath, int SourceIndex, int Bank, int MotionId, string Name);
public sealed record MotionTreeVariable(string Name, string Guid, float DefaultValue, float Minimum, float Maximum);
public sealed record MotionTreeDescription(bool Available, string Description, MotionTreeVariable? Variable,
    IReadOnlyList<MotionTreeSource> Sources);

/// <summary>Explicit inspection inputs, independent of game-controlled variables.</summary>
public sealed class MotionTreeSettings
{
    public MotionTreeResources? Resources { get; init; }
    public string? BankPath { get; init; }
    public IReadOnlyDictionary<string, float> Variables { get; init; } = new Dictionary<string, float>();
}

/// <summary>A versioned resource index and its exact MOTBANK registrations.</summary>
public sealed class MotionTreeResources(Func<string, Stream?> open, IEnumerable<string> resourcePaths)
{
    private readonly string[] paths = resourcePaths.Select(p => p.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private readonly Dictionary<string, Bank> banks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Definition[]> lists = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private sealed record Bank(string Path, (int Id, string Path)[] Lists);
    private sealed record Definition(int Index, int Id, int Branch, string Name, bool Direct, int Flags, uint Mask);

    internal Stream Read(string path) => open(path) ?? throw new FileNotFoundException("缺少动作资源：" + path, path);
    internal string Resource(string reference)
    {
        var key = reference.Replace('\\', '/').TrimStart('/');
        if (!key.StartsWith("natives/", StringComparison.OrdinalIgnoreCase)) key = "natives/stm/" + key;
        var matches = paths.Where(p => p.Equals(key, StringComparison.OrdinalIgnoreCase)
            || (p.StartsWith(key + ".", StringComparison.OrdinalIgnoreCase) && int.TryParse(p[(key.Length + 1)..], out _))).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"动作资源引用无法唯一解析：{reference}（匹配 {matches.Length} 项）");
    }

    private Bank ReadBank(string path)
    {
        if (banks.TryGetValue(path, out var prior)) return prior;
        using var s = Read(path); using var b = new MotbankFile(new FileHandler(s, path));
        if (!b.Read() || b.version != 3) throw new InvalidDataException("不支持的动作库：" + path);
        if (b.MotlistItems.Any(e => e.BankType != 0 || e.BankTypeMaskBits != 0))
            throw new NotSupportedException("动作库包含动态类型或遮罩：" + path);
        // Preserve registration text here. A missing optional list must not
        // invalidate a different, readable Bank ID in the same context.
        return banks[path] = new(path, b.MotlistItems.Select(e => ((int)e.BankID, e.Path)).ToArray());
    }

    private static string RegistrationKey(string path)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        if (!path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase)) path = "natives/stm/" + path;
        var dot = path.LastIndexOf('.');
        return dot > 0 && int.TryParse(path[(dot + 1)..], out _) ? path[..dot] : path;
    }

    internal MotionTreeSource Resolve(string owner, int bankId, int motionId, string? explicitBank)
    {
        lock (gate)
        {
            var directory = owner[..(owner.LastIndexOf('/') + 1)];
            var candidates = explicitBank == null
                ? paths.Where(p => p.StartsWith(directory, StringComparison.OrdinalIgnoreCase) && p.EndsWith(".motbank.3", StringComparison.OrdinalIgnoreCase)
                    // Player and follower banks can register the same tree while
                    // supplying different Bank 100 payloads. Default player trees
                    // to the player family; callers can explicitly supply a bank.
                    && (!(Path.GetFileName(owner).StartsWith("plw_", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(owner).StartsWith("plc_", StringComparison.OrdinalIgnoreCase))
                        || Path.GetFileName(p).StartsWith("plw_", StringComparison.OrdinalIgnoreCase)))
                : [Resource(explicitBank)];
            var owners = new List<Bank>(); var failures = new List<string>();
            foreach (var path in candidates)
            {
                try
                {
                    var bank = ReadBank(path);
                    if (bank.Lists.Any(e => RegistrationKey(e.Path).Equals(RegistrationKey(owner), StringComparison.OrdinalIgnoreCase))) owners.Add(bank);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                { failures.Add(path + ": " + ex.Message); }
            }
            if (owners.Count == 0) throw new InvalidDataException("找不到登记当前列表的动作库：" + owner
                + (failures.Count == 0 ? "" : "；" + string.Join("；", failures)));
            // Shared trees may be registered by several weapons. Their referenced
            // bank must agree; never choose an arbitrary weapon's Bank 100.
            var sourcePaths = owners.SelectMany(b => b.Lists.Where(e => e.Id == bankId).Select(e => Resource(e.Path)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (sourcePaths.Length != 1) throw new InvalidDataException($"组合树动作库不唯一：Bank {bankId}，来源 {owner}（匹配 {sourcePaths.Length} 项）");
            var sourcePath = sourcePaths[0];
            if (!lists.TryGetValue(sourcePath, out var definitions))
            {
                using var stream = Read(sourcePath); using var list = ResolvedMotionList.Read(stream, sourcePath, open);
                definitions = list.List.Motions.Select((m, i) => new Definition(i, m.motNumber, m.Switch,
                    m.MotFile?.Name ?? "", m.MotFile is MotFile, (int)m.flags, m.jointMaskId)).ToArray();
                lists[sourcePath] = definitions;
            }
            var matches = definitions.Where(d => d.Id == motionId).ToArray();
            if (matches.Length != 1) throw new InvalidDataException($"组合树缺少或无法唯一确定源动作：Bank {bankId} / Motion {motionId}，列表 {sourcePath}（匹配 {matches.Length} 项）");
            var source = matches[0];
            if (!source.Direct || source.Branch != 0 || source.Flags != 0 || source.Mask != 0)
                throw new NotSupportedException($"组合树源动作包含未支持的分支、遮罩或引用：{sourcePath} / Motion {motionId}");
            return new(sourcePath, source.Index, bankId, motionId, source.Name);
        }
    }
}

/// <summary>Topology-based support for the audited nonpolar Rise sync trees.
/// Evaluates an explicit, fixed input; it does not execute the game's FSM.</summary>
public static class RiseMotionTrees
{
    public static bool AppliesTo(string path) => path.EndsWith(".motlist.528", StringComparison.OrdinalIgnoreCase);
    private sealed record Plan(MotionTreeDescription Info, MotionTreeSource[] Pins, bool ThreeWay, float Rate);
    private static object? Parameter(MotionTreeNode n, string name) => n.Parameters.SingleOrDefault(p => p.name == name)?.value;
    private static MotionTreeResources Resources(Func<string, Stream?>? open, MotionTreeSettings? settings)
        => settings?.Resources ?? (open?.Target as PakService)?.MotionTreeResources
            ?? throw new InvalidOperationException("组合预览需要已加载的动作库资源索引");

    public static MotionTreeDescription Describe(MotlistFile list, string path, int index,
        Func<string, Stream?>? open, MotionTreeSettings? settings = null)
    {
        try { return Build(list, path, index, Resources(open, settings), settings).Info; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or InvalidOperationException)
        {
            var sources = new List<MotionTreeSource>();
            if (list.Motions[index].MotFile is MotTreeFile tree)
                foreach (var node in tree.Nodes.Where(n => n.typeName == "MotionNode"))
                    try
                    {
                        if (Parameter(node, "Asset") is "" && Parameter(node, "BankID") is uint bank
                            && Parameter(node, "MotionID") is uint id && bank <= int.MaxValue && id <= ushort.MaxValue)
                            sources.Add(Resources(open, settings).Resolve(path, (int)bank, (int)id, settings?.BankPath));
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or InvalidOperationException) { }
            return new(false, ex.Message, null, sources.Distinct().ToArray());
        }
    }

    private static Plan Build(MotlistFile list, string path, int index, MotionTreeResources resources, MotionTreeSettings? settings)
    {
        var entry = list.Motions[index];
        if (list.Header.version != ReeLib.Motlist.MotlistVersion.MHR || entry.MotFile is not MotTreeFile tree
            || entry.flags != 0 || entry.jointMaskId != 0 || entry.Switch != 0)
            throw new NotSupportedException("当前组合树的格式、分支或遮罩不受支持");
        var roots = tree.Nodes.Select((n, i) => (n, i)).Where(x => x.n.typeName is "SyncBlendNode" or "OctSyncBlendNode").ToArray();
        if (roots.Length != 1) throw new NotSupportedException("组合树不是已支持的同步混合结构");
        var root = roots[0]; var three = root.n.typeName == "OctSyncBlendNode"; var count = three ? 3 : 2;
        if (tree.Nodes.Count(n => n.typeName == "MotionNode") != count
            || tree.Nodes.Any(n => n.typeName is not ("MotionNode" or "VariablesNode" or "SyncBlendNode" or "OctSyncBlendNode"))
            || tree.Nodes.Count(n => n.typeName == "VariablesNode") > 1)
            throw new NotSupportedException("组合树含有尚未支持的节点或速度计算；可单独查看源动作");
        if (Parameter(root.n, "RootTransPolarBlend") is not false || Parameter(root.n, "CircledBlend") is not false
            || Parameter(root.n, "BlendType") is not uint blendType || blendType != 0
            || root.n.Parameters.Any(p => p.name is not ("RootTransPolarBlend" or "CircledBlend" or "BlendType" or "BlendRate")))
            throw new NotSupportedException("组合树的极坐标、循环或混合模式不受支持");
        var links = tree.Links.Where(l => l.type == MotionTreeLinkType.Motion).ToArray();
        if (links.Length != count || links.Any(l => l.inputNodeIndex != root.i || l.outputPinNo != 0)
            || !links.Select(l => l.inputPinNo).Order().SequenceEqual(Enumerable.Range(0, count)))
            throw new InvalidDataException("组合树连线不完整或存在重复输入");
        var sources = new MotionTreeSource[count]; var errors = new List<string>();
        foreach (var link in links)
        {
            if ((uint)link.outputNodeIndex >= tree.Nodes.Count || tree.Nodes[link.outputNodeIndex].typeName != "MotionNode")
                throw new InvalidDataException("组合树源节点无效");
            var leaf = tree.Nodes[link.outputNodeIndex];
            if (Parameter(leaf, "Asset") is not "" || Parameter(leaf, "BankID") is not uint bank
                || Parameter(leaf, "MotionID") is not uint id || bank > int.MaxValue || id > ushort.MaxValue
                || leaf.Parameters.Any(p => p.name is not ("Asset" or "BankID" or "MotionID")))
                throw new NotSupportedException("组合树的外部资源或动作参数不受支持");
            try { sources[link.inputPinNo] = resources.Resolve(path, (int)bank, (int)id, settings?.BankPath); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { errors.Add(ex.Message); }
        }
        if (errors.Count > 0) throw new InvalidDataException(string.Join("；", errors.Distinct()));
        if (sources.Distinct().Count() != count || links.Select(l => l.outputNodeIndex).Distinct().Count() != count)
            throw new NotSupportedException("组合树含有重复的源输入");

        var variableLinks = tree.Links.Where(l => l.type == MotionTreeLinkType.Param).ToArray();
        if (tree.Links.Count != links.Length + variableLinks.Length || variableLinks.Length > 1)
            throw new NotSupportedException("组合树含有未支持的参数连线");
        var rate = Parameter(root.n, "BlendRate") is float value ? value : throw new InvalidDataException("组合树缺少混合参数");
        MotionTreeVariable? variable = null;
        if (variableLinks.Length == 1)
        {
            var l = variableLinks[0];
            if (l.inputNodeIndex != root.i || l.inputPinNo != 0 || (uint)l.outputNodeIndex >= tree.Nodes.Count
                || tree.Nodes[l.outputNodeIndex].typeName != "VariablesNode") throw new InvalidDataException("组合树变量连接无效");
            var node = tree.Nodes[l.outputNodeIndex];
            if (Parameter(node, "Asset") is not string reference || string.IsNullOrWhiteSpace(reference) || node.Parameters.Count != 1)
                throw new InvalidDataException("组合树变量资源无效");
            var variablePath = resources.Resource(reference);
            using var stream = resources.Read(variablePath); using var uvar = new UVarFile(new FileHandler(stream, variablePath));
            if (!uvar.Read() || (uint)l.outputPinNo >= uvar.Variables.Count) throw new InvalidDataException("组合树变量编号越界");
            var v = uvar.Variables[l.outputPinNo];
            if (v.Expression != null || v.Value is not float defaultValue) throw new NotSupportedException("组合树的表达式变量暂不支持");
            variable = new(v.Name, v.guid.ToString(), defaultValue, three ? -1 : 0, 1);
            rate = settings?.Variables.GetValueOrDefault(variable.Name, defaultValue) ?? defaultValue;
        }
        else if (tree.Nodes.Any(n => n.typeName == "VariablesNode")) throw new InvalidDataException("组合树存在未连接的变量节点");
        if (!float.IsFinite(rate) || rate < (three ? -1 : 0) || rate > 1)
            throw new InvalidDataException("组合预览参数超出范围");
        if (settings?.Variables.Keys.Any(key => key != variable?.Name) == true) throw new InvalidDataException("组合预览包含未知变量");
        var description = $"组合预览 · 固定输入 {variable?.Name ?? "BlendRate"}={rate:G4}；"
            + string.Join(" / ", sources.Select(s => $"Bank {s.Bank} · {s.MotionId}"))
            + "。当前动画导出使用此输入；批量导出使用资源默认值。";
        return new(new(true, description, variable, sources), sources, three, rate);
    }

    internal static AnimationClip Load(MotlistFile list, string path, int index, Func<string, Stream?>? open,
        IReadOnlyList<string>? boneNames, IReadOnlyList<ViewportMesh>? meshes, MotionTreeSettings? settings = null)
    {
        var resources = Resources(open, settings); var plan = Build(list, path, index, resources, settings);
        var clips = new List<AnimationClip>();
        var bindings = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in plan.Pins)
        {
            using var s = resources.Read(source.ListPath); using var resolved = ResolvedMotionList.Read(s, source.ListPath, open);
            var mot = (MotFile)resolved.List.Motions[source.SourceIndex].MotFile!;
            foreach (var b in mot.Bones) bindings.TryAdd(b.boneName,
                Matrix4x4.CreateFromQuaternion(b.quaternion) * Matrix4x4.CreateTranslation(b.translation));
            boneNames ??= mot.Bones.Select(b => b.boneName).ToArray();
            clips.Add(ViewportDataLoader.BuildMotClip(mot, source.MotionId, source.ListPath, boneNames, meshes, bakeConstraints: false));
        }
        foreach (var mesh in meshes ?? []) foreach (var bone in mesh.Bones) bindings[bone.Name] = bone.LocalBind;
        if (clips.Any(c => !float.IsFinite(c.Duration) || c.Duration <= 0))
            throw new InvalidDataException("组合树源动作没有有效时钟");
        if (clips.Any(c => c.NamedTracks.Values.Any(t => t.IsAdditive))) throw new NotSupportedException("组合树源轨道为附加层，暂不支持此混合");
        // Signed three-way input: left (-1), centre (0), right (+1).
        // Two-way SyncBlend uses its ordinary 0..1 input. Both are fixed inspection values.
        var a = plan.ThreeWay ? (plan.Rate < 0 ? 0 : 1) : 0;
        var bIndex = plan.ThreeWay ? (plan.Rate < 0 ? 1 : 2) : 1;
        var weight = plan.ThreeWay ? (plan.Rate < 0 ? plan.Rate + 1 : plan.Rate) : plan.Rate;
        var duration = float.Lerp(clips[a].Duration, clips[bIndex].Duration, weight);
        var fps = clips.Max(c => c.FrameRate);
        var names = boneNames!.ToArray();
        var result = new AnimationClip { Name = list.Motions[index].MotFile!.Name + "（组合预览）", PoseAdaptation = plan.Info.Description,
            Duration = duration, FrameRate = fps, FrameCount = (int)MathF.Ceiling(duration * fps), Tracks = [] };
        // SyncBlend shares normalized phase, not absolute source seconds. Include
        // both clocks' original key instants when their cycle lengths differ.
        var times = Enumerable.Range(0, result.FrameCount + 1).Select(i => Math.Min(i / (float)fps, duration))
            .Concat(new[] { a, bIndex }.SelectMany(c => clips[c].NamedTracks.Values.SelectMany(t =>
                (t.TransTimes ?? []).Concat(t.RotTimes ?? []).Concat(t.ScaleTimes ?? []))
                .Select(t => t * duration / clips[c].Duration)))
            .Select(t => Math.Clamp(t, 0, duration)).Distinct().Order().ToArray();
        float SourceTime(int c, float t) => t * clips[c].Duration / duration;
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i]; clips[a].NamedTracks.TryGetValue(name, out var first); clips[bIndex].NamedTracks.TryGetValue(name, out var second);
            if (first == null && second == null) continue;
            var bind = bindings.GetValueOrDefault(name, Matrix4x4.Identity);
            Matrix4x4.Decompose(bind, out var scale, out var rotation, out var translation);
            var track = new BoneTrack();
            if (first?.Translations != null || second?.Translations != null)
            { track.TransTimes = times; track.Translations = times.Select(t => Vector3.Lerp(Sample(first?.Translations, first?.TransTimes, SourceTime(a, t), translation, Vector3.Lerp), Sample(second?.Translations, second?.TransTimes, SourceTime(bIndex, t), translation, Vector3.Lerp), weight)).ToArray(); }
            if (first?.Rotations != null || second?.Rotations != null)
            { track.RotTimes = times; track.Rotations = times.Select(t => Quaternion.Normalize(Quaternion.Slerp(Sample(first?.Rotations, first?.RotTimes, SourceTime(a, t), rotation, Quaternion.Slerp), Sample(second?.Rotations, second?.RotTimes, SourceTime(bIndex, t), rotation, Quaternion.Slerp), weight))).ToArray(); }
            if (first?.Scales != null || second?.Scales != null)
            { track.ScaleTimes = times; track.Scales = times.Select(t => Vector3.Lerp(Sample(first?.Scales, first?.ScaleTimes, SourceTime(a, t), scale, Vector3.Lerp), Sample(second?.Scales, second?.ScaleTimes, SourceTime(bIndex, t), scale, Vector3.Lerp), weight)).ToArray(); }
            result.Tracks[i] = track; result.NamedTracks[name] = track;
        }
        var driver = meshes == null ? null : AnimationSkeleton.SelectDriver(meshes);
        return driver?.JointConstraints?.Bake(driver, result) ?? result;
    }

    private static T Sample<T>(T[]? values, float[]? times, float time, T fallback, Func<T, T, float, T> lerp)
    {
        if (values is not { Length: > 0 } || times is not { Length: > 0 }) return fallback;
        if (values.Length != times.Length) throw new InvalidDataException("组合树源动作采样数量不一致");
        if (time <= times[0]) return values[0]; if (time >= times[^1]) return values[^1];
        var high = Array.BinarySearch(times, time); if (high >= 0) return values[high]; high = ~high;
        return lerp(values[high - 1], values[high], (time - times[high - 1]) / (times[high] - times[high - 1]));
    }
}
