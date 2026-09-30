using ReExtractor.Core;
using System.Numerics;
static class BlendDiagnosis {
 public static void Run(PakService pak) {
 var path="natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
 var mp="natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
 using var ms=pak.ReadFile(mp);var mesh=ViewportDataLoader.LoadMesh(ms,mp,0,null,false);
 AnimationClip Load(int i){using var s=pak.ReadFile(path);return ViewportDataLoader.LoadAnimation(s,path,i,sceneMeshes:new[]{mesh});}
 foreach(var pair in new[]{(44,45),(52,53),(8,40)}) {
 var basis=Load(pair.Item1);var overlay=Load(pair.Item2);
 Console.WriteLine($"PAIR {pair} durations={basis.Duration}/{overlay.Duration}");
 foreach(var bone in mesh.Bones.Where(b=>b.Name.Contains("Arm")||b.Name.Contains("Spine")||b.Name.Contains("Hand"))) {
 if(!overlay.NamedTracks.TryGetValue(bone.Name,out var t)||t.Rotations is not {Length:>0})continue;
 Matrix4x4.Decompose(bone.LocalBind,out _,out var q,out _);
 Console.WriteLine($"{bone.Name} bind={q} raw0={t.Rotations[0]} identityDot={Math.Abs(t.Rotations[0].W):F4} bindDot={Math.Abs(Quaternion.Dot(q,t.Rotations[0])):F4}");
 }
 foreach(var mode in new[]{"rotationDelta","first"}) {
 var raw=Load(pair.Item2);
 foreach(var bone in mesh.Bones) {
 if(!raw.NamedTracks.TryGetValue(bone.Name,out var t))continue;
 Matrix4x4.Decompose(bone.LocalBind,out _,out var bindQ,out var bindT);
 if(t.Rotations is {Length:>0} qs){var refQ=mode=="first"?qs[0]:Quaternion.Identity;t.Rotations=qs.Select(q=>Quaternion.Normalize(bindQ*Quaternion.Inverse(refQ)*q)).ToArray();}
 if(t.Translations is {Length:>0} ts){var refT=mode=="first"?ts[0]:bindT;t.Translations=ts.Select(t=>bindT+t-refT).ToArray();}
 }
 var mix=AnimationPreviewMixer.Compose(basis,raw,mesh.Bones);
 new ViewportExportService().ConvertToAnimatedGlb(mesh,mesh.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet(),mix,$"artifacts/re4-audit/{mode}{pair.Item2}.glb");
 }
 }
 }
}
