using ReExtractor.Core;
using System.Text.Json;
static class TrackDumps
{
 public static void Run(PakService pak)
 {
  var root="artifacts/onimusha-all-faces";var data=JsonDocument.Parse(File.ReadAllText(root+"/results.json"));var rows=new List<object>();
  foreach(var r in data.RootElement.EnumerateArray())foreach(var check in r.GetProperty("checks").EnumerateArray().GroupBy(x=>x.GetProperty("path").GetString()).Select(g=>g.First())) {
   var p=check.GetProperty("path").GetString()!;var model=r.GetProperty("model").GetString()!;using var ms=pak.ReadFile(model);var mesh=ViewportDataLoader.LoadMesh(ms,model,0,null,false);using var s=pak.ReadFile(p);var c=ViewportDataLoader.LoadAnimation(s,p,check.GetProperty("index").GetInt32(),sceneMeshes:new[]{mesh});
   rows.Add(new{id=r.GetProperty("id").GetString(),path=p,tracks=c.NamedTracks.Select(k=>{var b=mesh.Bones.FirstOrDefault(x=>x.Name==k.Key);return new{name=k.Key,add=k.Value.IsAdditive,parent=b!=null&&b.ParentIndex>=0?mesh.Bones[b.ParentIndex].Name:null,bind=b?.LocalBind.Translation.ToString(),raw=k.Value.Translations?.FirstOrDefault().ToString(),rotation=k.Value.Rotations?.FirstOrDefault().ToString()};}).ToArray()});
  }
  File.WriteAllText(root+"/track-dumps.json",JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
 }
}
