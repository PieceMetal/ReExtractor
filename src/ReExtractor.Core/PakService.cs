using ReeLib;
using ReeLib.Pak;

namespace ReExtractor.Core;

/// <summary>
/// A resolved file entry inside one or more PAK archives.
/// </summary>
public sealed record PakEntryInfo(string Path, long DecompressedSize, long CompressedSize, string SourcePak);

/// <summary>
/// Wraps REE-Lib PAK reading: multi-PAK priority (base + patches) and list-file path resolution.
/// </summary>
public sealed class PakService
{
    private readonly List<string> _pakFiles = new();
    private readonly Dictionary<ulong, string> _knownPaths = new();

    /// <summary>Add a PAK file. Add in chronological order: base first, patches after (patches win).</summary>
    public void AddPak(string pakPath)
    {
        if (!File.Exists(pakPath)) throw new FileNotFoundException("PAK not found", pakPath);
        _pakFiles.Add(pakPath);
    }

    /// <summary>Auto-detect re_chunk_000.pak + patch paks in a game directory, in chronological order.</summary>
    public int AddPaksFromGameDir(string gameDir)
    {
        var paks = Directory.GetFiles(gameDir, "*.pak")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var pak in paks) AddPak(pak);
        return paks.Count;
    }

    /// <summary>Load a RE list file (one native path per line) to resolve entry hashes into paths.</summary>
    public int LoadListFile(string listPath)
    {
        var count = 0;
        foreach (var rawLine in File.ReadLines(listPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (_knownPaths.TryAdd(PakUtils.GetFilepathHash(line), line)) count++;
        }
        return count;
    }

    public int KnownPathCount => _knownPaths.Count;
    public int PakCount => _pakFiles.Count;

    /// <summary>
    /// Enumerate all entries that resolve to a known path.
    /// Higher-priority PAKs (patches) are visited first; duplicates fall back to the highest-priority copy.
    /// </summary>
    public IReadOnlyList<PakEntryInfo> EnumerateFiles()
    {
        var result = new List<PakEntryInfo>();
        var seen = new HashSet<ulong>();
        for (var i = _pakFiles.Count - 1; i >= 0; i--)
        {
            using var pak = new PakFile();
            pak.ReadContents(_pakFiles[i], _knownPaths);
            foreach (var entry in pak.Entries)
            {
                if (entry.path == null || !seen.Add(entry.CombinedHash)) continue;
                result.Add(new PakEntryInfo(
                    entry.path,
                    entry.decompressedSize,
                    entry.compressedSize,
                    Path.GetFileName(_pakFiles[i])));
            }
        }
        return result.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Read one file (by native path) into memory, searching highest-priority PAK first.</summary>
    public MemoryStream ReadFile(string nativePath)
    {
        var hash = PakUtils.GetFilepathHash(nativePath);
        for (var i = _pakFiles.Count - 1; i >= 0; i--)
        {
            using var pak = new PakFile { filepath = _pakFiles[i] };
            pak.ReadContents(_pakFiles[i], _knownPaths);
            var entry = pak.Entries.FirstOrDefault(e => e.CombinedHash == hash);
            if (entry == null) continue;

            var ms = new MemoryStream((int)entry.decompressedSize);
            pak.Read(entry, ms);
            ms.Position = 0;
            return ms;
        }
        throw new FileNotFoundException($"Entry not found in any loaded PAK: {nativePath}");
    }

    /// <summary>Extract one file to an output directory, preserving its native path.</summary>
    public string ExtractFile(string nativePath, string outputDir)
    {
        var ms = ReadFile(nativePath);
        var outPath = Path.Combine(outputDir, nativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using (var fs = File.Create(outPath))
            ms.CopyTo(fs);
        ms.Dispose();
        return outPath;
    }
}
