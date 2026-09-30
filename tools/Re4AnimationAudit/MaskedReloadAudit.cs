using ReExtractor.Core;
using ReeLib;
using ReeLib.Common;
using System.Numerics;
static class MaskedReloadAudit {
 public static void Run(PakService pak){
 var mp="natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";using var ms=pak.ReadFile(mp);var mesh=ViewportDataLoader.LoadMesh(ms,mp,0,null,false);
 var jp="natives/stm/_chainsaw/appsystem/character/ch0common/jointmap/ch0commonjointmap.jmap.19";using var js=pak.ReadFile(jp);using var jm=new JmapFile(new FileHandler(js,jp));jm.Read();var masks=jm.MaskGroups.Single(g=>g.groupId==14).Masks.ToDictionary(m=>m.jointHash);
 var path="natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
 AnimationClip Load(int i){using var s=pak.ReadFile(path);return ViewportDataLoader.LoadAnimation(s,path,i,sceneMeshes:new[]{mesh});}
 var gunPath="natives/stm/_chainsaw/character/wp/wp40/wp4000/00/wp4000_00.mesh.221108797";
 using(var gs=pak.ReadFile(gunPath)){var gun=ViewportDataLoader.LoadMesh(gs,gunPath,0,null,false);new ViewportExportService().ConvertToGlb(gun,gun.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet(),"artifacts/re4-audit/wp4000.glb");Console.WriteLine("WEAPON_BONES "+string.Join(",",gun.Bones.Select(b=>b.Name)));}
 var basis=Load(52);var raw=Load(53);
 var mask=AnimationPreviewProfiles.ReadRe4ReloadMask(pak,mesh.Bones);
 var mix=AnimationPreviewMixer.Compose(basis,raw,mesh.Bones,1,mask);
 var baseOnly=AnimationPreviewMixer.Compose(basis,raw,mesh.Bones,0);
 foreach(var name in new[]{"L_UpperArm","R_UpperArm","L_Hand","R_Hand","L_Forearm","R_Forearm"}) {
 if(mask.ContainsKey(name))throw new Exception("Unexpected masked limb");
 var actual=mix.NamedTracks[name];var expected=baseOnly.NamedTracks[name];
 for(int i=0;i<actual.Rotations!.Length;i++)if(Math.Abs(Quaternion.Dot(actual.Rotations[i],expected.Rotations![i]))<.99999 || Vector3.Distance(actual.Translations![i],expected.Translations![i])>1e-6)throw new Exception("Limb changed: "+name);
 }
 if(mask["Spine_0"].Rotation!=.6f || mask["Spine_1"].Rotation!=.8f || mask["Spine_2"].Rotation!=1f)throw new Exception("Unexpected game mask weights");
 Console.WriteLine("MASKED_LIMBS_EQUAL_BASE_ALL_FRAMES_PASS");
 var hand=Load(0);
 foreach(var bone in mesh.Bones.Where(b=>b.Name.Contains("IndexF")||b.Name.Contains("MiddleF"))) {
 var t=mix.NamedTracks[bone.Name];var bt=baseOnly.NamedTracks[bone.Name];
 var same=t.Rotations!.Zip(bt.Rotations!, (a,b)=>Math.Abs(Quaternion.Dot(a,b))).Min();
 basis.NamedTracks.TryGetValue(bone.Name,out var rawFinger);hand.NamedTracks.TryGetValue(bone.Name,out var handFinger);
 Console.WriteLine($"FINGER {bone.Name} sourceRotationKeys={rawFinger?.Rotations?.Length??0} unchangedDot={same} reload0={rawFinger?.Rotations?.FirstOrDefault()} hand90={handFinger?.Rotations?.FirstOrDefault()}");
 }

 new ViewportExportService().ConvertToAnimatedGlb(mesh,mesh.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet(),mix,"artifacts/re4-audit/masked53.glb");
 Console.WriteLine("MASK14_DIAGNOSTIC_PASS: only "+string.Join(",",mask.Keys));
 }
}
