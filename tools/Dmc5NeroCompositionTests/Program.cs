using System.Numerics;
using System.Text.Json;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using ReExtractor.Core;
using ReExtractor.Gui;
using ReeLib;

if (args.Length < 3) throw new ArgumentException("<game directory> <list> <output>");
var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
var pak = new PakService(); pak.AddPaksFromGameDir(args[0]); pak.LoadListFile(args[1]);
const string path = "natives/x64/animation/player/pl0000/motlist/pl0000_br.motlist.85";
using var original = pak.ReadFile(path);
using var list = new MotlistFile(new FileHandler(original, path));
if (!list.Read()) throw new Exception("Cannot read source list");
int Index(int id) => list.Motions.Select((m,i)=>(m,i)).Single(x=>x.m.motNumber==id).i;
var modelPaths = new[] {
 "natives/x64/character/player/pl0000_nero/pl0000_body/pl0000.mesh.1808282334",
 "natives/x64/character/player/pl0000_nero/pl0000_01_head/pl0000_01.mesh.1808282334",
 "natives/x64/character/player/pl0000_nero/pl0000_03_hair/pl0000_03.mesh.1808282334",
 "natives/x64/character/player/pl0000_nero/pl0000_08_humanarm/pl0000_08.mesh.1808282334" };
var models = modelPaths.Select(p => {using var s=pak.ReadFile(p);return ViewportDataLoader.LoadMesh(s,p,openResource:pak.ReadFile,loadTextures:args.Contains("--textures"));}).ToArray();
var binds=models.Select(m=>m.Bones.Select(b=>b.InverseGlobalBind).ToArray()).ToArray();
AnimationClip Load(int id, bool composed=true, string? sourcePath=null) {
 using var s=pak.ReadFile(path);
 return ViewportDataLoader.LoadAnimation(s,sourcePath??path,Index(id),sceneMeshes:models,fileResolver:pak.ReadFile,composeDmc5Motions:composed);
}
void Check(bool ok,string message) {if(!ok)throw new Exception(message); Console.WriteLine("PASS "+message);}
using(var s=pak.ReadFile(path)) {
 var motions=ViewportDataLoader.ListMotions(s,path,pak.ReadFile);
 if(motions.Count!=31) foreach(var m in list.Motions.Where(m=>m.MotFile is MotTreeFile)) {
  var tree=(MotTreeFile)m.MotFile!;
  foreach(var n in tree.Nodes)Console.WriteLine($"TREE {m.motNumber} {n.typeName} "+string.Join(";",n.Parameters.Select(p=>$"{p.name}={p.value}({p.value?.GetType().Name})")));
 }
 Check(motions.Count==31,"all 29 MOTs and both authored trees are selectable");
 Check(motions.Single(m=>m.MotionNumber==680).DisplayName.Contains("组合"),"680 advertises composed preview");
}
var cases=new List<object>();
foreach(var ids in new[]{(Lower:680,Upper:1100,Tree:700),(Lower:670,Upper:640,Tree:690)}) {
 var lower=Load(ids.Lower,false); var upper=Load(ids.Upper,false); var fixedClip=Load(ids.Lower);var tree=Load(ids.Tree);
 Check(!lower.NamedTracks.ContainsKey("L_UpperArm") && fixedClip.NamedTracks.ContainsKey("L_UpperArm"),$"{ids.Lower} now has original authored arm tracks");
 Check(Math.Abs(fixedClip.Duration-((MotFile)list.Motions[Index(ids.Lower)].MotFile!).Header.frameCount/60f)<1e-6,$"{ids.Tree} preserves authored clock");
 var maxLower=0f; var maxUpper=0f; var maxTree=0f;
 foreach(var frame in new[]{0,7,20,40,60,83}) {
  float t=Math.Min(frame/60f,fixedClip.Duration);
  foreach(var (name,track) in fixedClip.NamedTracks) {
   var expectedT=lower.NamedTracks.GetValueOrDefault(name)?.Translations is {Length:>0} ? lower.NamedTracks[name] : upper.NamedTracks.GetValueOrDefault(name);
   var expectedR=lower.NamedTracks.GetValueOrDefault(name)?.Rotations is {Length:>0} ? lower.NamedTracks[name] : upper.NamedTracks.GetValueOrDefault(name);
   if(track.Translations is {Length:>0}) {
    float error=Vector3.Distance(V(track,t),V(expectedT!,t));
    if(lower.NamedTracks.GetValueOrDefault(name)?.Translations is {Length:>0})maxLower=Math.Max(maxLower,error);else maxUpper=Math.Max(maxUpper,error);
    maxTree=Math.Max(maxTree,Vector3.Distance(V(track,t),V(tree.NamedTracks[name],t)));
   }
   if(track.Rotations is {Length:>0}) {
    float error=1-Math.Abs(Quaternion.Dot(Q(track,t),Q(expectedR!,t)));
    if(lower.NamedTracks.GetValueOrDefault(name)?.Rotations is {Length:>0})maxLower=Math.Max(maxLower,error);else maxUpper=Math.Max(maxUpper,error);
    maxTree=Math.Max(maxTree,1-Math.Abs(Quaternion.Dot(Q(track,t),Q(tree.NamedTracks[name],t))));
   }
  }
 }
 Check(maxLower<0.00001f && maxUpper<0.00001f && maxTree<0.00001f,$"{ids.Tree} source channels, legs, arms and direct tree match ({maxLower}, {maxUpper}, {maxTree})");
 cases.Add(new{ids=new{ids.Lower,ids.Upper,ids.Tree},maxLower,maxUpper,maxTree,duration=fixedClip.Duration,tracks=fixedClip.NamedTracks.Count});
 if(ids.Lower==680) {
  var parts=models.Select(m=>(m,(IReadOnlySet<int>)m.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet())).ToArray();
  var exporter=new ViewportExportService();
  exporter.ConvertMergedToAnimatedGlb(parts,lower,Path.Combine(output,"before.glb"));
  exporter.ConvertMergedToAnimatedGlb(parts,fixedClip,Path.Combine(output,"after.glb"));
  var merged=exporter.BuildMergedExportModel(parts);
  exporter.ConvertToGlb(merged.Mesh,merged.VisibleGroups,Path.Combine(output,"model.glb"));
  using var single=pak.ReadFile(path);
  new AnimationService().ConvertOneToGlbWithAnimation(merged.Mesh,single,path,Index(680),Path.Combine(output,"one"),fileResolver:pak.ReadFile);
  var viewportSamples=new List<object>();
  foreach(var frame in new[]{0,20,40,65,83}) {
   var poses=Evaluate(models,fixedClip,frame/60f);
   Check(poses.All(p=>p.Vertices.All(v=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z))),"headless application viewport evaluates frame "+frame);
   viewportSamples.Add(new{frame,parts=models.Select((m,i)=>new{path=modelPaths[i],vertices=poses[i].Vertices.Length,
    bones=m.Bones.Select((b,j)=>new{name=b.Name,position=new[]{poses[i].Globals[j].Translation.X,poses[i].Globals[j].Translation.Y,poses[i].Globals[j].Translation.Z}})})});
  }
  File.WriteAllText(Path.Combine(output,"application-preview-readback.json"),JsonSerializer.Serialize(viewportSamples));
 }
}
for(var i=0;i<models.Length;i++)Check(binds[i].SequenceEqual(models[i].Bones.Take(binds[i].Length).Select(b=>b.InverseGlobalBind)),"source inverse bind unchanged "+modelPaths[i]);
var normal=Load(10);var normalRaw=Load(10,false);
Check(normal.Name==normalRaw.Name && normal.NamedTracks.Count==normalRaw.NamedTracks.Count,"ordinary motion unaffected");
var outside=Load(680,sourcePath:path.Replace("pl0000_br.","other."));
Check(!outside.NamedTracks.ContainsKey("L_UpperArm"),"other paths are not guessed as Nero combinations");
try {using var s=pak.ReadFile(path);ViewportDataLoader.LoadAnimation(s,path,Index(680),sceneMeshes:models,fileResolver:p=>p.Contains(".motbank.")?null:pak.ReadFile(p));throw new Exception("Missing bank accepted");}
catch(FileNotFoundException){Console.WriteLine("PASS missing bank is reported instead of guessing");}
using(var s=pak.ReadFile(path)) {
 var merged=new ViewportExportService().BuildMergedExportModel(models.Select(m=>(m,(IReadOnlySet<int>)m.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet())).ToArray());
 var files=new AnimationService().ConvertAllToGlbWithAnimation(merged.Mesh,s,path,Path.Combine(output,"all"),fileResolver:pak.ReadFile);
 Check(files.Count==31,"all export includes both combinations");
}
File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new{cases,modelPaths,result="PASS",notice="fixed central aim; no game runtime validation"},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("ALL PASS");

static (int,int,float) Segment(float[] times,float t){if(t<=times[0])return(0,0,0);if(t>=times[^1])return(times.Length-1,times.Length-1,0);int b=Array.BinarySearch(times,t);if(b>=0)return(b,b,0);b=~b;return(b-1,b,(t-times[b-1])/(times[b]-times[b-1]));}
static Vector3 V(BoneTrack track,float t){var(a,b,w)=Segment(track.TransTimes!,t);return Vector3.Lerp(track.Translations![a],track.Translations[b],w);}
static Quaternion Q(BoneTrack track,float t){var(a,b,w)=Segment(track.RotTimes!,t);return Quaternion.Normalize(Quaternion.Slerp(track.Rotations![a],track.Rotations[b],w));}

// Exercise the application's exact pose evaluator without constructing a
// window or starting its rendering/desktop event loop.
static (Vector3[] Vertices,Matrix4x4[] Globals)[] Evaluate(ViewportMesh[] meshes,AnimationClip clip,float time) {
 var type=typeof(GlViewport);var modelType=type.GetNestedType("GlModel",BindingFlags.NonPublic)!;
 void Set(object o,string name,object value)=>o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.SetValue(o,value);
 object Model(ViewportMesh mesh) {
  var model=Activator.CreateInstance(modelType,true)!;
  Set(model,"Mesh",mesh);Set(model,"BoneGlobals",new Matrix4x4[mesh.Bones.Length]);Set(model,"JointMats",new Matrix4x4[mesh.DeformToBone.Length]);
  Set(model,"Posed",new Vector3[mesh.VertexCount]);Set(model,"PosedNormals",new Vector3[mesh.VertexCount]);
  Set(model,"Tracks",mesh.Bones.Select((b,i)=>(b,i)).Where(x=>clip.NamedTracks.ContainsKey(x.b.Name)).ToDictionary(x=>x.i,x=>clip.NamedTracks[x.b.Name]));return model;
 }
 var viewport=RuntimeHelpers.GetUninitializedObject(type);var models=meshes.Select(Model).ToArray();
 Set(viewport,"_primary",models[0]);Set(viewport,"_clip",clip);
 var extras=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(modelType))!;foreach(var model in models.Skip(1))extras.Add(model);Set(viewport,"_extras",extras);
 type.GetMethod("EvaluateAllPose",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(viewport,[time]);
 return models.Select(m=>((Vector3[])modelType.GetField("Posed")!.GetValue(m)!,(Matrix4x4[])modelType.GetField("BoneGlobals")!.GetValue(m)!)).ToArray();
}
