using ReExtractor.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length != 4) throw new ArgumentException("Usage: catalog|preview output-directory game-directory list-file");
var root = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
Shared.Game = Path.GetFullPath(args[2]); Shared.List = Path.GetFullPath(args[3]);
if (args[0] == "catalog")
{
    var pak = Shared.Open();
    var config = Re4AnimationLayerResolver.Resolve(pak);
    Shared.Save(root, "runtime-catalog", config);
    var pairs = config.Pairs.Where(p => p.Composition == AnimationLayerComposition.PlayerShootEndpoint).ToArray();
    Console.WriteLine($"SHOOT_CATALOG lists={pairs.Select(p => p.Path).Distinct().Count()} pairs={pairs.Length} usages={config.Usages?.Count} inherited={config.Sources?.Count(s => s.ListPath != s.SourcePath)}");
    foreach (var diagnostic in config.Diagnostics) Console.WriteLine("DIAGNOSTIC " + diagnostic);
}
else if (args[0] == "preview") PreviewAudit.Run(root);
else if (args[0] == "raw-export-boundary") RawExportBoundary.Run(root);
else if (args[0] == "neutral-preview") PreviewAudit.Run(root,true);
else if (args[0] == "verify-results")
{
    var pak=Shared.Open();ResultVerifier.Run(root,pak,Re4AnimationLayerResolver.Resolve(pak));
}
else if (args[0] == "timing")
{
    var pak=Shared.Open();
    var path="natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4100h.motlist.663";
    var resources=new Re4AnimationResourceCatalog(pak);
    foreach(var id in new[]{160,165,167,500})
    {
        var motion=resources.List(path).Motions.Single(m=>m.Id==id);
        using var s=pak.ReadFile(path);
        var raw=ViewportDataLoader.LoadAnimation(s,path,motion.Index);
        Console.WriteLine($"SOURCE_TIMING id={id} authored={motion.Duration} keyDuration={raw.Duration} fps={motion.FrameRate}");
    }
}
else if (args[0] == "zero-shoot")
{
    var pak=Shared.Open();
    const string path="natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4410h.motlist.663";
    const string body="natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
    using var ms=pak.ReadFile(body);var mesh=ViewportDataLoader.LoadMesh(ms,body,0,null,false);
    using var ns=pak.ReadFile(path);var motions=ViewportDataLoader.ListMotions(ns,path);
    var reports=new List<object>();
    foreach(var id in new[]{500,501,502})
    {
        using var s=pak.ReadFile(path);var raw=ViewportDataLoader.LoadAnimation(s,path,motions.Single(m=>m.MotionNumber==id).SourceIndex,
            sceneMeshes:new[]{mesh});
        var trans=raw.NamedTracks.Values.SelectMany(t=>t.Translations??[]).Select(v=>v.Length()).DefaultIfEmpty().Max();
        var rot=raw.NamedTracks.Values.SelectMany(t=>t.Rotations??[]).Select(q=>1-Math.Abs(q.W)).DefaultIfEmpty().Max();
        var channels=raw.NamedTracks.Select(kv=>new{Bone=kv.Key,TranslationSamples=kv.Value.Translations?.Length??0,
            RotationSamples=kv.Value.Rotations?.Length??0,
            TranslationMax=(kv.Value.Translations??[]).Select(v=>v.Length()).DefaultIfEmpty().Max(),
            RotationDeltaMax=(kv.Value.Rotations??[]).Select(q=>1-Math.Abs(q.W)).DefaultIfEmpty().Max()}).ToArray();
        reports.Add(new{Path=path,Id=id,raw.Duration,Tracks=raw.NamedTracks.Count,TranslationMax=trans,RotationDeltaMax=rot,Channels=channels});
        Console.WriteLine($"ZERO_SHOOT_SOURCE id={id} duration={raw.Duration} tracks={raw.NamedTracks.Count} transMax={trans} rotDeltaMax={rot}");
    }
    Shared.Save(root,"wp4410-source-data",reports);
}
else throw new ArgumentException("Expected catalog or preview mode");

static class Shared
{
    public static string Game = "";
    public static string List = "";
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static PakService Open()
    {
        var pak = new PakService();
        foreach (var file in Directory.GetFiles(Game, "*.pak", SearchOption.AllDirectories).Order()) pak.AddPak(file);
        pak.LoadListFile(List);
        return pak;
    }
    public static void Save(string root, string name, object value) => File.WriteAllText(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(value, Json));
}
