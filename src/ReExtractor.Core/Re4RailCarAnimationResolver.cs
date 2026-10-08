using ReeLib;
using ReeLib.MotTree;

namespace ReExtractor.Core;

/// <summary>
/// Resolves the rail-car shoot tree's fixed direction inputs. This is not a
/// general MotionTree evaluator: runtime YawBlend, FSM transitions and IK are
/// deliberately not baked into these endpoint previews.
/// </summary>
public static class Re4RailCarAnimationResolver
{
    public static ResolvedAnimationLayers Resolve(PakService pak)
    {
        const string prefix = "natives/stm/_chainsaw/appsystem/gimmick/details/railcar/motion/";
        var paths = pak.EnumerateFiles().Select(f => f.Path).ToArray();
        if (!paths.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return new([], []);
        string Resource(string reference)
        {
            var path = reference.Replace('\\', '/');
            if (!path.StartsWith("natives/stm/", StringComparison.OrdinalIgnoreCase)) path = "natives/stm/" + path;
            return paths.Single(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)
                || (p.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(p[(path.Length + 1)..], out _)));
        }
        try
        {
            using var workspace = new Workspace(new GameConfig(new GameIdentifier("re4")));
            var prefabPath = Resource("_Chainsaw/AppSystem/Character/ch0a0z0/ch0a0z0_body.pfb");
            using var ps = pak.ReadFile(prefabPath);
            using var prefab = new PfbFile(workspace.RszFileOption, new FileHandler(ps, prefabPath));
            if (!prefab.Read()) throw new InvalidDataException("玩家骨骼配置读取失败");
            var motion = prefab.GetAllRSZFiles().SelectMany(r => r.InstanceList)
                .Single(i => i.RszClass.name == "via.motion.Motion");
            var jointMap = Resource((string)motion.GetFieldValue("JointMap")!);
            var bankPath = Resource(prefix + "bank/railcar_pl.motbank");
            using var bs = pak.ReadFile(bankPath);
            using var bank = new MotbankFile(new FileHandler(bs, bankPath));
            if (!bank.Read()) throw new InvalidDataException("矿车动作库读取失败");
            if (!string.IsNullOrEmpty(bank.JmapPath)) jointMap = Resource(bank.JmapPath);
            string BankList(int id)
            {
                var entry = bank.MotlistItems.Single(e => e.BankID == id);
                if (entry.BankType != 0 || entry.BankTypeMaskBits != 0)
                    throw new InvalidDataException("矿车动作库类型不支持");
                return Resource(entry.Path);
            }
            var fsmPath = Resource(prefix + "fsm/railcar_pl.motfsm2");
            using var fs = pak.ReadFile(fsmPath);
            using var fsm = new Motfsm2File(workspace.RszFileOption, new FileHandler(fs, fsmPath));
            if (!fsm.Read()) throw new InvalidDataException("矿车状态机读取失败");
            var shoot = fsm.BhvtFile.Nodes.Single(n => n.Name == "shoot");
            var action = shoot.Actions.Actions.Select(a => a.Instance)
                .Single(i => i?.RszClass.name == "via.motion.Fsm2ActionPlayMotion"
                    && Equals(i.GetFieldValue("v0_Enabled"), true))!;
            if (Convert.ToSingle(action.GetFieldValue("v5_Speed")) != 1
                || Convert.ToInt32(action.GetFieldValue("v6_WrapMode")) != 0
                || Convert.ToInt32(action.GetFieldValue("v8_FrameControl")) != 0
                || !Equals(action.GetFieldValue("v7_Mirror"), false))
                throw new InvalidDataException("矿车射击时序或镜像配置不支持");
            var treePath = BankList(Convert.ToInt32(action.GetFieldValue("v3_BankID")));
            var treeId = Convert.ToInt32(action.GetFieldValue("v4_MotionID"));
            using var ts = pak.ReadFile(treePath);
            using var list = new MotlistFile(new FileHandler(ts, treePath));
            if (!list.Read()) throw new InvalidDataException("矿车动作树列表读取失败");
            var tree = list.Motions.Single(m => m.motNumber == treeId).MotFile as MotTreeFile
                ?? throw new InvalidDataException("矿车射击动作不是内嵌动作树");
            var endpoints = ReadEndpoints(tree);
            var variableNode = tree.Nodes[endpoints.VariableNode];
            var variablePath = Resource((string)Parameter(variableNode, "Asset"));
            if (!variablePath.Equals(Resource(bank.UvarPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("矿车方向变量上下文不一致");
            using var us = pak.ReadFile(variablePath);
            using var uvar = new UVarFile(new FileHandler(us, variablePath));
            if (!uvar.Read()) throw new InvalidDataException("矿车方向变量读取失败");
            var variable = uvar.Variables.Single(v => v.guid == endpoints.VariableGuid);
            if (variable.Name != "YawBlend" || variable.type != ReeLib.UVar.Variable.TypeKind.Single
                || variable.Expression != null)
                throw new InvalidDataException("矿车方向变量类型不支持");
            (string Path, int Id, int Mask) MotionNode(int index)
            {
                var node = tree.Nodes[index];
                if (node.typeName != "MotionNode" || (string)Parameter(node, "Asset") != "")
                    throw new InvalidDataException("矿车方向不是直接动作引用");
                return (BankList(Convert.ToInt32(Parameter(node, "BankID"))),
                    Convert.ToInt32(Parameter(node, "MotionID")), Convert.ToInt32(Parameter(node, "JointMaskID")));
            }
            var add = MotionNode(endpoints.AdditiveNode);
            var bases = endpoints.BaseNodes.Select(MotionNode).ToArray();
            if (bases.Select(b => b.Id).Distinct().Count() != 3
                || bases.Any(b => b.Path != add.Path || b.Id == add.Id)
                || !Re4AnimationLayerResolver.AppliesTo(add.Path))
                throw new InvalidDataException("矿车动作引用不唯一或跨列表");
            using var ms = pak.ReadFile(add.Path);
            using var motions = new MotlistFile(new FileHandler(ms, add.Path));
            if (!motions.Read()) throw new InvalidDataException("矿车动作轨道读取失败");
            foreach (var id in bases.Select(b => b.Id).Append(add.Id))
            {
                var item = motions.Motions.Single(m => m.motNumber == id);
                if (item.MotFile is not MotFile || (int)item.flags != 0)
                    throw new InvalidDataException("矿车引用缺少直接可读的原始轨道");
            }
            using var js = pak.ReadFile(jointMap);
            using var jmap = new JmapFile(new FileHandler(js, jointMap));
            if (!jmap.Read() || bases.Select(b => b.Mask).Append(add.Mask)
                .Any(id => !jmap.MaskGroups.Any(g => g.groupId == id)))
                throw new InvalidDataException("矿车骨骼遮罩缺失");
            return new(bases.Select(b => new AnimationLayerPair(b.Path, b.Id, add.Id, b.Mask, add.Mask, jointMap,
                AnimationLayerComposition.MotionTreeEndpoint, treePath + "#" + treeId + ":" + tree.Name)).ToArray(), []);
        }
        catch (Exception e) { return new([], ["矿车射击组合不可用：" + e.Message]); }
    }

    private static object Parameter(MotionTreeNode node, string name)
        => node.Parameters.Single(p => p.name == name).value
            ?? throw new InvalidDataException("缺少动作树参数：" + name);

    private sealed record Endpoints(int AdditiveNode, int[] BaseNodes, int VariableNode, Guid VariableGuid);

    private static Endpoints ReadEndpoints(MotTreeFile tree)
    {
        if (tree.Nodes.Count != 7 || tree.Links.Count != 6 || tree.Nodes.Any(n => n.flags != 0)
            || tree.UvarPath != "" || tree.ResourcePath != "")
            throw new InvalidDataException("矿车动作树包含未支持的节点或引用");
        var root = Enumerable.Range(0, tree.Nodes.Count).Single(i => tree.Nodes[i].typeName == "AddBlendNode");
        var blend = tree.Nodes[root];
        if (Convert.ToInt32(Parameter(blend, "BaseNo")) != 0
            || Convert.ToSingle(Parameter(blend, "BlendRate")) != 1
            || !Equals(Parameter(blend, "WorldRotation"), false))
            throw new InvalidDataException("矿车附加混合方式不支持");
        int Input(int node, int pin) => tree.Links.Single(l => l.type == MotionTreeLinkType.Motion
            && l.inputNodeIndex == node && l.inputPinNo == pin && l.outputPinNo == 0).outputNodeIndex;
        var syncIndex = Input(root, 0);
        var addIndex = Input(root, 1);
        var sync = tree.Nodes[syncIndex];
        if (sync.typeName != "OctSyncBlendNode" || !Equals(Parameter(sync, "RootTransPolarBlend"), false)
            || !Equals(Parameter(sync, "CircledBlend"), false)
            || Convert.ToInt32(Parameter(sync, "BlendType")) != 0
            || Convert.ToInt32(Parameter(sync, "_SyncBlendNode_357794474")) != 0)
            throw new InvalidDataException("矿车方向混合方式不支持");
        var bases = Enumerable.Range(0, 3).Select(pin => Input(syncIndex, pin)).ToArray();
        var variable = tree.Links.Single(l => l.type == MotionTreeLinkType.Param
            && l.inputNodeIndex == syncIndex && l.inputPinNo == 0 && l.outputPinNo == 0);
        if (tree.Nodes[variable.outputNodeIndex].typeName != "VariablesNode" || variable.outputGuid == Guid.Empty
            || bases.Append(root).Append(syncIndex).Append(addIndex).Append(variable.outputNodeIndex).Distinct().Count() != 7)
            throw new InvalidDataException("矿车方向混合连接不完整");
        return new(addIndex, bases, variable.outputNodeIndex, variable.outputGuid);
    }
}
