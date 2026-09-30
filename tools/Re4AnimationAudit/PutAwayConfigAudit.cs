using ReExtractor.Core;
using ReeLib;
static class PutAwayConfigAudit {
 public static void Run(PakService pak) {
 using var ws=new Workspace(new GameConfig(new GameIdentifier("re4")));
 var fp="natives/stm/_chainsaw/appsystem/character/ch0common/motion/fsm/ch0commonupperbody.motfsm2.43";
 using var fs=pak.ReadFile(fp);using var f=new Motfsm2File(ws.RszFileOption,new FileHandler(fs,fp));f.Read();
 using var o=new StreamWriter("artifacts/preview-fix/putaway-state.txt");
 foreach(var n in f.BhvtFile.Nodes.Where(n=>true)){
 o.WriteLine($"NODE {n.ID} {n.Name} parent={n.ParentID}");
 foreach(var a in n.Actions.Actions){if(a.Instance==null)continue;o.WriteLine(" ACTION "+a.Instance);foreach(var (field,i) in a.Instance.IndexedFields)o.WriteLine($"   {field.name}={a.Instance.Values[i]}");}
 foreach(var st in n.States.States)o.WriteLine($" STATE -> {st.TargetNode} condition={st.Condition} transition={st.TransitionData}");
 }
 var p="natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
 using var s=pak.ReadFile(p);using var ml=new MotlistFile(new FileHandler(s,p));ml.Read();
 foreach(var idx in new[]{44,45,48,49}) {var mot=(MotFile)ml.Motions[idx].MotFile!;o.WriteLine("MOTION "+mot.Name);foreach(var c in mot.Clips){o.WriteLine("CLIP "+c.MainTrackName);foreach(var t in c.ClipEntry.Tracks){o.WriteLine(" TRACK "+t.Name);foreach(var prop in t.Properties)o.WriteLine($"  {prop} keys={string.Join(" | ",prop.Keys?.Select(k=>k.ToString())??[])}");}}}
 }
}
