namespace ReExtractor.Core;

public sealed record MaterialCandidate(string Path, int MatchedNames, int RequiredNames);
public sealed record MaterialResolution(string MeshPath, string? SelectedPath,
    IReadOnlyList<MaterialCandidate> Candidates, IReadOnlyList<string> Diagnostics)
{
    public bool RequiresSelection => SelectedPath == null && Candidates.Count > 1;
}

public sealed class MaterialSelectionRequiredException(MaterialResolution resolution)
    : InvalidOperationException($"{Path.GetFileName(resolution.MeshPath)} 有 {resolution.Candidates.Count} 个匹配材质，请先选择材质版本")
{
    public MaterialResolution Resolution { get; } = resolution;
}

/// <summary>One resource snapshot / operation. Never assigns materials by list position.</summary>
public sealed class MaterialResolver
{
    private readonly Dictionary<string, string[]> _folders;
    private readonly Dictionary<string, string> _choices;
    private readonly Dictionary<string, (string[] Names, MaterialResolution Result)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public MaterialResolver(IEnumerable<string>? resourcePaths = null, IReadOnlyDictionary<string, string>? choices = null)
    {
        _folders = (resourcePaths ?? []).Select(Normalize)
            .Where(p => p.Contains(".mdf2.", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(Folder, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Order(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        _choices = new(StringComparer.OrdinalIgnoreCase);
        if (choices != null) foreach (var pair in choices) _choices[Normalize(pair.Key)] = Normalize(pair.Value);
    }

    public void Choose(MaterialResolution resolution, string path)
    {
        path = Normalize(path);
        if (!resolution.Candidates.Any(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("所选材质未通过材质名校验");
        _choices[Normalize(resolution.MeshPath)] = path;
        _cache.Remove(Normalize(resolution.MeshPath));
    }

    public MaterialResolution Resolve(string meshPath, IReadOnlyList<string> materialNames, Func<string, Stream?> open)
    {
        meshPath = Normalize(meshPath);
        var diagnostics = new List<string>();
        var required = materialNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_cache.TryGetValue(meshPath, out var cached) && cached.Names.SequenceEqual(required, StringComparer.OrdinalIgnoreCase))
            return cached.Result;
        var checkedPaths = new Dictionary<string, MaterialCandidate?>(StringComparer.OrdinalIgnoreCase);
        MaterialCandidate? Check(string path)
        {
            if (checkedPaths.TryGetValue(path, out var previous)) return previous;
            MaterialCandidate? candidate = null;
            try
            {
                using var stream = open(path);
                if (stream != null)
                {
                    using var mdf = MdfService.Read(stream, path);
                    var names = mdf.Materials.Select(m => m.Name).ToArray();
                    var unique = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var matched = required.Count(unique.Contains);
                    if (required.Length > 0 && matched == required.Length && names.Length == unique.Count)
                        candidate = new(path, matched, required.Length);
                    else diagnostics.Add($"{path}：材质名匹配 {matched}/{required.Length}" +
                        (names.Length != unique.Count ? "，存在重名材质" : ""));
                }
            }
            catch (Exception e) { diagnostics.Add($"{path}：{e.Message}"); }
            checkedPaths[path] = candidate;
            return candidate;
        }
        MaterialResolution Finish(string? selected, IReadOnlyList<MaterialCandidate> candidates)
        {
            var result = new MaterialResolution(meshPath, selected, candidates, diagnostics.ToArray());
            _cache[meshPath] = (required, result);
            return result;
        }
        var marker = meshPath.IndexOf(".mesh.", StringComparison.OrdinalIgnoreCase);
        if (marker < 0 || required.Length == 0) return Finish(null, []);
        var stem = meshPath[..marker];
        // Explicit choices survive repeated preview/export operations, but are revalidated
        // against this resource snapshot before use.
        if (_choices.TryGetValue(meshPath, out var chosen) && Check(chosen) is { } explicitCandidate)
            return Finish(chosen, [explicitCandidate]);
        foreach (var suffix in ViewportDataLoader.MdfNameSuffixCandidates)
        foreach (var version in ViewportDataLoader.MdfVersionCandidates)
            if (Check(stem + suffix + version) is { } known) return Finish(known.Path, [known]);

        var siblings = _folders.GetValueOrDefault(Folder(meshPath)) ?? [];
        bool SameStem(string path) => path.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) ||
                                      path.StartsWith(stem + ".mdf2.", StringComparison.OrdinalIgnoreCase);
        var candidates = siblings.Where(SameStem).Select(Check).OfType<MaterialCandidate>().ToArray();
        // A differently named sibling can be shared by model parts. Only consider it
        // if there is no compatible same-stem material, and require full name coverage.
        if (candidates.Length == 0)
            candidates = siblings.Where(p => !SameStem(p)).Select(Check).OfType<MaterialCandidate>().ToArray();
        if (candidates.Length == 0) diagnostics.Add($"{meshPath}：未找到材质名全部匹配的 MDF");
        return Finish(candidates.Length == 1 ? candidates[0].Path : null, candidates);
    }

    internal static string Normalize(string path) => path.Replace('\\', '/');
    private static string Folder(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
}
