using ReExtractor.Core;
using ReeLib;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
static class FullAudit
{
 public static void Run(PakService pak,string[] files)
 {
  var dir="artifacts/onimusha-all-faces";Directory.CreateDirectory(dir);
  var inventory=JsonDocument.Parse(File.ReadAllText(dir+"/inventory.json")).RootElement.EnumerateArray().Where(x=>x.TryGetProperty("motions",out var a)&&a.GetArrayLength()>0).Select(x=>x.GetProperty("path").GetString()!).ToArray();
  var rows=new List<object>();var export=new ViewportExportService();
  foreach(var model in files.Where(p=>Regex.IsMatch(p,@"/ch0/ch\d{3}_00/10/ch\d{3}_00_10\.mesh\." )).Order()) {
   var id=Regex.Match(model,@"/ch0/ch(\d{3})").Groups[1].Value;var token=id=="001"?"pl000":"npc"+id;
   // Some ch0 heads belong to enemies, not same-numbered NPCs. Resolve their
   // owner from the character prefab's actual mesh resource references.
   var mapping=JsonDocument.Parse(File.ReadAllText(dir+"/mapping.json"));
   var owners=mapping.RootElement.EnumerateArray().Where(x=>x.GetProperty("path").GetString()!.Contains("/_prefab/character/")&&x.GetProperty("resources").EnumerateArray().Any(v=>v.GetString()!.Contains($"/ch{id}_00/10/ch{id}_00_10.mesh"))).Select(x=>Regex.Match(Path.GetFileName(x.GetProperty("path").GetString()!),@"em\d{3}").Value).Where(x=>x.Length>0).Append(token).Distinct().ToArray();
   var candidates=inventory.Where(p=>owners.Any(owner=>p.Contains(owner+"_"))&&!Path.GetFileName(p).StartsWith("plw_",StringComparison.OrdinalIgnoreCase));
   var lists=candidates.GroupBy(p=>p.Contains("/cutscene/")?"cinematic":p.Contains("/talk/")?"talk":"common").Select(g=>(kind:g.Key,path:g.Order().First())).ToArray();
   var actions=0;var checks=new List<object>();
   foreach(var (kind,path) in lists) {
    using var ms=pak.ReadFile(model);var mesh=ViewportDataLoader.LoadMesh(ms,model,0,null,false);var visible=mesh.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet();
    using var s=pak.ReadFile(path);using var list=new MotlistFile(new FileHandler(s,path));list.Read();
    var exportDone=false;
    foreach(var (entry,i) in list.Motions.Select((m,i)=>(m,i))) {
     if(entry.MotFile is not MotFile mot)continue;
     using var a=pak.ReadFile(path);var c=ViewportDataLoader.LoadAnimation(a,path,i,sceneMeshes:new[]{mesh});
     if(c.NamedTracks.Any(k=>k.Key.StartsWith("bsControl")&&k.Value.IsAdditive))throw new Exception("Morph treated as bone "+path);
     var suspicious=new List<string>();
     foreach(var (name,t) in c.NamedTracks) {
      if(t.Translations?.Any(v=>!float.IsFinite(v.Length()))==true||t.Rotations?.Any(v=>!float.IsFinite(v.Length()))==true)throw new Exception("Nonfinite "+path);
      var b=mesh.Bones.FirstOrDefault(b=>b.Name==name);if(b==null)continue;
      if(t.IsAdditive) {
       if(Vector3.Distance(t.ResolveTranslation(Vector3.Zero,b.LocalBind),b.LocalBind.Translation)>1e-7)throw new Exception("Lost neutral translation "+name);
       if(Math.Abs(Quaternion.Dot(t.ResolveRotation(Quaternion.Identity,b.LocalBind),Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(b.LocalBind))))<.99999)throw new Exception("Lost neutral rotation "+name);
      } else if(!name.StartsWith("bsControl")&&t.Translations is {Length:>0} tr&&tr.All(v=>v.Length()<1e-6)&&b.LocalBind.Translation.Length()>.001) suspicious.Add(name);
     }
     checks.Add(new{path,index=i,motion=mot.Name,additive=c.NamedTracks.Values.Count(t=>t.IsAdditive),suspicious});actions++;
     if(!exportDone) {
      export.ConvertToAnimatedGlb(mesh,visible,c,$"{dir}/ch{id}-{kind}.glb");
      if(id is "023" or "024" or "035") {
       using var e=pak.ReadFile(path);new AnimationService().ConvertOneToGlbWithAnimation(mesh,e,path,i,$"{dir}/export-ch{id}-{kind}");
       using var lm=pak.ReadFile(model);using var la=pak.ReadFile(path);new AnimationService().ConvertToGlbWithAnimation(lm,model,la,path,$"{dir}/legacy-ch{id}-{kind}.glb",i);
      }
      exportDone=true;
     }
    }
   }
   rows.Add(new{id,model,owners,actions,checks});Console.WriteLine($"ch{id}: {actions} motions, {lists.Length} lists");
   File.WriteAllText(dir+"/results.json",JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
  }
 }
}
