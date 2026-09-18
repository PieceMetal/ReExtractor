using ReExtractor.Core;
using ReeLib;
using System.Text.Json;
using System.Text.RegularExpressions;
static class Inventory
{
 public static void Run(PakService pak,string[] files,bool bonesOnly=false)
 {
  var rows=new List<object>();
  foreach(var p in files.Where(p=>!bonesOnly&&p.Contains("/face/")&&p.Contains(".motlist.")).Order()) {
   try {
    using var s=pak.ReadFile(p);using var l=new MotlistFile(new FileHandler(s,p));l.Read();
    var motions=l.Motions.Where(m=>m.MotFile is MotFile).Select(m=>(MotFile)m.MotFile!).ToArray();
    rows.Add(new{path=p,motions=motions.Select(m=>new{name=m.Name,bones=m.Bones.Count,tracks=m.BoneClips.Count}).ToArray(),external=l.Motions.Count-motions.Length});
   }catch(Exception e){rows.Add(new{path=p,error=e.Message});}
  }
  Directory.CreateDirectory("artifacts/onimusha-all-faces");
  if(!bonesOnly)File.WriteAllText("artifacts/onimusha-all-faces/inventory.json",JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
  foreach(var id in new[]{"021","024","035"}) {
   var p=files.Where(p=>p.Contains("npc"+id+"_")&&p.Contains("/face/")&&p.Contains(".motlist.")).Order().First();
   var mp=$"natives/stm/art/model/character/ch0/ch{id}_00/10/ch{id}_00_10.mesh.260209350";
   using var ms=pak.ReadFile(mp);var mesh=ViewportDataLoader.LoadMesh(ms,mp,0,null,false);
   using var s=pak.ReadFile(p);using var l=new MotlistFile(new FileHandler(s,p));l.Read();var m=(MotFile)l.Motions.First(a=>a.MotFile is MotFile).MotFile!;
   using var a=pak.ReadFile(p);var c=ViewportDataLoader.LoadAnimation(a,p,sceneMeshes:new[]{mesh});
   File.WriteAllText($"artifacts/onimusha-all-faces/ch{id}-bones.json",JsonSerializer.Serialize(mesh.Bones.Select(b=>new{name=b.Name,parent=b.ParentIndex>=0?mesh.Bones[b.ParentIndex].Name:null,bind=b.LocalBind.Translation.ToString(),track=c.NamedTracks.TryGetValue(b.Name,out var t)?new{add=t.IsAdditive,raw=t.Translations?.FirstOrDefault().ToString(),rotation=t.Rotations?.FirstOrDefault().ToString()}:null,motParent=m.Bones.FirstOrDefault(x=>x.boneName==b.Name)?.Parent?.boneName}),new JsonSerializerOptions{WriteIndented=true}));
  }
 }
}
