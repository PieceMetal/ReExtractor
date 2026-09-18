using ReExtractor.Core;
using ReeLib;
using System.Text.Json;
static class Regression
{
 public static void Run(PakService pak,string[] files)
 {
  var rows=new List<object>();
  foreach(var (id,owner,required) in new[]{("012","em510",new[]{"C_NoseBridge","Left_Eye"}),("021","npc021",new[]{"LeftEye","Jaw_Jnt"}),("024","npc024",new[]{"Skull","Jaw_Jnt","LOD1_NoseBase_C"}),("035","npc035",new[]{"Front_Eye","L_Eye","L_Mouth_Corner_C","L_masseter_muscle"}),("038","em512",new[]{"C_Jaw","Left_Eye"}),("039","em513",new[]{"C_Jaw","Lower_Mask"}),("040","em507",new[]{"C_UOom","L_Eye"})}) {
   var model=$"natives/stm/art/model/character/ch0/ch{id}_00/10/ch{id}_00_10.mesh.260209350";
   var path=files.Where(p=>p.Contains(owner+"_")&&p.Contains("/face/")&&p.Contains(".motlist.")&&!Path.GetFileName(p).StartsWith("plw_")&&(id!="039"||p.Contains("em513_00_archive"))).Order().First();
   using var m=pak.ReadFile(model);var mesh=ViewportDataLoader.LoadMesh(m,model,0,null,false);using var s=pak.ReadFile(path);var clip=ViewportDataLoader.LoadAnimation(s,path,sceneMeshes:new[]{mesh});
   foreach(var name in required)if(!clip.NamedTracks.TryGetValue(name,out var track)||!track.IsAdditive)throw new Exception($"Missing delta: ch{id}/{name}");
   if(id=="021"&&clip.NamedTracks["R_Eye"].IsAdditive)throw new Exception("Absolute eye helper misclassified");
   rows.Add(new{type="face",id,path,required});
  }
  var bodyPaths=files.Where(p=>p.Contains("/motion/talk/")&&p.Contains("/body/")&&p.Contains(".motlist.")&&(p.Contains("npc002")||p.Contains("npc004")||p.Contains("em507"))).GroupBy(p=>p.Contains("npc002")?"npc002":p.Contains("npc004")?"npc004":"em507").Select(g=>g.Order().First()).Concat(files.Where(p=>p.Contains("/em504/face/plw_em504_grapple/")&&p.Contains(".motlist."))).ToArray();
  foreach(var path in bodyPaths) {
   using var s=pak.ReadFile(path);using var list=new MotlistFile(new FileHandler(s,path));list.Read();var count=0;
   foreach(var (entry,i) in list.Motions.Select((x,i)=>(x,i))) {
    if(entry.MotFile is not MotFile)continue;using var a=pak.ReadFile(path);var clip=ViewportDataLoader.LoadAnimation(a,path,i);
    if(clip.NamedTracks.Values.Any(t=>t.IsAdditive)||clip.Tracks.Values.Any(t=>t.IsAdditive))throw new Exception("Absolute motion misclassified "+path);count++;
   }
   if(count==0)throw new Exception("Empty regression "+path);rows.Add(new{type="absolute",path,count});
  }
  File.WriteAllText("artifacts/onimusha-all-faces/regression.json",JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("REGRESSION PASS");
 }
}
