using ReeLib;
using ReeLib.MotTree;
using System.Collections;

namespace ReExtractor.Core;

/// <summary>Leon common-player HOLD and SHOOT tree inputs. These are explicitly
/// selected inspection poses/shot variants, not a runtime FSM or Wheel evaluator.</summary>
public static class Re4PlayerShootAnimationResolver
{
    private static Dictionary<string, object?> Fields(RszInstance instance) => instance.IndexedFields
        .Where(f => f.index < instance.Values.Length).ToDictionary(f => f.field.name, f => (object?)instance.Values[f.index]);
    private static int Number(Dictionary<string, object?> f, string key) => Convert.ToInt32(f[key]);
    private static bool Flag(Dictionary<string, object?> f, string key) => f.GetValueOrDefault(key) is true;
    private static object Parameter(MotionTreeNode node, string name) => node.Parameters.Single(p => p.name == name).value
        ?? throw new InvalidDataException("缺少动作树参数：" + name);
    private sealed record TreeInput(int Bank, int Id, int Mask);
    private sealed record HoldInputs(TreeInput[] Motions, int VariableNode, Guid BlendGuid, Guid SpeedGuid);

    public static ResolvedAnimationLayers Resolve(PakService pak)
    {
        var paths = pak.EnumerateFiles().Select(f => f.Path).ToArray();
        if (!paths.Any(p => p.Contains("/ch0common/motion/", StringComparison.OrdinalIgnoreCase))) return new([], []);
        var resources = new Re4AnimationResourceCatalog(pak);
        var pairs = new List<AnimationLayerPair>();
        var sources = new List<ResolvedAnimationMotion>();
        var usages = new List<ResolvedAnimationUsage>();
        var diagnostics = new List<string>();
        try
        {
            using var workspace = new Workspace(new GameConfig(new GameIdentifier("re4")));
            var prefabPath = resources.Resource("_Chainsaw/AppSystem/Character/ch0a0z0/ch0a0z0_body.pfb");
            using var ps = pak.ReadFile(prefabPath);
            using var prefab = new PfbFile(workspace.RszFileOption, new FileHandler(ps, prefabPath));
            if (!prefab.Read()) throw new InvalidDataException("玩家层配置读取失败");
            var instances = prefab.GetAllRSZFiles().SelectMany(r => r.InstanceList).ToArray();
            var motion = Fields(instances.Single(i => i.RszClass.name == "via.motion.Motion"));
            if (Flag(motion, "EnableLayerUpdateOrder")) throw new InvalidDataException("自定义层顺序不支持");
            var layers = ((IList)motion["Layer"]!).Cast<RszInstance>().Select(Fields).ToArray();
            var jointMap = resources.Resource((string)motion["JointMap"]!);
            var fsmLayers = ((IList)Fields(instances.Single(i => i.RszClass.name == "via.motion.MotionFsm2"))["v11_Layer"]!)
                .Cast<RszInstance>().Select(Fields).ToArray();
            (string Path, int Layer) FsmContext(string suffix, int blendMode)
            {
                var context = fsmLayers.Select((f, index) => (f, index)).Single(x => Flag(x.f, "Enabled")
                    && x.f.GetValueOrDefault("MotionFsm2Resource") is string path && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                var index = Number(context.f, "TargetMotionLayerNo");
                if (index < 0) index = context.index;
                if (Flag(context.f, "DynamicReference") || Flag(context.f, "OverwriteJointMaskID")
                    || Flag(context.f, "OverwriteBlendMode") || Flag(context.f, "OverwriteBlendRate")
                    || Flag(context.f, "OverwriteLocalBankType") || index < 0 || index >= layers.Length
                    || Number(layers[index], "BlendMode") != blendMode || Number(layers[index], "JointMaskID") != 0
                    || Convert.ToSingle(layers[index]["BlendRate"]) != 1 || Flag(layers[index], "WorldRotationBlend"))
                    throw new InvalidDataException("持枪或射击层配置不支持：" + suffix);
                return (resources.Resource((string)context.f["MotionFsm2Resource"]!), index);
            }
            var baseContext = FsmContext("/ch0Common.motfsm2", 0);
            var addContext = FsmContext("/ch0CommonUpperBodyAdd.motfsm2", 1);
            if (baseContext.Layer != 0 || addContext.Layer <= baseContext.Layer)
                throw new InvalidDataException("持枪与射击层顺序不支持");
            var commonBankPath = resources.Resource((string)motion["MotionBankAsset"]!);
            using var bs = pak.ReadFile(commonBankPath);
            using var commonBank = new MotbankFile(new FileHandler(bs, commonBankPath));
            if (!commonBank.Read()) throw new InvalidDataException("玩家动作库读取失败");
            string TreeList(int bankId)
            {
                var bank = commonBank.MotlistItems.Single(e => e.BankID == bankId && e.BankType == 0 && e.BankTypeMaskBits == 0);
                return resources.Resource(bank.Path);
            }
            var opened = new List<MotlistFile>();
            try
            {
                (string Path, int Id, MotTreeFile Tree) ReadStateTree((string Path, int Layer) context, string suffix)
                {
                    using var fs = pak.ReadFile(context.Path);
                    using var fsm = new Motfsm2File(workspace.RszFileOption, new FileHandler(fs, context.Path));
                    if (!fsm.Read()) throw new InvalidDataException("玩家状态机读取失败");
                    var candidates = new List<(string Path, int Id, MotTreeFile Tree)>();
                    foreach (var node in fsm.BhvtFile.Nodes.Where(n => n.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    {
                        var actions = node.Actions.Actions.Select(a => a.Instance).Where(i => i != null
                            && i.RszClass.name == "chainsaw.BehaviorTreeAction_MFSM_AppPlayMotion"
                            && i.GetFieldValue("v0_Enabled") is true).ToArray();
                        if (actions.Length == 0) continue;
                        if (actions.Length != 1) throw new InvalidDataException("状态动作引用不唯一");
                        var action = Fields(actions[0]!);
                        if (Convert.ToSingle(action["_Speed"]) != 1 || Number(action, "_WrapMode") != 0
                            || Number(action, "_StartFrame") != 0 || Number(action, "_FrameControl") != 0
                            || Flag(action, "_Mirror") || Flag(action, "_RandamizeStartFrame"))
                            throw new InvalidDataException("持枪或射击状态时序不支持");
                        var visited = new HashSet<string>();
                        var parent = node;
                        while (parent != null)
                        {
                            if (!visited.Add(parent.ID.ToString())) throw new InvalidDataException("状态机父级循环");
                            foreach (var a in parent.Actions.Actions.Select(a => a.Instance).Where(i => i != null && i.GetFieldValue("v0_Enabled") is true))
                                if (a!.RszClass.name.Contains("SetJointMask", StringComparison.Ordinal)
                                    || a.RszClass.name.Contains("SetBlendRate", StringComparison.Ordinal)
                                    || a.RszClass.name.Contains("SetBlendMode", StringComparison.Ordinal))
                                    throw new InvalidDataException("持枪或射击状态覆盖了层配置");
                            parent = fsm.BhvtFile.Nodes.FirstOrDefault(n => n.ID.Equals(parent.ParentID));
                        }
                        var path = TreeList(Number(action, "_BankID"));
                        var id = Number(action, "_MotionID");
                        var list = new MotlistFile(new FileHandler(pak.ReadFile(path), path));
                        opened.Add(list);
                        if (!list.Read()) throw new InvalidDataException("动作树列表读取失败");
                        var item = list.Motions.Single(m => m.motNumber == id);
                        if ((int)item.flags != 0 || item.MotFile is not MotTreeFile tree)
                            throw new InvalidDataException("状态引用不是可解析的动作树");
                        candidates.Add((path, id, tree));
                    }
                    var unique = candidates.DistinctBy(c => (c.Path, c.Id)).ToArray();
                    return unique.Length == 1 ? unique[0] : throw new InvalidDataException("状态动作树上下文不唯一");
                }
                var holdTree = ReadStateTree(baseContext, "_HOLD_LOOP");
                var shotTree = ReadStateTree(addContext, "_SHOOT");
                var holds = ReadHoldInputs(holdTree.Tree);
                var shots = ReadShotInputs(shotTree.Tree);
                if (holds.Motions.Concat(shots).Select(i => i.Bank).Distinct().Count() != 1
                    || holds.Motions.Concat(shots).Any(i => i.Mask != 0))
                    throw new InvalidDataException("持枪与射击动作库或遮罩上下文不一致");
                var uvarPath = resources.Resource((string)Parameter(holdTree.Tree.Nodes[holds.VariableNode], "Asset"));
                if (!uvarPath.Equals(resources.Resource((string)motion["UserVariables"]!), StringComparison.OrdinalIgnoreCase)
                    || !uvarPath.Equals(resources.Resource(commonBank.UvarPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("持枪变量资源上下文不一致");
                using var us = pak.ReadFile(uvarPath);
                using var variables = new UVarFile(new FileHandler(us, uvarPath));
                if (!variables.Read()) throw new InvalidDataException("持枪变量读取失败");
                foreach (var (guid, name) in new[] { (holds.BlendGuid, "WheelBlendRate"), (holds.SpeedGuid, "WheelSpeed") })
                {
                    var variable = variables.Variables.Single(v => v.guid == guid);
                    if (variable.Name != name || variable.type != ReeLib.UVar.Variable.TypeKind.Single || variable.Expression != null)
                        throw new InvalidDataException("持枪变量类型不支持");
                }
                using var js = pak.ReadFile(jointMap);
                using var jmap = new JmapFile(new FileHandler(js, jointMap));
                if (!jmap.Read() || !jmap.MaskGroups.Any(g => g.groupId == 0)) throw new InvalidDataException("完整骨骼遮罩缺失");
                var bankId = shots[0].Bank;
                var listContexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths.Where(p => p.Contains("/_chainsaw/animation/ch/cha0/motbank/cha0_wp", StringComparison.OrdinalIgnoreCase)
                    && p.Contains(".motbank.", StringComparison.OrdinalIgnoreCase)))
                {
                    using var ws = pak.ReadFile(path);
                    using var bank = new MotbankFile(new FileHandler(ws, path));
                    if (!bank.Read()) throw new InvalidDataException("武器动作库读取失败：" + path);
                    if (!string.IsNullOrEmpty(bank.JmapPath) && !resources.Resource(bank.JmapPath).Equals(jointMap, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("武器使用了不同骨骼遮罩：" + path);
                    foreach (var entry in bank.MotlistItems.Where(e => e.BankID == bankId))
                    {
                        var listPath = resources.Resource(entry.Path);
                        if (Re4AnimationLayerResolver.AppliesTo(listPath))
                            listContexts.TryAdd(listPath, $"{path}; Bank={entry.BankID}; Type={entry.BankType}; MaskBits={entry.BankTypeMaskBits}");
                    }
                }
                foreach (var (path, bankContext) in listContexts)
                {
                    var metadata = resources.List(path);
                    // Limit this adapter to weapon lists authoring their own
                    // shoot variants. Other contexts need separate confirmation.
                    var authoredShots = metadata.Motions.Where(m => shots.Any(s => s.Id == m.Id) && m.Embedded).ToArray();
                    if (authoredShots.Length == 0) continue;
                    var context = $"{holdTree.Path}#{holdTree.Id}:{holdTree.Tree.Name}; {shotTree.Path}#{shotTree.Id}:{shotTree.Tree.Name}; "
                        + $"Layers={baseContext.Layer}→{addContext.Layer}; {bankContext}";
                    foreach (var shot in authoredShots)
                        usages.Add(new(path, shot.Id, shot.Name!, new(AnimationUsageKind.Additive, [context]), shot.IsNeutralDelta));
                    try
                    {
                        if (authoredShots.Length != shots.Length || authoredShots.Any(s => s.HasScale || s.Flags != 0))
                            throw new InvalidDataException("射击变体缺少直接轨道或含不支持的缩放/标志");
                        var bases = holds.Motions.Select(h => resources.ResolveMotion(path, h.Id)).ToArray();
                        sources.AddRange(bases);
                        foreach (var basis in bases)
                            foreach (var shot in shots)
                                pairs.Add(new(path, basis.Motion.MotionNumber, shot.Id, 0, shot.Mask, jointMap,
                                    AnimationLayerComposition.PlayerShootEndpoint, context));
                    }
                    catch (Exception e) { diagnostics.Add("普通射击组合不可用：" + path + ": " + e.Message); }
                }
            }
            finally { foreach (var list in opened) list.Dispose(); }
        }
        catch (Exception e) { diagnostics.Add("普通射击配置不可用：" + e.Message); }
        return new(pairs, diagnostics, sources, usages);
    }

    private static void ValidateTree(MotTreeFile tree, int nodes, int links)
    {
        if (tree.Nodes.Count != nodes || tree.Links.Count != links || tree.Nodes.Any(n => n.flags != 0)
            || !string.IsNullOrEmpty(tree.ResourcePath) || !string.IsNullOrEmpty(tree.UvarPath))
            throw new InvalidDataException("持枪或射击动作树结构不支持");
    }
    private static HoldInputs ReadHoldInputs(MotTreeFile tree)
    {
        ValidateTree(tree, 6, 6);
        int Find(string type) => tree.Nodes.FindIndex(n => n.typeName == type);
        var syncIndex = Find("OctSyncBlendNode"); var speedIndex = Find("PlaySpeedNode"); var variableIndex = Find("VariablesNode");
        if (syncIndex < 0 || speedIndex < 0 || variableIndex < 0) throw new InvalidDataException("持枪混合节点缺失");
        var sync = tree.Nodes[syncIndex];
        if (Convert.ToBoolean(Parameter(sync, "RootTransPolarBlend")) || Convert.ToBoolean(Parameter(sync, "CircledBlend"))
            || Convert.ToInt32(Parameter(sync, "BlendType")) != 0 || Convert.ToInt32(Parameter(sync, "_SyncBlendNode_357794474")) != 0
            || Convert.ToSingle(Parameter(tree.Nodes[speedIndex], "Speed")) != 1)
            throw new InvalidDataException("持枪同步或速度配置不支持");
        int Input(int node, int pin) => tree.Links.Single(l => l.type == MotionTreeLinkType.Motion
            && l.inputNodeIndex == node && l.inputPinNo == pin && l.outputPinNo == 0
            && l.inputGuid == Guid.Empty && l.outputGuid == Guid.Empty).outputNodeIndex;
        if (Input(speedIndex, 0) != syncIndex) throw new InvalidDataException("持枪输出连接不支持");
        var inputs = Enumerable.Range(0, 3).Select(pin => Input(syncIndex, pin)).ToArray();
        if (inputs.Append(syncIndex).Append(speedIndex).Append(variableIndex).Distinct().Count() != 6)
            throw new InvalidDataException("持枪输入连接不完整");
        var motions = inputs.Select(index => {
            var node = tree.Nodes[index];
            if (node.typeName != "MotionNode" || (string)Parameter(node, "Asset") != "")
                throw new InvalidDataException("持枪输入不是直接动作引用");
            return new TreeInput(Convert.ToInt32(Parameter(node, "BankID")), Convert.ToInt32(Parameter(node, "MotionID")),
                Convert.ToInt32(Parameter(node, "JointMaskID")));
        }).ToArray();
        if (motions.Select(m => m.Id).Distinct().Count() != 3 || tree.MotionIDRemaps.Count != 3
            || motions.Any(m => !tree.MotionIDRemaps.Any(r => r.source == m.Id && r.target == m.Bank)))
            throw new InvalidDataException("持枪动作重映射不支持");
        Guid Variable(int node) => tree.Links.Single(l => l.type == MotionTreeLinkType.Param && l.inputNodeIndex == node
            && l.inputPinNo == 0 && l.outputNodeIndex == variableIndex && l.outputPinNo == 0 && l.inputGuid == Guid.Empty).outputGuid;
        var blend = Variable(syncIndex); var speed = Variable(speedIndex);
        if (blend == Guid.Empty || speed == Guid.Empty || blend == speed) throw new InvalidDataException("持枪变量连接缺失");
        return new(motions, variableIndex, blend, speed);
    }
    private static TreeInput[] ReadShotInputs(MotTreeFile tree)
    {
        ValidateTree(tree, 2, 1);
        if (tree.MotionIDRemaps.Count != 0) throw new InvalidDataException("射击动作重映射不支持");
        var selectIndex = tree.Nodes.FindIndex(n => n.typeName == "SelectMotionNode");
        var randomIndex = tree.Nodes.FindIndex(n => n.typeName == "RandomParameterNode");
        if (selectIndex < 0 || randomIndex < 0) throw new InvalidDataException("射击变体选择节点缺失");
        var link = tree.Links.Single();
        var select = tree.Nodes[selectIndex]; var random = tree.Nodes[randomIndex];
        if (link.type != MotionTreeLinkType.Param || link.inputNodeIndex != selectIndex || link.inputPinNo != 0
            || link.outputNodeIndex != randomIndex || link.outputPinNo != 1 || link.inputGuid != Guid.Empty || link.outputGuid != Guid.Empty
            || Convert.ToInt32(Parameter(select, "BankSelectMode")) != 0 || Convert.ToSingle(Parameter(random, "InMin")) != 0
            || Convert.ToSingle(Parameter(random, "InMax")) != 2)
            throw new InvalidDataException("射击变体选择连接不支持");
        var bank = Convert.ToInt32(Parameter(select, "BankID")); var mask = Convert.ToInt32(Parameter(select, "JointMaskID"));
        var inputs = new List<TreeInput>();
        for (var i = 0; i < 8; i++)
        {
            if (Convert.ToInt32(Parameter(select, $"No{i:00}_BankID")) != 0) throw new InvalidDataException("射击变体使用不同动作库");
            var id = Convert.ToUInt32(Parameter(select, $"No{i:00}_MotionID"));
            if (i < 3 && id <= int.MaxValue) inputs.Add(new(bank, (int)id, mask));
            else if (i < 3 || id != uint.MaxValue) throw new InvalidDataException("射击变体数量不支持");
        }
        if (inputs.Select(i => i.Id).Distinct().Count() != 3) throw new InvalidDataException("射击变体引用不唯一");
        return inputs.ToArray();
    }
}
