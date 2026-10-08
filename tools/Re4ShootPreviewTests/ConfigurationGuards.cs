using ReExtractor.Core;
using ReeLib;
using System.Reflection;

static class ConfigurationGuards
{
    public static void Run(PakService pak)
    {
        static void Reject(Action action, string label)
        {
            try { action(); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { return; }
            catch (InvalidDataException) { return; }
            throw new Exception("Unsupported configuration accepted: " + label);
        }
        var resources = new Re4AnimationResourceCatalog(pak);
        var inheritedPath = resources.Resource("_Chainsaw/Animation/ch/cha0/motlist/cha0_wp4411H.motlist");
        var source = resources.ResolveMotion(inheritedPath, 160);
        if (source.ReferenceChain.Length != 3 || source.SourcePath == inheritedPath)
            throw new Exception("Expected the exact wp4411H→wp4401H→wp4400H inheritance chain");
        Reject(() => resources.Resource("_Chainsaw/Animation/missing-test-resource.motlist"), "Missing resource");
        var cache = (Dictionary<string, Re4AnimationResourceCatalog.MotionList>)typeof(Re4AnimationResourceCatalog)
            .GetField("lists", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resources)!;
        var original = cache[inheritedPath];
        var modified = original.Motions.ToArray();
        var index = Array.FindIndex(modified, m => m.Id == 160);
        modified[index] = modified[index] with { Flags = 1 };
        cache[inheritedPath] = original with { Motions = modified };
        Reject(() => resources.ResolveMotion(inheritedPath, 160), "Mirrored inherited slot");
        cache[inheritedPath] = original with { BasePath = inheritedPath };
        Reject(() => resources.ResolveMotion(inheritedPath, 160), "Cyclic inherited resource");
        cache[inheritedPath] = original;

        var path = resources.Resource("_Chainsaw/AppSystem/Character/ch0Common/Motion/List/cha_HOLD.motlist");
        using var stream = pak.ReadFile(path);
        using var list = new MotlistFile(new FileHandler(stream, path));
        if (!list.Read()) throw new Exception("Tree parse failed");
        var hold = (MotTreeFile)list.Motions.Single(m => m.motNumber == 160).MotFile!;
        var shoot = (MotTreeFile)list.Motions.Single(m => m.motNumber == 500).MotFile!;
        var holdRead = typeof(Re4PlayerShootAnimationResolver).GetMethod("ReadHoldInputs", BindingFlags.NonPublic | BindingFlags.Static)!;
        var shootRead = typeof(Re4PlayerShootAnimationResolver).GetMethod("ReadShotInputs", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var (tree, method, nodeType, parameterName, value) in new (MotTreeFile, MethodInfo, string, string, object)[] {
            (hold, holdRead, "OctSyncBlendNode", "RootTransPolarBlend", true),
            (hold, holdRead, "OctSyncBlendNode", "CircledBlend", true),
            (hold, holdRead, "PlaySpeedNode", "Speed", .5f),
            (shoot, shootRead, "RandomParameterNode", "InMax", 3f),
            (shoot, shootRead, "SelectMotionNode", "BankSelectMode", 1),
            (shoot, shootRead, "SelectMotionNode", "No01_BankID", 1u) })
        {
            var parameter = tree.Nodes.Single(n => n.typeName == nodeType).Parameters.Single(p => p.name == parameterName);
            var old = parameter.value;
            parameter.value = value;
            try { Reject(() => method.Invoke(null, [tree]), parameterName); }
            finally { parameter.value = old; }
        }
        var link = shoot.Links.Single();
        var pin = link.outputPinNo;
        link.outputPinNo = 0;
        try { Reject(() => shootRead.Invoke(null, [shoot]), "Wrong random output pin"); }
        finally { link.outputPinNo = pin; }
        Console.WriteLine("RESOURCE_SCOPE_INHERITANCE_CYCLE_MIRROR_TREE_SPEED_RANDOM_SELECTION_GUARDS_PASS");
    }
}
