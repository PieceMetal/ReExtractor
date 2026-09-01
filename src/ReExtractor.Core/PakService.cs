using ReeLib;
using ReeLib.Pak;

namespace ReExtractor.Core;

/// <summary>
/// A resolved file entry inside one or more PAK archives.
/// </summary>
public sealed record PakEntryInfo(string Path, long DecompressedSize, long CompressedSize, string SourcePak);
public sealed record PakExtractionResult(int Exported, int Failed, IReadOnlyList<string> Failures);

/// <summary>
/// Wraps REE-Lib PAK reading: multi-PAK priority (base + patches) and list-file path resolution.
/// </summary>
public sealed class PakService
{
    private readonly List<string> _pakFiles = new();
    private readonly List<FolderMount> _folderMounts = new();
    private readonly Dictionary<ulong, string> _knownPaths = new();
    // Built while populating the GUI tree. Maps every PAK entry hash to its
    // highest-priority source PAK, so preview texture reads do not rescan all patches.
    private readonly Dictionary<ulong, int> _pakEntrySources = new();
    private bool _pakEntryIndexReady;
    private readonly Dictionary<string, FolderFile> _folderFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _folderAliases =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record FolderMount(string Root, string VirtualRoot, string DisplayName);
    private sealed record FolderFile(string PhysicalPath, string DisplayPath, long Size, string SourceFolder);

    /// <summary>Add a PAK file. Add in chronological order: base first, patches after (patches win).</summary>
    public void AddPak(string pakPath)
    {
        if (!File.Exists(pakPath)) throw new FileNotFoundException("PAK not found", pakPath);
        _pakFiles.Add(pakPath);
        _pakEntrySources.Clear();
        _pakEntryIndexReady = false;
    }

    /// <summary>
    /// Add an already-unpacked RE Engine resource directory.
    ///
    /// The method accepts either a native resource root (containing natives/),
    /// a re_chunk_000 directory, or a larger extraction workspace.  It also
    /// recognizes the project's convenience "motion" directory and exposes it
    /// both as motion/... and natives/STM/Motion/... so embedded references can
    /// still resolve when the original game is not installed.
    /// </summary>
    public void AddFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Resource folder not found: {folderPath}");

        var fullPath = Path.GetFullPath(folderPath);
        var mounts = DetectFolderMounts(fullPath);
        if (mounts.Count == 0)
            mounts.Add(new FolderMount(fullPath, string.Empty, Path.GetFileName(fullPath)));

        foreach (var mount in mounts)
        {
            _folderMounts.Add(mount);
            IndexFolderMount(mount);
        }
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
    public int FolderCount => _folderMounts.Select(m => m.DisplayName)
        .Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public bool HasFolders => _folderMounts.Count > 0;

    /// <summary>
    /// Enumerate all entries that resolve to a known path.
    /// Higher-priority PAKs (patches) are visited first; duplicates fall back to the highest-priority copy.
    /// </summary>
    public IReadOnlyList<PakEntryInfo> EnumerateFiles()
    {
        var result = new List<PakEntryInfo>();
        var seen = new HashSet<ulong>();
        _pakEntrySources.Clear();
        _pakEntryIndexReady = false;

        // A folder is already path-addressable, so it does not require a .list.
        // Later folder mounts win over earlier mounts, matching patch PAK priority.
        foreach (var file in _folderFiles.Values
                     .OrderByDescending(file => _folderMounts.FindIndex(m =>
                         m.DisplayName.Equals(file.SourceFolder, StringComparison.OrdinalIgnoreCase)))
                     .ThenBy(file => file.DisplayPath, StringComparer.OrdinalIgnoreCase))
        {
            var hash = PakUtils.GetFilepathHash(file.DisplayPath);
            if (!seen.Add(hash)) continue;
            result.Add(new PakEntryInfo(
                file.DisplayPath,
                file.Size,
                file.Size,
                file.SourceFolder));
        }

        for (var i = _pakFiles.Count - 1; i >= 0; i--)
        {
            using var pak = new PakFile();
            pak.ReadContents(_pakFiles[i], _knownPaths);
            foreach (var entry in pak.Entries)
            {
                // We iterate newest to oldest, so the first source wins just like the
                // visible resource tree. Keep entries with unknown paths too: material
                // references can still resolve by hash even when a list file omitted them.
                _pakEntrySources.TryAdd(entry.CombinedHash, i);
                if (entry.path == null || !seen.Add(entry.CombinedHash)) continue;
                result.Add(new PakEntryInfo(
                    entry.path,
                    entry.decompressedSize,
                    entry.compressedSize,
                Path.GetFileName(_pakFiles[i])));
            }
        }
        _pakEntryIndexReady = true;
        return result.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Read one file (by native path) into memory, searching highest-priority PAK first.</summary>
    public MemoryStream ReadFile(string nativePath)
    {
        if (TryReadFolderFile(nativePath, out var folderStream))
            return folderStream;

        var hash = PakUtils.GetFilepathHash(nativePath);
        if (_pakEntrySources.TryGetValue(hash, out var cachedPakIndex))
        {
            var cached = TryReadPakFile(cachedPakIndex, hash);
            if (cached != null) return cached;
            // The archive changed after indexing. Drop the stale route and fall back to
            // a full lookup once, so a valid resource is never hidden by the cache.
            _pakEntrySources.Remove(hash);
        }

        // The GUI always builds the PAK table before previewing. Once that pass has
        // completed, a failed candidate (for example one of many TEX version suffixes)
        // can be rejected immediately instead of reopening every patch PAK.
        if (_pakEntryIndexReady)
            throw new FileNotFoundException($"Entry not found in any loaded PAK: {nativePath}");

        for (var i = _pakFiles.Count - 1; i >= 0; i--)
        {
            var stream = TryReadPakFile(i, hash);
            if (stream == null) continue;
            _pakEntrySources[hash] = i;
            return stream;
        }
        throw new FileNotFoundException($"Entry not found in any loaded PAK: {nativePath}");
    }

    private MemoryStream? TryReadPakFile(int pakIndex, ulong hash)
    {
        if ((uint)pakIndex >= (uint)_pakFiles.Count) return null;
        using var pak = new PakFile { filepath = _pakFiles[pakIndex] };
        pak.ReadContents(_pakFiles[pakIndex], _knownPaths);
        var entry = pak.Entries.FirstOrDefault(candidate => candidate.CombinedHash == hash);
        if (entry == null) return null;

        var stream = new MemoryStream((int)entry.decompressedSize);
        pak.Read(entry, stream);
        stream.Position = 0;
        return stream;
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

    /// <summary>Extract every list-resolved entry while opening each PAK only once.</summary>
    public PakExtractionResult ExtractAllKnown(string outputDir,
        Action<int, string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_pakFiles.Count == 0) throw new InvalidOperationException("当前没有已加载的 PAK");
        var root = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(root);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var seen = new HashSet<ulong>();
        var failures = new List<string>();
        var exported = 0;
        var failed = 0;

        // Patches win: extract the first occurrence while traversing newest to oldest.
        for (var pakIndex = _pakFiles.Count - 1; pakIndex >= 0; pakIndex--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pak = new PakFile { filepath = _pakFiles[pakIndex] };
            pak.ReadContents(_pakFiles[pakIndex], _knownPaths);
            foreach (var entry in pak.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.path == null || !seen.Add(entry.CombinedHash)) continue;
                string? target = null;
                try
                {
                    var relative = entry.path.Replace('/', Path.DirectorySeparatorChar)
                        .Replace('\\', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(relative)) throw new InvalidDataException("资源路径不能是绝对路径");
                    target = Path.GetFullPath(Path.Combine(root, relative));
                    if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("资源路径超出输出目录");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                    pak.Read(entry, output);
                    exported++;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (target != null && File.Exists(target))
                    {
                        try { File.Delete(target); } catch { }
                    }
                    if (failures.Count < 200) failures.Add($"{entry.path}: {ex.Message}");
                }
                progress?.Invoke(exported + failed, entry.path);
            }
        }
        return new PakExtractionResult(exported, failed, failures);
    }

    private static List<FolderMount> DetectFolderMounts(string root)
    {
        var result = new List<FolderMount>();

        void AddIfDirectory(string physicalRoot, string virtualRoot, string displayName)
        {
            if (!Directory.Exists(physicalRoot)) return;
            result.Add(new FolderMount(
                Path.GetFullPath(physicalRoot),
                NormalizePath(virtualRoot),
                displayName));
        }

        // Native root selected directly: <folder>/natives/...
        AddIfDirectory(Path.Combine(root, "natives"), string.Empty,
            Path.GetFileName(root));

        // Typical ree-pak-rs / RE Engine extraction layout:
        // <root>/re_chunk_000/natives/... or
        // <root>/Game_Extract/MHWILDS_EXTRACT/re_chunk_000/natives/...
        IEnumerable<string> chunks;
        try
        {
            chunks = Directory.EnumerateDirectories(root, "re_chunk_*",
                SearchOption.AllDirectories).ToArray();
        }
        catch
        {
            chunks = Array.Empty<string>();
        }
        foreach (var chunk in chunks)
        {
            var natives = Path.Combine(chunk, "natives");
            if (Directory.Exists(natives))
                AddIfDirectory(chunk, string.Empty, Path.GetFileName(root));
        }

        // The workspace used by this project contains a shortened motion tree.
        // Keep the original relative path and add the native alias.  When the
        // user selects re_chunk_000 directly, the sibling "motion" directory
        // lives several levels above it, so walk a few ancestors as well.
        var motionRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = new DirectoryInfo(root);
        for (var depth = 0; cursor != null && depth <= 6; depth++, cursor = cursor.Parent)
        {
            var motion = Path.Combine(cursor.FullName, "motion");
            if (Directory.Exists(motion)) motionRoots.Add(Path.GetFullPath(motion));
        }
        foreach (var motion in motionRoots)
        {
            AddIfDirectory(motion, "motion", Path.GetFileName(root));
            AddIfDirectory(motion, "natives/STM/Motion", Path.GetFileName(root));
        }

        // If the selected directory itself is a native root, do not add the
        // fallback root mount as it would duplicate every entry.
        if (result.Count == 0)
            AddIfDirectory(root, string.Empty, Path.GetFileName(root));

        return result;
    }

    private void IndexFolderMount(FolderMount mount)
    {
        foreach (var physicalPath in Directory.EnumerateFiles(
                     mount.Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(mount.Root, physicalPath);
            var displayPath = string.IsNullOrEmpty(mount.VirtualRoot)
                ? relative
                : Path.Combine(mount.VirtualRoot, relative);
            displayPath = NormalizePath(displayPath);
            if (displayPath.Length == 0) continue;

            // Newer mounts overwrite older mounts, just like patch PAKs.
            _folderFiles[displayPath] = new FolderFile(
                physicalPath,
                displayPath,
                new FileInfo(physicalPath).Length,
                mount.DisplayName);

            // An extracted RE chunk is commonly opened at different levels:
            // some callers use natives/STM/..., while the GUI tree may show
            // STM/... after selecting the natives folder.  Keep one canonical
            // enumerated path and add read-only aliases for embedded links.
            if (displayPath.StartsWith("natives/", StringComparison.OrdinalIgnoreCase))
            {
                var withoutNatives = displayPath["natives/".Length..];
                _folderAliases[withoutNatives] = displayPath;
            }
            else if (displayPath.StartsWith("STM/", StringComparison.OrdinalIgnoreCase))
            {
                _folderAliases["natives/" + displayPath] = displayPath;
            }
        }
    }

    private bool TryReadFolderFile(string nativePath, out MemoryStream stream)
    {
        stream = null!;
        var normalized = NormalizePath(nativePath);
        if (!_folderFiles.TryGetValue(normalized, out var file) &&
            _folderAliases.TryGetValue(normalized, out var canonical))
        {
            _folderFiles.TryGetValue(canonical, out file);
        }

        if (file == null)
        {
            // Embedded references normally use the native path, while some
            // extracted convenience trees use a shortened path.
            if (normalized.StartsWith("natives/STM/Motion/", StringComparison.OrdinalIgnoreCase))
            {
                var shortPath = "motion/" + normalized["natives/STM/Motion/".Length..];
                if (!_folderFiles.TryGetValue(shortPath, out file) &&
                    _folderAliases.TryGetValue(shortPath, out canonical))
                    _folderFiles.TryGetValue(canonical, out file);
            }
            else if (normalized.StartsWith("motion/", StringComparison.OrdinalIgnoreCase))
            {
                var nativeAlias = "natives/STM/Motion/" + normalized["motion/".Length..];
                if (!_folderFiles.TryGetValue(nativeAlias, out file) &&
                    _folderAliases.TryGetValue(nativeAlias, out canonical))
                    _folderFiles.TryGetValue(canonical, out file);
            }
        }

        if (file == null) return false;
        stream = new MemoryStream((int)Math.Min(file.Size, int.MaxValue));
        using (var input = File.OpenRead(file.PhysicalPath))
            input.CopyTo(stream);
        stream.Position = 0;
        return true;
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').Trim('/');
}
