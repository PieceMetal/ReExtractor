using ReeLib;
using System.Collections;
using System.Runtime.CompilerServices;

namespace ReExtractor.Core;

public sealed record ResolvedAnimationLayers(IReadOnlyList<AnimationLayerPair> Pairs, IReadOnlyList<string> Diagnostics)
{
    public AnimationLayerPair[] ForList(string path, IReadOnlyList<MotionInfo> motions)
    {
        var ids = motions.GroupBy(m => m.MotionNumber).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
        return Pairs.Where(p => p.Path.Equals(path.Replace('\\','/'), StringComparison.OrdinalIgnoreCase)
            && ids.Contains(p.BaseId) && ids.Contains(p.AdditiveId)).ToArray();
    }
}

/// <summary>
/// RE4's common-player upper-body adapter. Resolves the loaded archive's bank
/// references and enabled FSM actions; it is not a clip-ID whitelist or a
/// simulator of every RE game/character. Unsupported or ambiguous contexts are
/// retained as diagnostics rather than assigned guessed layers.
/// </summary>
public static class Re4AnimationLayerResolver
{
    private static readonly ConditionalWeakTable<PakService, Lazy<ResolvedAnimationLayers>> Cache = new();
    public static bool AppliesTo(string path) => path.Replace('\\','/').StartsWith(
        "natives/stm/_chainsaw/animation/ch/cha0/", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(".motlist.663", StringComparison.OrdinalIgnoreCase);

    public static ResolvedAnimationLayers Resolve(PakService pak) => Cache.GetValue(pak,
        key => new Lazy<ResolvedAnimationLayers>(() => Build(key))).Value;

    private static Dictionary<string, object?> Fields(RszInstance instance) => instance.IndexedFields
        .Where(f => f.index < instance.Values.Length).ToDictionary(f => f.field.name, f => (object?)instance.Values[f.index]);
    private static int Number(Dictionary<string, object?> f, string key, int fallback = -1)
    {
        if (!f.TryGetValue(key, out var value) || value == null) return fallback;
        var number = Convert.ToInt64(value);
        return number is >= int.MinValue and <= int.MaxValue ? (int)number : fallback;
    }
    private static bool Flag(Dictionary<string, object?> f, string key) => f.TryGetValue(key, out var v) && v is true;
    private static bool Enabled(Dictionary<string, object?> f) => !f.TryGetValue("v0_Enabled", out var v) || v is not false;
    private static bool Synchronous(Dictionary<string, object?> f) => Number(f,"_BankID") >= 0
        && f.TryGetValue("_Speed",out var speed) && Convert.ToSingle(speed) == 1
        && Number(f,"_StartFrame") == 0 && Number(f,"_FrameControl") == 0
        && !Flag(f,"_Mirror") && !Flag(f,"_RandamizeStartFrame");
    private sealed record Action(string Type, Dictionary<string, object?> Fields);
    private sealed record Node(string Id, string Parent, string Name, Action[] Actions);

    private static ResolvedAnimationLayers Build(PakService pak)
    {
        var diagnostics = new List<string>();
        var pairs = new List<AnimationLayerPair>();
        var paths = pak.EnumerateFiles().Select(f => f.Path).ToArray();
        string Resource(string reference)
        {
            var prefix = reference.Replace('\\','/').ToLowerInvariant();
            if (!prefix.StartsWith("natives/stm/")) prefix = "natives/stm/" + prefix;
            var matches = paths.Where(p => p.Equals(prefix,StringComparison.OrdinalIgnoreCase)
                || (p.StartsWith(prefix+".",StringComparison.OrdinalIgnoreCase) && int.TryParse(p[(prefix.Length+1)..],out _))).ToArray();
            return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"资源引用无法唯一解析：{reference}");
        }
        try
        {
            using var workspace = new Workspace(new GameConfig(new GameIdentifier("re4")));
            var prefabPath = Resource("_Chainsaw/AppSystem/Character/ch0a0z0/ch0a0z0_body.pfb");
            using var ps = pak.ReadFile(prefabPath);
            using var prefab = new PfbFile(workspace.RszFileOption,new FileHandler(ps,prefabPath));
            if(!prefab.Read()) throw new InvalidDataException("玩家层配置读取失败");
            var instances = prefab.GetAllRSZFiles().SelectMany(r => r.InstanceList).ToArray();
            var motion = Fields(instances.Single(i => i.RszClass.name == "via.motion.Motion"));
            if(Flag(motion,"EnableLayerUpdateOrder")) throw new InvalidDataException("该配置使用自定义层顺序");
            var layers = ((IList)motion["Layer"]!).Cast<RszInstance>().Select(Fields).ToArray();
            var jointMap = Resource((string)motion["JointMap"]!);
            var fsmComponent = Fields(instances.First(i => i.RszClass.name == "via.motion.MotionFsm2"));
            var fsmLayers = ((IList)fsmComponent["v11_Layer"]!).Cast<RszInstance>().Select(Fields).ToArray();
            var context = fsmLayers.Select((f,index) => (f,index)).Single(x => Flag(x.f,"Enabled")
                && x.f.GetValueOrDefault("MotionFsm2Resource") is string name
                && name.EndsWith("ch0CommonUpperBody.motfsm2",StringComparison.OrdinalIgnoreCase));
            int baseLayer = Number(context.f,"TargetMotionLayerNo");
            if(baseLayer < 0) baseLayer = context.index;
            if(Flag(context.f,"OverwriteJointMaskID") || Flag(context.f,"OverwriteBlendMode") || Flag(context.f,"OverwriteBlendRate")
                || Number(layers[baseLayer],"BlendMode") != 0 || Flag(layers[baseLayer],"WorldRotationBlend")
                || Convert.ToSingle(layers[baseLayer]["BlendRate"]) != 1)
                throw new InvalidDataException("上半身层配置不适用于当前预览求值器");
            var fsmPath = Resource((string)context.f["MotionFsm2Resource"]!);
            using var fs = pak.ReadFile(fsmPath);
            using var fsm = new Motfsm2File(workspace.RszFileOption,new FileHandler(fs,fsmPath));
            if(!fsm.Read()) throw new InvalidDataException("上半身状态机读取失败");
            var nodes = fsm.BhvtFile.Nodes.Select(n => new Node(n.ID.ToString(),n.ParentID.ToString(),n.Name,
                n.Actions.Actions.Where(a => a.Instance != null).Select(a => new Action(a.Instance!.RszClass.name,Fields(a.Instance!)))
                    .Where(a => Enabled(a.Fields)).ToArray())).ToArray();
            var byId = nodes.ToDictionary(n => n.Id);
            var banks = new Dictionary<int,HashSet<string>>();
            foreach(var bankPath in paths.Where(p => p.Contains("/_chainsaw/animation/ch/cha0/motbank/",StringComparison.OrdinalIgnoreCase)
                && p.Contains(".motbank.",StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var bs = pak.ReadFile(bankPath);
                    using var bank = new MotbankFile(new FileHandler(bs,bankPath));
                    if(!bank.Read()) throw new InvalidDataException("动作库读取失败");
                    foreach(var entry in bank.MotlistItems)
                    {
                        var list = Resource(entry.Path);
                        if(!AppliesTo(list)) continue;
                        int id = Convert.ToInt32(entry.BankID);
                        if(!banks.TryGetValue(id,out var refs)) banks[id] = refs = new(StringComparer.OrdinalIgnoreCase);
                        refs.Add(list);
                    }
                }
                catch(Exception e) { diagnostics.Add(bankPath+": "+e.Message); }
            }
            void AddPair(Node node,int bankId,int baseId,int addId,int addLayer)
            {
                if(baseId < 0 || addId < 0 || addLayer < 0 || addLayer >= baseLayer
                    || Number(layers[addLayer],"BlendMode") != 1 || !banks.TryGetValue(bankId,out var lists)) return;
                if(Flag(layers[addLayer],"WorldRotationBlend") || Convert.ToSingle(layers[addLayer]["BlendRate"]) != 1)
                { diagnostics.Add("附加层混合方式不支持："+node.Name); return; }
                int baseMask = Number(layers[baseLayer],"JointMaskID"), addMask = Number(layers[addLayer],"JointMaskID");
                var ancestors = new List<Node>(); Node? parent = node;
                while(parent != null)
                {
                    if(ancestors.Any(n => n.Id == parent.Id)) throw new InvalidDataException("状态机父级存在循环");
                    ancestors.Add(parent); parent = byId.GetValueOrDefault(parent.Parent);
                }
                foreach(var ancestor in ancestors.AsEnumerable().Reverse())
                    foreach(var action in ancestor.Actions)
                    {
                        if(action.Type.EndsWith("_SetJointMask")) baseMask = Number(action.Fields,"_JointMaskID");
                        if(action.Type.EndsWith("_SetJointMaskOptionalLayer") && Number(action.Fields,"_LayerID") == addLayer)
                            addMask = Number(action.Fields,"_JointMaskID");
                    }
                if(baseMask < 0 || addMask < 0) return;
                foreach(var list in lists) pairs.Add(new(list,baseId,addId,baseMask,addMask,jointMap));
            }
            foreach(var node in nodes)
            {
                var bases = node.Actions.Where(a => a.Type == "chainsaw.BehaviorTreeAction_MFSM_AppPlayMotion").ToArray();
                var adds = node.Actions.Where(a => a.Type == "chainsaw.BehaviorTreeAction_MFSM_AppPlayMotionOptionalLayer").ToArray();
                if(bases.Length == 1 && adds.Length == 1 && Synchronous(bases[0].Fields) && Synchronous(adds[0].Fields)
                    && Number(bases[0].Fields,"_BankID") == Number(adds[0].Fields,"_BankID"))
                    AddPair(node,Number(bases[0].Fields,"_BankID"),Number(bases[0].Fields,"_MotionID"),
                        Number(adds[0].Fields,"_MotionID"),Number(adds[0].Fields,"_LayerIndex"));
                foreach(var action in node.Actions.Where(a => a.Type == "chainsaw.PlayerBehaviorTreeAction_MFSM_WeaponPutOutVariationMotion"))
                    foreach(var value in action.Fields.Values.OfType<RszInstance>())
                    {
                        var f = Fields(value);
                        if(Flag(f,"_IsOtherLayerSet") && Number(f,"_BankID") == Number(f,"_BankID_Optional"))
                            AddPair(node,Number(f,"_BankID"),Number(f,"_MotionID"),Number(f,"_MotionID_Optional"),Number(f,"_LayerIndex_Optional"));
                    }
            }
            var unique = pairs.Distinct().ToArray();
            var consistent = new List<AnimationLayerPair>();
            foreach(var group in unique.GroupBy(p => (p.Path,p.BaseId)))
            {
                if(group.Count() != 1) { diagnostics.Add($"配对上下文不唯一：{group.Key}"); continue; }
                consistent.Add(group.Single());
            }
            return new(consistent,diagnostics);
        }
        catch(Exception e) { diagnostics.Add(e.GetType().Name+": "+e.Message); return new([],diagnostics); }
    }
}
