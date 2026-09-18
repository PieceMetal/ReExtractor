using ReExtractor.Core;
using System.Text.Json;
static class ExportChecks
{
 public static void Run(PakService pak)
 {
  var root="artifacts/onimusha-all-faces";var data=JsonDocument.Parse(File.ReadAllText(root+"/results.json"));Directory.CreateDirectory(root+"/route-main");Directory.CreateDirectory(root+"/route-legacy");
  foreach(var r in data.RootElement.EnumerateArray().Where(r=>new[]{"012","023","024","035","038","039","040"}.Contains(r.GetProperty("id").GetString())))foreach(var check in r.GetProperty("checks").EnumerateArray().GroupBy(x=>x.GetProperty("path").GetString()).Select(g=>g.First())) {
   var p=check.GetProperty("path").GetString()!;var model=r.GetProperty("model").GetString()!;var i=check.GetProperty("index").GetInt32();
   var kind=p.Contains("/cutscene/")?"cinematic":p.Contains("/talk/")?"talk":"common";var name="ch"+r.GetProperty("id").GetString()+"-"+kind;
   using var ms=pak.ReadFile(model);var mesh=ViewportDataLoader.LoadMesh(ms,model,0,null,false);using(var a=pak.ReadFile(p))ViewportDataLoader.LoadAnimation(a,p,i,sceneMeshes:new[]{mesh});
   using(var s=pak.ReadFile(p)){var result=new AnimationService().ConvertOneToGlbWithAnimation(mesh,s,p,i,root+"/route-temp");File.Copy(result[0],root+"/route-main/"+name+".glb",true);}
   using var lm=pak.ReadFile(model);using var ls=pak.ReadFile(p);new AnimationService().ConvertToGlbWithAnimation(lm,model,ls,p,root+"/route-legacy/"+name+".glb",i);
   Console.WriteLine("EXPORT "+name);
  }
 }
}
