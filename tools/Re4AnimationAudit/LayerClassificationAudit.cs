using ReExtractor.Core;
using ReeLib;
using System.Collections;

static class LayerClassificationAudit
{
    public static void Run(PakService pak)
    {
        using var ws = new Workspace(new GameConfig(new GameIdentifier("re4")));
        const string prefabPath = "natives/stm/_chainsaw/appsystem/character/ch0a0z0/ch0a0z0_body.pfb.17";
        using var ps = pak.ReadFile(prefabPath);
        using var prefab = new PfbFile(ws.RszFileOption, new FileHandler(ps, prefabPath)); prefab.Read();
        object Field(RszInstance instance, string name) => instance.IndexedFields
            .Where(f => f.field.name == name).Select(f => instance.Values[f.index]).Single();
        var instances = prefab.GetAllRSZFiles().SelectMany(r => r.InstanceList).ToArray();
        var layers = ((IList)Field(instances.Single(i => i.RszClass.name == "via.motion.Motion"), "Layer")).Cast<RszInstance>().ToArray();
        var fsmLayers = ((IList)Field(instances.First(i => i.RszClass.name == "via.motion.MotionFsm2"), "v11_Layer")).Cast<RszInstance>().ToArray();
        if (Convert.ToInt32(Field(layers[5], "BlendMode")) != 1
            || !Field(fsmLayers[5], "MotionFsm2Resource").ToString()!.EndsWith("ch0CommonUpperBodyAdd.motfsm2")
            || Convert.ToInt32(Field(fsmLayers[5], "TargetMotionLayerNo")) != -1
            || Convert.ToBoolean(Field(fsmLayers[5], "OverwriteBlendMode")))
            throw new Exception("UpperBodyAdd is no longer on an additive layer");
        Console.WriteLine("REAL_UPPER_BODY_ADD_LAYER_5_PASS");
        const string fp = "natives/stm/_chainsaw/appsystem/character/ch0common/motion/fsm/ch0commonupperbodyadd.motfsm2.43";
        using var fs = pak.ReadFile(fp); using var fsm = new Motfsm2File(ws.RszFileOption, new FileHandler(fs, fp)); fsm.Read();
        const string path = "natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
        using var ls = pak.ReadFile(path); var motions = ViewportDataLoader.ListMotions(ls, path);
        bool foundDanger = false;
        foreach (var node in fsm.BhvtFile.Nodes)
        foreach (var action in node.Actions.Actions)
        {
            var instance = action.Instance;
            if (instance?.RszClass.name != "chainsaw.BehaviorTreeAction_MFSM_AppPlayMotion"
                || !Convert.ToBoolean(Field(instance, "v0_Enabled"))
                || Convert.ToUInt32(Field(instance, "_BankID")) != 2000) continue;
            var id = Convert.ToInt32(Field(instance, "_MotionID"));
            var motion = motions.FirstOrDefault(m => m.MotionNumber == id);
            if (motion == null) continue;
            Console.WriteLine($"ACTIVE_ADD node={node.Name} id={id} name={motion.DisplayName} classified={AnimationLayerCatalog.IsAdditive(path, motion)}");
            if (!AnimationLayerCatalog.IsAdditive(path, motion)) throw new Exception("Active ADD misclassified: " + id);
            if (id == 161 && node.Name == "wp4000H_0161_hold_danger_loop") foundDanger = true;
        }
        if (!foundDanger) throw new Exception("161 additive state was not found");
        Console.WriteLine("REAL_161_ACTIVE_ADDITIVE_STATE_PASS");
    }
}
