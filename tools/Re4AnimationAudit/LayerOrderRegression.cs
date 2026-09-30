using ReExtractor.Core;
using ReeLib;
using System.Numerics;
using System.Text.Json;
using System.Collections;

static class LayerOrderRegression
{
    public static void Run(PakService pak)
    {
        // Read the real prefab's ordered layer arrays, not a dump sorted by type.
        using var ws = new Workspace(new GameConfig(new GameIdentifier("re4")));
        const string prefabPath = "natives/stm/_chainsaw/appsystem/character/ch0a0z0/ch0a0z0_body.pfb.17";
        using var ps = pak.ReadFile(prefabPath);
        using var prefab = new PfbFile(ws.RszFileOption, new FileHandler(ps, prefabPath)); prefab.Read();
        object Field(RszInstance instance, string name) => instance.IndexedFields
            .Where(f => f.field.name == name).Select(f => instance.Values[f.index]).Single();
        var instances = prefab.GetAllRSZFiles().SelectMany(r => r.InstanceList).ToArray();
        var motion = instances.Single(i => i.RszClass.name == "via.motion.Motion");
        var layers = ((IList)Field(motion, "Layer")).Cast<RszInstance>().ToArray();
        if (Convert.ToInt32(Field(layers[3],"BlendMode")) != 1 || Convert.ToInt32(Field(layers[3],"JointMaskID")) != 4
            || Convert.ToInt32(Field(layers[4],"BlendMode")) != 0 || Convert.ToInt32(Field(layers[4],"JointMaskID")) != 3
            || Convert.ToBoolean(Field(motion,"EnableLayerUpdateOrder")))
            throw new Exception("Game motion layer configuration changed");
        var fsm = instances.First(i => i.RszClass.name == "via.motion.MotionFsm2");
        var fsmLayers = ((IList)Field(fsm,"v11_Layer")).Cast<RszInstance>().ToArray();
        if (!Field(fsmLayers[4],"MotionFsm2Resource").ToString()!.EndsWith("ch0CommonUpperBody.motfsm2")
            || Convert.ToInt32(Field(fsmLayers[4],"TargetMotionLayerNo")) != -1)
            throw new Exception("Upper-body FSM no longer occupies layer 4");
        Console.WriteLine("REAL_PREFAB_LAYER_3_ADD_MASK4_THEN_LAYER_4_UPPER_MASK3_PASS");
        const string path = "natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
        const string mp = "natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
        using var ms = pak.ReadFile(mp); var mesh = ViewportDataLoader.LoadMesh(ms, mp, 0, null, false);
        using var ls = pak.ReadFile(path); var motions = ViewportDataLoader.ListMotions(ls, path);
        AnimationClip Load(int id) { using var s = pak.ReadFile(path); return ViewportDataLoader.LoadAnimation(s, path,
            motions.Single(m => m.MotionNumber == id).SourceIndex, sceneMeshes: new[] { mesh }); }
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        string Snapshot(AnimationClip clip) => JsonSerializer.Serialize(clip, new JsonSerializerOptions { IncludeFields = true });
        Quaternion At(BoneTrack track, float time)
        {
            var times = track.RotTimes!; var q = track.Rotations!;
            var next = Array.FindIndex(times, t => t >= time);
            if (next < 0) return q[^1]; if (next == 0) return q[0];
            return Quaternion.Slerp(q[next - 1], q[next], (time - times[next - 1]) / (times[next] - times[next - 1]));
        }
        var configuredPairs = motions.SelectMany(b => AnimationLayerCatalog.Find(path, b, motions)
            .Select(a => (b.MotionNumber, a.Motion.MotionNumber))).ToArray();
        Check(configuredPairs.Length == 17, "Missing configured motion pairs");
        Check(AnimationLayerCatalog.GetProfile(path,156,157) == new AnimationLayerProfile(10,11)
            && AnimationLayerCatalog.GetProfile(path,158,159) == new AnimationLayerProfile(10,11), "Hold-end masks lost");
        foreach (var (bid, aid) in configuredPairs.Concat(new[] { (543,542), (543,1542), (551,1552), (810,1811) }))
        {
            var b = Load(bid); var a = Load(aid); var bs = Snapshot(b); var ads = Snapshot(a);
            var profile = AnimationLayerCatalog.GetProfile(path, bid, aid)!;
            Check(profile != null, "Missing layer profile");
            var upper = AnimationPreviewProfiles.ReadRe4Mask(pak, mesh.Bones, profile!.BaseMaskId);
            var add = AnimationPreviewProfiles.ReadRe4Mask(pak, mesh.Bones, profile.AdditiveMaskId);
            var disabled = AnimationPreviewMixer.ComposeUpperBody(b, null, mesh.Bones, upper, add);
            var zero = AnimationPreviewMixer.ComposeUpperBody(b, a, mesh.Bones, upper, add, 0);
            foreach (var name in disabled.NamedTracks.Keys)
                Check(zero.NamedTracks[name].Rotations!.Zip(disabled.NamedTracks[name].Rotations!,
                    (x,y) => Math.Abs(Quaternion.Dot(x,y)) > .99999f).All(v => v), "Disabling ADD differs from 0%: " + name);
            foreach (var weight in new[] { 0f,.5f,1f })
            {
                var result = AnimationPreviewMixer.ComposeUpperBody(b, a, mesh.Bones, upper, add, weight);
                Check(result.Duration == b.Duration, "Base time changed");
                // The upper layer replaces the lower ADD on arms and upper spine.
                foreach (var name in upper.Where(p => p.Value.Rotation >= 1
                    && b.NamedTracks.TryGetValue(p.Key, out var track) && track.Rotations is {Length: > 0}).Select(p => p.Key))
                {
                    var track = result.NamedTracks[name];
                    for (int i = 0; i < track.RotTimes!.Length; i++)
                        Check(Math.Abs(Quaternion.Dot(track.Rotations![i], At(b.NamedTracks[name], track.RotTimes[i]))) > .99999f,
                            $"Double rotation: {bid} {name} frame {i}");
                }
                // Spine_0 is NOT covered by mask 3. It receives the weighted ADD
                // on the standing reference, never the already-animated base twice.
                var bone = mesh.Bones.Single(v => v.Name == "Spine_0");
                Matrix4x4.Decompose(bone.LocalBind, out _, out var reference, out _);
                var spine = result.NamedTracks[bone.Name];
                for (int i = 0; i < spine.RotTimes!.Length && upper.GetValueOrDefault(bone.Name).Rotation == 0; i++)
                {
                    var expected = Quaternion.Normalize(reference * Quaternion.Slerp(Quaternion.Identity,
                        At(a.NamedTracks[bone.Name], spine.RotTimes[i]), add.GetValueOrDefault(bone.Name).Rotation * weight));
                    Check(Math.Abs(Quaternion.Dot(expected, spine.Rotations![i])) > .99999f, "Spine reference doubled");
                }
                foreach (var track in result.NamedTracks.Values)
                    Check(track.Rotations!.All(q => float.IsFinite(q.LengthSquared()) && Math.Abs(q.LengthSquared()-1)<1e-4), "Invalid rotation");
            }
            Check(Snapshot(b) == bs && Snapshot(a) == ads, "Input clip mutated");
            Console.WriteLine($"LAYER_ORDER_{bid}_{aid}_ALL_FRAMES_0_50_100_PASS");
        }
        Check(AnimationLayerCatalog.GetProfile("other", 551, 552) == null, "Profile leaked to another list");
        Check(AnimationLayerCatalog.GetProfile(path, 551, 1552) == AnimationLayerCatalog.GetProfile(path, 551, 552), "Candidate changed base layer context");
        Check(!AnimationLayerCatalog.Find(path, motions.Single(m => m.MotionNumber == 551), motions)
            .Any(c => c.Motion.MotionNumber == 1552), "Unverified pairing offered");
        Console.WriteLine("UPPER_BODY_LAYER_ORDER_REGRESSION_PASS");
    }
}
