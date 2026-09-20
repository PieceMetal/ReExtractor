using ReExtractor.Core;
using ReExtractor.Gui;
using System.Reflection;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Collections;
var pak=new PakService();var game=@"E:\Steam\steamapps\common\OnimushaWotS";pak.AddPaksFromGameDir(game);pak.LoadListFile(Path.Combine(game,"OWOTS_STM_Release.list"));
var meshes=new List<ViewportMesh>();foreach(var part in new[]{"00","10","20"}){var p=$"natives/stm/art/model/character/ch0/ch004_00/{part}/ch004_00_{part}.mesh.260209350";using var s=pak.ReadFile(p);meshes.Add(ViewportDataLoader.LoadMesh(s,p,0,null,false));}
var path="natives/stm/motion/npc/face/npc004_00/npc004_00_common/npc004_00_common.motlist.1036";using var stream=pak.ReadFile(path);var clip=ViewportDataLoader.LoadAnimation(stream,path,sceneMeshes:meshes);
var type=typeof(GlViewport);var mt=type.GetNestedType("GlModel",BindingFlags.NonPublic)!;
void Set(object o,string n,object v)=>o.GetType().GetField(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(o,v);
object Model(ViewportMesh m){var o=Activator.CreateInstance(mt,true)!;Set(o,"Mesh",m);Set(o,"BoneGlobals",new Matrix4x4[m.Bones.Length]);Set(o,"JointMats",new Matrix4x4[m.DeformToBone.Length]);Set(o,"Posed",new Vector3[m.VertexCount]);Set(o,"PosedNormals",new Vector3[m.VertexCount]);Set(o,"Tracks",m.Bones.Select((b,i)=>(b,i)).Where(x=>clip.NamedTracks.ContainsKey(x.b.Name)).ToDictionary(x=>x.i,x=>clip.NamedTracks[x.b.Name]));return o;}
Vector3[] Eval(ViewportMesh[] ms,float t){var v=RuntimeHelpers.GetUninitializedObject(type);var models=ms.Select(Model).ToArray();Set(v,"_primary",models[0]);Set(v,"_clip",clip);var extras=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(mt))!;foreach(var m in models.Skip(1))extras.Add(m);Set(v,"_extras",extras);type.GetMethod("EvaluateAllPose",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(v,new object[]{t});return (Vector3[])mt.GetField("Posed")!.GetValue(models[Array.IndexOf(ms,meshes[1])])!;}
var helpers=meshes.SelectMany(m=>m.Bones).Where(b=>b.IsMotionHelper).ToArray();
foreach(var t in Enumerable.Range(0,61).Select(i=>i*clip.Duration/61).Append(2.52f)){var solo=Eval(new[]{meshes[1]},t);var fixedPose=Eval(meshes.ToArray(),t);var offset=fixedPose[0]-solo[0];var residual=fixedPose.Zip(solo,(a,b)=>Vector3.Distance(a-b,offset)).Max();Console.WriteLine($"rigidOffset={offset} residual={residual}");if(residual>1e-4)throw new Exception("Facial deformation differs");float Error(Vector3[] p)=>p.Zip(solo,(a,b)=>Vector3.Distance(a,b)).Max();Console.WriteLine($"t={t} maxVertexDifference={Error(fixedPose)}");if(Error(fixedPose)>.0001)throw new Exception("Merged head differs from standalone");if(t==2.52f){Directory.CreateDirectory("artifacts/merged-face-proof");File.WriteAllText("artifacts/merged-face-proof/faces.json",System.Text.Json.JsonSerializer.Serialize(meshes[1].Faces.Select(f=>new[]{f.A,f.B,f.C})));foreach(var item in new[]{("after",fixedPose)}){File.WriteAllLines($"artifacts/merged-face-proof/{item.Item1}.xyz",item.Item2.Select(p=>$"{p.X} {p.Y} {p.Z}"));}}}
Console.WriteLine("MERGED_FACE_PASS");





var export = new ViewportExportService();
var merged=export.BuildMergedExportModel(meshes.Select(m=>(m,(IReadOnlySet<int>)m.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet())).ToArray());
Directory.CreateDirectory("artifacts/merged-face-proof/glb");
using(var ms=pak.ReadFile(path))new AnimationService().ConvertOneToGlbWithAnimation(merged.Mesh,ms,path,0,"artifacts/merged-face-proof/glb");
using(var ms=pak.ReadFile(path))new AnimationService().ConvertOneToGlbWithAnimation(meshes[1],ms,path,0,"artifacts/merged-face-proof/solo");
