using ReExtractor.Core;
using ReeLib;
using SharpGLTF.Schema2;

internal static class RawExportBoundary
{
    public static void Run(string root)
    {
        var pak = Shared.Open();
        const string body = "natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
        using var bodyStream = pak.ReadFile(body);
        var mesh = ViewportDataLoader.LoadMesh(bodyStream, body, 0, null, false);
        var configuration = Re4AnimationLayerResolver.Resolve(pak);
        var reports = new List<object>();
        foreach (var bank in new[] { "wp4100h", "wp4411h" })
        {
            var path = $"natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_{bank}.motlist.663";
            using var originalStream = pak.ReadFile(path);
            using var original = new MotlistFile(new FileHandler(originalStream, path));
            if (!original.Read()) throw new Exception("Raw source unreadable");
            var expected = original.Motions.Select((m, i) => (Motion: m, Index: i))
                .Where(m => m.Motion.MotFile is MotFile).ToArray();
            using var namesStream = pak.ReadFile(path);
            var actual = ViewportDataLoader.ListMotions(namesStream, path, pak.ReadFile);
            if (!actual.Select(m => m.SourceIndex).SequenceEqual(expected.Select(m => m.Index)) ||
                !actual.Select(m => m.MotionNumber).SequenceEqual(expected.Select(m => m.Motion.motNumber)))
                throw new Exception("RE4 generic source listing expanded inherited slots: " + path);
            if (bank == "wp4411h" && (!configuration.Sources!.Any(s => s.ListPath == path && s.SourcePath != path) ||
                actual.Any(m => m.MotionNumber is 160 or 165 or 167)))
                throw new Exception("Missing inherited-preview versus raw-export boundary");

            var output = Path.Combine(root, bank);
            using var exportStream = pak.ReadFile(path);
            var files = new AnimationService().ConvertAllToGlbWithAnimation(mesh, exportStream, path,
                output, fileResolver: pak.ReadFile);
            if (files.Count != expected.Length) throw new Exception("RE4 raw-all export changed track count: " + path);
            foreach (var file in files)
            {
                var glb = ModelRoot.Load(file);
                if (glb.LogicalAnimations.Count != 1 || glb.LogicalAnimations[0].Channels.Count == 0)
                    throw new Exception("Raw GLB export has no animation: " + file);
            }
            reports.Add(new { Path = path, EmbeddedTracks = expected.Length, ExportedTracks = files.Count,
                InheritedPreviewSources = configuration.Sources!.Count(s => s.ListPath == path && s.SourcePath != path) });
            Console.WriteLine($"RE4_RAW_EXPORT_BOUNDARY_PASS {bank} embedded={expected.Length} exported={files.Count}");
        }
        Shared.Save(root, "raw-export-boundary", reports);
    }
}
