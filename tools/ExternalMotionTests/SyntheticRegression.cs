using System.Numerics;
using ReExtractor.Core;
using ReeLib;
using ReeLib.Common;
using ReeLib.Mot;
using ReeLib.Motlist;

internal static class SyntheticRegression
{
    private const string Root = "natives/stm/motion/derived.motlist.1036";

    public static void Run()
    {
        var basis = Encode(null, (10, 0, "ten"), (20, 0, "twenty"), (10, 1, "ten-switch-one"));
        var derived = Encode(@"Motion\base.motlist", (20, 0, null), (10, 1, null), (10, 0, "local-ten"), (99, 0, null));
        var streams = new List<Stream>();
        Stream? Open(string path)
        {
            if (!path.Equals("natives/stm/motion/base.motlist.1036", StringComparison.OrdinalIgnoreCase)) return null;
            var stream = new MemoryStream(basis); streams.Add(stream); return stream;
        }
        using (var stream = new MemoryStream(derived))
        {
            var motions = ViewportDataLoader.ListMotions(stream, Root, Open);
            Require(motions.Select(m => m.SourceIndex).SequenceEqual(new[] { 0, 1, 2 }), "source indices / unmatched slot");
            Require(motions.Select(m => m.MotionNumber).SequenceEqual(new[] { 20, 10, 10 }), "ID mapping");
            Require(motions[0].DisplayName.StartsWith("twenty") && motions[1].DisplayName.StartsWith("ten-switch-one") &&
                motions[2].DisplayName.StartsWith("local-ten"), "reordered IDs, switch match, local override");
        }
        Require(streams.All(stream => !stream.CanRead), "dependency stream disposal on success");
        using (var stream = new MemoryStream(derived))
            Require(ViewportDataLoader.ListMotions(stream, Root).Count == 1, "embedded-only API compatibility");

        var allLocal = Encode("motion/absent.motlist", (10, 0, "own"));
        using (var stream = new MemoryStream(allLocal))
            Require(ViewportDataLoader.ListMotions(stream, Root, _ => throw new Exception("unneeded dependency read")).Count == 1,
                "complete local list does not require base");

        var grandchild = Encode("natives/stm/motion/derived.motlist.1036", (20, 0, null));
        var resources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Root] = derived, ["natives/stm/motion/base.motlist.1036"] = basis
        };
        Stream? OpenChain(string path) => resources.TryGetValue(path, out var bytes) ? new MemoryStream(bytes) : null;
        using (var stream = new MemoryStream(grandchild))
            Require(ViewportDataLoader.ListMotions(stream, "natives/stm/motion/grandchild.motlist.1036", OpenChain)
                .Single().DisplayName.StartsWith("twenty"), "multi-level / explicitly versioned native reference");

        using (var stream = new MemoryStream(derived))
            Expect<FileNotFoundException>(() => ViewportDataLoader.ListMotions(stream, Root, _ => null), "缺少基础动画列表");
        using (var stream = new MemoryStream(derived))
            Expect<FileNotFoundException>(() => ViewportDataLoader.ListMotions(stream, Root,
                _ => throw new FileNotFoundException()), "base.motlist");
        var cycle = Encode("Motion/derived.motlist", (10, 0, null));
        var cycleStreams = new List<Stream>();
        using (var stream = new MemoryStream(cycle))
            Expect<InvalidDataException>(() => ViewportDataLoader.ListMotions(stream, Root, _ =>
            {
                var opened = new MemoryStream(cycle); cycleStreams.Add(opened); return opened;
            }), "循环");
        Require(cycleStreams.All(stream => !stream.CanRead), "dependency stream disposal on cycle");

        var corrupt = new MemoryStream(new byte[1]);
        using (var stream = new MemoryStream(derived))
        {
            var rejected = false;
            try { ViewportDataLoader.ListMotions(stream, Root, _ => corrupt); }
            catch (Exception) { rejected = true; }
            Require(rejected && !corrupt.CanRead, "corrupt base rejected and stream disposed");
        }
        var duplicate = Encode(null, (10, 0, "first"), (10, 0, "second"));
        using (var stream = new MemoryStream(Encode("motion/base.motlist", (10, 0, null))))
            Expect<InvalidDataException>(() => ViewportDataLoader.ListMotions(stream, Root, _ => new MemoryStream(duplicate)), "编号重复");
        Console.WriteLine("SYNTHETIC_PASS reordered IDs, switches, overrides, nested references, missing/cyclic/corrupt/ambiguous dependencies, stream cleanup");
    }

    private static byte[] Encode(string? basePath, params (ushort Id, ushort Switch, string? Name)[] slots)
    {
        using var stream = new MemoryStream();
        using var list = new MotlistFile(new FileHandler(stream, Root));
        list.Header.version = MotlistVersion.OniWS;
        list.Header.BaseMotListPath = basePath;
        foreach (var slot in slots)
        {
            MotFile? mot = null;
            if (slot.Name != null)
            {
                mot = new MotFile(new FileHandler()) { Embedded = true, Name = slot.Name };
                mot.Header.version = MotVersion.OniWS;
                mot.Header.FrameRate = 60;
                var bone = new MotBone { boneName = "root", boneHash = MurMur3HashUtils.GetHash("root"), quaternion = Quaternion.Identity };
                mot.Bones.Add(bone); mot.RootBones.Add(bone);
            }
            list.Motions.Add(new MotIndex(MotlistVersion.OniWS) { motNumber = slot.Id, Switch = slot.Switch, MotFile = mot });
        }
        list.Write();
        return stream.ToArray();
    }

    internal static void Require(bool value, string label)
    {
        if (!value) throw new Exception("FAIL " + label);
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T ex) { Require(ex.Message.Contains(message), message); return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
