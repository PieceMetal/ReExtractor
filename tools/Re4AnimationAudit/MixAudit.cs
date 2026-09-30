using ReExtractor.Core;
using System.Numerics;
static class MixAudit {
 public static void Run(PakService pak) {
 var path="natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
 var mp="natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
 using var ms=pak.ReadFile(mp);var mesh=ViewportDataLoader.LoadMesh(ms,mp,0,null,false);
 AnimationClip Load(int i){using var s=pak.ReadFile(path);return ViewportDataLoader.LoadAnimation(s,path,i,sceneMeshes:new[]{mesh});}
 foreach(var pair in new[]{(44,45),(52,53),(8,40)}){
 var basis=Load(pair.Item1);var add=Load(pair.Item2);
 var snapshot=add.NamedTracks.ToDictionary(k=>k.Key,k=>k.Value.Rotations?.ToArray());
 var mix=AnimationPreviewMixer.Compose(basis,add,mesh.Bones);
 foreach(var kv in snapshot) if(kv.Value!=null&&!kv.Value.SequenceEqual(add.NamedTracks[kv.Key].Rotations!))throw new Exception("Mutated input");
 if(mix.NamedTracks.Values.Any(t=>t.Rotations!.Any(q=>!float.IsFinite(q.Length()))))throw new Exception("Invalid rotation");
 new ViewportExportService().ConvertToAnimatedGlb(mesh,mesh.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet(),mix,$"artifacts/re4-audit/mix{pair.Item2}.glb");
 Console.WriteLine($"MIX_PASS {pair}");
 }
 var bind=Matrix4x4.CreateRotationZ(.3f);bind.Translation=new Vector3(1,2,3);
 var bone=new ViewportBone{Name="test",ParentIndex=0,LocalBind=bind,InverseGlobalBind=Matrix4x4.Identity};
 AnimationClip Clip(Quaternion q,Vector3 t)=>new(){Name="test",Duration=1,FrameRate=30,FrameCount=30,Tracks=new(),NamedTracks=new(){{"test",new(){RotTimes=[0,1],Rotations=[q,q],TransTimes=[0,1],Translations=[t,t]}}}};
 var bq=Quaternion.CreateFromAxisAngle(Vector3.UnitX,.5f);var basisTest=Clip(bq,new(4,5,6));var neutral=Clip(Quaternion.Identity,Vector3.Zero);
 foreach(var weight in new[]{0f,.5f,1f}){var result=AnimationPreviewMixer.Compose(basisTest,neutral,[bone],weight).NamedTracks["test"];if(Vector3.Distance(result.Translations![0],new(4,5,6))>1e-5||Math.Abs(Quaternion.Dot(result.Rotations![0],bq))<.99999)throw new Exception("Neutral failure");}
 Console.WriteLine("NEUTRAL_WEIGHT_PASS");
 var delta=Quaternion.CreateFromAxisAngle(Vector3.UnitY,.4f);
 var nonNeutral=Clip(delta,new Vector3(0,1,0));
 foreach(var w in new[]{0f,.5f,1f}) {
 var result=AnimationPreviewMixer.Compose(basisTest,nonNeutral,[bone],w).NamedTracks["test"];
 var expected=Quaternion.Normalize(bq*Quaternion.Slerp(Quaternion.Identity,delta,w));
 if(Math.Abs(Quaternion.Dot(result.Rotations![0],expected))<.99999 || Vector3.Distance(result.Translations![0],new Vector3(4,5+w,6))>1e-5)throw new Exception("Delta weight failure");
 }
 nonNeutral.NamedTracks["test"].Rotations=null;
 var missing=AnimationPreviewMixer.Compose(basisTest,nonNeutral,[bone]).NamedTracks["test"];
 if(Math.Abs(Quaternion.Dot(missing.Rotations![0],bq))<.99999)throw new Exception("Missing channel failure");
 Console.WriteLine("DELTA_WEIGHT_AND_MISSING_CHANNEL_PASS");
 }
}
