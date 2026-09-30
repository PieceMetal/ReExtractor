using ReExtractor.Core;
using ReeLib;
using System.Text.Json;
var game = @"E:\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4";
var pak=new PakService();
foreach(var p in Directory.GetFiles(game,"*.pak",SearchOption.AllDirectories).Order())pak.AddPak(p);
pak.LoadListFile("artifacts/re4-audit/RE4_STM_Release.list");
var files=pak.EnumerateFiles();
if(args.Contains("--runtime-pairs")){RuntimePairAudit.Run(pak);return;}
if(args.Contains("--811-effect")){ReloadEffectAudit.Run(pak);return;}
if(args.Contains("--classify-layers")){LayerClassificationAudit.Run(pak);return;}
if(args.Contains("--fsm-sweep")){FsmSweepAudit.Run(pak);return;}
if(args.Contains("--putaway-config")){PutAwayConfigAudit.Run(pak);return;}
if(args.Contains("--layer-order")){LayerOrderRegression.Run(pak);return;}
if(args.Contains("--preview-regression")){PreviewRegression.Run(pak);return;}
if(args.Contains("--translation-check")){TranslationCheck.Run(pak);return;}
if(args.Contains("--diagnose-blend")){BlendDiagnosis.Run(pak);return;}
if(args.Contains("--reload-config")){ReloadConfigAudit.Run(pak);return;}
if(args.Contains("--masked-reload")){MaskedReloadAudit.Run(pak);return;}
if(args.Contains("--hand-config")){HandConfigAudit.Run(pak);return;}
if(args.Contains("--mix")){MixAudit.Run(pak);return;}
if(args.Contains("--config")){ConfigAudit.Run(pak);return;}
var paths=files.Where(f=>f.Path.Contains("/cha0/motlist/") && (f.Path.Contains("wp6") || f.Path.Contains("wp4000h.motlist"))).ToArray();
var rows=new List<object>();
foreach(var f in paths){
 using var s=pak.ReadFile(f.Path);using var ml=new MotlistFile(new FileHandler(s,f.Path));ml.Read();
 Console.WriteLine($"LIST {f.Path} base={ml.Header.BaseMotListPath} count={ml.Motions.Count}");
 foreach(var (m,i) in ml.Motions.Select((m,i)=>(m,i))){
 var mot=m.MotFile as MotFile;
 rows.Add(new{path=f.Path,index=i,id=m.motNumber,flags=m.flags.ToString(),mask=m.jointMaskId,type=m.MotFile?.GetType().Name,name=m.MotFile?.Name,frames=mot?.Header.frameCount,tracks=mot?.BoneClips.Count,basePath=ml.Header.BaseMotListPath});
 Console.WriteLine($"{i} id={m.motNumber} flags={m.flags} mask={m.jointMaskId} type={m.MotFile?.GetType().Name} name={m.MotFile?.Name} tracks={mot?.BoneClips.Count}");
 }
}
File.WriteAllText("artifacts/re4-audit/motions.json",JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));

var meshPath="natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
var motionPath="natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
foreach(var index in new[]{2,3,8,10,13,40,41,44,45,50,51,52,53,54,55,60,61}){
 using var ms=pak.ReadFile(meshPath);var mesh=ViewportDataLoader.LoadMesh(ms,meshPath,0,null,false);
 using var a=pak.ReadFile(motionPath);var clip=ViewportDataLoader.LoadAnimation(a,motionPath,index,sceneMeshes:new[]{mesh});
 Console.WriteLine($"SAMPLE {index} bones={mesh.Bones.Length} tracks={clip.NamedTracks.Count} duration={clip.Duration}");
 new ViewportExportService().ConvertToAnimatedGlb(mesh,mesh.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet(),clip,$"artifacts/re4-audit/sample{index}.glb");
}
