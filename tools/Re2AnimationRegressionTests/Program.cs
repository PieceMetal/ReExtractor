using ReExtractor.Core;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using SharpGLTF.Schema2;
if (args.Length is < 3 or > 4) throw new ArgumentException("Usage: <game-directory> <RE2-RT-list> <output-directory>");
var gameDirectory = Path.GetFullPath(args[0]);
var listPath = Path.GetFullPath(args[1]);
Directory.CreateDirectory(args[2]); Directory.SetCurrentDirectory(args[2]);
var source = new PakService();
foreach(var p in Directory.GetFiles(gameDirectory, "*.pak", SearchOption.AllDirectories).Where(p=>new FileInfo(p).Length>0).OrderBy(p=>Path.GetRelativePath(gameDirectory,p).Count(c=>c is '\\' or '/')).ThenBy(p=>Path.GetRelativePath(gameDirectory,p),StringComparer.OrdinalIgnoreCase)) source.AddPak(p);
source.LoadListFile(listPath);
const string meshPath="natives/stm/sectionroot/character/player/pl0000/pl0000/pl0000.mesh.2109108288";
const string movePath="natives/stm/sectionroot/animation/player/pl00/list/cmn/base_cmn_move.motlist.524";
const string figPrefix="natives/stm/sectionroot/animation/figure/pl0000/pl0000_";
ViewportMesh Mesh(){using var s=source.ReadFile(meshPath);return ViewportDataLoader.LoadMesh(s,meshPath,0,loadTextures:false);}
var eval=typeof(ReExtractor.Gui.MainWindow).Assembly.GetType("ReExtractor.Gui.GlViewport")!.GetMethod("EvaluateLocal",BindingFlags.NonPublic|BindingFlags.Static)!;
Vector3[] Pose(ViewportMesh mesh, AnimationClip clip,float time) {
 var g=new Matrix4x4[mesh.Bones.Length];var done=new bool[g.Length];
 Matrix4x4 Global(int i){if(done[i])return g[i];var b=mesh.Bones[i];var local=b.LocalBind;if(clip.NamedTracks.TryGetValue(b.Name,out var tr))local=(Matrix4x4)eval.Invoke(null,new object[]{tr,time,local})!;g[i]=b.ParentIndex>=0?local*Global(b.ParentIndex):local;done[i]=true;return g[i];}
 var joints=mesh.DeformToBone.Select(i=>mesh.Bones[i].InverseGlobalBind*Global(i)).ToArray();
 return mesh.Vertices.Select((v,i)=>mesh.Weights[i].Aggregate(Vector3.Zero,(a,w)=>a+Vector3.Transform(v,joints[w.Joint])*w.Weight)).ToArray();
}
void SavePose(string file,ViewportMesh mesh, AnimationClip clip,float t){var p=Pose(mesh,clip,t);File.WriteAllText(file,JsonSerializer.Serialize(new{vertices=p.Select(v=>new[]{v.X,v.Y,v.Z}),faces=mesh.Faces.Select(f=>new[]{f.A,f.B,f.C})}));}
if (args.Length == 4 && args[3] == "--sweep")
{
    var paths = source.EnumerateFiles().Where(e => e.Path.Contains("/animation/player/pl00/list/", StringComparison.OrdinalIgnoreCase) && e.Path.Contains(".motlist.", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path).OrderBy(p => p).ToArray();
    var rows = new List<object>();
    var counts = new Dictionary<string,int>();
    using var live = new StreamWriter("sweep-progress.log") { AutoFlush = true };
    void Record(string path, int index, string name, string status, string detail, float duration=0, float diagonal=0)
    {
        counts[status] = counts.GetValueOrDefault(status) + 1;
        rows.Add(new { path,index,name,status,detail,duration,diagonal });
        live.WriteLine($"{status} {path} #{index} {name}: {detail} diagonal={diagonal:F3}");
    }
    foreach (var path in paths)
    {
        using var raw=source.ReadFile(path);
        using var list=new ReeLib.MotlistFile(new ReeLib.FileHandler(raw,path));
        try { if(!list.Read())throw new InvalidDataException("MOTLIST parse failed"); }
        catch(Exception ex){Record(path,-1,"","PARSE_ERROR",ex.Message);continue;}
        var embedded=0;
        for(int index=0;index<list.Motions.Count;index++)
        {
            if(list.Motions[index].MotFile is not ReeLib.MotFile mot)continue;
            embedded++;
            var mesh=Mesh();
            try
            {
                using var stream=source.ReadFile(path);
                var clip=ViewportDataLoader.LoadAnimation(stream,path,index,mesh.Bones.Select(b=>b.Name).ToArray(),new[]{mesh});
                float diagonal=0;
                foreach(var track in clip.NamedTracks.Values)
                {
                    if(track.Translations != null && track.TransTimes?.Length != track.Translations.Length)throw new Exception("translation key/time count mismatch");
                    if(track.Scales != null && track.ScaleTimes?.Length != track.Scales.Length)throw new Exception("scale key/time count mismatch");
                    if(track.Rotations != null && track.RotTimes?.Length != track.Rotations.Length)throw new Exception("rotation key/time count mismatch");
                    foreach(var times in new[]{track.TransTimes,track.RotTimes,track.ScaleTimes})
                        if(times != null && times.Zip(times.Skip(1),(a,b)=>b<a).Any(x=>x))throw new Exception("non-monotonic key times");
                }
                for(int sample=0;sample<9;sample++)
                {
                    var pose=Pose(mesh,clip,clip.Duration*sample/8);
                    if(pose.Any(v=>!float.IsFinite(v.X)||!float.IsFinite(v.Y)||!float.IsFinite(v.Z)))throw new Exception("non-finite skinned vertex");
                    diagonal=Math.Max(diagonal,Vector3.Distance(pose.Aggregate(Vector3.Min),pose.Aggregate(Vector3.Max)));
                }
                var status=diagonal>4?"DEFORMATION_REVIEW":"PASS_SAMPLES";
                Record(path,index,mot.Name,status,$"{clip.NamedTracks.Count} tracks, 9 time samples",clip.Duration,diagonal);
                if(status!="PASS_SAMPLES")SavePose($"suspect_{rows.Count}.json",mesh,clip,clip.Duration*.5f);
            }
            catch(InvalidDataException ex){Record(path,index,mot.Name,"REJECTED",ex.Message);}
            catch(Exception ex){Record(path,index,mot.Name,"ERROR",ex.ToString());}
        }
        if(embedded==0)Record(path,-1,"","EXTERNAL_LINKS_ONLY",$"{list.Motions.Count} motion entries; no embedded MOT");
        Console.WriteLine($"Scanned {path}: embedded={embedded}, total={rows.Count}");
    }
    File.WriteAllText("sweep.json",JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
    File.WriteAllText("sweep-summary.json",JsonSerializer.Serialize(new{files=paths.Length,counts},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine(JsonSerializer.Serialize(counts));
    return counts.GetValueOrDefault("ERROR")+counts.GetValueOrDefault("PARSE_ERROR")+counts.GetValueOrDefault("DEFORMATION_REVIEW")>0?1:0;
}
var report=new List<string>();int good=0,blocked=0;
using(var names=source.ReadFile(movePath)) {
 foreach(var mi in ViewportDataLoader.ListMotions(names,movePath)) {
  var mesh=Mesh();using var s=source.ReadFile(movePath);
  try {
   var clip=ViewportDataLoader.LoadAnimation(s,movePath,mi.SourceIndex,mesh.Bones.Select(b=>b.Name).ToArray(),new[]{mesh});
   float extent=0;
   for(int k=0;k<=16;k++){var p=Pose(mesh,clip,clip.Duration*k/16);if(p.Any(v=>!float.IsFinite(v.X)||!float.IsFinite(v.Y)||!float.IsFinite(v.Z)))throw new Exception("nonfinite skinned vertex");var min=p.Aggregate(Vector3.Min);var max=p.Aggregate(Vector3.Max);extent=Math.Max(extent,Vector3.Distance(min,max));}
   if(extent>4)throw new Exception($"body bounds exploded {extent}");
   report.Add($"PASS {mi.DisplayName} duration={clip.Duration:F3} tracks={clip.NamedTracks.Count} maxDiagonal={extent:F3}"); good++;
   if(mi.SourceIndex==0||mi.SourceIndex==8){using var exp=source.ReadFile(movePath);var outFiles=new AnimationService().ConvertOneToGlbWithAnimation(mesh,exp,movePath,mi.SourceIndex,Directory.GetCurrentDirectory());foreach(var f in outFiles){var glb=ModelRoot.Load(f);if(glb.LogicalAnimations.Count!=1)throw new Exception("export animation missing");report.Add($"EXPORTED {f}");}for(int k=0;k<3;k++)SavePose($"{(mi.SourceIndex==0?"idle":"walk")}_{k}.json",mesh,clip,clip.Duration*k/2);}
  }catch(Exception ex){if(mi.SourceIndex==61 && ex is InvalidDataException){report.Add($"EXPECTED REJECTION dummy placeholder: {ex.Message}");}else{report.Add($"FAIL {mi.DisplayName}: {ex.Message}");blocked++;}}
 }
}
foreach(var kind in new[]{"body","face","weapon"}) {
 var path=figPrefix+kind+"_figuremotionlist.motlist.524";
 for(int i=0;i<2;i++){
  var mesh=Mesh();var originalBones=mesh.Bones.Length;
  using var s=source.ReadFile(path);
  try{var clip=ViewportDataLoader.LoadAnimation(s,path,i,mesh.Bones.Select(b=>b.Name).ToArray(),new[]{mesh});if(kind!="body")throw new Exception("incompatible accepted");report.Add($"PASS figure body {i} source root={clip.NamedTracks["root"].Translations![0]}");}
  catch(InvalidDataException ex){if(kind=="body")throw;if(mesh.Bones.Length!=originalBones)throw new Exception("rejected clip mutated skeleton");report.Add($"EXPECTED REJECTION {kind} {i}: {ex.Message}");}
  if(kind!="body"){
   using var exp=source.ReadFile(path);try{new AnimationService().ConvertOneToGlbWithAnimation(mesh,exp,path,i,"rejected");throw new Exception("incompatible export accepted");}catch(InvalidDataException){report.Add($"PASS export rejects {kind} {i}");}
  }
 }
}
// Reproduce the former failure without the compatibility guard, solely for diagnostic comparison.
{var mesh=Mesh();var path=figPrefix+"face_figuremotionlist.motlist.524";using var s=source.ReadFile(path);var clip=ViewportDataLoader.LoadAnimation(s,path,0,mesh.Bones.Select(b=>b.Name).ToArray());SavePose("old_face_failure.json",mesh,clip,6.4f);}
{
 var mesh=Mesh();
 AnimationClip Load(string path,int index){using var s=source.ReadFile(path);return ViewportDataLoader.LoadAnimation(s,path,index,mesh.Bones.Select(b=>b.Name).ToArray(),new[]{mesh});}
 var initial=Pose(mesh,Load(movePath,0),9.1f);var bones=mesh.Bones.Length;
 try{Load(figPrefix+"face_figuremotionlist.motlist.524",0);throw new Exception("face accepted after idle");}catch(InvalidDataException){}
 if(bones!=mesh.Bones.Length)throw new Exception("rejected face mutated loaded skeleton");
 Load(movePath,8);var final=Pose(mesh,Load(movePath,0),9.1f);
 if(initial.Zip(final,(a,b)=>Vector3.Distance(a,b)).Max()>1e-6f)throw new Exception("animation switch retained old pose");
 report.Add("PASS same-mesh switch idle -> rejected face -> walk -> idle: identical final pose, no skeleton mutation on rejection");
 using var ms=source.ReadFile(meshPath);using var fs=source.ReadFile(figPrefix+"face_figuremotionlist.motlist.524");
 try{new AnimationService().ConvertToGlbWithAnimation(ms,meshPath,fs,figPrefix+"face_figuremotionlist.motlist.524","rejected/legacy.glb",0);throw new Exception("legacy export accepted incompatible face");}catch(InvalidDataException){report.Add("PASS legacy export rejects incompatible face");}
}
{
 const string damagePath="natives/stm/sectionroot/animation/player/pl00/list/cmn/base_cmn_damg.motlist.524";
 const string muscle="r_leg_side_muscle";
 var mesh=Mesh();using var s=source.ReadFile(damagePath);
 var clip=ViewportDataLoader.LoadAnimation(s,damagePath,0,mesh.Bones.Select(b=>b.Name).ToArray(),new[]{mesh});
 var track=clip.NamedTracks[muscle];var expected=new Vector3(.8915391f,.91564155f,1);
 if(track.Scales is not {Length:>0} || Vector3.Distance(track.Scales[0],expected)>1e-6f)throw new Exception("RE2 damage muscle scale lost while loading");
 var binding=mesh.Bones.First(b=>b.Name==muscle).LocalBind;
 foreach(var type in new[]{"GlViewport","Viewport3D"}){
  var evaluator=typeof(ReExtractor.Gui.MainWindow).Assembly.GetType("ReExtractor.Gui."+type)!.GetMethod("EvaluateLocal",BindingFlags.NonPublic|BindingFlags.Static)!;
  var local=(Matrix4x4)evaluator.Invoke(null,new object[]{track,0f,binding})!;
  Matrix4x4.Decompose(local,out var scale,out _,out _);
  if(Vector3.Distance(scale,expected)>1e-5f)throw new Exception(type+" ignored scale");
 }
 var scaled=Pose(mesh,clip,0);var original=clip.NamedTracks.Values.Where(t=>t.Scales!=null).Select(t=>(Track:t,Values:t.Scales)).ToArray();
 foreach(var (tr,values) in original)tr.Scales=null;
 var unscaled=Pose(mesh,clip,0);
 foreach(var (tr,values) in original)tr.Scales=values;
 var displacement=scaled.Zip(unscaled,(a,b)=>Vector3.Distance(a,b)).Max();
 if(displacement<1e-5f)throw new Exception("Scale has no effect on weighted body vertices");
 report.Add($"PASS both viewport evaluators apply native muscle scale {expected}; max vertex correction at frame 0={displacement:F6} m");
 using var exp=source.ReadFile(damagePath);var modern=new AnimationService().ConvertOneToGlbWithAnimation(mesh,exp,damagePath,0,"scale-modern").Single();
 using var legacyMesh=source.ReadFile(meshPath);using var legacyMotion=source.ReadFile(damagePath);
 Directory.CreateDirectory("scale-legacy");var legacy=new AnimationService().ConvertToGlbWithAnimation(legacyMesh,meshPath,legacyMotion,damagePath,"scale-legacy/damage.glb",0);
 foreach(var output in new[]{modern,legacy}){
  var bytes=File.ReadAllBytes(output);var jsonLength=BitConverter.ToInt32(bytes,12);using var json=JsonDocument.Parse(bytes.AsMemory(20,jsonLength));var root=json.RootElement;
  var nodes=root.GetProperty("nodes");var animation=root.GetProperty("animations")[0];
  var channel=animation.GetProperty("channels").EnumerateArray().Single(c=>c.GetProperty("target").GetProperty("path").GetString()=="scale" && nodes[c.GetProperty("target").GetProperty("node").GetInt32()].GetProperty("name").GetString()==muscle);
  var sampler=animation.GetProperty("samplers")[channel.GetProperty("sampler").GetInt32()];var accessor=root.GetProperty("accessors")[sampler.GetProperty("output").GetInt32()];var view=root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
  var offset=20+jsonLength+8+(view.TryGetProperty("byteOffset",out var vo)?vo.GetInt32():0)+(accessor.TryGetProperty("byteOffset",out var ao)?ao.GetInt32():0);
  var first=new Vector3(BitConverter.ToSingle(bytes,offset),BitConverter.ToSingle(bytes,offset+4),BitConverter.ToSingle(bytes,offset+8));
  if(Vector3.Distance(first,expected)>1e-6f)throw new Exception("Exported scale key differs from original data");
  ModelRoot.Load(output);report.Add($"PASS scale channel and exact original first key retained: {output}");
 }
}
File.WriteAllLines("audit.txt",report);Console.WriteLine(string.Join("\n",report));Console.WriteLine($"BASIC MOTIONS: {good} PASS, {blocked} FAIL");return blocked==0?0:1;
