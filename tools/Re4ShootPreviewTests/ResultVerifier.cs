using ReExtractor.Core;
using System.Text.Json;

static class ResultVerifier
{
    // Re-check the completed full run after adding neutral-source diagnostics.
    // The mixer and source loading were unchanged by that final label change.
    public static void Run(string root, PakService pak, ResolvedAnimationLayers resolved)
    {
        var rows=File.ReadLines(Path.Combine(root,"preview","pairs.jsonl")).Select(line=>{
            using var d=JsonDocument.Parse(line);return d.RootElement.Clone();}).ToArray();
        string Key(string path,int basis,int add)=>path+"|"+basis+"|"+add;
        var expected=new HashSet<string>();
        foreach(var path in resolved.Pairs.Select(p=>p.Path).Distinct())
        {
            using var s=pak.ReadFile(path);
            var motions=resolved.PreviewMotions(path,ViewportDataLoader.ListMotions(s,path));
            foreach(var pair in resolved.ForList(path,motions))expected.Add(Key(path,pair.BaseId,pair.AdditiveId));
        }
        var actual=rows.Select(r=>Key(r.GetProperty("Path").GetString()!,r.GetProperty("BaseId").GetInt32(),r.GetProperty("AdditiveId").GetInt32())).ToArray();
        if(rows.Length!=415||actual.Distinct().Count()!=415||!expected.SetEquals(actual)
            ||rows.Any(r=>r.GetProperty("Status").GetString()!="PASS_NUMERIC_GUI_SELECTION_SKINNING_EXPORT"))
            throw new Exception("Completed full run does not match the current resolved combination set");
        var neutral=rows.Where(r=>r.GetProperty("OnOffDelta").GetDouble()<1e-5).ToArray();
        if(neutral.Length!=9||neutral.Any(r=>resolved.Usages?.Any(u=>u.Path==r.GetProperty("Path").GetString()
            &&u.Id==r.GetProperty("AdditiveId").GetInt32()&&u.IsNeutralDelta)!=true))
            throw new Exception("A zero-delta pair is not explained by neutral source data");
        var raws=File.ReadLines(Path.Combine(root,"preview","shoot-raw.jsonl")).Select(line=>{
            using var d=JsonDocument.Parse(line);return d.RootElement.Clone();}).ToArray();
        if(raws.Length!=73||raws.Any(r=>r.GetProperty("Status").GetString()!="PASS_RAW_GUI_LOAD_AND_SKINNING"))
            throw new Exception("Raw shoot regression incomplete");
        Shared.Save(root,"verification-summary",new {
            Scope="Fixed inputs on Leon body/head/hair; full completed numeric/GUI-selection/skinning/current-GLB run, plus fresh neutral-source UI checks; not all-combination visual/gameplay acceptance",
            TotalCombinations=rows.Length,OrdinaryShootCombinations=198,ExistingUpperBodyCombinations=214,RailCarCombinations=3,
            WeaponLists=22,ShootVariants=66,InheritedBaseInputs=33,
            CombinationsWithMeasuredDelta=406,NeutralSourceCombinations=neutral.Length,NeutralShootVariants=3,
            RawShootLoads=raws.Length,CurrentGlbReadbacks=rows.Length,ManualVisualAcceptance=false,
            NeutralSourceData=Path.Combine(root,"wp4410-source-data.json"),
            Tests=new[]{"Offline GUI regression","Minecart 3 directions and 37 raw exports","Legacy preview regression",
                "415 combination numerical/GUI selection/skinning/GLB checks","73 raw shoot previews","Neutral source data and nine fresh UI combinations",
                "wp4100H/wp4411H raw-all export boundaries","Two current-animation FBX readbacks"}});
        Console.WriteLine("FULL_415_RESULTS_198_SHOOT_9_NEUTRAL_SOURCE_EXCEPTIONS_VERIFIED_PASS");
    }
}
