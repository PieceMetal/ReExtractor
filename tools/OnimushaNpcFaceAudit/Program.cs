using ReExtractor.Core;
using ReeLib;
var game=@"E:\Steam\steamapps\common\OnimushaWotS";
var pak=new PakService();pak.AddPaksFromGameDir(game);pak.LoadListFile(Path.Combine(game,"OWOTS_STM_Release.list"));var files=pak.EnumerateFiles();
if(args.Contains("--inventory")){Inventory.Run(pak,files.Select(f=>f.Path).ToArray());return;}
if(args.Contains("--bones")){Inventory.Run(pak,files.Select(f=>f.Path).ToArray(),true);return;}
if(args.Contains("--mapping")){Mapping.Run(pak,files.Select(f=>f.Path).ToArray());return;}
if(args.Contains("--full")){FullAudit.Run(pak,files.Select(f=>f.Path).ToArray());return;}
if(args.Contains("--dumps")){TrackDumps.Run(pak);return;}
if(args.Contains("--regression")){Regression.Run(pak,files.Select(f=>f.Path).ToArray());return;}
if(args.Contains("--exports")){ExportChecks.Run(pak);return;}
if(args.Contains("--others")){OtherCharacters.Run(pak,files.Select(f=>f.Path).ToArray());return;}
var dir="artifacts/onimusha-npc-face";Directory.CreateDirectory(dir);
var mp="natives/stm/art/model/character/ch0/ch004_00/10/ch004_00_10.mesh.260209350";
using var ms=pak.ReadFile(mp);var mesh=ViewportDataLoader.LoadMesh(ms,mp,0,null,false);
var ex=new ViewportExportService();var visible=mesh.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet();ex.ConvertToGlb(mesh,visible,dir+"/ch004-static.glb");
Console.WriteLine("BONES "+string.Join(",",mesh.Bones.Select(b=>b.Name)));
foreach(var path in files.Select(f=>f.Path).Where(p=>p.Contains("npc004")&&p.Contains("/face/")&&p.Contains(".motlist.")).Take(2))
{
 using var s=pak.ReadFile(path);using var list=new MotlistFile(new FileHandler(s,path));list.Read();
 foreach(var (entry,i) in list.Motions.Select((m,i)=>(m,i)))
 {
  if(entry.MotFile is not MotFile mot)continue;
  Console.WriteLine($"MOTION {mot.Name} v={mot.Header.version}");
  using var a=pak.ReadFile(path);var clip=ViewportDataLoader.LoadAnimation(a,path,i,sceneMeshes:new[]{mesh});
  Console.WriteLine($"TRACKS {clip.NamedTracks.Count} additive={clip.NamedTracks.Values.Count(t=>t.IsAdditive)}");
  foreach(var kv in clip.NamedTracks.Where(kv=>kv.Key=="Head"||kv.Key=="Neck_1"||kv.Key=="Facial_Root"||kv.Key=="Skull"||kv.Key=="R_Eye"||kv.Key=="LeftEye"||kv.Key=="Jaw_Jnt"||kv.Key=="UprTeeth_Jnt"||kv.Key=="LOD0_UprLp_1_C"))Console.WriteLine($"{kv.Key} add={kv.Value.IsAdditive} first={kv.Value.Translations?.FirstOrDefault()} bind={mesh.Bones.FirstOrDefault(b=>b.Name==kv.Key)?.LocalBind.Translation} rot={kv.Value.Rotations?.FirstOrDefault()}");
  if(clip.NamedTracks.Values.Count(t=>t.IsAdditive)<320)throw new Exception("NPC facial tracks omitted");
  if(clip.NamedTracks.Any(kv=>kv.Key.StartsWith("bsControl")&&kv.Value.IsAdditive))throw new Exception("Morph control misclassified");
  foreach(var kv in clip.NamedTracks.Where(kv=>kv.Value.IsAdditive)) {
   var bone=mesh.Bones.FirstOrDefault(b=>b.Name==kv.Key);if(bone==null)continue;
   if(System.Numerics.Vector3.Distance(kv.Value.ResolveTranslation(System.Numerics.Vector3.Zero,bone.LocalBind),bone.LocalBind.Translation)>1e-7)throw new Exception("Neutral bind lost");
  }
  if(i==0)ex.ConvertToAnimatedGlb(mesh,visible,clip,dir+"/"+mot.Name+".glb");
 }
}
