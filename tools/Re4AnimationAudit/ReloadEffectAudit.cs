using ReExtractor.Core;
using System.Numerics;
using System.Text.Json;

static class ReloadEffectAudit
{
    public static void Run(PakService pak)
    {
        const string path = "natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
        const string mp = "natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
        using var ms = pak.ReadFile(mp);
        var mesh = ViewportDataLoader.LoadMesh(ms, mp, 0, null, false);
        using var ls = pak.ReadFile(path);
        var motions = ViewportDataLoader.ListMotions(ls, path);
        AnimationClip Load(int id) { using var s = pak.ReadFile(path); return ViewportDataLoader.LoadAnimation(s, path,
            motions.Single(m => m.MotionNumber == id).SourceIndex, sceneMeshes: new[] { mesh }); }
        var basis = Load(810); var overlay = Load(811);
        var upper = AnimationPreviewProfiles.ReadRe4Mask(pak, mesh.Bones, 3);
        var add = AnimationPreviewProfiles.ReadRe4Mask(pak, mesh.Bones, 14);
        var off = AnimationPreviewMixer.ComposeUpperBody(basis, null, mesh.Bones, upper, add);
        var on = AnimationPreviewMixer.ComposeUpperBody(basis, overlay, mesh.Bones, upper, add);
        var count = on.NamedTracks.First().Value.Rotations!.Length;
        var maxRot = new double[mesh.Bones.Length];
        var maxPos = new double[mesh.Bones.Length];
        var maxFrame = new int[mesh.Bones.Length];
        for (int frame = 0; frame < count; frame++)
        {
            Matrix4x4[] World(AnimationClip clip)
            {
                var matrices = new Matrix4x4[mesh.Bones.Length];
                var done = new bool[mesh.Bones.Length];
                Matrix4x4 Get(int i)
                {
                    if (done[i]) return matrices[i];
                    var bone = mesh.Bones[i]; var t = clip.NamedTracks[bone.Name];
                    Matrix4x4.Decompose(bone.LocalBind, out var scale, out _, out _);
                    var local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(t.Rotations![frame])
                        * Matrix4x4.CreateTranslation(t.Translations![frame]);
                    matrices[i] = bone.ParentIndex < 0 ? local : local * Get(bone.ParentIndex);
                    done[i] = true; return matrices[i];
                }
                for (int i = 0; i < matrices.Length; i++) Get(i);
                return matrices;
            }
            var a = World(off); var b = World(on);
            for (int i = 0; i < mesh.Bones.Length; i++)
            {
                var name = mesh.Bones[i].Name;
                var q0 = off.NamedTracks[name].Rotations![frame]; var q1 = on.NamedTracks[name].Rotations![frame];
                var delta = Quaternion.Normalize(Quaternion.Inverse(q0) * q1);
                var angle = 2 * Math.Atan2(new Vector3(delta.X, delta.Y, delta.Z).Length(), Math.Abs(delta.W)) * 180 / Math.PI;
                maxRot[i] = Math.Max(maxRot[i], angle);
                var distance = Vector3.Distance(a[i].Translation, b[i].Translation);
                if (distance > maxPos[i]) { maxPos[i] = distance; maxFrame[i] = frame; }
            }
        }
        var rows = mesh.Bones.Select((bone, i) => new { bone.Name, AddRotationWeight = add.GetValueOrDefault(bone.Name).Rotation,
            UpperRotationWeight = upper.GetValueOrDefault(bone.Name).Rotation, MaxLocalDegrees = maxRot[i],
            MaxWorldDistance = maxPos[i], FrameAtMaxDistance = maxFrame[i] }).ToArray();
        var report = new { basis.Duration, basis.FrameRate, SampleCount = count, Comparison = "masked standing reference: 810 without ADD versus 810+811 at 100%", Bones = rows };
        File.WriteAllText("artifacts/preview-fix/811-effect.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var row in rows.Where(r => r.MaxLocalDegrees > .01 || r.MaxWorldDistance > .0001))
            Console.WriteLine($"{row.Name} localDeg={row.MaxLocalDegrees:F5} worldDistance={row.MaxWorldDistance:F6} frame={row.FrameAtMaxDistance} addMask={row.AddRotationWeight} upperMask={row.UpperRotationWeight}");
        Console.WriteLine($"811_EFFECT_DONE frames={count} duration={basis.Duration} fps={basis.FrameRate}");
    }
}
