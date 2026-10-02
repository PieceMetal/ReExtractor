using ReExtractor.Core;
using System.Text.Json;
var output=Path.GetFullPath("artifacts/feedback-batch-4a67");
Directory.CreateDirectory(output);
var pak=new PakService();
if(args.Length!=1)throw new ArgumentException("Pass the local RE4 installation directory");
var game=Path.GetFullPath(args[0]);
foreach(var file in Directory.GetFiles(game,"*.pak").Order())pak.AddPak(file);
const string meshPath="natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
const string path="natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
using var ms=pak.ReadFile(meshPath);
var mesh=ViewportDataLoader.LoadMesh(ms,meshPath,0,null,false);
using var ls=pak.ReadFile(path);
var motions=ViewportDataLoader.ListMotions(ls,path);
AnimationClip Load(int id){using var stream=pak.ReadFile(path);return ViewportDataLoader.LoadAnimation(stream,path,motions.Single(m=>m.MotionNumber==id).SourceIndex,sceneMeshes:new[]{mesh});}
var basis=Load(541);var overlay=Load(544);
var visible=mesh.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet();
var outputs=new List<object>();
foreach(var weight in new[]{0f,.5f,1f}){
 var clip=AnimationPreviewMixer.Compose(basis,overlay,mesh.Bones,weight);
 var name=$"re4_541_544_weight{weight*100:0}";
 var file=Path.Combine(output,"glb",name+".glb");
 new ViewportExportService().ConvertToAnimatedGlb(mesh,visible,clip,file);
 var gltf=SharpGLTF.Schema2.ModelRoot.Load(file);
 var animation=gltf.LogicalAnimations.Single();
 float max=0;int samples=0;
 foreach(var node in gltf.LogicalNodes){
   if(!clip.NamedTracks.TryGetValue(node.Name??"",out var track))continue;
   var channel=animation.FindRotationChannel(node);
   if(channel==null||track.RotTimes==null)continue;
   var sampler=channel.GetRotationSampler().CreateCurveSampler();
   for(int i=0;i<track.RotTimes.Length;i++){
     var actual=sampler.GetPoint(track.RotTimes[i]);var expected=track.Rotations![i];
     max=Math.Max(max,1-Math.Abs(System.Numerics.Quaternion.Dot(actual,expected)));samples++;
   }
 }
 if(samples==0||max>1e-5)throw new Exception($"Export did not preserve composed rotations: {samples} {max}");
 outputs.Add(new{name,samples,max,clip.Duration});
 Console.WriteLine($"{name}: samples={samples}, maxQuaternionError={max}");
}
File.WriteAllText(Path.Combine(output,"audit.json"),JsonSerializer.Serialize(outputs,new JsonSerializerOptions{WriteIndented=true}));
new ViewportExportService().ConvertToGlb(mesh,visible,Path.Combine(output,"model","re4_body.glb"));
Directory.CreateDirectory("artifacts/re4-audit"); File.WriteAllText("artifacts/re4-audit/motions.json",JsonSerializer.Serialize(motions.Select(m=>new{path,index=m.SourceIndex,name=m.DisplayName,id=m.MotionNumber})));
