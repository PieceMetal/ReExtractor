using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Microsoft.Win32;
using ReExtractor.Core;

namespace ReExtractor.Gui;

/// <summary>One node in the left folder tree (folder or file leaf).</summary>
public sealed class FileTreeNode
{
    public string Name { get; }
    public string? FilePath { get; set; }
    public string Display { get; }
    public List<FileTreeNode> Children { get; } = new();
    private Dictionary<string, FileTreeNode>? _lookup;

    public FileTreeNode(string name, string? display = null)
    {
        Name = name;
        Display = display ?? name;
    }

    public FileTreeNode GetOrAddChild(string key, string? displayName)
    {
        _lookup ??= new Dictionary<string, FileTreeNode>(StringComparer.OrdinalIgnoreCase);
        if (_lookup.TryGetValue(key, out var node)) return node;
        node = new FileTreeNode(key, displayName);
        _lookup[key] = node;
        Children.Add(node);
        return node;
    }

    public void SortRecursive()
    {
        Children.Sort((a, b) =>
        {
            // folders first, then files; alphabetical within each group
            var fa = a.FilePath == null ? 0 : 1;
            var fb = b.FilePath == null ? 0 : 1;
            return fa != fb ? fa - fb : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        foreach (var c in Children) c.SortRecursive();
    }
}

public partial class MainWindow : Window
{
    private sealed class VisconGroupRow
    {
        public required int Key { get; init; }
        public required bool IsVisible { get; init; }
        public required string Display { get; init; }
        public required string MaterialTip { get; init; }
    }

    private sealed class TextObserver : IObserver<string?>
    {
        private readonly Action<string?> _onNext;
        public TextObserver(Action<string?> onNext) => _onNext = onNext;
        public void OnNext(string? value) => _onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed record EntryRow(string Path, long Size, string SourcePak)
    {
        public string SizeText => Size >= 1 << 20 ? $"{Size / 1048576.0:F1} MB" : $"{Size / 1024.0:F1} KB";
    }

    private PakService? _pak;
    private List<EntryRow> _all = new();
    private readonly Dictionary<string, EntryRow> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedPath;
    private string? _contextPath;
    private string? _lastMeshPath;
    private readonly List<string> _previewMeshPaths = new();
    private readonly string _tempDir = Path.Combine(AppContext.BaseDirectory, "temp");
    private readonly string _logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private int _progressOperation;
    private AppSettings _settings = AppSettingsService.Load();
    private readonly List<string> _loadedPakPaths = new();
    private bool _syncingManagedList;
    private readonly IDisposable? _actionStatusLogSubscription;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_logDirectory);
        GameDirBox.Text = _settings.LastGameDirectory;
        RefreshManagedLists(_settings.LastListPath);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);

        // tunnel phase: runs BEFORE the tree/listbox handles the click and changes selection,
        // so right-click selection can be told apart from left-click preview
        FileTree.AddHandler(PointerPressedEvent, OnListPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        SearchResults.AddHandler(PointerPressedEvent, OnListPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        // GlViewport is hit-testable and handles input internally (OpenGlControlBase).
        // FPS display ticks every 500 ms.
        _fpsTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fpsTimer.Tick += (_, _) =>
        {
            var b = Viewport.IsVisible ? $"图形加速 {Viewport.Bounds.Width:F0}×{Viewport.Bounds.Height:F0}" : "";
            var f = Viewport.IsPlaying ? $"帧率 {Viewport.CurrentFps:F0}" : "";
            FpsText.Text = string.Join(' ', new[] { b, f }.Where(x => x != ""));
            if (Viewport.IsVisible) UpdateViewportChrome();
        };
        _fpsTimer.Start();
        Viewport.StateChanged += UpdateViewportChrome;
        _actionStatusLogSubscription = ActionStatus.GetObservable(TextBlock.TextProperty)
            .Subscribe(new TextObserver(AppendLog));
        AppendLog("工具已启动，请选择路径列表并加载 PAK");

    }

    private Avalonia.Threading.DispatcherTimer? _fpsTimer;

    private string CurrentOutputDirectory => string.IsNullOrWhiteSpace(_settings.OutputDirectory)
        ? Path.Combine(AppContext.BaseDirectory, "output")
        : _settings.OutputDirectory;
    private string CurrentBlenderPath => _settings.BlenderPath?.Trim() ?? "";

    private void AppendLog(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || RunLogBox is null) return;
        var line = $"[{DateTime.Now:HH:mm:ss}] {message.Trim()}";
        try
        {
            var logPath = Path.Combine(_logDirectory, $"ReExtractor_{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch { }
        var lines = (RunLogBox.Text ?? "").Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries).ToList();
        lines.Add(line);
        if (lines.Count > 300) lines.RemoveRange(0, lines.Count - 300);
        RunLogBox.Text = string.Join(Environment.NewLine, lines);
        RunLogBox.CaretIndex = RunLogBox.Text.Length;
    }

    private static string KindOf(string path)
    {
        var name = path.ToLowerInvariant();
        if (name.Contains(".tex.")) return "tex";
        if (name.Contains(".mesh.")) return "mesh";
        if (name.Contains(".motlist.")) return "motlist";
        if (name.Contains(".mot.")) return "mot";
        if (name.Contains(".mdf2.")) return "mdf";
        return "other";
    }

    private int BeginProgress(string text, bool indeterminate = true, double maximum = 100)
    {
        var operation = ++_progressOperation;
        ProgressPanel.IsVisible = true;
        ProgressText.Text = text;
        WorkProgress.Maximum = Math.Max(1, maximum);
        WorkProgress.Value = 0;
        WorkProgress.IsIndeterminate = indeterminate;
        return operation;
    }

    private void UpdateProgress(int operation, double value, string? text = null)
    {
        if (operation != _progressOperation) return;
        WorkProgress.IsIndeterminate = false;
        WorkProgress.Value = Math.Clamp(value, 0, WorkProgress.Maximum);
        if (text != null) ProgressText.Text = text;
    }

    private void UpdateCountProgress(int operation, int current, int total, string stage)
    {
        if (operation != _progressOperation) return;
        WorkProgress.Maximum = Math.Max(1, total);
        UpdateProgress(operation, current, $"{stage}（{current}/{total}）");
    }

    private void EndProgress(int operation)
    {
        if (operation != _progressOperation) return;
        ProgressPanel.IsVisible = false;
        ProgressText.Text = "";
    }

    private string? SelectedListPath => (ManagedListCombo.SelectedItem as ManagedFileList)?.FilePath;

    private void RefreshManagedLists(string? selectPath = null)
    {
        var lists = new FileListManagerService().GetLocalLists();
        _syncingManagedList = true;
        ManagedListCombo.ItemsSource = lists;
        ManagedListCombo.SelectedItem = lists.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(selectPath) &&
            Path.GetFullPath(item.FilePath).Equals(Path.GetFullPath(selectPath), StringComparison.OrdinalIgnoreCase));
        _syncingManagedList = false;
    }

    private async Task<string?> ChooseGameDirectoryAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择游戏文件夹",
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private async void OnChooseGameDirClicked(object? sender, RoutedEventArgs e)
    {
        var path = await ChooseGameDirectoryAsync();
        if (path != null) GameDirBox.Text = path;
    }

    private async void OnSelectGameDirClicked(object? sender, RoutedEventArgs e)
    {
        var path = await ChooseGameDirectoryAsync();
        if (path == null) return;
        GameDirBox.Text = path;
        await LoadGameDirectoryAsync();
    }

    private async void OnLoadGameDirClicked(object? sender, RoutedEventArgs e) => await LoadGameDirectoryAsync();

    private async Task LoadGameDirectoryAsync()
    {
        var gameDir = GameDirBox.Text?.Trim() ?? "";
        if (!Directory.Exists(gameDir))
        {
            ActionStatus.Text = "请选择有效的游戏文件夹";
            return;
        }
        _settings.LastGameDirectory = gameDir;
        AppSettingsService.Save(_settings);
        var paks = Directory.GetFiles(gameDir, "*.pak", SearchOption.TopDirectoryOnly);
        if (paks.Length == 0)
        {
            ActionStatus.Text = "该文件夹中没有找到 PAK 文件";
            return;
        }
        await LoadPakFilesAsync(paks);
    }

    private async void OnManageFileListsClicked(object? sender, RoutedEventArgs e)
    {
        var selected = await new FileListManagerWindow(SelectedListPath).ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            RefreshManagedLists(selected);
            _settings.LastListPath = selected;
            AppSettingsService.Save(_settings);
            ActionStatus.Text = $"已选择资源列表：{Path.GetFileNameWithoutExtension(selected)}";
        }
        else
        {
            RefreshManagedLists(SelectedListPath);
        }
    }

    private async void OnManagedListChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingManagedList) return;
        _settings.LastListPath = SelectedListPath ?? "";
        AppSettingsService.Save(_settings);
        await TryAutoSelectGameDirectoryForListAsync(ManagedListCombo.SelectedItem as ManagedFileList);
    }

    private async Task TryAutoSelectGameDirectoryForListAsync(ManagedFileList? list)
    {
        if (list == null) return;

        var match = await Task.Run(() => FindMatchingGameDirectory(list));
        if (match == null)
        {
            ActionStatus.Text = $"没有自动找到与 {list.Title} 匹配的游戏目录，请手动选择游戏文件夹";
            return;
        }

        GameDirBox.Text = match.Value.Directory;
        ActionStatus.Text = $"已自动找到 {match.Value.Name}：{match.Value.Directory}";
        await LoadGameDirectoryAsync();
    }

    private sealed record SteamGameInstall(string Name, string InstallDir, string Directory);

    private static (string Name, string Directory)? FindMatchingGameDirectory(ManagedFileList list)
    {
        var queries = BuildListMatchKeys(list).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (queries.Length == 0) return null;

        var steamMatch = EnumerateInstalledSteamGames()
            .Select(game => new { Game = game, Score = ScoreSteamGameMatch(queries, game) })
            .Where(item => item.Score > 0 && IsLikelyGamePakDirectory(item.Game.Directory))
            .OrderByDescending(item => item.Score)
            .Select(item => ((string Name, string Directory)?)(item.Game.Name, item.Game.Directory))
            .FirstOrDefault();
        if (steamMatch != null) return steamMatch;

        return EnumerateCommonGameDirectories()
            .Select(path => new { Path = path, Score = ScoreDirectoryMatch(queries, path) })
            .Where(item => item.Score > 0 && IsLikelyGamePakDirectory(item.Path))
            .OrderByDescending(item => item.Score)
            .Select(item => ((string Name, string Directory)?)(Path.GetFileName(item.Path), item.Path))
            .FirstOrDefault();
    }

    private static IEnumerable<string> BuildListMatchKeys(ManagedFileList list)
    {
        foreach (var value in new[] { list.Title, list.Identifier, Path.GetFileNameWithoutExtension(list.FilePath) })
        {
            var normalized = NormalizeGameName(value);
            if (normalized.Length >= 3) yield return normalized;

            var trimmed = value
                .Replace("_STM", "", StringComparison.OrdinalIgnoreCase)
                .Replace("_Release", "", StringComparison.OrdinalIgnoreCase)
                .Replace("_Demo", "", StringComparison.OrdinalIgnoreCase);
            normalized = NormalizeGameName(trimmed);
            if (normalized.Length >= 3) yield return normalized;
        }
    }

    private static int ScoreSteamGameMatch(IEnumerable<string> queries, SteamGameInstall game)
    {
        var name = NormalizeGameName(game.Name);
        var installDir = NormalizeGameName(game.InstallDir);
        var acronym = BuildAcronym(game.Name);
        var best = 0;

        foreach (var query in queries)
        {
            if (query.Equals(name, StringComparison.OrdinalIgnoreCase) || query.Equals(installDir, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 100);
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase) || query.Contains(name, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 80);
            if (installDir.Contains(query, StringComparison.OrdinalIgnoreCase) || query.Contains(installDir, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 70);
            if (query.Equals(acronym, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 90);
        }

        return best;
    }

    private static int ScoreDirectoryMatch(IEnumerable<string> queries, string path)
    {
        var folder = NormalizeGameName(Path.GetFileName(path));
        var best = 0;
        foreach (var query in queries)
        {
            if (query.Equals(folder, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 85);
            if (folder.Contains(query, StringComparison.OrdinalIgnoreCase) || query.Contains(folder, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 60);
        }
        return best;
    }

    private static string NormalizeGameName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }

    private static string BuildAcronym(string value)
    {
        var words = value.Split([' ', '-', '_', ':', '.', '\'', '’'], StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder(words.Length + 2);
        foreach (var word in words)
        {
            if (word.Length == 0) continue;
            if (word.All(char.IsDigit)) builder.Append(word);
            else if (char.IsLetterOrDigit(word[0])) builder.Append(char.ToLowerInvariant(word[0]));
        }
        return builder.ToString();
    }

    private static IEnumerable<SteamGameInstall> EnumerateInstalledSteamGames()
    {
        foreach (var library in EnumerateSteamLibraries().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
            {
                var values = ReadSteamManifestValues(manifest);
                if (!values.TryGetValue("name", out var name) || !values.TryGetValue("installdir", out var installDir))
                    continue;

                var directory = Path.Combine(steamApps, "common", installDir);
                if (Directory.Exists(directory)) yield return new SteamGameInstall(name, installDir, directory);
            }
        }
    }

    private static IEnumerable<string> EnumerateCommonGameDirectories()
    {
        var roots = new List<string>();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop)) roots.Add(desktop);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            roots.Add(Path.Combine(profile, "Downloads"));
            roots.Add(Path.Combine(profile, "Games"));
        }

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
        {
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Game"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Capcom"));
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var path in EnumerateDirectoriesShallow(root, 2))
                yield return path;
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesShallow(string root, int maxDepth)
    {
        if (maxDepth < 0) yield break;
        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray(); }
        catch { yield break; }

        foreach (var child in children)
        {
            yield return child;
            foreach (var nested in EnumerateDirectoriesShallow(child, maxDepth - 1))
                yield return nested;
        }
    }

    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        var steamPath = ReadSteamInstallPath();
        if (Directory.Exists(steamPath))
        {
            yield return steamPath;
            foreach (var path in ReadSteamLibraryFolders(Path.Combine(steamPath, "steamapps", "libraryfolders.vdf")))
                yield return path;
        }

        foreach (var path in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
            if (Directory.Exists(path)) yield return path;
    }

    private static string? ReadSteamInstallPath()
    {
        foreach (var keyPath in new[]
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
        })
        {
            if (Registry.GetValue(keyPath, "SteamPath", null) is string steamPath && Directory.Exists(steamPath))
                return steamPath.Replace('/', Path.DirectorySeparatorChar);
            if (Registry.GetValue(keyPath, "InstallPath", null) is string installPath && Directory.Exists(installPath))
                return installPath.Replace('/', Path.DirectorySeparatorChar);
        }
        return null;
    }

    private static IEnumerable<string> ReadSteamLibraryFolders(string vdfPath)
    {
        if (!File.Exists(vdfPath)) yield break;
        foreach (var rawLine in File.ReadLines(vdfPath))
        {
            var line = rawLine.Trim();
            if (!line.Contains("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            var path = parts[^1].Replace(@"\\", @"\").Replace('/', Path.DirectorySeparatorChar);
            if (Directory.Exists(path)) yield return path;
        }
    }

    private static Dictionary<string, string> ReadSteamManifestValues(string manifestPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(manifestPath))
        {
            var parts = rawLine.Trim().Split('"', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4) values[parts[0]] = parts[^1];
        }
        return values;
    }

    private static bool IsLikelyGamePakDirectory(string path)
    {
        return Directory.Exists(path) && Directory.EnumerateFiles(path, "*.pak", SearchOption.TopDirectoryOnly).Any();
    }

    private async void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        var updated = await new SettingsWindow(_settings).ShowDialog<AppSettings?>(this);
        if (updated == null) return;
        updated.LastGameDirectory = GameDirBox.Text?.Trim() ?? _settings.LastGameDirectory;
        updated.LastListPath = SelectedListPath ?? _settings.LastListPath;
        _settings = updated;
        AppSettingsService.Save(_settings);
        ActionStatus.Text = "设置已保存";
    }

    private async void OnAboutClicked(object? sender, RoutedEventArgs e)
        => await new AboutWindow().ShowDialog(this);
    private async void OnOpenPakClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开 RE Engine PAK 文件",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("RE Engine PAK") { Patterns = ["*.pak"] }],
        });
        if (files.Count > 0) await LoadPakFilesAsync(files.Select(file => file.Path.LocalPath));
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles()?.ToArray() ?? [];
        e.DragEffects = files.Length > 0 && files.All(file => file.Path.LocalPath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        var paths = e.Data.GetFiles()?.Select(file => file.Path.LocalPath)
            .Where(path => path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        if (paths.Length > 0) await LoadPakFilesAsync(paths);
    }

    private async Task LoadPakFilesAsync(IEnumerable<string> pakPaths)
    {
        var paths = pakPaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
        var listFile = SelectedListPath;
        if (paths.Length == 0) { ActionStatus.Text = "没有可加载的 PAK 文件"; return; }
        if (string.IsNullOrWhiteSpace(listFile) || !File.Exists(listFile))
        {
            ActionStatus.Text = "请先从路径列表中选择对应游戏的 .list";
            return;
        }

        var progress = BeginProgress("正在加载游戏资源…");
        try
        {
            StatusText.Text = "加载中…";
            var (pak, entries, roots) = await Task.Run(() =>
            {
                var p = new PakService();
                foreach (var pakPath in paths) p.AddPak(pakPath);
                p.LoadListFile(listFile);
                var list = p.EnumerateFiles()
                    .Select(file => new EntryRow(file.Path, file.DecompressedSize, file.SourcePak)).ToList();
                return (p, list, BuildTree(list));
            });

            _pak = pak;
            _all = entries;
            _byPath.Clear();
            foreach (var row in entries) _byPath[row.Path] = row;
            FileTree.ItemsSource = roots;
            ApplyFilter();
            _loadedPakPaths.Clear();
            _loadedPakPaths.AddRange(paths);
            StatusText.Text = $"{paths.Length} 个 PAK · {entries.Count:N0} 个文件";
            ActionStatus.Text = $"PAK 加载完成：{string.Join("、", paths.Select(Path.GetFileName))}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败：" + ex.Message;
        }
        finally
        {
            EndProgress(progress);
        }
    }

    private async void OnReloadFileTreeClicked(object? sender, RoutedEventArgs e)
    {
        if (_loadedPakPaths.Count == 0)
        {
            ActionStatus.Text = "请先打开 PAK 文件或扫描游戏文件夹";
            return;
        }
        await LoadPakFilesAsync(_loadedPakPaths.ToArray());
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();
    private static List<FileTreeNode> BuildTree(List<EntryRow> entries)
    {
        var root = new FileTreeNode("");
        foreach (var entry in entries)
        {
            var parts = entry.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var node = root;
            for (var i = 0; i < parts.Length; i++)
            {
                var isLeaf = i == parts.Length - 1;
                var name = isLeaf ? $"{parts[i]}  ({entry.SizeText})" : parts[i];
                node = node.GetOrAddChild(parts[i], isLeaf ? name : null);
                if (isLeaf) node.FilePath = entry.Path;
            }
        }
        root.SortRecursive();
        return root.Children;
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        var searching = !string.IsNullOrEmpty(q);
        FileTree.IsVisible = !searching;
        SearchResults.IsVisible = searching;
        if (searching)
        {
            var terms = q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            SearchResults.ItemsSource = _all
                .Select(row => (Row: row, Score: FuzzyPathScore(row.Path, terms)))
                .Where(match => match.Score >= 0)
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Row.Path.Length)
                .ThenBy(match => match.Row.Path, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .Select(match => match.Row)
                .ToList();
        }
    }

    /// <summary>
    /// Scores a resource path against all query terms. Exact/contiguous filename matches
    /// rank first; otherwise characters may appear in order with gaps ("c001skin" matches
    /// "ch001_00_10_skin.mesh"). Separate terms are ANDed, e.g. "ch001 skin tex".
    /// </summary>
    private static int FuzzyPathScore(string path, IReadOnlyList<string> terms)
    {
        var normalizedPath = path.ToLowerInvariant();
        var slash = normalizedPath.LastIndexOf('/');
        var fileNameStart = slash + 1;
        var total = 0;

        foreach (var rawTerm in terms)
        {
            var term = rawTerm.ToLowerInvariant();
            var exactIndex = normalizedPath.IndexOf(term, StringComparison.Ordinal);
            if (exactIndex >= 0)
            {
                total += 10_000 + term.Length * 120;
                if (exactIndex >= fileNameStart) total += 4_000;
                if (exactIndex == fileNameStart) total += 1_500;
                total -= Math.Min(exactIndex, 1_000);
                continue;
            }

            var cursor = 0;
            var first = -1;
            var previous = -2;
            var consecutive = 0;
            var gaps = 0;
            foreach (var character in term)
            {
                var found = normalizedPath.IndexOf(character, cursor);
                if (found < 0) return -1;
                if (first < 0) first = found;
                if (found == previous + 1) consecutive++;
                else if (previous >= 0) gaps += found - previous - 1;
                previous = found;
                cursor = found + 1;
            }

            total += 2_000 + term.Length * 80 + consecutive * 100 - gaps * 12;
            if (first >= fileNameStart) total += 1_000;
        }

        return total;
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();
    private void SelectPath(string? path)
    {
        _selectedPath = path;
        if (path == null || !_byPath.TryGetValue(path, out var row))
        {
            InfoText.Text = "";
            return;
        }

        var kind = KindOf(path);
        if (kind == "mesh") _lastMeshPath = path;
        OpenFileText.Text = path.Split('/')[^1];
        InfoText.Text = $"{path}\n类型: {kind} | 大小: {row.SizeText} | 来源: {row.SourcePak}";

        // 单击只更新详情；双击才预览，避免合并模型时误替换当前预览。
    }
private void OnListPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var pointer = e.GetCurrentPoint(this).Properties;
        if (pointer.IsRightButtonPressed)
        {
            // The flyout belongs to the whole resource control, so right-clicking anywhere
            // on a row must first make that row current. Menu actions then use this selection.
            var row = (e.Source as Control)?.DataContext;
            _contextPath = row switch
            {
                FileTreeNode clickedNode => clickedNode.FilePath,
                EntryRow clickedEntry => clickedEntry.Path,
                _ => null,
            };
            if (sender is TreeView && row is FileTreeNode node)
                FileTree.SelectedItem = node;
            else if (sender is ListBox && row is EntryRow entry)
                SearchResults.SelectedItem = entry;
            if (_contextPath != null) SelectPath(_contextPath);
            return;
        }

        if (!pointer.IsLeftButtonPressed || e.ClickCount < 2)
            return;

        var path = sender switch
        {
            TreeView => (FileTree.SelectedItem as FileTreeNode)?.FilePath,
            ListBox => (SearchResults.SelectedItem as EntryRow)?.Path,
            _ => _selectedPath,
        } ?? _selectedPath;
        if (path == null) return;

        var kind = KindOf(path);
        if (kind is "tex" or "mesh" or "motlist")
        {
            _ = PreviewPathAsync(path);
            e.Handled = true;
        }
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var node = FileTree.SelectedItem as FileTreeNode;
        SelectPath(node?.FilePath);
    }

    private void OnSearchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var row = SearchResults.SelectedItem as EntryRow;
        SelectPath(row?.Path);
    }

    private void ShowImagePanel()
    {
        PreviewImage.IsVisible = true;
        Viewport.IsVisible = false;
        ViewportInput.IsVisible = false;
    }

    private void ShowViewport()
    {
        PreviewImage.IsVisible = false; // GlViewport 自我渲染，隐藏静态图片层
        Viewport.IsVisible = true;
        ViewportInput.IsVisible = true;
        Viewport.Refresh();
    }

    private bool _viewportDragging;

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control input || !Viewport.HasMesh) return;
        input.Focus();
        var point = e.GetCurrentPoint(input);
        if (point.Properties.IsLeftButtonPressed && VisconGroupList.SelectedItem != null)
            VisconGroupList.SelectedItem = null;
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var orbit = point.Properties.IsLeftButtonPressed || (alt && point.Properties.IsMiddleButtonPressed);
        var pan = point.Properties.IsRightButtonPressed || (point.Properties.IsMiddleButtonPressed && !alt);
        if (!orbit && !pan) return;
        _viewportDragging = true;
        Viewport.BeginCameraDrag(point.Position, orbit, pan);
        e.Pointer.Capture(input);
        e.Handled = true;
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_viewportDragging || sender is not Control input) return;
        Viewport.UpdateCameraDrag(e.GetPosition(input));
        e.Handled = true;
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_viewportDragging) return;
        _viewportDragging = false;
        Viewport.EndCameraDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnViewportPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _viewportDragging = false;
        Viewport.EndCameraDrag();
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!Viewport.HasMesh) return;
        Viewport.ZoomCamera(e.Delta.Y);
        e.Handled = true;
    }

    private void OnViewportKeyDown(object? sender, KeyEventArgs e)
    {
        if (!Viewport.HasMesh) return;
        e.Handled = Viewport.HandleCameraKey(e.Key);
        if (e.Handled) UpdateViewportChrome();
    }

    private int _previewSeq;

    private async Task PreviewPathAsync(string path)
    {
        if (_pak == null) return;
        var seq = ++_previewSeq;
        var progress = BeginProgress("正在生成预览…");
        bool IsStale() => seq != _previewSeq;

        var kind = KindOf(path);
        ActionStatus.Text = "预览生成中…";
        try
        {
            switch (kind)
            {
                case "tex":
                {
                    var png = await Task.Run(() =>
                    {
                        using var ms = _pak.ReadFile(path);
                        var outPng = Path.Combine(_tempDir, "preview_tex.png");
                        new TexService().ConvertToPng(ms, path, outPng);
                        return outPng;
                    });
                    if (IsStale()) return;
                    ShowImagePanel();
                    ShowImage(png);
                    ActionStatus.Text = "贴图预览完成";
                    break;
                }
                case "mesh":
                {
                    var viewportMesh = await Task.Run(() =>
                    {
                        using var ms = _pak.ReadFile(path);
                        return ViewportDataLoader.LoadMesh(ms, path, 1, OpenResource);
                    });
                    if (IsStale()) return;
                    ShowViewport();
                    Viewport.SetMesh(viewportMesh);
                    SetPreviewMeshPaths(path);
                    ClearMotionState();
                    RefreshVisconGroups();
                    ActionStatus.Text = $"模型已加载 | {Viewport.StatusInfo} | 贴图 {viewportMesh.Textures.Length} 张 | {viewportMesh.VisconInfo}";
                    break;
                }
                case "motlist":
                {
                    if (!Viewport.HasMesh) { ActionStatus.Text = "请先在预览区加载角色模型，再加载动画列表"; return; }
                    var (clip, motionNames) = await LoadMotionListAsync(path, 0);
                    if (IsStale()) return;
                    ShowViewport();
                    Viewport.SetAnimation(clip);
                    SetMotionListUi(path, motionNames, 0);
                    ShowTimeline(clip.Duration);
                    ActionStatus.Text = $"动画播放中：{clip.Name}（时长 {clip.Duration:F1} 秒，{clip.NamedTracks.Count} 条骨骼轨道）";
                    break;
                }
                default:
                    ActionStatus.Text = $"暂不支持 {kind} 类型的预览";
                    break;
            }
        }
        catch (Exception ex)
        {
            if (!IsStale()) ActionStatus.Text = "预览失败: " + ex.Message;
        }
        finally { EndProgress(progress); }
    }

    private void OnPlaybackClicked(object? sender, RoutedEventArgs e)
    {
        if (!Viewport.HasAnimation) { ActionStatus.Text = "当前没有已加载的动画"; return; }
        Viewport.TogglePlayback();
        UpdateViewportChrome();
    }

    // ---- viewport toolbar: skeleton / motion / timeline ----

    private string? _currentMotlistPath;
    private int[] _currentMotionIndices = [];
    private bool _suppressSlider;
    private bool _syncingMotionUi;
    private Avalonia.Threading.DispatcherTimer? _playheadTimer;

    /// <summary>Resource opener handed to the viewport loader: reads any native path from the loaded PAKs.</summary>
    private Stream? OpenResource(string nativePath)
    {
        if (_pak == null) return null;
        try { return _pak.ReadFile(nativePath); }
        catch { return null; }
    }

    private void OnSkeletonToggled(object? sender, RoutedEventArgs e)
    {
        Viewport.ShowSkeleton = SkeletonCheck.IsChecked == true;
        Viewport.Refresh();
    }

    private bool _syncingViewportUi;

    private void UpdateViewportChrome()
    {
        if (Viewport is null || ViewportLabel is null) return;
        ViewportLabel.Text = Viewport.ViewLabel;
        UpdateWorldAxisIndicator();
        _syncingViewportUi = true;
        ViewCombo.SelectedIndex = (int)Viewport.CurrentView;
        RenderModeCombo.SelectedIndex = (int)Viewport.RenderMode;
        GridCheck.IsChecked = Viewport.ShowGrid;
        AxesCheck.IsChecked = Viewport.ShowAxes;
        SkeletonCheck.IsChecked = Viewport.ShowSkeleton;
        PlaybackOverlay.IsVisible = Viewport.HasAnimation;
        PlaybackButton.Content = Viewport.IsPlaying ? "⏸" : "▶";
        ToolTip.SetTip(PlaybackButton, Viewport.IsPlaying ? "暂停" : "播放");
        _syncingViewportUi = false;
    }

    private void UpdateWorldAxisIndicator()
    {
        if (WorldAxisX is null || WorldAxisY is null || WorldAxisZ is null) return;
        var directions = Viewport.GetWorldAxisScreenDirections();
        const double origin = 45;
        const double length = 29;
        var lines = new[] { WorldAxisX, WorldAxisY, WorldAxisZ };
        var labels = new[] { WorldAxisXLabel, WorldAxisYLabel, WorldAxisZLabel };
        for (var i = 0; i < 3; i++)
        {
            var x = origin + directions[i].X * length;
            var y = origin + directions[i].Y * length;
            lines[i].StartPoint = new Avalonia.Point(origin, origin);
            lines[i].EndPoint = new Avalonia.Point(x, y);
            Canvas.SetLeft(labels[i], Math.Clamp(x + (directions[i].X >= 0 ? 3 : -12), 2, 78));
            Canvas.SetTop(labels[i], Math.Clamp(y - 9, 1, 72));
        }
    }

    private void OnViewportViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingViewportUi || Viewport is null || ViewCombo?.SelectedIndex < 0) return;
        Viewport.SetView((GlViewport.ViewPreset)(ViewCombo?.SelectedIndex ?? 0));
        UpdateViewportChrome();
    }

    private void OnViewportRenderModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingViewportUi || Viewport is null || RenderModeCombo?.SelectedIndex < 0) return;
        Viewport.SetRenderMode((GlViewport.ViewportRenderMode)(RenderModeCombo?.SelectedIndex ?? 0));
        UpdateViewportChrome();
    }

    private void OnViewportFrameAll(object? sender, RoutedEventArgs e) => Viewport.FrameAll();

    private void OnGridToggled(object? sender, RoutedEventArgs e)
    {
        if (_syncingViewportUi || Viewport is null || GridCheck is null) return;
        Viewport.ShowGrid = GridCheck.IsChecked == true;
        Viewport.Refresh();
    }

    private void OnAxesToggled(object? sender, RoutedEventArgs e)
    {
        if (_syncingViewportUi || Viewport is null || AxesCheck is null) return;
        Viewport.ShowAxes = AxesCheck.IsChecked == true;
        Viewport.Refresh();
    }

    private bool _syncingVisconUi;

    private void RefreshVisconGroups(int? selectKey = null)
    {
        if (VisconGroupList is null || Viewport is null) return;
        selectKey ??= (VisconGroupList.SelectedItem as VisconGroupRow)?.Key;
        _syncingVisconUi = true;
        var visible = Viewport.VisiblePrimaryGroups;
        var rows = Viewport.PrimaryGroups.Select(group => new VisconGroupRow
        {
            Key = group.Key,
            IsVisible = visible.Contains(group.Key),
            Display = $"组 {group.Id} · {group.FaceCount:N0} 面" +
                      (group.IsHelper ? "  [辅助]" : group.DefaultVisible ? "  [默认]" : "  [替代]"),
            MaterialTip = group.Materials.Length > 0
                ? string.Join("\n", group.Materials)
                : "无材质",
        }).ToArray();
        VisconGroupList.ItemsSource = rows;
        VisconGroupList.SelectedItem = selectKey.HasValue
            ? rows.FirstOrDefault(row => row.Key == selectKey.Value)
            : null;
        _syncingVisconUi = false;
    }

    private void OnVisconGroupClicked(object? sender, RoutedEventArgs e)
    {
        if (_syncingVisconUi || sender is not CheckBox { Tag: int key } check) return;
        var row = (VisconGroupList.ItemsSource as IEnumerable<VisconGroupRow>)?.FirstOrDefault(x => x.Key == key);
        if (row != null) VisconGroupList.SelectedItem = row;
        Viewport.SetPrimaryGroupVisible(key, check.IsChecked == true);
        ActionStatus.Text = $"模型组已更新 | {Viewport.StatusInfo} | F 可按当前可见组重新取景";
    }

    private void OnVisconSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingVisconUi || Viewport is null) return;
        Viewport.SelectPrimaryGroup((VisconGroupList.SelectedItem as VisconGroupRow)?.Key);
    }

    private void OnVisconReset(object? sender, RoutedEventArgs e)
    {
        Viewport.ResetPrimaryGroupVisibility();
        RefreshVisconGroups();
        ActionStatus.Text = $"已恢复默认外观 | {Viewport.StatusInfo}";
    }

    private void OnVisconShowAll(object? sender, RoutedEventArgs e)
    {
        Viewport.SetAllPrimaryGroupsVisible(true);
        RefreshVisconGroups();
        ActionStatus.Text = $"已显示全部模型组（替代壳可能互相覆盖） | {Viewport.StatusInfo}";
    }

    private void OnVisconHideAll(object? sender, RoutedEventArgs e)
    {
        Viewport.SetAllPrimaryGroupsVisible(false);
        RefreshVisconGroups();
        ActionStatus.Text = "已隐藏全部模型组；可勾选需要查看的组";
    }

    private bool TryGetSelectedVisconGroup(out VisconGroupRow row)
    {
        if (VisconGroupList.SelectedItem is VisconGroupRow selected)
        {
            row = selected;
            return true;
        }
        row = null!;
        ActionStatus.Text = "请先在模型组列表中选中一组";
        return false;
    }

    private void OnVisconIsolate(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedVisconGroup(out var row)) return;
        Viewport.IsolatePrimaryGroup(row.Key);
        RefreshVisconGroups(row.Key);
        ActionStatus.Text = $"已单独显示 {row.Display} | {Viewport.StatusInfo}";
    }

    private void OnVisconFrameSelected(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedVisconGroup(out var row)) return;
        Viewport.FramePrimaryGroup(row.Key);
        ActionStatus.Text = $"镜头已对准 {row.Display}；其他可见组保持不变";
    }

    private void ShowTimeline(float duration)
    {
        TimelineSlider.Maximum = Math.Max(0.01, duration);
        TimelineSlider.Value = 0;
        PlaybackOverlay.IsVisible = true;
        TimelineSlider.IsVisible = true;
        PlaybackButton.Content = Viewport.IsPlaying ? "⏸" : "▶";
        TimelineText.Text = $"0.0 / {duration:F1}s";
        _playheadTimer ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _playheadTimer.Tick -= OnPlayheadTick;
        _playheadTimer.Tick += OnPlayheadTick;
        _playheadTimer.Start();
    }

    private void OnPlayheadTick(object? sender, EventArgs e)
    {
        if (!Viewport.HasAnimation) { _playheadTimer?.Stop(); return; }
        if (Viewport.IsPlaying && !_suppressSlider)
        {
            _suppressSlider = true;
            TimelineSlider.Value = Viewport.CurrentTime;
            _suppressSlider = false;
        }
        TimelineText.Text = $"{Viewport.CurrentTime:F1} / {Viewport.Duration:F1}s";
    }

    private void OnTimelineScrubbed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlider) return;
        Viewport.ScrubTo((float)e.NewValue);
        TimelineText.Text = $"{Viewport.CurrentTime:F1} / {Viewport.Duration:F1}s";
    }

    private async void OnMotionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingMotionUi || _pak == null || _currentMotlistPath == null || MotionCombo.SelectedIndex < 0) return;
        var comboIndex = MotionCombo.SelectedIndex;
        if ((uint)comboIndex >= (uint)_currentMotionIndices.Length) return;
        var idx = _currentMotionIndices[comboIndex];
        var motlistPath = _currentMotlistPath;
        try
        {
            var clip = await Task.Run(() =>
            {
                using var motMs = _pak.ReadFile(motlistPath);
                return ViewportDataLoader.LoadAnimation(motMs, motlistPath, idx, Viewport.AllMeshBoneNames);
            });
            Viewport.SetAnimation(clip);
            ShowTimeline(clip.Duration);
            ActionStatus.Text = $"动画播放中: {clip.Name}（时长 {clip.Duration:F1}s）";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "动作加载失败：" + ex.Message;
        }
    }

    private async Task<(AnimationClip Clip, IReadOnlyList<MotionInfo> Motions)> LoadMotionListAsync(string path, int index)
    {
        if (_pak == null) throw new InvalidOperationException("尚未加载游戏资源");
        return await Task.Run(() =>
        {
            using var namesStream = _pak.ReadFile(path);
            var motions = ViewportDataLoader.ListMotions(namesStream, path);
            if (motions.Count == 0) throw new InvalidDataException("动画列表中没有可读取的内嵌动作");
            using var motionStream = _pak.ReadFile(path);
            var clip = ViewportDataLoader.LoadAnimation(motionStream, path,
                motions[Math.Clamp(index, 0, motions.Count - 1)].SourceIndex, Viewport.AllMeshBoneNames);
            return (clip, motions);
        });
    }

    private void SetMotionListUi(string path, IReadOnlyList<MotionInfo> motions, int selectedIndex)
    {
        _currentMotlistPath = path;
        _currentMotionIndices = motions.Select(motion => motion.SourceIndex).ToArray();
        _syncingMotionUi = true;
        MotionCombo.ItemsSource = motions.Select(motion => motion.DisplayName).ToArray();
        MotionCombo.SelectedIndex = selectedIndex;
        MotionCombo.IsVisible = motions.Count > 0;
        MotionLabel.IsVisible = motions.Count > 0;
        _syncingMotionUi = false;
    }

    private void ClearMotionState()
    {
        _currentMotlistPath = null;
        _currentMotionIndices = [];
        _syncingMotionUi = true;
        MotionCombo.ItemsSource = null;
        MotionCombo.SelectedIndex = -1;
        MotionCombo.IsVisible = false;
        MotionLabel.IsVisible = false;
        _syncingMotionUi = false;
        PlaybackOverlay.IsVisible = false;
        TimelineSlider.IsVisible = false;
        TimelineSlider.Value = 0;
        TimelineText.Text = "";
        PlaybackButton.Content = "▶";
        _playheadTimer?.Stop();
        Viewport.SetAnimation(null);
    }

    private void SetPreviewMeshPaths(params string[] paths)
    {
        _previewMeshPaths.Clear();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            _previewMeshPaths.Add(path);
    }

    private string RenderMeshPreview(string meshPath, string? motlistPath, int frame, string blenderPath)
    {
        var glb = Path.Combine(_tempDir, "preview.glb");
        var png = Path.Combine(_tempDir, "preview_mesh.png");
        if (File.Exists(png)) File.Delete(png); // avoid showing a stale image if render fails
        if (motlistPath == null)
        {
            using var ms = _pak!.ReadFile(meshPath);
            new MeshService().ConvertToGlb(ms, meshPath, glb);
            RunBlender("render_glb.py", glb, png, blenderPath);
        }
        else
        {
            using var meshMs = _pak!.ReadFile(meshPath);
            using var motMs = _pak.ReadFile(motlistPath);
            new AnimationService().ConvertToGlbWithAnimation(meshMs, meshPath, motMs, motlistPath, glb, 0);
            RunBlender("render_anim.py", glb, png, blenderPath, frame.ToString());
        }
        return png;
    }

    private void RunBlender(string scriptName, string glb, string png, string blender, string? extra = null)
    {
        if (!File.Exists(blender)) throw new FileNotFoundException($"找不到 Blender: {blender}");

        var script = ResolveToolsScript(scriptName);
        var args = $"--background --python \"{script}\" -- \"{glb}\" \"{png}\" {extra}".TrimEnd();
        var psi = new ProcessStartInfo(blender, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Blender 启动失败");

        // must drain pipes or Blender blocks once the buffer fills (classic pipe deadlock)
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        var exited = proc.WaitForExit(180_000);
        if (!exited)
        {
            try { proc.Kill(); } catch { /* ignore */ }
            throw new TimeoutException("Blender 渲染超时（180s）");
        }

        if (!File.Exists(png))
        {
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var tail = stdout.Length > 500 ? stdout[^500..] : stdout;
            throw new InvalidOperationException($"Blender 未产出预览图（exit={proc.ExitCode}）\n{tail}");
        }
    }

    private static string ResolveToolsScript(string scriptName)
    {
        // try cwd first (dotnet run from repo root), then exe directory (published exe)
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "tools", scriptName),
            Path.Combine(AppContext.BaseDirectory, "tools", scriptName),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        throw new FileNotFoundException($"找不到脚本 {scriptName}（已尝试: {string.Join(", ", candidates)}）");
    }

    private void ShowImage(string png)
    {
        // load via stream so the file is not locked
        using var fs = File.OpenRead(png);
        PreviewImage.Source = new Bitmap(fs);
    }

    private async void OnExportClicked(object? sender, RoutedEventArgs e)
    {
        // Freeze the assembled scene before taking the export snapshot. Any automatic
        // preview started by an earlier row selection is now stale and may not replace it.
        ++_previewSeq;
        if (_pak == null || !Viewport.HasMesh || _previewMeshPaths.Count == 0)
        { ActionStatus.Text = "请先在预览区加载需要导出的模型"; return; }
        await ExportPreviewModelsFbxAsync();
    }
    private async void OnExportAnimClicked(object? sender, RoutedEventArgs e)
    {
        if (_pak == null || _currentMotlistPath == null)
        { ActionStatus.Text = "请先在当前预览模型上加载一个动画列表"; return; }
        var exportModels = Viewport.ExportModels;
        if (exportModels.Count == 0)
        { ActionStatus.Text = "当前预览缺少可用于生成骨架的模型"; return; }

        var exportCurrent = ExportAnimScopeCombo.SelectedIndex <= 0;
        var exportFps = ExportAnimFpsCombo.SelectedIndex == 1 ? 30 : 60;
        var selectedMotionComboIndex = MotionCombo.SelectedIndex;
        if (exportCurrent && ((uint)selectedMotionComboIndex >= (uint)_currentMotionIndices.Length))
        { ActionStatus.Text = "请先在动画下拉框中选择要导出的当前动画"; return; }
        var selectedMotionSourceIndex = exportCurrent ? _currentMotionIndices[selectedMotionComboIndex] : -1;

        var motlistPath = _currentMotlistPath;
        var outDir = Path.GetFullPath(CurrentOutputDirectory);
        var blender = CurrentBlenderPath;
        var scopeText = exportCurrent ? "当前动画" : "全部动画";
        var progress = BeginProgress($"正在导出{scopeText}…");
        ActionStatus.Text = $"正在导出{scopeText}（{exportFps} FPS）…";
        ExportAnimButton.IsEnabled = false;
        ExportAnimScopeCombo.IsEnabled = false;
        ExportAnimFpsCombo.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                EnsureBlender(blender);
                var stem = NativeStem(motlistPath, ".motlist");
                var finalDir = Path.Combine(outDir, stem + "_动画");
                var workDir = CreateExportWorkDir("animations");
                try
                {
                    var models = exportModels
                        .Select(model => (model.Mesh, (IReadOnlySet<int>)model.VisibleGroups))
                        .ToArray();
                    var merged = new ViewportExportService().BuildMergedExportModel(models);
                    using var motMs = _pak.ReadFile(motlistPath);
                    if (exportCurrent)
                    {
                        new AnimationService().ConvertOneToGlbWithAnimation(
                            merged.Mesh, motMs, motlistPath, selectedMotionSourceIndex, workDir,
                            (current, total) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                UpdateCountProgress(progress, current, total, "准备动作")));
                    }
                    else
                    {
                        new AnimationService().ConvertAllToGlbWithAnimation(
                            merged.Mesh, motMs, motlistPath, workDir,
                            (current, total) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                UpdateCountProgress(progress, current, total, "准备动作")));
                    }
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        UpdateCountProgress(progress, 0,
                            Directory.GetFiles(workDir, "*.glb").Length, "导出动画"));
                    RunBlenderBatch("export_animations_fbx.py", blender, workDir, finalDir,
                        (current, total) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            UpdateCountProgress(progress, current, total, "导出动画")),
                        exportFps.ToString());
                    return (Directory.GetFiles(finalDir, "*.fbx").Length, finalDir);
                }
                finally { TryDeleteDirectory(workDir); }
            });
            ActionStatus.Text = $"动画导出完成：{result.Item1} 个 FBX，{exportFps} FPS，不含模型 | {result.finalDir}";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "动画导出失败：" + ex.Message;
        }
        finally
        {
            ExportAnimButton.IsEnabled = true;
            ExportAnimScopeCombo.IsEnabled = true;
            ExportAnimFpsCombo.IsEnabled = true;
            EndProgress(progress);
        }
    }
    private async Task ExportPreviewModelsFbxAsync()
    {
        if (_pak == null) return;
        var paths = _previewMeshPaths.ToArray();
        var exportModels = Viewport.ExportModels;
        var sourceCount = paths.Length;
        if (exportModels.Count == 0)
            throw new InvalidOperationException("预览场景没有可导出的模型");
        var outDir = Path.GetFullPath(CurrentOutputDirectory);
        var blender = CurrentBlenderPath;
        var outputPath = Path.Combine(outDir, NativeStem(paths[0], ".mesh") +
            (sourceCount > 1 ? $"_合并{sourceCount}个模型" : "") + ".fbx");
        var progress = BeginProgress("正在合并并导出 FBX…");
        ActionStatus.Text = $"正在导出预览中的全部 {sourceCount} 个源模型（场景对象 {exportModels.Count} 个）…";
        ExportButton.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                EnsureBlender(blender);
                var workDir = CreateExportWorkDir("models");
                try
                {
                    var models = exportModels
                        .Select(model => (model.Mesh, (IReadOnlySet<int>)model.VisibleGroups))
                        .ToArray();
                    new ViewportExportService().ConvertMergedToGlb(models,
                        Path.Combine(workDir, $"001_{NativeStem(paths[0], ".mesh")}_合并.glb"));
                    RunBlenderBatch("export_models_fbx.py", blender, workDir, outputPath);
                }
                finally { TryDeleteDirectory(workDir); }
            });
            ActionStatus.Text = $"模型 FBX 导出完成：已将全部 {sourceCount} 个源模型合并为 1 个模型 | {outputPath}";
        }
        catch (Exception ex) { ActionStatus.Text = "模型导出失败：" + ex.Message; }
        finally
        {
            ExportButton.IsEnabled = true;
            EndProgress(progress);
        }
    }

    private IReadOnlyList<MotionInfo> ReadMotions(string motlistPath)
    {
        using var stream = _pak!.ReadFile(motlistPath);
        return ViewportDataLoader.ListMotions(stream, motlistPath);
    }

    private static string NativeStem(string path, string marker)
    {
        var name = path.Replace('\\', '/').Split('/')[^1];
        var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index > 0 ? name[..index] : Path.GetFileNameWithoutExtension(name);
    }

    private static string SafeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value.Length > 80 ? value[..80] : value;
    }

    private string CreateExportWorkDir(string kind)
    {
        var path = Path.Combine(_tempDir, $"export_{kind}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* 临时文件会由系统稍后清理，不影响最终导出 */ }
    }

    private static void EnsureBlender(string blender)
    {
        if (!File.Exists(blender))
            throw new FileNotFoundException("找不到 FBX 转换程序，请检查顶部的转换程序路径", blender);
    }

    private static void RunBlenderBatch(string scriptName, string blender, string input, string output,
        Action<int, int>? progress = null, params string[] extraArgs)
    {
        var script = ResolveToolsScript(scriptName);
        var start = new ProcessStartInfo(blender)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--background");
        start.ArgumentList.Add("--python");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(output);
        foreach (var extraArg in extraArgs) start.ArgumentList.Add(extraArg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 FBX 转换程序");
        var stdoutText = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stdoutText) stdoutText.AppendLine(e.Data);
            const string marker = "REEXTRACTOR_PROGRESS:";
            var markerIndex = e.Data.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) return;
            var counts = e.Data[(markerIndex + marker.Length)..].Split('/');
            if (counts.Length == 2 && int.TryParse(counts[0], out var current) &&
                int.TryParse(counts[1], out var total)) progress?.Invoke(current, total);
        };
        process.BeginOutputReadLine();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(3_600_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("FBX 转换超过 60 分钟，已停止");
        }
        process.WaitForExit(); // ensure asynchronous OutputDataReceived has drained
        string stdout;
        lock (stdoutText) stdout = stdoutText.ToString();
        var outputText = stdout + "\n" + stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !outputText.Contains("REEXTRACTOR_OK:", StringComparison.Ordinal))
        {
            var tail = outputText.Length > 2000 ? outputText[^2000..] : outputText;
            throw new InvalidOperationException($"FBX 转换失败（代码 {process.ExitCode}）\n{tail}");
        }
    }

    private async Task ExportPathAsync(string path)
    {
        if (_pak == null) { ActionStatus.Text = "先加载 PAK"; return; }
        var kind = KindOf(path);
        var outDir = CurrentOutputDirectory;
        try
        {
            var result = await Task.Run(() =>
            {
                using var ms = _pak.ReadFile(path);
                switch (kind)
                {
                    case "tex":
                        return new TexService().ConvertToPng(ms, path,
                            Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + ".png"));
                    case "mesh":
                        return new MeshService().ConvertToGlb(ms, path,
                            Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + ".glb"));
                    default:
                        return _pak.ExtractFile(path, outDir);
                }
            });
            ActionStatus.Text = $"已导出: {result}";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "导出失败: " + ex.Message;
        }
    }

    // ---- context menu (tree / search results) ----

    private string? PathFromSender(object? sender)
    {
        if (sender is MenuItem mi)
        {
            var boundPath = mi.DataContext switch
            {
                FileTreeNode clickedNode => clickedNode.FilePath,
                EntryRow row => row.Path,
                _ => null,
            };
            if (boundPath != null) return boundPath;
        }
        return _contextPath;
    }

    private async void OnCtxAddModel(object? sender, RoutedEventArgs e)
    {
        var path = PathFromSender(sender);
        if (_pak == null || path == null) return;
        if (KindOf(path) != "mesh") { ActionStatus.Text = "叠加模型需要选择 .mesh 文件"; return; }
        // A left-click selection may still be decoding its automatic single-model preview.
        // Invalidate it before changing the assembled scene so it cannot finish later and
        // silently replace the primary mesh/export source list.
        var sceneOperation = ++_previewSeq;
        var progress = BeginProgress("正在叠加模型…");
        ActionStatus.Text = "叠加模型加载中…";
        try
        {
            var vm = await Task.Run(() =>
            {
                using var ms = _pak.ReadFile(path);
                return ViewportDataLoader.LoadMesh(ms, path, 1, OpenResource);
            });
            if (sceneOperation != _previewSeq) return;
            ShowViewport();
            if (Viewport.HasMesh)
            {
                Viewport.AddMesh(vm, path.Split('/')[^1]);
                if (!_previewMeshPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    _previewMeshPaths.Add(path);
                ActionStatus.Text = $"已叠加模型（共 {Viewport.ExtraModelCount + 1} 个）: {path.Split('/')[^1]}";
            }
            else
            {
                Viewport.SetMesh(vm);
                SetPreviewMeshPaths(path);
                ClearMotionState();
                RefreshVisconGroups();
                ActionStatus.Text = $"模型已加载 | {Viewport.StatusInfo}";
            }
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "叠加失败: " + ex.Message;
        }
        finally { EndProgress(progress); }
    }

    // ---- Merge panel: collect models, then load-merge with smart skeleton check ----
    private readonly List<string> _mergeQueue = new();

    private void RefreshMergeList()
    {
        MergeListBox.ItemsSource = null;
        MergeListBox.ItemsSource = _mergeQueue.ToArray();
    }

    private void OnCtxAddToMerge(object? sender, RoutedEventArgs e)
    {
        var path = PathFromSender(sender);
        if (path == null || KindOf(path) != "mesh") return;
        if (!_mergeQueue.Contains(path)) _mergeQueue.Add(path);
        RefreshMergeList();
        ActionStatus.Text = $"已加入合并队列（{_mergeQueue.Count} 个）: {path.Split('/')[^1]}";
    }

    private void OnMergeAddClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedPath == null || KindOf(_selectedPath) != "mesh")
        { ActionStatus.Text = "请先在左侧选中一个 .mesh 文件"; return; }
        if (!_mergeQueue.Contains(_selectedPath)) _mergeQueue.Add(_selectedPath);
        RefreshMergeList();
    }

    private void OnMergeRemoveClicked(object? sender, RoutedEventArgs e)
    {
        if (MergeListBox.SelectedItem is string sel) { _mergeQueue.Remove(sel); RefreshMergeList(); }
    }

    private void OnMergeClearClicked(object? sender, RoutedEventArgs e)
    {
        _mergeQueue.Clear();
        RefreshMergeList();
    }

    private static bool SameSkeleton(ViewportMesh a, ViewportMesh b)
    {
        if (a.Bones.Length != b.Bones.Length) return false;
        var setA = new HashSet<string>(a.Bones.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        return setA.SetEquals(b.Bones.Select(x => x.Name));
    }

    private async void OnMergeLoadClicked(object? sender, RoutedEventArgs e)
    {
        if (_mergeQueue.Count == 0) { ActionStatus.Text = "合并队列为空"; return; }
        if (_pak == null) { ActionStatus.Text = "请先加载 PAK"; return; }
        // Joining the queue starts from selected rows, and row selection also starts an
        // asynchronous single-model preview. Give this merge operation ownership of the
        // scene so an older preview cannot overwrite the merged result after it completes.
        var sceneOperation = ++_previewSeq;
        var progress = BeginProgress($"正在加载模型 0/{_mergeQueue.Count}", indeterminate: false,
            maximum: _mergeQueue.Count);
        ActionStatus.Text = $"合并加载中（{_mergeQueue.Count} 个模型）…";
        try
        {
            var paths = _mergeQueue.ToArray();
            var meshes = new ViewportMesh[paths.Length];
            for (var i = 0; i < paths.Length; i++)
            {
                var index = i;
                meshes[index] = await Task.Run(() =>
                {
                    using var ms = _pak.ReadFile(paths[index]);
                    return ViewportDataLoader.LoadMesh(ms, paths[index], 1, OpenResource);
                });
                UpdateProgress(progress, index + 1,
                    $"正在加载模型 {index + 1}/{paths.Length}");
            }
            if (sceneOperation != _previewSeq) return;

            // smart check: if every queued model shares the same bone-name set, geometrically
            // merge into ONE mesh and play a single animation (Noesis-style, same character parts);
            // otherwise load the first as primary and the rest as independent animated extras.
            var allSame = meshes.Length > 1 && meshes.All(m => SameSkeleton(meshes[0], m));
            ShowViewport();
            SetPreviewMeshPaths(paths);
            ClearMotionState();
            if (allSame)
            {
                var merged = ViewportMesh.Merge(meshes);
                Viewport.SetMesh(merged);
                RefreshVisconGroups();
                ActionStatus.Text = $"已几何合并 {meshes.Length} 个模型（同骨骼）→ 预览 1 个整体，导出包含全部 {paths.Length} 个源模型";
            }
            else
            {
                Viewport.SetMesh(meshes[0]);
                RefreshVisconGroups();
                for (var i = 1; i < meshes.Length; i++)
                    Viewport.AddMesh(meshes[i], paths[i].Split('/')[^1]);
                var verb = meshes.Length > 1 ? $"已加载 {meshes.Length} 个模型（骨骼不同 → 各自独立动画）" : "模型已加载";
                ActionStatus.Text = $"{verb} | 导出包含全部 {paths.Length} 个源模型 | {Viewport.StatusInfo}";
            }
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "合并加载失败: " + ex.Message;
        }
        finally { EndProgress(progress); }
    }

    private async void OnCtxAddAnim(object? sender, RoutedEventArgs e)
    {
        var path = PathFromSender(sender);
        if (_pak == null || path == null) return;
        if (KindOf(path) != "motlist") { ActionStatus.Text = "加载动画需要选择 .motlist 文件"; return; }
        if (!Viewport.HasMesh) { ActionStatus.Text = "请先在视口加载一个模型（点选 .mesh）"; return; }
        ActionStatus.Text = "动画加载中…";
        try
        {
            var (clip, motionNames) = await LoadMotionListAsync(path, 0);
            Viewport.SetAnimation(clip);
            SetMotionListUi(path, motionNames, 0);
            ShowTimeline(clip.Duration);
            ActionStatus.Text = $"动画已叠加: {clip.Name}（时长 {clip.Duration:F1}s）";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "动画叠加失败: " + ex.Message;
        }
    }

    private async void OnCtxExport(object? sender, RoutedEventArgs e)
    {
        var path = PathFromSender(sender);
        if (path != null) await ExportPathAsync(path);
    }

    private async void OnCtxShowExplorer(object? sender, RoutedEventArgs e)
    {
        var path = PathFromSender(sender);
        if (_pak == null || path == null) return;
        var outDir = CurrentOutputDirectory;
        try
        {
            var extracted = await Task.Run(() => _pak.ExtractFile(path, outDir));
            Process.Start("explorer.exe", $"/select,\"{extracted}\"");
            ActionStatus.Text = $"已提取并定位: {extracted}";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "提取失败: " + ex.Message;
        }
    }
}
