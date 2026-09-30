using ReExtractor.Core;
static class TranslationCheck
{
 public static void Run(PakService pak)
 {
  const string mp="natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
  const string ap="natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
  using var ms=pak.ReadFile(mp);var mesh=ViewportDataLoader.LoadMesh(ms,mp,0,null,false);
  using var ls=pak.ReadFile(ap);var motions=ViewportDataLoader.ListMotions(ls,ap);
  AnimationClip Load(int id){using var stream=pak.ReadFile(ap);return ViewportDataLoader.LoadAnimation(stream,ap,motions.Single(m=>m.MotionNumber==id).SourceIndex,sceneMeshes:new[]{mesh});}
  foreach(var pair in new[]{(551,552),(810,811),(140,141)})
  {
   var basis=Load(pair.Item1);var add=Load(pair.Item2);
   foreach(var w in new[]{0f,.5f,1f})
   {
    var mix=AnimationPreviewMixer.Compose(basis,add,mesh.Bones,w);
    var zero=AnimationPreviewMixer.Compose(basis,add,mesh.Bones,0);
    var hip=mix.NamedTracks["Hip"].Translations!;
    if(hip.Any(v=>!float.IsFinite(v.Y)||v.Y<.7f))throw new Exception("Hip sunk "+pair);
    if(!mix.NamedTracks["root"].Translations!.SequenceEqual(zero.NamedTracks["root"].Translations!))throw new Exception("Root motion doubled");
    var expected=basis.NamedTracks["Hip"].Translations![0]+w*add.NamedTracks["Hip"].Translations![0];
    if(System.Numerics.Vector3.Distance(hip[0],expected)>1e-6)throw new Exception("Incorrect delta");
    Console.WriteLine($"HEIGHT_PASS {pair} weight={w} hipMin={hip.Min(v=>v.Y)}");
    if(w==1)new ViewportExportService().ConvertToAnimatedGlb(mesh,mesh.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet(),mix,$"artifacts/re4-audit/height-fixed-{pair.Item1}.glb");
   }
  }
  foreach(var id in new[]{551,552,810,811,140,141})
  {
   using var stream=pak.ReadFile(ap);var clip=ViewportDataLoader.LoadAnimation(stream,ap,motions.Single(m=>m.MotionNumber==id).SourceIndex,sceneMeshes:new[]{mesh});
   foreach(var bone in mesh.Bones.Where(b=>b.Name is "root" or "Hip" or "COG" or "Spine_0"))
   {
    clip.NamedTracks.TryGetValue(bone.Name,out var t);
    Console.WriteLine($"{id} {bone.Name} bind={bone.LocalBind.Translation} additive={t?.IsAdditive} first={t?.Translations?.FirstOrDefault()} last={t?.Translations?.LastOrDefault()} keys={t?.Translations?.Length} rot={t?.Rotations?.FirstOrDefault()}");
   }
  }
 }
}
