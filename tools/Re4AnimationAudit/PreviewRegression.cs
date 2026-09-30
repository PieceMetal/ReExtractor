using ReExtractor.Core;
using System.Numerics;

static class PreviewRegression
{
    public static void Run(PakService pak)
    {
        const string path = "natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
        const string meshPath = "natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
        using var ms = pak.ReadFile(meshPath);
        var mesh = ViewportDataLoader.LoadMesh(ms, meshPath, 0, null, false);
        using var ls = pak.ReadFile(path);
        var motions = ViewportDataLoader.ListMotions(ls, path);
        AnimationClip Load(int id) { using var s = pak.ReadFile(path); return ViewportDataLoader.LoadAnimation(s, path,
            motions.Single(m => m.MotionNumber == id).SourceIndex, sceneMeshes: new[] { mesh }); }
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        Quaternion At(BoneTrack track, float time)
        {
            var times = track.RotTimes!; var values = track.Rotations!;
            var next = Array.FindIndex(times, t => t >= time);
            if (next < 0) return values[^1];
            if (next == 0) return values[0];
            return Quaternion.Slerp(values[next - 1], values[next], (time - times[next - 1]) / (times[next] - times[next - 1]));
        }
        foreach (var (baseId, addId) in new[] { (810, 811), (910, 911), (1810, 1811), (1910, 1911) })
        {
            Check(AnimationLayerCatalog.UsesReloadMask(path, baseId, addId), "Reload mask routing missing");
            var b = Load(baseId); var a = Load(addId);
            var mask = AnimationPreviewProfiles.ReadRe4ReloadMask(pak, mesh.Bones);
            var mix = AnimationPreviewMixer.Compose(b, a, mesh.Bones, 1, mask);
            foreach (var name in new[] { "L_UpperArm", "R_UpperArm", "L_Hand", "R_Hand", "L_Thigh", "R_Thigh" })
            {
                Check(!mask.ContainsKey(name), "Unexpected mask limb");
                var actual = mix.NamedTracks[name]; var original = b.NamedTracks[name];
                for (var i = 0; i < actual.RotTimes!.Length; i++)
                    Check(MathF.Abs(Quaternion.Dot(actual.Rotations![i], At(original, actual.RotTimes[i]))) > .99999f,
                        $"Unmasked limb changed: {baseId} {name}");
            }
            Console.WriteLine($"RELOAD_{baseId}_{addId}_LIMBS_PRESERVED_PASS");
        }
        Check(!AnimationLayerCatalog.UsesReloadMask(path, 810, 1811), "Cross variant received verified mask");
        Check(!AnimationLayerCatalog.UsesReloadMask("other.motlist.663", 910, 911), "Mask leaked to another list");
        var basis = Load(541); var overlay = Load(544);
        var zero = AnimationPreviewMixer.Compose(basis, overlay, mesh.Bones, 0);
        Check(zero.Duration == basis.Duration && zero.FrameCount == basis.FrameCount && zero.FrameRate == basis.FrameRate, "Zero changed clock");
        foreach (var (name, track) in basis.NamedTracks)
        {
            var copy = zero.NamedTracks[name];
            Check((track.Rotations ?? []).SequenceEqual(copy.Rotations ?? []) && (track.Translations ?? []).SequenceEqual(copy.Translations ?? [])
                && (track.RotTimes ?? []).SequenceEqual(copy.RotTimes ?? []) && (track.TransTimes ?? []).SequenceEqual(copy.TransTimes ?? []), "Zero changed source track");
            Check(!ReferenceEquals(copy, track), "Zero aliases source track");
        }
        var active = AnimationPreviewMixer.Compose(basis, overlay, mesh.Bones, 1);
        Check(active.Duration == basis.Duration, "Active layer retimed base");
        Console.WriteLine($"TIME_AND_ZERO_SOURCE_EQUALITY_PASS base={basis.Duration} zero={zero.Duration} active={active.Duration}");
        var hold = motions.Single(m => m.MotionNumber == 160);
        var candidates = AnimationLayerCatalog.Find(path, hold, motions);
        Check(candidates.Select(c => c.Motion.MotionNumber).Order().SequenceEqual(new[] { 500, 501, 502 }), "Shoot candidates missing");
        foreach (var id in new[] { 500, 501, 502 })
        {
            var motion = motions.Single(m => m.MotionNumber == id);
            Check(AnimationLayerCatalog.IsAdditive(path, motion) && !AnimationLayerCatalog.IsAdditive("other.motlist.663", motion), "Shoot scope incorrect");
            var b = Load(160); var a = Load(id);
            var mix = AnimationPreviewMixer.Compose(b, a, mesh.Bones, 1);
            Check(mix.Duration == a.Duration && mix.Duration > 0, "Static hold froze shoot");
            Check(mix.NamedTracks["Hip"].Translations![0].Y > .9f, "Shoot collapsed hip to raw zero");
            Console.WriteLine($"SHOOT_{id}_ON_HOLD_PASS duration={mix.Duration} hipY={mix.NamedTracks["Hip"].Translations![0].Y}");
        }
        var scaled = Load(541);
        scaled.NamedTracks["Hip"].ScaleTimes = [0, .25f, scaled.Duration];
        scaled.NamedTracks["Hip"].Scales = [Vector3.One, new(1.2f, .8f, 1.1f), Vector3.One];
        foreach (var w in new[] { 0f, .5f, 1f })
        {
            var mixed = AnimationPreviewMixer.Compose(scaled, Load(544), mesh.Bones, w);
            Check(mixed.NamedTracks["Hip"].Scales!.SequenceEqual(scaled.NamedTracks["Hip"].Scales!), "Base scale values lost");
            Check(mixed.NamedTracks["Hip"].ScaleTimes!.SequenceEqual(scaled.NamedTracks["Hip"].ScaleTimes!), "Base scale times changed");
            Check(!ReferenceEquals(mixed.NamedTracks["Hip"].Scales, scaled.NamedTracks["Hip"].Scales), "Scale arrays alias source");
        }
        Console.WriteLine("V144_SCALE_CHANNEL_PRESERVATION_PASS");
        Console.WriteLine("RE4_PREVIEW_REGRESSION_PASS");
    }
}
