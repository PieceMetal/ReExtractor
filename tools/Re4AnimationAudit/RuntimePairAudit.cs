using ReExtractor.Core;
using System.Numerics;
using System.Text.Json;

static class RuntimePairAudit
{
    public static void Run(PakService pak)
    {
        var configuration = Re4AnimationLayerResolver.Resolve(pak);
        Console.WriteLine($"RESOLVED definitions={configuration.Pairs.Count} lists={configuration.Pairs.Select(p=>p.Path).Distinct().Count()} diagnostics={configuration.Diagnostics.Count}");
        foreach(var d in configuration.Diagnostics) Console.WriteLine("DIAGNOSTIC "+d);
        if(configuration.Pairs.Count==0)throw new Exception("Live configuration produced no pairs");
        const string meshPath="natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
        var results = new List<object>();
        var allPairs = new List<AnimationLayerPair>();
        int errorCount = 0;
        foreach(var path in configuration.Pairs.Select(p=>p.Path).Distinct().Order())
        {
            using var ns=pak.ReadFile(path);
            var motions=ViewportDataLoader.ListMotions(ns,path);
            var pairs=configuration.ForList(path,motions);
            allPairs.AddRange(pairs);
            using var ms=pak.ReadFile(meshPath);
            var mesh=ViewportDataLoader.LoadMesh(ms,meshPath,0,null,false);
            var clips=new Dictionary<int,AnimationClip>();
            AnimationClip Load(int id)
            {
                if(clips.TryGetValue(id,out var clip))return clip;
                using var s=pak.ReadFile(path);
                return clips[id]=ViewportDataLoader.LoadAnimation(s,path,motions.Single(m=>m.MotionNumber==id).SourceIndex,sceneMeshes:new[]{mesh});
            }
            var errors=new List<string>();
            foreach(var pair in pairs)
            {
                try
                {
                    var basis=Load(pair.BaseId);var add=Load(pair.AdditiveId);
                    var profile=AnimationLayerCatalog.GetProfile(path,pair.BaseId,pair.AdditiveId,pairs)!;
                    var upperMask=AnimationPreviewProfiles.ReadRe4Mask(pak,mesh.Bones,profile.BaseMaskId,profile.JointMapPath);
                    var addMask=AnimationPreviewProfiles.ReadRe4Mask(pak,mesh.Bones,profile.AdditiveMaskId,profile.JointMapPath);
                    foreach(var weight in new[]{0f,.5f,1f})
                    {
                        var combined=AnimationPreviewMixer.ComposeUpperBody(basis,add,mesh.Bones,upperMask,addMask,weight);
                        foreach(var track in combined.NamedTracks.Values)
                        {
                            if(track.Rotations!.Any(q=>!float.IsFinite(q.LengthSquared())||Math.Abs(q.LengthSquared()-1)>1e-4))throw new Exception("Invalid rotation");
                            if(track.Translations!.Any(v=>!float.IsFinite(v.LengthSquared())))throw new Exception("Invalid translation");
                        }
                    }
                    var candidates=AnimationLayerCatalog.Find(path,motions.Single(m=>m.MotionNumber==pair.BaseId),motions,pairs);
                    if(candidates.Count!=1||candidates[0].Motion.MotionNumber!=pair.AdditiveId)throw new Exception("Wrong live pair candidate");
                }
                catch(Exception ex) { errors.Add($"{pair.BaseId}+{pair.AdditiveId}: {ex.Message}"); }
            }
            results.Add(new {Path=path,Motions=motions.Count,Pairs=pairs,Errors=errors});
            errorCount += errors.Count;
            Console.WriteLine($"RUNTIME_LIST {Path.GetFileName(path)} motions={motions.Count} pairs={pairs.Length} errors={errors.Count}");
            foreach(var error in errors)Console.WriteLine("ERROR "+error);
        }
        File.WriteAllText("artifacts/preview-fix/runtime-pairs-r10.json",JsonSerializer.Serialize(new{configuration.Diagnostics,Lists=results},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"RUNTIME_PAIR_AUDIT_COMPLETE lists={results.Count} pairs={allPairs.Count}");
        if(errorCount > 0)throw new Exception($"Runtime pair audit had {errorCount} failures");
    }
}
