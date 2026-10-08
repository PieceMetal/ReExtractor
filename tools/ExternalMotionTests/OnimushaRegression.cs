using ReExtractor.Core;
using ReeLib;
using SharpGLTF.Schema2;
using static SyntheticRegression;

internal static class OnimushaRegression
{
    public static void Run(string game)
    {
        var pak = new PakService();
        pak.AddPaksFromGameDir(game);
        pak.LoadListFile(Path.Combine(game, "OWOTS_STM_Release.list"));
        var entries = pak.EnumerateFiles();
        var damage = entries.Single(e => e.Path.EndsWith("em101_00_01_damage.motlist.1036", StringComparison.OrdinalIgnoreCase)).Path;
        var meshPath = "natives/stm/art/model/character/ch1/ch101/00/00/ch101_00_00.mesh.260209350";
        using var meshStream = pak.ReadFile(meshPath);
        var mesh = ViewportDataLoader.LoadMesh(meshStream, meshPath, 0, null, false);
        var names = mesh.Bones.Select(b => b.Name).ToArray();
        using var rootStream = pak.ReadFile(damage);
        using var raw = new MotlistFile(new FileHandler(rootStream, damage));
        raw.Read();
        Require(raw.Motions.Count == 148 && raw.Motions.All(m => m.MotFile == null), "original feedback reproduction");
        var basePath = "natives/stm/" + raw.Header.BaseMotListPath + ".1036";
        using var baseStream = pak.ReadFile(basePath);
        using var basis = new MotlistFile(new FileHandler(baseStream, basePath));
        basis.Read();
        using var resolvedStream = pak.ReadFile(damage);
        var motions = ViewportDataLoader.ListMotions(resolvedStream, damage, pak.ReadFile);
        Require(motions.Count == 148, "all inherited damage motions listed");

        var trackCount = 0;
        foreach (var item in motions)
        {
            var source = raw.Motions[item.SourceIndex];
            var baseIndex = basis.Motions.FindIndex(m => m.motNumber == source.motNumber && m.Switch == source.Switch);
            using var derivedStream = pak.ReadFile(damage);
            using var originalStream = pak.ReadFile(basePath);
            var clip = ViewportDataLoader.LoadAnimation(derivedStream, damage, item.SourceIndex, names, new[] { mesh }, pak.ReadFile);
            var expected = ViewportDataLoader.LoadAnimation(originalStream, basePath, baseIndex, names, new[] { mesh });
            Require(clip.Duration > 0 && clip.NamedTracks.Count > 0, "non-empty inherited motion " + item.MotionNumber);
            Require(clip.Duration == expected.Duration && clip.FrameCount == expected.FrameCount && clip.FrameRate == expected.FrameRate,
                "inherited timing " + item.MotionNumber);
            Require(clip.NamedTracks.Keys.Order().SequenceEqual(expected.NamedTracks.Keys.Order()), "track names");
            foreach (var (name, track) in clip.NamedTracks)
            {
                var other = expected.NamedTracks[name];
                Require(Equal(track.TransTimes, other.TransTimes) && Equal(track.Translations, other.Translations) &&
                    Equal(track.RotTimes, other.RotTimes) && Equal(track.Rotations, other.Rotations) &&
                    track.IsAdditive == other.IsAdditive, "track values " + item.MotionNumber + "/" + name);
            }
            trackCount += clip.NamedTracks.Count;
        }
        Console.WriteLine($"REAL_DAMAGE_PASS motions={motions.Count} namedTracks={trackCount} exact base-keyframe equality and ch101 skeleton compatibility");

        var outDir = Path.GetFullPath("artifacts/oni-external-motion/damage-glb");
        using var exportStream = pak.ReadFile(damage);
        var progress = new List<(int, int)>();
        var outputs = new AnimationService().ConvertAllToGlbWithAnimation(mesh, exportStream, damage, outDir,
            (current, total) => progress.Add((current, total)), pak.ReadFile);
        Require(outputs.Count == 148 && progress.Last() == (148, 148), "batch export count and progress");
        foreach (var path in outputs)
        {
            var model = ModelRoot.Load(path);
            Require(model.LogicalAnimations.Count == 1 && model.LogicalAnimations[0].Channels.Count > 0, "exported animation channels");
        }
        Console.WriteLine($"REAL_BATCH_EXPORT_PASS glb={outputs.Count} all reloaded with animation channels");

        var listCount = 0; var inheritedCount = 0;
        foreach (var entry in entries.Where(e => e.Path.Contains("/motion/enemy/em101/00/", StringComparison.OrdinalIgnoreCase) && e.Path.Contains(".motlist.")))
        {
            using var stream = pak.ReadFile(entry.Path);
            var readable = ViewportDataLoader.ListMotions(stream, entry.Path, pak.ReadFile);
            if (entry.Path.Contains("_tree/")) continue;
            Require(readable.Count > 0, "sibling list " + entry.Path);
            listCount++; inheritedCount += readable.Count;
        }
        Console.WriteLine($"REAL_VARIANT_PASS lists={listCount} readableMotions={inheritedCount}");
    }

    private static bool Equal<T>(T[]? a, T[]? b) => a == null ? b == null : b != null && a.SequenceEqual(b);
}
