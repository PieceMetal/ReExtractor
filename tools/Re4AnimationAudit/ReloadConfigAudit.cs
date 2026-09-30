using ReExtractor.Core;
using ReeLib;
using System.Collections;
using ReeLib.Common;
static class ReloadConfigAudit {
 public static void Run(PakService pak){
 using var ws=new Workspace(new GameConfig(new GameIdentifier("re4")));
 var mp="natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
 using var ms=pak.ReadFile(mp);var mesh=ViewportDataLoader.LoadMesh(ms,mp,0,null,false);
 var names=mesh.Bones.GroupBy(b=>MurMur3HashUtils.GetHash(b.Name)).ToDictionary(g=>g.Key,g=>g.First().Name);
 var jp="natives/stm/_chainsaw/appsystem/character/ch0common/jointmap/ch0commonjointmap.jmap.19";
 using(var js=pak.ReadFile(jp))using(var jm=new JmapFile(new FileHandler(js,jp))){jm.Read();using var o=new StreamWriter("artifacts/re4-audit/reload-masks.txt");foreach(var group in jm.MaskGroups){o.WriteLine("GROUP "+group.groupId);foreach(var m in group.Masks)o.WriteLine($"{names.GetValueOrDefault(m.jointHash,m.jointHash.ToString())} mask={m.mask} weight={m.weight}");}}
 var paths=new[]{"natives/stm/_chainsaw/appsystem/character/ch0a0z0/ch0a0z0_body.pfb.17","natives/stm/_chainsaw/appsystem/prefab/weapon/wp4000.pfb.17","natives/stm/_chainsaw/appsystem/character/ch0common/userdata/animation/ch0commonanimationcontroluserdata.user.2"};
 foreach(var path in paths){
 try{using var s=pak.ReadFile(path);using BaseRszFile f=path.Contains(".pfb.")?new PfbFile(ws.RszFileOption,new FileHandler(s,path)):new UserFile(ws.RszFileOption,new FileHandler(s,path));f.Read();
 using var output=new StreamWriter("artifacts/re4-audit/"+Path.GetFileName(path)+".txt");
 foreach(var rsz in f.GetAllRSZFiles()) foreach(var inst in rsz.InstanceList){output.WriteLine(inst.ToString()+" "+inst.RszClass.name);foreach(var (field,i) in inst.IndexedFields){if(i>=inst.Values.Length){output.WriteLine("  "+field.name+" = <external/unavailable>");continue;}var v=inst.Values[i];output.WriteLine("  "+field.name+" = "+(v is IList list?string.Join(" | ",list.Cast<object>()):v));}}
 Console.WriteLine("READ "+path);
 }catch(Exception e){Console.WriteLine(e.Message);}
 }
 }
}
