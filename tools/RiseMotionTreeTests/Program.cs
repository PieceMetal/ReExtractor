using System.Numerics;
using System.Reflection;
using System.Text.Json;
using ReExtractor.Core;
using ReeLib;
using SharpGLTF.Schema2;

if(args.Length<3)throw new ArgumentException("<Rise directory> <Rise list> <output directory>");
var output=Path.GetFullPath(args[2]);Directory.CreateDirectory(output);
var pak=new PakService();pak.AddPaksFromGameDir(args[0]);pak.LoadListFile(args[1]);
var files=pak.EnumerateFiles().Select(f=>f.Path).ToArray();
var failures=new List<string>();var cases=new List<object>();int checkedMotions=0,treeLoads=0,missingTrees=0,exports=0,guards=0;
void Check(bool ok,string message){if(!ok)failures.Add(message);}
void Save()=>File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new{checkedMotions,treeLoads,missingTrees,exports,guards,failures,cases},new JsonSerializerOptions{WriteIndented=true}));
var build=typeof(ViewportDataLoader).GetMethod("BuildMotClip",BindingFlags.Static|BindingFlags.NonPublic)!;
AnimationClip Raw(MotFile mot,int id,string path)=> (AnimationClip)build.Invoke(null,[mot,id,path,mot.Bones.Select(b=>b.boneName).ToArray(),null,true,true])!;
string[] weapons=["greatsword","longsword","shortsword","dualblades","hammer","horn","lance","gunlance","slashaxe","chargeaxe","insectglaive","lightbowgun","heavybowgun","bow"];
var rawClips=new Dictionary<(string,int),AnimationClip>();
if(args.Contains("--resource-guards")){ResourceGuards.Run(pak,output);return 0;}
foreach(var weapon in args.Contains("--trees-only")?Array.Empty<string>():weapons){
 var path=$"natives/stm/player/mot/plw_{weapon}_100.motlist.528";using var s=pak.ReadFile(path);using var list=new MotlistFile(new FileHandler(s,path));list.Read();
 using(var names=pak.ReadFile(path))Check(ViewportDataLoader.ListMotions(names,path,pak.ReadFile).Count==list.Motions.Count,"Direct listing changed: "+weapon);
 for(var i=0;i<list.Motions.Count;i++){
  var m=list.Motions[i];var mot=(MotFile)m.MotFile!;var originalLengths=mot.BoneClips.Select(c=>(c.Translation?.translations?.Length,c.Rotation?.rotations?.Length,c.Scale?.translations?.Length)).ToArray();
  try{
   var clip=Raw(mot,m.motNumber,path);checkedMotions++;
   Check(clip.Duration+1e-6>=mot.Header.frameCount/Math.Max(1,(int)mot.Header.FrameRate),$"Clock truncated {weapon}/{m.motNumber}");
   Validate(clip,$"{weapon}/{m.motNumber}");
   Check(originalLengths.SequenceEqual(mot.BoneClips.Select(c=>(c.Translation?.translations?.Length,c.Rotation?.rotations?.Length,c.Scale?.translations?.Length))),"Source data mutated "+weapon);
   if(i==0||i==list.Motions.Count/2||i==list.Motions.Count-1){using var ss=pak.ReadFile(path);var viaPublic=ViewportDataLoader.LoadAnimation(ss,path,i,mot.Bones.Select(b=>b.boneName).ToArray(),fileResolver:pak.ReadFile);
    Check(Math.Abs(viaPublic.Duration-clip.Duration)<1e-6 && viaPublic.NamedTracks.Count==clip.NamedTracks.Count,"Public dispatch diverged "+weapon);}
  }catch(Exception ex){failures.Add($"Direct {weapon}/{m.motNumber}: {ex}");}
 }
 Console.WriteLine($"DIRECT {weapon} {list.Motions.Count} checked; failures={failures.Count}");Save();
}
const string playerPath="natives/stm/player/mod/m/bone/m_shadow.mesh.2109148288";
using var playerStream=pak.ReadFile(playerPath);var player=ViewportDataLoader.LoadMesh(playerStream,playerPath,openResource:pak.ReadFile,loadTextures:false);
var boneNames=player.Bones.Select(b=>b.Name).ToArray();
var treePaths=files.Where(p=>p.Contains("/player/mot/")&&(p.Contains("/plw_")||p.Contains("/plc_common_tree_"))&&p.Contains("_tree_")&&p.EndsWith(".motlist.528")).ToArray();
foreach(var path in treePaths){
 using var s=pak.ReadFile(path);using var list=new MotlistFile(new FileHandler(s,path));list.Read();
 using var ns=pak.ReadFile(path);var motions=ViewportDataLoader.ListMotions(ns,path,pak.ReadFile);
 Check(motions.Count==list.Motions.Count,"Trees hidden in list "+path);
 foreach(var choice in motions){
  var tree=(MotTreeFile)list.Motions[choice.SourceIndex].MotFile!;
  var info=choice.Tree!;
  Console.WriteLine($"TREE_CHECK {path} #{choice.MotionNumber} available={info.Available} nodes={string.Join(',',tree.Nodes.Select(n=>n.typeName))} {info.Description}");
  if(!info.Available){
   missingTrees++;var missingInput=path.Contains("greatsword")&&choice.MotionNumber is 6 or 7;
   Check(missingInput || path.Contains("plc_common_tree")&&choice.MotionNumber is 6 or 7,"Unexpected unavailable tree: "+info.Description);
   try{using var bad=pak.ReadFile(path);ViewportDataLoader.LoadAnimation(bad,path,choice.SourceIndex,boneNames,fileResolver:pak.ReadFile);failures.Add("Missing input accepted "+path);}
   catch(Exception ex) when(ex is InvalidDataException or NotSupportedException){Check(!missingInput || ex.Message.Contains("Bank 100")&&ex.Message.Contains("Motion"),"Missing input diagnosis incomplete: "+ex.Message);}
   cases.Add(new{path,id=choice.MotionNumber,available=false,diagnostic=info.Description});continue;
  }
  var variable=info.Variable!;var three=tree.Nodes.Any(n=>n.typeName=="OctSyncBlendNode");
  var rates=three?new[]{-1f,-0.5f,0f,0.5f,1f}:new[]{0f,0.25f,0.5f,0.75f,1f};
  var sources=new List<AnimationClip>();
  foreach(var source in info.Sources){var key=(source.ListPath,source.SourceIndex);if(!rawClips.TryGetValue(key,out var clip)){
    using var ss=pak.ReadFile(source.ListPath);clip=ViewportDataLoader.LoadAnimation(ss,source.ListPath,source.SourceIndex,boneNames,fileResolver:pak.ReadFile);rawClips[key]=clip;}
   sources.Add(clip);}
  foreach(var rate in rates){
   try{
    using var ss=pak.ReadFile(path);var clip=ViewportDataLoader.LoadAnimation(ss,path,choice.SourceIndex,boneNames,fileResolver:pak.ReadFile,
      treeSettings:new MotionTreeSettings{Variables=new Dictionary<string,float>{[variable.Name]=rate}});treeLoads++;Validate(clip,path+"/"+choice.MotionNumber+"/"+rate);
    int left=three?(rate<0?0:1):0,right=three?(rate<0?1:2):1;float w=three?(rate<0?rate+1:rate):rate;
    float maxPosition=0,maxRotation=0;
    foreach(var t in new[]{0f,clip.Duration*0.27f,clip.Duration*0.61f,clip.Duration})foreach(var (name,track)in clip.NamedTracks){
     var a=sources[left].NamedTracks.GetValueOrDefault(name);var b=sources[right].NamedTracks.GetValueOrDefault(name);
     var ta=t*sources[left].Duration/clip.Duration;var tb=t*sources[right].Duration/clip.Duration;
     if(track.Translations!=null&&a?.Translations!=null&&b?.Translations!=null){var expected=Vector3.Lerp(V(a,ta),V(b,tb),w);maxPosition=Math.Max(maxPosition,Vector3.Distance(V(track,t),expected));}
     if(track.Rotations!=null&&a?.Rotations!=null&&b?.Rotations!=null){var expected=Quaternion.Normalize(Quaternion.Slerp(Q(a,ta),Q(b,tb),w));maxRotation=Math.Max(maxRotation,1-Math.Abs(Quaternion.Dot(Q(track,t),expected)));}
    }
    // Baked rotations between frame keys are interpolated again by playback.
    var endpoint=rate==-1||rate==0||rate==1;
    Check(maxPosition<0.002f&&maxRotation<(endpoint?0.000002f:0.001f),$"Blend mismatch {path}/{choice.MotionNumber}/{rate}: {maxPosition} {maxRotation}");
    cases.Add(new{path,id=choice.MotionNumber,rate,available=true,maxPosition,maxRotation,clip.Duration,tracks=clip.NamedTracks.Count});
   }catch(Exception ex){failures.Add($"Tree {path}/{choice.MotionNumber}/{rate}: {ex}");}
  }
  try{
   using var ss=pak.ReadFile(path);var clip=ViewportDataLoader.LoadAnimation(ss,path,choice.SourceIndex,sceneMeshes:[player],fileResolver:pak.ReadFile,
    treeSettings:new MotionTreeSettings{Variables=new Dictionary<string,float>{[variable.Name]=0.5f}});
   var f=Path.Combine(output,"glb",Path.GetFileName(path)+"_"+choice.MotionNumber+".glb");
   new ViewportExportService(true).ConvertToAnimatedGlb(player,player.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet(),clip,f);
   var glb=ModelRoot.Load(f);Check(glb.LogicalAnimations.Count==1&&Math.Abs(glb.LogicalAnimations[0].Duration-clip.Duration)<1e-5,"Tree GLB clock mismatch "+path);exports++;
  }catch(Exception ex){failures.Add("Tree export "+path+"/"+choice.MotionNumber+": "+ex);}
 }
 Console.WriteLine($"TREES {Path.GetFileName(path)}; loads={treeLoads} exports={exports} failures={failures.Count}");Save();
 // Mutate real topology only in memory: unknown semantics and broken links must
 // fail closed rather than selecting a convenient pose.
 var first=(MotTreeFile)list.Motions[0].MotFile!;
 var blend=first.Nodes.Single(n=>n.typeName is "SyncBlendNode" or "OctSyncBlendNode");
 var polar=blend.Parameters.Single(p=>p.name=="RootTransPolarBlend");var before=polar.value;polar.value=true;
 Check(!RiseMotionTrees.Describe(list,path,0,pak.ReadFile).Available,"Polar mode was silently accepted");guards++;polar.value=before;
 var l=first.Links.First(x=>x.type==ReeLib.MotTree.MotionTreeLinkType.Motion);var old=l.outputNodeIndex;l.outputNodeIndex=9999;
 Check(!RiseMotionTrees.Describe(list,path,0,pak.ReadFile).Available,"Bad source link was accepted");guards++;l.outputNodeIndex=old;
}
// Batch export is preflighted: incomplete trees cannot leave a seemingly
// successful partial set. Valid lists include their composed entries.
foreach(var name in new[]{"bow","greatsword"}){
 var path=$"natives/stm/player/mot/plw_{name}_tree_51.motlist.528";
 var dir=Path.Combine(output,"batch",name);
 try{using var ss=pak.ReadFile(path);var written=new AnimationService(true).ConvertAllToGlbWithAnimation(player,ss,path,dir,fileResolver:pak.ReadFile);
  Check(name=="bow"&&written.Count==2,"Batch tree count incorrect "+name);exports+=written.Count;
 }catch(InvalidDataException e){Check(name=="greatsword"&&e.Message.Contains("编号 6")&&e.Message.Contains("编号 7")&&(!Directory.Exists(dir)||!Directory.EnumerateFiles(dir).Any()),"Batch preflight is not atomic "+e.Message);guards++;}
}
// Recheck every multi-frame/zero-time case from the original audit. Native bone
// skeletons isolate serialization and timing; these are not visual acceptance.
string[] clockPaths=["wpg_bow_main_00_common_000","wpg_chargeaxe_main_00_unique_001","wpg_greatsword_main_01_special_002","wpg_heavybowgun_main_00_common_000","wpg_heavybowgun_main_00_special_002","plc_dlcv8_026"];
int[] clockIds=[1,109,4,1,1,1];
for(int i=0;i<clockPaths.Length;i++){
 var path="natives/stm/player/mot/"+clockPaths[i]+".motlist.528";using var s=pak.ReadFile(path);using var list=new MotlistFile(new FileHandler(s,path));list.Read();var ix=list.Motions.FindIndex(m=>m.motNumber==clockIds[i]);var mot=(MotFile)list.Motions[ix].MotFile!;var rig=Rig(mot);
 using var a=pak.ReadFile(path);var clip=ViewportDataLoader.LoadAnimation(a,path,ix,rig.Bones.Select(b=>b.Name).ToArray(),fileResolver:pak.ReadFile);
 Check(Math.Abs(clip.Duration-mot.Header.frameCount/mot.Header.FrameRate)<1e-5,"Constant clip clock still lost: "+path);Validate(clip,path);
 var outputs=new AnimationService(true).ConvertOneToGlbWithAnimation(rig,pak.ReadFile(path),path,ix,Path.Combine(output,"clock",i.ToString()),fileResolver:pak.ReadFile);
 var glb=ModelRoot.Load(outputs[0]);Check(glb.LogicalAnimations.Count==1&&Math.Abs(glb.LogicalAnimations[0].Duration-clip.Duration)<1e-5,"Constant GLB clock lost "+path);exports++;
 cases.Add(new{path,id=clockIds[i],sourceFrames=mot.Header.frameCount,clip.Duration,exportedDuration=glb.LogicalAnimations[0].Duration});
}
Save();foreach(var f in failures)Console.WriteLine("FAIL "+f);
Console.WriteLine($"RISE_MOTION_TREES {(failures.Count==0?"PASS":"FAIL")} direct={checkedMotions} treeLoads={treeLoads} missingTrees={missingTrees} exports={exports} guards={guards} errors={failures.Count}");
return failures.Count==0?0:1;

void Validate(AnimationClip clip,string label){
 Check(float.IsFinite(clip.Duration)&&clip.Duration>0,"Nonpositive duration "+label);
 foreach(var t in clip.NamedTracks.Values){
  foreach(var ts in new[]{t.TransTimes,t.RotTimes,t.ScaleTimes})if(ts is{Length:>0})Check(ts.All(float.IsFinite)&&ts.SequenceEqual(ts.Order())&&ts[^1]+1e-6>=clip.Duration,"Invalid times "+label);
  if(t.Translations is{}vs)Check(vs.All(v=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z)),"Invalid positions "+label);
  if(t.Rotations is{}rs)Check(rs.All(q=>float.IsFinite(q.LengthSquared())&&Math.Abs(q.LengthSquared()-1)<0.0001),"Invalid rotations "+label);
 }
}
static T Sample<T>(T[] values,float[] times,float time,Func<T,T,float,T> lerp){if(time<=times[0])return values[0];if(time>=times[^1])return values[^1];int hi=Array.FindIndex(times,t=>t>=time);return lerp(values[hi-1],values[hi],(time-times[hi-1])/(times[hi]-times[hi-1]));}
static Vector3 V(BoneTrack t,float time)=>Sample(t.Translations!,t.TransTimes!,time,Vector3.Lerp);
static Quaternion Q(BoneTrack t,float time)=>Quaternion.Normalize(Sample(t.Rotations!,t.RotTimes!,time,Quaternion.Slerp));
static ViewportMesh Rig(MotFile mot){
 var bones=mot.Bones.Select(b=>new ViewportBone{Name=b.boneName,ParentIndex=b.Parent==null?-1:mot.Bones.IndexOf(b.Parent),LocalBind=Matrix4x4.CreateFromQuaternion(b.quaternion)*Matrix4x4.CreateTranslation(b.translation),InverseGlobalBind=Matrix4x4.Identity}).ToArray();
 Matrix4x4 Global(int i)=>bones[i].ParentIndex<0?bones[i].LocalBind:bones[i].LocalBind*Global(bones[i].ParentIndex);
 for(int i=0;i<bones.Length;i++){Matrix4x4.Invert(Global(i),out var inv);bones[i].InverseGlobalBind=inv;}
 return new ViewportMesh{Vertices=[Vector3.Zero,Vector3.UnitX,Vector3.UnitY],Normals=[Vector3.UnitZ,Vector3.UnitZ,Vector3.UnitZ],Uvs=[Vector2.Zero,Vector2.UnitX,Vector2.UnitY],Faces=[(0,1,2)],FaceTexture=[-1],Textures=[],Weights=[[(0,1f)],[(0,1f)],[(0,1f)]],Bones=bones,DeformToBone=Enumerable.Range(0,bones.Length).ToArray()};
}
