using ReExtractor.Core;
using ReeLib;
static class ConfigAudit {
 public static void Run(PakService pak) {
 foreach(var bank in new[]{"cha0_wp4000","cha0_wp6000","cha0_wp6001"}) { try { var bp=$"natives/stm/_chainsaw/animation/ch/cha0/motbank/{bank}.motbank.3";using var bs=pak.ReadFile(bp);using var bf=new MotbankFile(new FileHandler(bs,bp));bf.Read();foreach(var e in bf.MotlistItems)Console.WriteLine($"BANK {bank} {e}"); }catch(FileNotFoundException){Console.WriteLine($"BANK {bank} missing");} }
using var ws=new Workspace(new GameConfig(new GameIdentifier("re4")));
 foreach(var name in new[]{"ch0commonupperbody","ch0commonupperbodyadd","ch0commonarm"}) {
 var path=$"natives/stm/_chainsaw/appsystem/character/ch0common/motion/fsm/{name}.motfsm2.43";
 using var s=pak.ReadFile(path);using var f=new Motfsm2File(ws.RszFileOption,new FileHandler(s,path));f.Read();
 using var output=new StreamWriter($"artifacts/re4-audit/{name}.txt");
 foreach(var node in f.BhvtFile.Nodes){output.WriteLine("NODE "+node.FullName);foreach(var act in node.Actions.Actions)output.WriteLine(" ACTION "+act.Instance);}
 foreach(var rsz in f.GetAllRSZFiles()) foreach(var inst in rsz.InstanceList){
 output.WriteLine(inst.RszClass.name);
 foreach(var (field,i) in inst.IndexedFields) output.WriteLine($"  {field.name} = {inst.Values[i]}");
 }
 Console.WriteLine($"CONFIG {name} nodes={f.BhvtFile.Nodes.Count}");
 }
 }
}
