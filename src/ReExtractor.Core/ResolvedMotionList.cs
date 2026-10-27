using ReeLib;
using ReeLib.Motlist;

namespace ReExtractor.Core;

/// <summary>
/// Keeps the source lists alive while filling inherited slots from BaseMotListPath.
/// A motbank registers lists; it is not a container of MOT payloads.
/// </summary>
internal sealed class ResolvedMotionList : IDisposable
{
    private readonly List<MotlistFile> _sources = [];
    private readonly Dictionary<MotIndex, string> _origins = new();
    private readonly HashSet<string> _activePaths = new(StringComparer.OrdinalIgnoreCase);

    public MotlistFile List { get; private set; } = null!;

    public string? OriginOf(MotIndex motion) => _origins.GetValueOrDefault(motion);

    public static ResolvedMotionList Read(Stream stream, string path, Func<string, Stream?>? fileResolver)
    {
        var result = new ResolvedMotionList();
        try
        {
            result.List = result.ReadList(stream, path, fileResolver);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private MotlistFile ReadList(Stream stream, string path, Func<string, Stream?>? fileResolver)
    {
        var key = path.Replace('\\', '/');
        if (_activePaths.Count >= 32 || !_activePaths.Add(key))
        {
            stream.Dispose();
            throw new InvalidDataException($"基础动画列表引用循环或层级过深：{path}");
        }

        try
        {
            var list = new MotlistFile(new FileHandler(stream, path));
            _sources.Add(list);
            // Even the oldest format has a 44-byte fixed header. Reject short
            // buffers before unchecked library reads can look like an empty list.
            if (stream.Length < 44) throw new InvalidDataException($"动画列表文件头不完整：{path}");
            if (!list.Read()) throw new InvalidDataException($"无法解析动画列表：{path}");

            foreach (var motion in list.Motions.Where(m => m.MotFile != null))
                _origins[motion] = key;

            // RE4 uses its own configuration-checked preview sources. Its raw
            // list and batch exports must retain the original embedded tracks.
            if (list.Header.version == ReeLib.Motlist.MotlistVersion.RE4) return list;

            // An embedded MOT or tree is a local override. Only null pointers inherit.
            var missing = list.Motions.Where(motion => motion.MotFile == null).ToArray();
            if (missing.Length == 0 || fileResolver == null || string.IsNullOrWhiteSpace(list.Header.BaseMotListPath))
                return list;

            var candidates = ReferencePaths(path, list.Header.BaseMotListPath, (int)list.Header.version);
            Stream? baseStream = null;
            string? basePath = null;
            foreach (var candidate in candidates)
            {
                try { baseStream = fileResolver(candidate); }
                catch (FileNotFoundException) { continue; }
                if (baseStream == null) continue;
                basePath = candidate;
                break;
            }
            if (baseStream == null)
                throw new FileNotFoundException($"缺少基础动画列表：{list.Header.BaseMotListPath}（来源：{path}）。请加载包含该资源的 PAK 或资源目录。", candidates[0]);

            var basis = ReadList(baseStream, basePath!, fileResolver);
            var byId = basis.Motions.Where(m => m.MotFile != null)
                .ToLookup(m => (m.motNumber, m.Switch));
            foreach (var motion in missing)
            {
                // Slot positions can differ across variants. Match the declared ID and switch.
                var matches = byId[(motion.motNumber, motion.Switch)].Select(m => m.MotFile!).Distinct().ToArray();
                if (matches.Length > 1)
                    throw new InvalidDataException($"基础动画编号重复，无法确定来源：{basePath}，编号 {motion.motNumber}，分支 {motion.Switch}");
                if (matches.Length == 1)
                {
                    motion.MotFile = matches[0];
                    var source = byId[(motion.motNumber, motion.Switch)].First(m => m.MotFile == matches[0]);
                    _origins[motion] = _origins[source];
                }
            }
            return list;
        }
        finally
        {
            _activePaths.Remove(key);
        }
    }

    private static string[] ReferencePaths(string ownerPath, string reference, int version)
    {
        var path = reference.Replace('\\', '/').TrimStart('/');
        if (path.EndsWith(".motlist", StringComparison.OrdinalIgnoreCase)) path += "." + version;
        if (path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase)) return [path];

        var owner = ownerPath.Replace('\\', '/').Split('/');
        var nativeRoot = owner.Length > 2 && owner[0].Equals("natives", StringComparison.OrdinalIgnoreCase)
            ? $"{owner[0]}/{owner[1]}/" : "natives/stm/";
        return [nativeRoot + path, path];
    }

    public void Dispose()
    {
        for (var i = _sources.Count - 1; i >= 0; i--) _sources[i].Dispose();
        _sources.Clear();
    }
}
