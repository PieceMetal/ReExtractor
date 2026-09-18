using ReExtractor.Core;
using ReeLib;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Numerics;
static class OtherCharacters
{
 public static void Run(PakService pak,string[] files)
 {
  var dir="artifacts/onimusha-other-faces";Directory.CreateDirectory(dir);var rows=new List<object>();
  foreach(var path in files.Where(p=>Regex.IsMatch(p,@"/ch0/ch\d{3}_00/10/ch\d{3}_00_10\.mesh\." )).Order())
  {
   var id=Regex.Match(path,@"/ch0/ch(\d{3})").Groups[1].Value;
   try {
    using var s=pak.ReadFile(path);var mesh=ViewportDataLoader.LoadMesh(s,path,0,null,false);
    var exp=new ViewportExportService();var visible=mesh.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet();
    exp.ConvertToGlb(mesh,visible,$"{dir}/ch{id}-static.glb");
    var token=id=="001"?"pl000":"npc"+id;
    var lists=files.Where(p=>p.Contains(token+"_")&&p.Contains("/face/")&&p.Contains(".motlist.")).Order().Take(1).ToArray();
    var count=0;var warnings=new HashSet<string>();
    foreach(var ap in lists){using var a=pak.ReadFile(ap);using var list=new MotlistFile(new FileHandler(a,ap));list.Read();
     foreach(var (entry,i) in list.Motions.Select((m,i)=>(m,i))){if(entry.MotFile is not MotFile)continue;
      using var m=pak.ReadFile(ap);var clip=ViewportDataLoader.LoadAnimation(m,ap,i,sceneMeshes:new[]{mesh});
      foreach(var kv in clip.NamedTracks){
       if(kv.Value.Translations?.Any(v=>!float.IsFinite(v.Length()))==true)throw new Exception("Nonfinite position");
       if(kv.Value.Rotations?.Any(v=>!float.IsFinite(v.Length()))==true)throw new Exception("Nonfinite rotation");
       var b=mesh.Bones.FirstOrDefault(b=>b.Name==kv.Key);if(b==null)continue;
       if(kv.Value.IsAdditive&&Vector3.Distance(kv.Value.ResolveTranslation(Vector3.Zero,b.LocalBind),b.LocalBind.Translation)>1e-7)throw new Exception("Lost neutral bind");
       if(!kv.Value.IsAdditive&&(kv.Key.StartsWith("LOD0_")||kv.Key.StartsWith("LOD1_")||kv.Key.Contains("Eye")||kv.Key.Contains("Jaw")||kv.Key.Contains("Teeth")))warnings.Add(kv.Key);
      }
      if(count==0) {
       Console.WriteLine($"FIRST {id}: {ap} NAME={((MotFile)entry.MotFile).Name}");
       foreach(var kv in clip.NamedTracks.Where(k=>k.Key.Contains("Eye")||k.Key is "Head" or "Neck_1" or "Skull" or "Facial_Root" or "Jaw_Jnt" or "LOD0_UprLp_1_C" or "LOD1_UprLp_1_C"))
        Console.WriteLine($"{kv.Key} add={kv.Value.IsAdditive} raw={kv.Value.Translations?.FirstOrDefault()} bind={mesh.Bones.FirstOrDefault(b=>b.Name==kv.Key)?.LocalBind.Translation} rot={kv.Value.Rotations?.FirstOrDefault()}");
       exp.ConvertToAnimatedGlb(mesh,visible,clip,$"{dir}/ch{id}-face.glb");
      }count++;
     }
    }
    rows.Add(new{id,model=path,lists,actions=count,warnings=warnings.ToArray(),status=count>0?"sample-tested":"static-only"});
    Console.WriteLine($"ch{id}: actions={count}, warnings={string.Join(',',warnings)}");
   }catch(Exception e){rows.Add(new{id,status="failed",error=e.Message});Console.WriteLine($"FAIL ch{id}: {e.Message}");}
  }
  File.WriteAllText(dir+"/results.json",JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
 }
}
