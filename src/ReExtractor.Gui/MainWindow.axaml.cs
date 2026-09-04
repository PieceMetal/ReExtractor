using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Microsoft.Win32;
using ReExtractor.Core;

namespace ReExtractor.Gui;

/// <summary>One node in the left folder tree (folder or file leaf).</summary>
public sealed class FileTreeNode : INotifyPropertyChanged
{
    public string Name { get; }
    public string? FilePath { get; set; }
    public string Display { get; }
    public List<FileTreeNode> Children { get; } = new();
    private Dictionary<string, FileTreeNode>? _lookup;
    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

    public FileTreeNode? FindChild(string key)
    {
        return _lookup != null && _lookup.TryGetValue(key, out var node) ? node : null;
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

/// <summary>One item in the UE-style exported skeleton hierarchy.</summary>
public sealed class BoneTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public int Index { get; }
    public string Name { get; }
    public int ParentIndex { get; }
    public bool IsVirtualExportRoot { get; }
    public bool IsDeform { get; }
    public string Badge => IsVirtualExportRoot ? "导出补全" : IsDeform ? "" : "非蒙皮";
    public string ToolTip { get; }
    public List<BoneTreeNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BoneTreeNode(int index, string name, int parentIndex, bool isVirtualExportRoot,
        bool isDeform, string toolTip)
    {
        Index = index;
        Name = name;
        ParentIndex = parentIndex;
        IsVirtualExportRoot = isVirtualExportRoot;
        IsDeform = isDeform;
        ToolTip = toolTip;
    }

    public void SetExpandedRecursive(bool expanded)
    {
        IsExpanded = expanded;
        foreach (var child in Children) child.SetExpandedRecursive(expanded);
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
    private FileTreeNode? _treeRoot;
    private string? _selectedPath;
    private string? _contextPath;
    private string? _lastMeshPath;
    private readonly List<string> _previewMeshPaths = new();
    private string? _previewScenePath;
    private readonly string _tempDir = AppPaths.TempDirectory;
    private readonly string _logDirectory = AppPaths.LogsDirectory;
    private readonly ModelAssemblyPresetService _assemblyPresetService = new();
    private readonly UpdateService _updateService = new();
    private int _progressOperation;
    private AppSettings _settings = AppSettingsService.Load();
    private readonly List<string> _loadedPakPaths = new();
    private string? _loadedFolderPath;
    private bool _syncingManagedList;
    private readonly IDisposable? _actionStatusLogSubscription;
    private EnvironmentWindow? _environmentWindow;
    private ViewportBone[]? _boneTreeSource;
    private BoneTreeNode? _selectedBoneTreeNode;
    private List<BoneTreeNode> _boneTreeRoots = new();
    private bool _syncingBoneTreeUi;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_logDirectory);
        RefreshManagedLists(_settings.LastListPath);
        if (ManagedListCombo.SelectedItem is ManagedFileList)
        {
            GameDirBox.Text = _settings.LastGameDirectory;
        }
        else
        {
            // A game path without a path list cannot be loaded meaningfully and
            // makes the two fields look associated when they are not.
            GameDirBox.Text = "";
            _settings.LastGameDirectory = "";
        }
        AppSettingsService.Save(_settings);
        RefreshAssemblyPresetHint();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
        AddHandler(PointerPressedEvent, OnMainWindowOutsideEnvironmentPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

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
            var d = Viewport.IsVisible ? Viewport.RenderDiagnostics : "";
            FpsText.Text = string.Join(" | ", new[] { b, f, d }.Where(x => x != ""));
            if (Viewport.IsVisible) UpdateViewportChrome();
        };
        _fpsTimer.Start();
        Viewport.StateChanged += OnViewportStateChanged;
        _actionStatusLogSubscription = ActionStatus.GetObservable(TextBlock.TextProperty)
            .Subscribe(new TextObserver(AppendLog));
        AppendLog("工具已启动，请选择路径列表并加载 PAK");
        Opened += async (_, _) =>
        {
            await ShowEnvironmentWindowAsync();
            await CheckForUpdatesAsync(false);
        };

    }

    private Avalonia.Threading.DispatcherTimer? _fpsTimer;

    private string CurrentOutputDirectory => string.IsNullOrWhiteSpace(_settings.OutputDirectory)
        ? AppPaths.OutputDirectory
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
        if (name.Contains(".scn.")) return "scn";
        if (name.Contains(".pfb.")) return "pfb";
        return "other";
    }

    private int BeginProgress(string text, bool indeterminate = true, double maximum = 100)
    {
        var operation = ++_progressOperation;
        // Each long-running action owns the workspace while it is active. This prevents
        // selection changes, preset loads, and viewport actions from racing a model or
        // texture decoder and applying results to the wrong scene.
        MainLayout.IsEnabled = false;
        MainMenu.IsEnabled = false;
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
        MainLayout.IsEnabled = true;
        MainMenu.IsEnabled = true;
    }

    private string? SelectedListPath => (ManagedListCombo.SelectedItem as ManagedFileList)?.FilePath;

    private string CurrentAssemblyGameKey
    {
        get
        {
            var selectedList = ManagedListCombo.SelectedItem as ManagedFileList;
            if (selectedList != null && !string.IsNullOrWhiteSpace(selectedList.Identifier))
                return selectedList.Identifier;

            if (!string.IsNullOrWhiteSpace(_loadedFolderPath))
            {
                var name = new DirectoryInfo(_loadedFolderPath).Name;
                return string.IsNullOrWhiteSpace(name) ? "未分类游戏" : name;
            }

            if (_loadedPakPaths.Count > 0)
            {
                var parent = Path.GetDirectoryName(_loadedPakPaths[0]);
                if (!string.IsNullOrWhiteSpace(parent)) return new DirectoryInfo(parent).Name;
            }

            return "未分类游戏";
        }
    }

    private string CurrentAssemblySourceFolder =>
        !string.IsNullOrWhiteSpace(_loadedFolderPath)
            ? _loadedFolderPath
            : (_loadedPakPaths.Count > 0 ? Path.GetDirectoryName(_loadedPakPaths[0]) ?? "" : "");

    private void RefreshAssemblyPresetHint()
    {
        if (AssemblyPresetHint == null) return;
        var gameKey = CurrentAssemblyGameKey;
        var count = _assemblyPresetService.List(gameKey).Count;
        AssemblyPresetHint.Text = $"当前分类：{gameKey} · 已保存 {count} 个预设";
    }

    private void OnAssemblyPresetSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_mergeQueue.Count == 0)
        {
            ActionStatus.Text = "合并队列为空，请先添加模型部件";
            return;
        }

        var name = AssemblyPresetNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            ActionStatus.Text = "请输入预设名称";
            return;
        }

        try
        {
            var file = _assemblyPresetService.Save(
                CurrentAssemblyGameKey, name, CurrentAssemblySourceFolder, _mergeQueue);
            AssemblyPresetNameBox.Text = "";
            RefreshAssemblyPresetHint();
            ActionStatus.Text = $"预设已保存：{file}";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "保存预设失败：" + ex.Message;
        }
    }

    private async void OnAssemblyPresetLoadClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "加载模型组装预设",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("模型组装预设") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;

        try
        {
            var preset = _assemblyPresetService.Load(files[0].Path.LocalPath);
            var missing = preset.MeshPaths.Where(path => !_byPath.ContainsKey(path)).ToArray();

            // A preset replaces the next scene that will be assembled. Do not leave an
            // earlier texture task owning the button (or the current preview) after it
            // finishes in the background.
            InvalidateTextureLoad();
            ++_previewSeq;
            _mergeQueue.Clear();
            _mergeQueue.AddRange(preset.MeshPaths.Where(path => _byPath.ContainsKey(path))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            RefreshMergeList();
            RefreshAssemblyPresetHint();
            ActionStatus.Text = missing.Length == 0
                ? $"已加载预设“{preset.Name}”：{_mergeQueue.Count} 个部件"
                : $"已加载预设“{preset.Name}”：{_mergeQueue.Count} 个部件；当前资源中缺少 {missing.Length} 个";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "加载预设失败：" + ex.Message;
        }
    }

    private void RefreshManagedLists(string? selectPath = null)
    {
        var lists = new FileListManagerService().GetLocalLists();
        var selected = lists.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(selectPath) &&
            Path.GetFullPath(item.FilePath).Equals(Path.GetFullPath(selectPath),
                StringComparison.OrdinalIgnoreCase)) ?? lists.FirstOrDefault();
        _syncingManagedList = true;
        ManagedListCombo.ItemsSource = lists;
        ManagedListCombo.SelectedItem = selected;
        _syncingManagedList = false;
        _settings.LastListPath = selected?.FilePath ?? "";
        if (selected == null)
        {
            GameDirBox.Text = "";
            _settings.LastGameDirectory = "";
        }
        AppSettingsService.Save(_settings);
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
        var paks = FindPakFiles(gameDir).ToArray();
        if (paks.Length == 0)
        {
            ActionStatus.Text = "该游戏文件夹中没有找到 PAK 文件";
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
            var selectedList = ManagedListCombo.SelectedItem as ManagedFileList;
            ActionStatus.Text = $"已选择资源列表：{Path.GetFileNameWithoutExtension(selected)}";
            await TryAutoSelectGameDirectoryForListAsync(selectedList);
        }
        else
        {
            RefreshManagedLists(SelectedListPath);
            await TryAutoSelectGameDirectoryForListAsync(
                ManagedListCombo.SelectedItem as ManagedFileList);
        }
    }

    private async void OnManagedListChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingManagedList) return;
        _settings.LastListPath = SelectedListPath ?? "";
        AppSettingsService.Save(_settings);
        await TryAutoSelectGameDirectoryForListAsync(ManagedListCombo.SelectedItem as ManagedFileList);
    }

    private void OnViewportStateChanged()
    {
        UpdateViewportChrome();
        var bones = Viewport.PrimaryBones;
        if (!ReferenceEquals(_boneTreeSource, bones)) RefreshBoneTree();
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

            // The canonical Ekey list for Monster Hunter Wilds is named
            // MHWs_STM_Release, while Steam publishes the installed title as
            // "Monster Hunter Wilds". "mhws" is not a substring or acronym
            // of that Steam title, so add the game-name key explicitly.
            if (normalized.StartsWith("mhws", StringComparison.OrdinalIgnoreCase))
                yield return "monsterhunterwilds";

            // The online MHS3_STM_Release list uses Capcom's short project
            // name; Steam uses the complete product title. Keep both the full
            // release list and the trial list on the same game-directory match.
            if (normalized.StartsWith("mhs3", StringComparison.OrdinalIgnoreCase))
                yield return "monsterhunterstories3twistedreflection";
        }
    }

    private static int ScoreSteamGameMatch(IEnumerable<string> queries, SteamGameInstall game)
    {
        var name = NormalizeGameName(game.Name);
        var installDir = NormalizeGameName(game.InstallDir);
        var acronym = BuildAcronym(game.Name);
        var extendedAcronym = BuildExtendedAcronym(game.Name);
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
            if (query.Equals(extendedAcronym, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 95);
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


    private static string BuildExtendedAcronym(string value)
    {
        var words = value.Split([' ', '-', '_', ':', '.', '\'', '’'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !word.Equals("demo", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (words.Length == 0) return "";

        var builder = new StringBuilder(words.Length + 4);
        var first = new string(words[0].Where(char.IsLetterOrDigit).Take(3).ToArray());
        builder.Append(first.ToLowerInvariant());
        foreach (var word in words.Skip(1))
            if (word.Length > 0 && char.IsLetterOrDigit(word[0])) builder.Append(char.ToLowerInvariant(word[0]));
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
        if (!OperatingSystem.IsWindows()) return null;

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
            if (parts.Length < 2) continue;
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
            if (parts.Length >= 2) values[parts[0]] = parts[^1];
        }
        return values;
    }

    private static bool IsLikelyGamePakDirectory(string path)
    {
        return Directory.Exists(path) && FindPakFiles(path).Any();
    }

    private static IEnumerable<string> FindPakFiles(string root)
    {
        foreach (var pak in EnumerateFilesShallow(root, "*.pak", 4))
            yield return pak;
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:N1} {units[unit]}";
    }

    private static IEnumerable<string> EnumerateFilesShallow(string root, string pattern, int maxDepth)
    {
        if (maxDepth < 0 || !Directory.Exists(root)) yield break;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).ToArray(); }
        catch { yield break; }
        foreach (var file in files) yield return file;

        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray(); }
        catch { yield break; }
        foreach (var child in children)
        {
            foreach (var nested in EnumerateFilesShallow(child, pattern, maxDepth - 1))
                yield return nested;
        }
    }

    private async void OnSettingsClicked(object? sender, RoutedEventArgs e)
        => await OpenSettingsDialogAsync();
    private async Task<bool> EnsureBlenderReadyAsync()
    {
        if (File.Exists(CurrentBlenderPath)) return true;
        ActionStatus.Text = "FBX 导出需要先安装 Blender，并在设置里选择 blender.exe";
        await ShowEnvironmentWindowAsync();
        return File.Exists(CurrentBlenderPath);
    }

    private async Task ShowEnvironmentWindowAsync()
    {
        if (_environmentWindow != null)
        {
            _environmentWindow.Activate();
            return;
        }
        var window = new EnvironmentWindow(_settings);
        _environmentWindow = window;
        var closed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_environmentWindow, window)) _environmentWindow = null;
            closed.TrySetResult(window.SelectedAction);
        };
        window.Show(this);
        var action = await closed.Task;
        if (action == "download")
        {
            OpenExternalUrl("https://www.blender.org/download/");
            ActionStatus.Text = "已打开 Blender 下载页面，安装后请在设置里选择 blender.exe";
            return;
        }
        if (action == "settings")
        {
            await OpenSettingsDialogAsync();
            return;
        }

    }

    private void OnMainWindowOutsideEnvironmentPressed(object? sender, PointerPressedEventArgs e)
    {
        // This handler belongs to the main window, so clicks inside the environment
        // window never arrive here. Closing only on an actual main-window click avoids
        // treating Alt-Tab, clicking the desktop, or switching apps as an outside click.
        _environmentWindow?.Close();
    }

    private async Task OpenSettingsDialogAsync()
    {
        var updated = await new SettingsWindow(_settings).ShowDialog<AppSettings?>(this);
        if (updated == null) return;
        updated.LastGameDirectory = GameDirBox.Text?.Trim() ?? _settings.LastGameDirectory;
        updated.LastListPath = SelectedListPath ?? _settings.LastListPath;
        _settings = updated;
        AppSettingsService.Save(_settings);
        ActionStatus.Text = "设置已保存";
    }

    private static void OpenExternalUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void OnAboutClicked(object? sender, RoutedEventArgs e)
        => await new AboutWindow().ShowDialog(this);

    private async void OnCheckUpdatesClicked(object? sender, RoutedEventArgs e)
        => await CheckForUpdatesAsync(true);

    private async Task CheckForUpdatesAsync(bool showCurrentStatus)
    {
        try
        {
            if (showCurrentStatus) ActionStatus.Text = "正在检查更新…";
            var release = await _updateService.CheckAsync();
            if (release == null)
            {
                if (showCurrentStatus)
                    ActionStatus.Text = $"当前已是最新版本 {_updateService.CurrentVersion.ToString(3)}";
                return;
            }

            var window = new UpdateWindow(_updateService, release);
            var install = await window.ShowDialog<bool>(this);
            if (!install || window.PreparedUpdate == null) return;
            _updateService.LaunchInstaller(window.PreparedUpdate);
            if (Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (Exception ex)
        {
            if (showCurrentStatus) ActionStatus.Text = "检查更新失败：" + ex.Message;
            else AppendLog("自动检查更新失败：" + ex.Message);
        }
    }

    private async void OnEnvironmentClicked(object? sender, RoutedEventArgs e)
        => await ShowEnvironmentWindowAsync();
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

    private async void OnOpenExtractedFolderClicked(object? sender, RoutedEventArgs e)
    {
        var folder = await ChooseResourceFolderAsync();
        if (folder != null) await LoadExtractedFolderAsync(folder);
    }

    private async void OnExtractAllClicked(object? sender, RoutedEventArgs e)
    {
        var lists = new FileListManagerService().GetLocalLists();
        var previouslySelectedList = SelectedListPath;
        var request = await new FullExtractWindow(lists,
            ManagedListCombo.SelectedItem as ManagedFileList,
            GameDirBox.Text?.Trim() ?? "",
            Path.Combine(CurrentOutputDirectory, "unpacked"),
            async list => (await Task.Run(() => FindMatchingGameDirectory(list)))?.Directory)
            .ShowDialog<FullExtractRequest?>(this);

        // The extraction window can download/import/delete lists through its
        // manager. Reload the shared library after it closes so the main combo
        // never keeps the stale snapshot it had before opening the dialog.
        var refreshedSelection = request?.ListFile ?? previouslySelectedList;
        RefreshManagedLists(refreshedSelection);
        if (request != null)
        {
            _settings.LastListPath = request.ListFile;
            _settings.LastGameDirectory = request.PakDirectory;
            GameDirBox.Text = request.PakDirectory;
            AppSettingsService.Save(_settings);
        }
        if (request == null)
        {
            await TryAutoSelectGameDirectoryForListAsync(
                ManagedListCombo.SelectedItem as ManagedFileList);
            return;
        }

        var listFile = request.ListFile;
        var pakDirectory = request.PakDirectory;
        var pakPaths = FindPakFiles(pakDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Keep root/base archives first and nested DLC/patch archives later.
            // PakService searches in reverse, so later archives correctly win.
            .OrderBy(path => Path.GetRelativePath(pakDirectory, path)
                .Count(ch => ch is '\\' or '/'))
            .ThenBy(path => Path.GetRelativePath(pakDirectory, path),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pakPaths.Length == 0)
        {
            ActionStatus.Text = "所选目录及其子目录中没有找到 PAK 文件";
            return;
        }

        var output = request.OutputDirectory;
        var progress = BeginProgress("正在读取 PAK 文件表…");
        ExtractAllMenuItem.IsEnabled = false;
        ActionStatus.Text = $"正在扫描 {pakPaths.Length} 个 PAK：{pakDirectory}";
        try
        {
            var result = await Task.Run(() =>
            {
                var source = new PakService();
                foreach (var path in pakPaths) source.AddPak(path);
                source.LoadListFile(listFile);
                var entries = source.EnumerateFiles();
                var total = entries.Count;
                if (total == 0)
                    throw new InvalidDataException(
                        "没有找到可识别的资源，请检查路径列表是否与所选游戏及版本匹配");
                var requiredBytes = entries.Aggregate(0L, (sum, entry) =>
                    entry.DecompressedSize > long.MaxValue - sum
                        ? long.MaxValue
                        : sum + Math.Max(0, entry.DecompressedSize));
                var outputRoot = Path.GetPathRoot(Path.GetFullPath(output));
                if (!string.IsNullOrWhiteSpace(outputRoot))
                {
                    var availableBytes = new DriveInfo(outputRoot).AvailableFreeSpace;
                    if (requiredBytes > availableBytes)
                        throw new IOException(
                            $"输出盘空间不足：预计需要 {FormatByteSize(requiredBytes)}，当前可用 {FormatByteSize(availableBytes)}");
                }
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    WorkProgress.Maximum = Math.Max(1, total);
                    UpdateProgress(progress, 0,
                        $"准备解包 0/{total}（约 {FormatByteSize(requiredBytes)}）");
                });
                var extraction = source.ExtractAllKnown(output, (current, path) =>
                {
                    if (current == 1 || current == total || current % 50 == 0)
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            UpdateCountProgress(progress, current, total, $"全部解包 · {Path.GetFileName(path)}"));
                });
                return (extraction, total);
            });
            foreach (var failure in result.extraction.Failures) AppendLog("全部解包失败：" + failure);
            ActionStatus.Text = result.extraction.Failed == 0
                ? $"全部解包完成：{result.extraction.Exported:N0} 个文件 | {output}"
                : $"全部解包完成：成功 {result.extraction.Exported:N0}，失败 {result.extraction.Failed:N0}（详见日志） | {output}";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "全部解包失败：" + ex.Message;
        }
        finally
        {
            ExtractAllMenuItem.IsEnabled = true;
            EndProgress(progress);
        }
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles()?.ToArray() ?? [];
        e.DragEffects = files.Length > 0 &&
            (files.All(file => file.Path.LocalPath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) ||
             files.All(file => Directory.Exists(file.Path.LocalPath)))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        var paths = e.Data.GetFiles()?.Select(file => file.Path.LocalPath).ToArray() ?? [];
        var paks = paths.Where(path => path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (paks.Length > 0)
        {
            await LoadPakFilesAsync(paks);
            return;
        }

        var folder = paths.FirstOrDefault(Directory.Exists);
        if (folder != null) await LoadExtractedFolderAsync(folder);
    }

    private async Task<string?> ChooseResourceFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "打开已解包资源文件夹",
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private async void OnChooseResourceFolderClicked(object? sender, RoutedEventArgs e)
    {
        var folder = await ChooseResourceFolderAsync();
        if (folder != null) await LoadExtractedFolderAsync(folder);
    }

    private async Task LoadExtractedFolderAsync(string folderPath)
    {
        var folder = Path.GetFullPath(folderPath);
        if (!Directory.Exists(folder))
        {
            ActionStatus.Text = "请选择有效的解包文件夹";
            return;
        }

        var progress = BeginProgress("正在索引解包文件夹…");
        try
        {
            StatusText.Text = "索引中…";
            var (source, entries, treeRoot) = await Task.Run(() =>
            {
                var p = new PakService();
                p.AddFolder(folder);
                var list = p.EnumerateFiles()
                    .Select(file => new EntryRow(file.Path, file.DecompressedSize, file.SourcePak))
                    .ToList();
                return (p, list, BuildTree(list));
            });

            if (entries.Count == 0)
            {
                ActionStatus.Text = "解包文件夹中没有找到可识别的资源文件";
                return;
            }

            _pak = source;
            _all = entries;
            _treeRoot = treeRoot;
            _byPath.Clear();
            foreach (var row in entries) _byPath[row.Path] = row;
            FileTree.ItemsSource = treeRoot.Children;
            ApplyFilter();

            _loadedPakPaths.Clear();
            _loadedFolderPath = folder;
            // A loose/extracted folder is an independent resource source. Clear the
            // PAK-only selectors so the UI cannot imply that this tree came from the
            // previously selected list/game installation.
            _syncingManagedList = true;
            ManagedListCombo.SelectedItem = null;
            ManagedListCombo.SelectedIndex = -1;
            _syncingManagedList = false;
            GameDirBox.Text = "";
            _settings.LastListPath = "";
            _settings.LastGameDirectory = "";
            AppSettingsService.Save(_settings);
            RefreshAssemblyPresetHint();
            StatusText.Text = $"自定义已解包文件夹 · {entries.Count:N0} 个文件";
            ActionStatus.Text = $"解包文件夹加载完成：{folder}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败：" + ex.Message;
            ActionStatus.Text = "解包文件夹加载失败：" + ex.Message;
        }
        finally
        {
            EndProgress(progress);
        }
    }


    private static IEnumerable<string> ExpandPakSet(IEnumerable<string> pakPaths)
    {
        var explicitPaths = pakPaths.Where(File.Exists).ToArray();
        foreach (var path in explicitPaths) yield return path;

        foreach (var group in explicitPaths.GroupBy(path => Path.GetDirectoryName(path) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key) || !Directory.Exists(group.Key)) continue;
            foreach (var sibling in Directory.EnumerateFiles(group.Key, "*.pak", SearchOption.TopDirectoryOnly))
                yield return sibling;
        }
    }
    private async Task LoadPakFilesAsync(IEnumerable<string> pakPaths)
    {
        var paths = ExpandPakSet(pakPaths).Distinct(StringComparer.OrdinalIgnoreCase)
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
            var (pak, entries, treeRoot) = await Task.Run(() =>
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
            _treeRoot = treeRoot;
            _byPath.Clear();
            foreach (var row in entries) _byPath[row.Path] = row;
            FileTree.ItemsSource = treeRoot.Children;
            ApplyFilter();
            _loadedPakPaths.Clear();
            _loadedPakPaths.AddRange(paths);
            _loadedFolderPath = null;
            RefreshAssemblyPresetHint();
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
        if (!string.IsNullOrWhiteSpace(_loadedFolderPath))
        {
            await LoadExtractedFolderAsync(_loadedFolderPath);
            return;
        }
        if (_loadedPakPaths.Count == 0)
        {
            ActionStatus.Text = "请先打开 PAK 文件或扫描游戏文件夹";
            return;
        }
        await LoadPakFilesAsync(_loadedPakPaths.ToArray());
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();
    private static FileTreeNode BuildTree(List<EntryRow> entries)
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
        return root;
    }

    private bool TryFindTreeNode(string path, out FileTreeNode node)
    {
        node = _treeRoot!;
        if (_treeRoot == null) return false;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var child = node.FindChild(parts[i]);
            if (child == null) return false;
            node = child;
            if (i < parts.Length - 1) node.IsExpanded = true;
        }
        return string.Equals(node.FilePath, path, StringComparison.OrdinalIgnoreCase);
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
        var row = (e.Source as Control)?.DataContext;
        if (pointer.IsRightButtonPressed)
        {
            // The flyout belongs to the whole resource control, so right-clicking anywhere
            // on a row must first make that row current. Menu actions then use this selection.
            _contextPath = row switch
            {
                FileTreeNode clickedNode => clickedNode.FilePath,
                EntryRow contextEntry => contextEntry.Path,
                _ => null,
            };
            if (sender is TreeView && row is FileTreeNode node)
            {
                if (FileTree.SelectedItems?.Contains(node) != true)
                    FileTree.SelectedItem = node;
            }
            else if (sender is ListBox && row is EntryRow entry)
            {
                // Preserve a Ctrl/Shift multi-selection when opening the context menu on one
                // of its rows. Right-clicking an unselected row still makes only that row current.
                if (SearchResults.SelectedItems?.Contains(entry) != true)
                    SearchResults.SelectedItem = entry;
            }
            if (_contextPath != null) SelectPath(_contextPath);
            return;
        }

        if (!pointer.IsLeftButtonPressed || e.ClickCount < 2)
            return;

        var clickedRow = (e.Source as Control)?.DataContext;
        var clickedPath = clickedRow switch
        {
            FileTreeNode clickedNode => clickedNode.FilePath,
            EntryRow doubleClickEntry => doubleClickEntry.Path,
            _ => null,
        };
        if (sender is TreeView && clickedRow is FileTreeNode treeNode)
            FileTree.SelectedItem = treeNode;
        else if (sender is ListBox && clickedRow is EntryRow listEntry)
            SearchResults.SelectedItem = listEntry;

        var path = clickedPath ?? sender switch
        {
            TreeView => (FileTree.SelectedItem as FileTreeNode)?.FilePath,
            ListBox => (SearchResults.SelectedItem as EntryRow)?.Path,
            _ => _selectedPath,
        } ?? _selectedPath;
        if (path == null) return;

        var kind = KindOf(path);
        if (kind is "tex" or "mesh" or "motlist" or "scn" or "pfb")
        {
            _ = PreviewPathAsync(path);
            e.Handled = true;
        }
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var node = FileTree.SelectedItem as FileTreeNode;
        SelectPath(node?.FilePath);
        UpdateResourceSelectionStatus();
    }

    private void OnSearchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var row = SearchResults.SelectedItem as EntryRow;
        SelectPath(row?.Path);
        UpdateResourceSelectionStatus();
    }

    private void UpdateResourceSelectionStatus()
    {
        var count = SearchResults.IsVisible
            ? SearchResults.SelectedItems?.OfType<EntryRow>().Count() ?? 0
            : FileTree.SelectedItems?.OfType<FileTreeNode>().Count(node => node.FilePath != null) ?? 0;
        if (count > 1) ActionStatus.Text = $"已选择 {count} 个资源（Ctrl 追加，Shift 连选）";
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
        if (sender is not Control input) return;
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
        Viewport.ZoomCamera(e.Delta.Y);
        e.Handled = true;
    }

    private void OnViewportKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = Viewport.HandleCameraKey(e.Key);
        if (e.Handled) UpdateViewportChrome();
    }

    private int _previewSeq;
    private int _textureLoadSeq;

    private void InvalidateTextureLoad()
    {
        // Texture decoding runs asynchronously and cannot be interrupted inside the
        // decoder. A sequence token makes its result stale immediately if a scene change
        // is triggered programmatically; normal UI scene changes are blocked while busy.
        ++_textureLoadSeq;
        LoadTexturesButton.IsEnabled = true;
    }

    private async Task PreviewPathAsync(string path)
    {
        if (_pak == null) return;
        InvalidateTextureLoad();
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
                        using var ms = _pak.ReadPreferredTextureFile(path, out var resolvedPath);
                        var outPng = Path.Combine(_tempDir, "preview_tex.png");
                        new TexService().ConvertToPng(ms, resolvedPath, outPng);
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
                        return ViewportDataLoader.LoadMesh(ms, path, 1, OpenResource, loadTextures: false);
                    });
                    if (IsStale()) return;
                    ShowViewport();
                    Viewport.SetMesh(viewportMesh);
                    SetPreviewMeshPaths(path);
                    ClearMotionState();
                    RefreshVisconGroups();
                    ActionStatus.Text = $"模型已加载（未加载贴图） | {Viewport.StatusInfo} | {viewportMesh.VisconInfo}";
                    break;
                }
                case "scn":
                case "pfb":
                {
                    var result = await Task.Run(() =>
                    {
                        using var ms = _pak.ReadFile(path);
                        return SceneService.Load(ms, path, OpenResource, CurrentAssemblyGameKey, loadTextures: false);
                    });
                    if (IsStale()) return;
                    if (result.Mesh == null)
                    {
                        ActionStatus.Text = $"场景已解析：{result.ObjectCount} 个对象，引用 {result.MeshReferenceCount} 个模型，但当前资源中没有可加载的静态 Mesh（缺少 {result.MissingMeshCount}）";
                        return;
                    }
                    ShowViewport();
                    Viewport.SetMesh(result.Mesh);
                    SetPreviewMeshPaths();
                    _previewScenePath = path;
                    ClearMotionState();
                    RefreshVisconGroups();
                    ActionStatus.Text = $"场景已加载：{result.ObjectCount} 个对象，{result.LoadedMeshCount}/{result.MeshReferenceCount} 个静态模型，{result.PrefabReferenceCount} 个 PFB 引用，缺少 {result.MissingMeshCount} 个资源";
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

    private void RefreshBoneTree()
    {
        if (BoneTree is null || BoneTreeSummaryText is null || BoneRootStatusText is null) return;
        var bones = Viewport.PrimaryBones;
        _boneTreeSource = bones;
        var previousIndex = _selectedBoneTreeNode?.Index;
        var deformBones = Viewport.PrimaryDeformBoneIndices.ToHashSet();
        var hasExplicitRoot = bones.Any(bone =>
            string.Equals(bone.Name, "Root", StringComparison.OrdinalIgnoreCase));

        var nodes = new BoneTreeNode[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            var parent = bones[i].ParentIndex;
            var effectiveParent = parent >= 0 && parent < bones.Length
                ? bones[parent].Name
                : hasExplicitRoot ? "（无）" : "Root（导出补全）";
            nodes[i] = new BoneTreeNode(i, bones[i].Name, parent, false,
                deformBones.Contains(i),
                $"索引 #{i}\n父级：{effectiveParent}\n" +
                (deformBones.Contains(i) ? "参与蒙皮" : "非蒙皮 / 辅助骨骼"));
        }

        var roots = new List<BoneTreeNode>();
        for (var i = 0; i < bones.Length; i++)
        {
            var parent = bones[i].ParentIndex;
            if (parent >= 0 && parent < nodes.Length && parent != i)
                nodes[parent].Children.Add(nodes[i]);
            else
                roots.Add(nodes[i]);
        }

        if (!hasExplicitRoot && roots.Count > 0)
        {
            var virtualRoot = new BoneTreeNode(-1, "Root", -1, true, false,
                "FBX 导出根骨骼\n源骨架没有 Root 时自动补充真实根骨，不改变现有骨骼姿势和蒙皮");
            virtualRoot.Children.AddRange(roots);
            roots = [virtualRoot];
        }

        foreach (var root in roots) ExpandBoneDepth(root, 2);

        var query = BoneSearchBox?.Text?.Trim() ?? "";
        if (query.Length > 0)
            roots = roots.Select(root => FilterBoneTree(root, query))
                .Where(root => root != null).Cast<BoneTreeNode>().ToList();

        _boneTreeRoots = roots;
        _syncingBoneTreeUi = true;
        BoneTree.ItemsSource = roots;
        var selected = previousIndex.HasValue ? FindBoneTreeNode(roots, previousIndex.Value) : null;
        BoneTree.SelectedItem = selected;
        _selectedBoneTreeNode = selected;
        _syncingBoneTreeUi = false;

        var sourceRootCount = bones.Count(bone => bone.ParentIndex < 0 || bone.ParentIndex >= bones.Length);
        BoneTreeSummaryText.Text = bones.Length == 0
            ? "未加载"
            : hasExplicitRoot
                ? $"{bones.Length:N0} 根 · {sourceRootCount} 个顶层"
                : $"导出 {bones.Length + 1:N0} 根 · 源 {bones.Length:N0} 根";
        BoneRootStatusText.Text = bones.Length == 0
            ? "加载模型后显示骨骼层级"
            : hasExplicitRoot
                ? "✓ 源骨架包含 Root"
                : "⚠ 源骨架无 Root；导出时自动补充 Root 骨骼";
        BoneSelectionInfoText.Text = bones.Length == 0
            ? "未加载模型"
            : selected == null ? "选择骨骼可查看父级、子级和局部变换" : BoneSelectionInfo(selected);

        if (previousIndex.HasValue && selected == null) Viewport.SelectPrimaryBone(null);
    }

    private static void ExpandBoneDepth(BoneTreeNode node, int depth)
    {
        node.IsExpanded = depth > 0;
        foreach (var child in node.Children) ExpandBoneDepth(child, depth - 1);
    }

    private static BoneTreeNode? FilterBoneTree(BoneTreeNode source, string query)
    {
        var children = source.Children.Select(child => FilterBoneTree(child, query))
            .Where(child => child != null).Cast<BoneTreeNode>().ToList();
        if (!source.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && children.Count == 0)
            return null;
        var clone = new BoneTreeNode(source.Index, source.Name, source.ParentIndex,
            source.IsVirtualExportRoot, source.IsDeform, source.ToolTip) { IsExpanded = true };
        clone.Children.AddRange(children);
        return clone;
    }

    private static BoneTreeNode? FindBoneTreeNode(IEnumerable<BoneTreeNode> roots, int index)
    {
        foreach (var root in roots)
        {
            if (root.Index == index) return root;
            var child = FindBoneTreeNode(root.Children, index);
            if (child != null) return child;
        }
        return null;
    }

    private string BoneSelectionInfo(BoneTreeNode node)
    {
        if (node.IsVirtualExportRoot)
        {
            var children = string.Join("、", node.Children.Take(3).Select(child => child.Name));
            if (node.Children.Count > 3) children += $" 等 {node.Children.Count} 个";
            return $"Root  [导出补全]\n父级：（无）\n子级：{children}\n位置：世界原点\n作用：统一 UE 根层级，不改变源骨骼姿势";
        }

        var bones = Viewport.PrimaryBones;
        if (node.Index < 0 || node.Index >= bones.Length) return node.Name;
        var bone = bones[node.Index];
        var parentName = bone.ParentIndex >= 0 && bone.ParentIndex < bones.Length
            ? bones[bone.ParentIndex].Name
            : bones.Any(item => string.Equals(item.Name, "Root", StringComparison.OrdinalIgnoreCase))
                ? "（无）"
                : "Root（导出补全）";
        var childCount = bones.Count(item => item.ParentIndex == node.Index);
        Matrix4x4.Decompose(bone.LocalBind, out var scale, out _, out var translation);
        return $"{bone.Name}  [#{node.Index}]\n" +
               $"父级：{parentName}　子级：{childCount}\n" +
               $"类型：{(node.IsDeform ? "蒙皮骨骼" : "非蒙皮 / 辅助骨骼")}\n" +
               $"源局部位置：X {translation.X:F3}　Y {translation.Y:F3}　Z {translation.Z:F3}\n" +
               $"局部缩放：{scale.X:F3}, {scale.Y:F3}, {scale.Z:F3}";
    }

    private void OnBoneSearchChanged(object? sender, TextChangedEventArgs e) => RefreshBoneTree();

    private void OnBoneTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingBoneTreeUi) return;
        _selectedBoneTreeNode = BoneTree.SelectedItem as BoneTreeNode;
        Viewport.SelectPrimaryBone(_selectedBoneTreeNode?.Index);
        BoneSelectionInfoText.Text = _selectedBoneTreeNode == null
            ? "选择骨骼可查看父级、子级和局部变换"
            : BoneSelectionInfo(_selectedBoneTreeNode);
        if (_selectedBoneTreeNode != null)
            ActionStatus.Text = $"已选中骨骼：{_selectedBoneTreeNode.Name}；视口中以黄色高亮";
    }

    private void OnBoneExpandAll(object? sender, RoutedEventArgs e)
    {
        foreach (var root in _boneTreeRoots) root.SetExpandedRecursive(true);
    }

    private void OnBoneCollapseAll(object? sender, RoutedEventArgs e)
    {
        foreach (var root in _boneTreeRoots) root.SetExpandedRecursive(false);
    }

    private void OnBoneFrameSelected(object? sender, RoutedEventArgs e)
    {
        if (_selectedBoneTreeNode == null)
        {
            ActionStatus.Text = "请先在骨骼树中选择一根骨骼";
            return;
        }
        Viewport.FramePrimaryBone(_selectedBoneTreeNode.Index);
        ActionStatus.Text = $"镜头已对准骨骼：{_selectedBoneTreeNode.Name}";
    }

    private void OnBoneTreeDoubleTapped(object? sender, TappedEventArgs e)
        => OnBoneFrameSelected(sender, new RoutedEventArgs());

    private void UpdateWorldAxisIndicator()
    {
        if (WorldAxisX is null || WorldAxisY is null || WorldAxisZ is null) return;
        var directions = Viewport.GetWorldAxisScreenDirections();
        const double originX = 45;
        const double originY = 58;
        const double length = 32;
        var lines = new[] { WorldAxisX, WorldAxisY, WorldAxisZ };
        var labels = new[] { WorldAxisXLabel, WorldAxisYLabel, WorldAxisZLabel };
        for (var i = 0; i < 3; i++)
        {
            var x = originX + directions[i].X * length;
            var y = originY + directions[i].Y * length;
            lines[i].StartPoint = new Avalonia.Point(originX, originY);
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


    private async void OnLoadTexturesClicked(object? sender, RoutedEventArgs e)
    {
        if (_pak == null || _previewMeshPaths.Count == 0)
        {
            ActionStatus.Text = "请先加载模型，再加载贴图";
            return;
        }

        var paths = _previewMeshPaths.ToArray();
        var operation = ++_previewSeq;
        var textureOperation = ++_textureLoadSeq;
        var progress = BeginProgress("正在加载贴图…");
        LoadTexturesButton.IsEnabled = false;
        try
        {
            var meshes = new ViewportMesh[paths.Length];
            for (var index = 0; index < paths.Length; index++)
            {
                var path = paths[index];
                UpdateProgress(progress, index,
                    $"正在解码贴图 {index + 1}/{paths.Length}：{Path.GetFileName(path)}");
                meshes[index] = await Task.Run(() =>
                {
                    using var ms = _pak.ReadFile(path);
                    return ViewportDataLoader.LoadMesh(ms, path, 1, OpenResource, loadTextures: true);
                });
                if (operation != _previewSeq || textureOperation != _textureLoadSeq) return;
                UpdateProgress(progress, index + 1, $"正在加载贴图 {index + 1}/{paths.Length}");
            }
            if (operation != _previewSeq || textureOperation != _textureLoadSeq) return;

            ShowViewport();
            SetPreviewMeshPaths(paths);
            // Keep the topology chosen by the user: a right-click assembled scene has
            // independent extras, while a merge-queue scene has one merged primary.
            // Rebuilding through SetMesh/AddMesh would clear the animation and restart
            // the scene in bind pose, which made texture loading appear to break motion.
            var preserveOverlayLayout = Viewport.ExtraModelCount > 0;
            var shouldMerge = !preserveOverlayLayout &&
                              meshes.Length > 1 &&
                              meshes.All(m => SameSkeleton(meshes[0], m));
            Viewport.ReplaceSceneMeshes(meshes, shouldMerge);
            RefreshVisconGroups();
            var textureCount = meshes.Sum(mesh => mesh.Textures.Length);
            ActionStatus.Text = textureCount > 0
                ? Viewport.HasAnimation
                    ? $"贴图已加载（{textureCount} 张），动画与当前组装姿势已保留 | {Viewport.StatusInfo}"
                    : $"贴图已加载（{textureCount} 张） | {Viewport.StatusInfo}"
                : "未找到可解码贴图：请检查 MDF/tex 路径与版本后重试";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "加载贴图失败：" + ex.Message;
        }
        finally
        {
            // Do not let an invalidated older task alter the state of a newer one.
            if (textureOperation == _textureLoadSeq)
                LoadTexturesButton.IsEnabled = true;
            EndProgress(progress);
        }
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
    private bool _viewportFullscreen;
    private WindowState _windowStateBeforeViewportFullscreen;
    private bool _logWasVisibleBeforeViewportFullscreen;

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
        VisconGroupSummaryText.Text = rows.Length == 0
            ? "未加载"
            : $"{visible.Count}/{rows.Length} 可见";
        _syncingVisconUi = false;
    }

    private void OnVisconGroupClicked(object? sender, RoutedEventArgs e)
    {
        if (_syncingVisconUi || sender is not CheckBox { Tag: int key } check) return;
        var row = (VisconGroupList.ItemsSource as IEnumerable<VisconGroupRow>)?.FirstOrDefault(x => x.Key == key);
        if (row != null) VisconGroupList.SelectedItem = row;
        Viewport.SetPrimaryGroupVisible(key, check.IsChecked == true);
        // Rows are immutable snapshots. Rebuild immediately so virtualization or a later
        // repaint cannot restore the old check mark while the renderer keeps the new state.
        RefreshVisconGroups(key);
        ActionStatus.Text = $"模型组已更新 | {Viewport.StatusInfo} | 按（F）可按当前可见组重新取景";
    }

    private void OnViewportFullscreenClicked(object? sender, RoutedEventArgs e) => ToggleViewportFullscreen();

    private void OnMainWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F11) return;
        ToggleViewportFullscreen();
        e.Handled = true;
    }

    private void ToggleViewportFullscreen()
    {
        _viewportFullscreen = !_viewportFullscreen;
        if (_viewportFullscreen)
        {
            _windowStateBeforeViewportFullscreen = WindowState;
            _logWasVisibleBeforeViewportFullscreen = LogPanel.IsVisible;
            TitleStrip.IsVisible = MainMenu.IsVisible = StatusStrip.IsVisible = false;
            LeftPanel.IsVisible = LeftSplitter.IsVisible = RightSplitter.IsVisible = RightPanel.IsVisible = false;
            LogSplitter.IsVisible = LogPanel.IsVisible = false;
            Grid.SetColumn(CenterPreview, 0);
            Grid.SetColumnSpan(CenterPreview, 5);
            Grid.SetRowSpan(CenterPreview, 3);
            CenterPreview.Margin = new Thickness(0);
            WindowState = WindowState.FullScreen;
            ActionStatus.Text = "用户视图已全屏；按 F11 退出";
        }
        else
        {
            WindowState = _windowStateBeforeViewportFullscreen;
            Grid.SetColumn(CenterPreview, 2);
            Grid.SetColumnSpan(CenterPreview, 1);
            Grid.SetRowSpan(CenterPreview, 1);
            CenterPreview.Margin = new Thickness(6, 0, 6, 0);
            TitleStrip.IsVisible = MainMenu.IsVisible = StatusStrip.IsVisible = true;
            LeftPanel.IsVisible = LeftSplitter.IsVisible = RightSplitter.IsVisible = RightPanel.IsVisible = true;
            LogSplitter.IsVisible = LogPanel.IsVisible = _logWasVisibleBeforeViewportFullscreen;
            ActionStatus.Text = "已退出用户视图全屏";
        }
        Viewport.Refresh();
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
        // Resetting the slider for a newly selected animation is a programmatic
        // update, not a user scrub. Without suppression, ValueChanged reaches
        // OnTimelineScrubbed and immediately pauses the playback that
        // SetAnimation just started.
        _suppressSlider = true;
        try
        {
            TimelineSlider.Maximum = Math.Max(0.01, duration);
            TimelineSlider.Value = 0;
        }
        finally
        {
            _suppressSlider = false;
        }
        PlaybackOverlay.IsVisible = true;
        TimelineSlider.IsVisible = true;
        PlaybackButton.Content = Viewport.IsPlaying ? "⏸" : "▶";
        TimelineText.Text = FormatTimelineText(0, duration, Viewport.AnimationFrameRate, Viewport.AnimationFrameCount);
        _playheadTimer ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _playheadTimer.Tick -= OnPlayheadTick;
        _playheadTimer.Tick += OnPlayheadTick;
        _playheadTimer.Start();
    }


    private static string FormatTimelineText(float currentTime, float duration, int fps, int frameCount)
    {
        fps = fps > 0 ? fps : 60;
        var total = frameCount > 0 ? frameCount : Math.Max(0, (int)MathF.Round(duration * fps));
        var currentFrame = Math.Clamp((int)MathF.Round(currentTime * fps), 0, total);
        return $"{currentTime:F2} / {duration:F2}s    {currentFrame} / {total} 帧    {fps} FPS";
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
        TimelineText.Text = FormatTimelineText(Viewport.CurrentTime, Viewport.Duration, Viewport.AnimationFrameRate, Viewport.AnimationFrameCount);
    }

    private void OnTimelineScrubbed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlider) return;
        if (Viewport.IsPlaying) Viewport.PausePlayback();
        Viewport.ScrubTo((float)e.NewValue);
        PlaybackButton.Content = Viewport.IsPlaying ? "⏸" : "▶";
        TimelineText.Text = FormatTimelineText(Viewport.CurrentTime, Viewport.Duration, Viewport.AnimationFrameRate, Viewport.AnimationFrameCount);
    }

    private async void OnMotionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingMotionUi || _pak == null || _currentMotlistPath == null || MotionCombo.SelectedIndex < 0) return;
        var comboIndex = MotionCombo.SelectedIndex;
        if ((uint)comboIndex >= (uint)_currentMotionIndices.Length) return;
        var idx = _currentMotionIndices[comboIndex];
        var motlistPath = _currentMotlistPath;
        var sceneMeshes = Viewport.SceneMeshes;
        var meshBoneNames = Viewport.AllMeshBoneNames;
        try
        {
            var clip = await Task.Run(() =>
            {
                using var motMs = _pak.ReadFile(motlistPath);
                return ViewportDataLoader.LoadAnimation(motMs, motlistPath, idx,
                    meshBoneNames, sceneMeshes);
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
        var sceneMeshes = Viewport.SceneMeshes;
        var meshBoneNames = Viewport.AllMeshBoneNames;
        return await Task.Run(() =>
        {
            using var namesStream = _pak.ReadFile(path);
            var motions = ViewportDataLoader.ListMotions(namesStream, path);
            if (motions.Count == 0) throw new InvalidDataException("动画列表中没有可读取的内嵌动作");
            using var motionStream = _pak.ReadFile(path);
            var clip = ViewportDataLoader.LoadAnimation(motionStream, path,
                motions[Math.Clamp(index, 0, motions.Count - 1)].SourceIndex,
                meshBoneNames, sceneMeshes);
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
        _previewScenePath = null;
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
        var repoScript = Path.Combine(Directory.GetCurrentDirectory(), "tools", scriptName);
        if (File.Exists(repoScript)) return repoScript;

        var toolDir = AppPaths.ToolsDirectory;
        var targetPath = Path.Combine(toolDir, scriptName);
        if (File.Exists(targetPath)) return targetPath;

        Directory.CreateDirectory(toolDir);
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(name =>
            name.Contains("EmbeddedTools", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith("." + scriptName, StringComparison.OrdinalIgnoreCase));
        if (resourceName == null)
            throw new FileNotFoundException($"找不到内置脚本 {scriptName}");

        using var input = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"无法读取内置脚本 {scriptName}");
        using var output = File.Create(targetPath);
        input.CopyTo(output);
        return targetPath;
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
        if (_pak == null || !Viewport.HasMesh || (_previewMeshPaths.Count == 0 && _previewScenePath == null))
        { ActionStatus.Text = "请先在预览区加载需要导出的模型"; return; }
        if (_previewScenePath != null)
        {
            await ExportPreviewSceneFbxAsync(_previewScenePath);
            return;
        }
        await ExportPreviewModelsFbxAsync();
    }

    private async Task ExportPreviewSceneFbxAsync(string scenePath)
    {
        var models = Viewport.ExportModels;
        if (models.Count == 0) { ActionStatus.Text = "当前场景没有可导出的静态模型"; return; }
        if (!await EnsureBlenderReadyAsync()) return;
        var marker = scenePath.Contains(".scn.", StringComparison.OrdinalIgnoreCase) ? ".scn" : ".pfb";
        var outputPath = Path.Combine(Path.GetFullPath(CurrentOutputDirectory), NativeStem(scenePath, marker) + "_场景.fbx");
        var progress = BeginProgress("正在导出场景 FBX…");
        ExportButton.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                var workDir = CreateExportWorkDir("scene");
                try
                {
                    var export = models.Select(model => (model.Mesh, (IReadOnlySet<int>)model.VisibleGroups)).ToArray();
                    new ViewportExportService().ConvertMergedToGlb(export, Path.Combine(workDir, "001_scene.glb"));
                    RunBlenderBatch("export_models_fbx.py", CurrentBlenderPath, workDir, outputPath);
                }
                finally { TryDeleteDirectory(workDir); }
            });
            ActionStatus.Text = $"场景 FBX 已生成：{outputPath}";
        }
        catch (Exception ex) { ActionStatus.Text = "场景导出失败：" + ex.Message; }
        finally { ExportButton.IsEnabled = true; EndProgress(progress); }
    }
    private async void OnExportAnimClicked(object? sender, RoutedEventArgs e)
    {
        if (ExportAnimScopeCombo.SelectedIndex >= 2)
        {
            await ExportAnimationBatchAsync();
            return;
        }
        if (_pak == null || _currentMotlistPath == null)
        { ActionStatus.Text = "请先在当前预览模型上加载一个动画列表"; return; }
        var exportModels = Viewport.ExportModels;
        if (exportModels.Count == 0)
        { ActionStatus.Text = "当前预览缺少可用于生成骨架的模型"; return; }

        var exportCurrent = ExportAnimScopeCombo.SelectedIndex <= 0;
        var selectedMotionComboIndex = MotionCombo.SelectedIndex;
        if (exportCurrent && ((uint)selectedMotionComboIndex >= (uint)_currentMotionIndices.Length))
        { ActionStatus.Text = "请先在动画下拉框中选择要导出的当前动画"; return; }
        var selectedMotionSourceIndex = exportCurrent ? _currentMotionIndices[selectedMotionComboIndex] : -1;

        var motlistPath = _currentMotlistPath;
        var outDir = Path.GetFullPath(CurrentOutputDirectory);
        if (!await EnsureBlenderReadyAsync()) return;
        var blender = CurrentBlenderPath;
        var scopeText = exportCurrent ? "当前动画" : "全部动画";
        var exportFps = Viewport.AnimationFrameRate;
        var progress = BeginProgress($"正在导出{scopeText}…");
        ActionStatus.Text = $"正在导出{scopeText}…";
        ExportAnimButton.IsEnabled = false;
        ExportAnimScopeCombo.IsEnabled = false;
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
            ActionStatus.Text = $"动画导出完成：{result.Item1} 个 FBX | {result.finalDir}";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "动画导出失败：" + ex.Message;
        }
        finally
        {
            ExportAnimButton.IsEnabled = true;
            ExportAnimScopeCombo.IsEnabled = true;
            EndProgress(progress);
        }
    }

    private void OnExportAnimScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AnimBatchPanel is null) return;
        AnimBatchPanel.IsVisible = ExportAnimScopeCombo.SelectedIndex >= 2;
    }

    private async Task ExportPreviewModelsFbxAsync()
    {
        if (_pak == null) return;
        var pak = _pak;
        var paths = _previewMeshPaths.ToArray();
        var exportModels = Viewport.ExportModels;
        var sourceCount = paths.Length;
        if (exportModels.Count == 0)
            throw new InvalidOperationException("预览场景没有可导出的模型");
        var outDir = Path.GetFullPath(CurrentOutputDirectory);
        if (!await EnsureBlenderReadyAsync()) return;
        var blender = CurrentBlenderPath;
        var outputPath = Path.Combine(outDir, NativeStem(paths[0], ".mesh") +
            (sourceCount > 1 ? $"_合并{sourceCount}个模型" : "") + ".fbx");
        var progress = BeginProgress("正在合并并导出 FBX…");
        ActionStatus.Text = $"正在导出预览中的全部 {sourceCount} 个源模型（场景对象 {exportModels.Count} 个）…";
        ExportButton.IsEnabled = false;
        Stream? OpenCapturedResource(string nativePath)
        {
            try { return pak.ReadFile(nativePath); }
            catch { return null; }
        }
        try
        {
            await Task.Run(() =>
            {
                EnsureBlender(blender);
                var workDir = CreateExportWorkDir("models");
                try
                {
                    // The viewport normally loads geometry only. Re-read every source with MDF
                    // textures here so the GLB/FBX gets real material image references even when
                    // the user never clicked "加载贴图" before exporting.
                    var texturedMeshes = paths.Select(path =>
                    {
                        using var stream = pak.ReadFile(path);
                        return ViewportDataLoader.LoadMesh(stream, path, 1,
                            OpenCapturedResource, loadTextures: true);
                    }).ToArray();
                    (ViewportMesh Mesh, IReadOnlySet<int> VisibleGroups)[] models;
                    if (exportModels.Count == texturedMeshes.Length)
                    {
                        models = texturedMeshes.Select((mesh, index) =>
                            (mesh, (IReadOnlySet<int>)exportModels[index].VisibleGroups)).ToArray();
                    }
                    else
                    {
                        var merged = ViewportMesh.Merge(texturedMeshes);
                        var visible = exportModels.Count == 1
                            ? exportModels[0].VisibleGroups
                            : merged.Groups.Where(group => group.DefaultVisible)
                                .Select(group => group.Key).ToHashSet();
                        models = [(merged, (IReadOnlySet<int>)visible)];
                    }
                    new ViewportExportService().ConvertMergedToGlb(models,
                        Path.Combine(workDir, $"001_{NativeStem(paths[0], ".mesh")}_合并.glb"));
                    RunBlenderBatch("export_models_fbx.py", blender, workDir, outputPath);
                }
                finally { TryDeleteDirectory(workDir); }
            });
            ActionStatus.Text = $"模型 FBX 已生成：已将全部 {sourceCount} 个源模型合并为 1 个模型 | {outputPath}";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "模型导出失败：" + ex.Message;
            return;
        }
        finally
        {
            ExportButton.IsEnabled = true;
            EndProgress(progress);
        }

        // FBX is the primary result. Do not keep its progress bar or export button locked
        // while the optional MDF/TEX post-processing runs, which can take much longer for
        // characters with many material maps.
        ActionStatus.Text = $"模型 FBX 已生成，正在后台导出 MDF 关联贴图… | {outputPath}";
        try
        {
            var result = await Task.Run(() =>
            {
                var referencedTextures = paths.SelectMany(path =>
                        ViewportDataLoader.ListReferencedTexturePaths(path, OpenCapturedResource))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var textureResult = ExportTextureFiles(pak, referencedTextures, outDir);
                return (referencedTextures.Length, textureResult.exported, textureResult.failures);
            });
            foreach (var failure in result.failures) AppendLog("模型相关贴图导出失败：" + failure);
            var textureSummary = result.Item1 == 0
                ? "未找到 MDF 引用贴图"
                : $"相关贴图 {result.exported}/{result.Item1} 张 PNG";
            ActionStatus.Text = $"模型导出完成：{textureSummary} | {outputPath}";
        }
        catch (Exception ex)
        {
            AppendLog("模型 FBX 已生成，但关联贴图后处理失败：" + ex.Message);
            ActionStatus.Text = $"模型 FBX 已生成；关联贴图导出失败（详见日志） | {outputPath}";
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
                switch (kind)
                {
                    case "tex":
                    {
                        using var texStream = _pak.ReadPreferredTextureFile(path, out var resolvedPath);
                        return new TexService().ConvertToPng(texStream, resolvedPath,
                            Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + ".png"));
                    }
                    case "mesh":
                    {
                        using var ms = _pak.ReadFile(path);
                        return new MeshService().ConvertToGlb(ms, path,
                            Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + ".glb"));
                    }
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

    private static string TextureExportPath(string outputRoot, string nativePath)
    {
        var normalized = nativePath.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
            throw new InvalidDataException($"无效资源路径: {nativePath}");

        var invalid = Path.GetInvalidFileNameChars();
        static string SafeSegment(string value, char[] invalidChars) =>
            new(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());

        var safeParts = parts.Select(part => SafeSegment(part, invalid)).ToArray();
        var fileName = safeParts[^1];
        var texMarker = fileName.LastIndexOf(".tex.", StringComparison.OrdinalIgnoreCase);
        if (texMarker < 0)
            throw new InvalidDataException($"不是可导出的 TEX 资源: {nativePath}");
        safeParts[^1] = fileName[..texMarker] + ".png";

        var root = Path.GetFullPath(Path.Combine(outputRoot, "textures"));
        var result = Path.GetFullPath(Path.Combine(new[] { root }.Concat(safeParts).ToArray()));
        if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"资源路径超出导出目录: {nativePath}");
        return result;
    }

    private static (int exported, List<string> failures) ExportTextureFiles(
        PakService pak, IEnumerable<string> paths, string outputRoot, Action<int, int>? progress = null)
    {
        var textures = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var exported = 0;
        var failures = new List<string>();
        for (var index = 0; index < textures.Length; index++)
        {
            var path = textures[index];
            try
            {
                using var stream = pak.ReadPreferredTextureFile(path, out var resolvedPath);
                new TexService().ConvertToPng(stream, resolvedPath, TextureExportPath(outputRoot, path));
                exported++;
            }
            catch (Exception ex)
            {
                failures.Add($"{path}：{ex.Message}");
            }
            progress?.Invoke(index + 1, textures.Length);
        }
        return (exported, failures);
    }

    private async void OnCtxExportTextures(object? sender, RoutedEventArgs e)
    {
        if (_pak == null) { ActionStatus.Text = "请先加载 PAK"; return; }

        var contextPath = PathFromSender(sender);
        var paths = SelectedResourcePaths().ToList();
        if (contextPath != null && paths.All(path =>
                !path.Equals(contextPath, StringComparison.OrdinalIgnoreCase)))
            paths.Add(contextPath);
        var textures = paths.Where(path => KindOf(path) == "tex")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (textures.Length == 0)
        {
            ActionStatus.Text = "请选择一个或多个 .tex 贴图；搜索结果支持 Ctrl / Shift 多选";
            return;
        }

        var outputRoot = Path.GetFullPath(CurrentOutputDirectory);
        var progress = BeginProgress($"正在导出 {textures.Length} 张贴图…", false, textures.Length);
        ActionStatus.Text = $"正在导出 {textures.Length} 张贴图 PNG…";
        try
        {
            var result = await Task.Run(() => ExportTextureFiles(_pak, textures, outputRoot, (current, total) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    UpdateCountProgress(progress, current, total, "导出贴图"))));

            foreach (var failure in result.failures) AppendLog("贴图导出失败：" + failure);
            var textureDir = Path.Combine(outputRoot, "textures");
            ActionStatus.Text = result.failures.Count == 0
                ? $"贴图导出完成：{result.exported} 张 PNG | {textureDir}"
                : $"贴图导出完成：成功 {result.exported}，失败 {result.failures.Count}（详见日志） | {textureDir}";
        }
        finally
        {
            EndProgress(progress);
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

    private void OnCtxLocateInTree(object? sender, RoutedEventArgs e)
    {
        var path = PathFromSender(sender);
        if (path == null || !TryFindTreeNode(path, out var node))
        {
            ActionStatus.Text = "无法在当前目录树中定位该资源";
            return;
        }

        SearchBox.Text = "";
        FileTree.SelectedItem = node;
        SelectPath(path);
        FileTree.Focus();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var container = FileTree.GetVisualDescendants()
                .OfType<TreeViewItem>()
                .FirstOrDefault(item => ReferenceEquals(item.DataContext, node));
            container?.BringIntoView();
        }, Avalonia.Threading.DispatcherPriority.Background);
        ActionStatus.Text = $"已在目录树中定位：{path}";
    }

    private async void OnCtxAddModel(object? sender, RoutedEventArgs e)
    {
        var path = PathFromSender(sender);
        if (_pak == null || path == null) return;
        if (KindOf(path) != "mesh") { ActionStatus.Text = "叠加模型需要选择 .mesh 文件"; return; }
        // A left-click selection may still be decoding its automatic single-model preview.
        // Invalidate it before changing the assembled scene so it cannot finish later and
        // silently replace the primary mesh/export source list.
        InvalidateTextureLoad();
        var sceneOperation = ++_previewSeq;
        var progress = BeginProgress("正在叠加模型…");
        ActionStatus.Text = "叠加模型加载中…";
        try
        {
            var vm = await Task.Run(() =>
            {
                using var ms = _pak.ReadFile(path);
                return ViewportDataLoader.LoadMesh(ms, path, 1, OpenResource, loadTextures: false);
            });
            if (sceneOperation != _previewSeq) return;
            ShowViewport();
            if (Viewport.HasMesh)
            {
                // Do not infer that every mesh under one character directory has a compatible
                // skeleton. DMC5 body/head/hair variants can use different bind skeletons;
                // merging them corrupts the animated pose. Keep right-click overlay as a
                // non-destructive independent model operation.
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
                ActionStatus.Text = $"模型已加载（未加载贴图） | {Viewport.StatusInfo}";
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
    private readonly List<string> _animationExportQueue = new();

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

    private void RefreshAnimationExportList()
    {
        AnimBatchListBox.ItemsSource = null;
        AnimBatchListBox.ItemsSource = _animationExportQueue.ToArray();
        AnimBatchCountText.Text = $"{_animationExportQueue.Count} 个列表";
    }

    private int AddAnimationExportPaths(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths.Where(path => KindOf(path) == "motlist")
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_animationExportQueue.Any(existing =>
                    existing.Equals(path, StringComparison.OrdinalIgnoreCase)))
                continue;
            _animationExportQueue.Add(path);
            added++;
        }
        if (added > 0) RefreshAnimationExportList();
        return added;
    }

    private IReadOnlyList<string> SelectedResourcePaths()
    {
        var selected = new List<string>();
        if (SearchResults.IsVisible && SearchResults.SelectedItems != null)
            selected.AddRange(SearchResults.SelectedItems.OfType<EntryRow>().Select(row => row.Path));
        else if (FileTree.SelectedItems != null)
            selected.AddRange(FileTree.SelectedItems.OfType<FileTreeNode>()
                .Select(node => node.FilePath).Where(path => path != null).Select(path => path!));
        if (_selectedPath != null) selected.Add(_selectedPath);
        return selected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void OnCtxAddToAnimBatch(object? sender, RoutedEventArgs e)
    {
        var contextPath = PathFromSender(sender);
        var paths = SelectedResourcePaths().ToList();
        if (contextPath != null && paths.All(path =>
                !path.Equals(contextPath, StringComparison.OrdinalIgnoreCase)))
            paths.Add(contextPath);
        var added = AddAnimationExportPaths(paths);
        if (added > 0) ExportAnimScopeCombo.SelectedIndex = 2;
        ActionStatus.Text = added > 0
            ? $"已加入 {added} 个 MotionList，批量导出列表共 {_animationExportQueue.Count} 个"
            : "请选择一个或多个 MotionList";
    }

    private void OnAnimBatchAddSelectedClicked(object? sender, RoutedEventArgs e)
    {
        var added = AddAnimationExportPaths(SelectedResourcePaths());
        if (added > 0) ExportAnimScopeCombo.SelectedIndex = 2;
        ActionStatus.Text = added > 0
            ? $"已加入 {added} 个 MotionList，批量导出列表共 {_animationExportQueue.Count} 个"
            : "请先在左侧选择 MotionList；搜索结果支持 Ctrl / Shift 多选";
    }

    private void OnAnimBatchAddSearchResultsClicked(object? sender, RoutedEventArgs e)
    {
        if (!SearchResults.IsVisible || SearchResults.ItemsSource is not IEnumerable<EntryRow> rows)
        {
            ActionStatus.Text = "请先在左侧输入搜索条件，再添加当前搜索结果";
            return;
        }
        var added = AddAnimationExportPaths(rows.Select(row => row.Path));
        if (added > 0) ExportAnimScopeCombo.SelectedIndex = 2;
        ActionStatus.Text = added > 0
            ? $"已从搜索结果加入 {added} 个 MotionList，批量导出列表共 {_animationExportQueue.Count} 个"
            : "当前搜索结果中没有可新增的 MotionList";
    }

    private void OnAnimBatchRemoveClicked(object? sender, RoutedEventArgs e)
    {
        var selected = AnimBatchListBox.SelectedItems?.OfType<string>().ToArray() ?? [];
        if (selected.Length == 0 && AnimBatchListBox.SelectedItem is string item) selected = [item];
        foreach (var path in selected) _animationExportQueue.Remove(path);
        RefreshAnimationExportList();
        ActionStatus.Text = selected.Length > 0
            ? $"已移除 {selected.Length} 项，批量导出列表剩余 {_animationExportQueue.Count} 个"
            : "请先在动画批量导出列表中选择要移除的项目";
    }

    private void OnAnimBatchClearClicked(object? sender, RoutedEventArgs e)
    {
        _animationExportQueue.Clear();
        RefreshAnimationExportList();
        ActionStatus.Text = "动画批量导出列表已清空";
    }

    private async Task ExportAnimationBatchAsync()
    {
        if (_pak == null) { ActionStatus.Text = "请先加载游戏 PAK"; return; }
        if (_animationExportQueue.Count == 0)
        { ActionStatus.Text = "请先把 MotionList 加入动画批量导出列表"; return; }

        var exportModels = Viewport.ExportModels;
        if (exportModels.Count == 0)
        { ActionStatus.Text = "请先加载与这些 MotionList 对应的角色模型，批量导出需要它的骨架"; return; }
        if (!await EnsureBlenderReadyAsync()) return;

        var paths = _animationExportQueue.ToArray();
        var outDir = Path.GetFullPath(CurrentOutputDirectory);
        var blender = CurrentBlenderPath;
        var progress = BeginProgress($"正在统计 {paths.Length} 个 MotionList…");
        ActionStatus.Text = $"正在批量导出 {paths.Length} 个 MotionList 的全部动画…";
        ExportAnimButton.IsEnabled = false;
        ExportAnimScopeCombo.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                EnsureBlender(blender);
                var models = exportModels
                    .Select(model => (model.Mesh, (IReadOnlySet<int>)model.VisibleGroups))
                    .ToArray();
                var merged = new ViewportExportService().BuildMergedExportModel(models);
                var motionCounts = new int[paths.Length];
                var failures = new List<string>();
                for (var listIndex = 0; listIndex < paths.Length; listIndex++)
                {
                    try
                    {
                        using var stream = _pak.ReadFile(paths[listIndex]);
                        motionCounts[listIndex] = ViewportDataLoader.ListMotions(stream, paths[listIndex]).Count;
                        if (motionCounts[listIndex] == 0)
                            failures.Add($"{paths[listIndex].Split('/')[^1]}：没有可导出的内嵌动画");
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{paths[listIndex].Split('/')[^1]}：{ex.Message}");
                    }
                }

                var totalAnimations = motionCounts.Sum();
                if (totalAnimations == 0)
                    throw new InvalidOperationException("所选 MotionList 中没有可导出的内嵌动画");

                const int exportFps = 60;
                var totalWork = totalAnimations * 2;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    UpdateCountProgress(progress, 0, totalWork,
                        $"批量导出 · {paths.Length} 个列表 / {totalAnimations} 个动画"));

                var batchRoot = Path.Combine(outDir, "批量动画");
                Directory.CreateDirectory(batchRoot);
                var workRoot = CreateExportWorkDir("animation_batch");
                var preparedOffset = 0;
                var exportedOffset = 0;
                var exportedFbx = 0;
                var successfulLists = 0;
                try
                {
                    for (var listIndex = 0; listIndex < paths.Length; listIndex++)
                    {
                        var motionCount = motionCounts[listIndex];
                        if (motionCount == 0) continue;
                        var listNumber = listIndex + 1;
                        var motlistPath = paths[listIndex];
                        var stem = NativeStem(motlistPath, ".motlist");
                        var listWorkDir = Path.Combine(workRoot, $"{listNumber:D3}_{SafeFileName(stem)}");
                        var finalDir = Path.Combine(batchRoot,
                            $"{listNumber:D2}_{SafeFileName(stem)}_动画");
                        Directory.CreateDirectory(listWorkDir);
                        var prepareBase = preparedOffset;
                        var exportBase = exportedOffset;
                        try
                        {
                            using var motMs = _pak.ReadFile(motlistPath);
                            new AnimationService().ConvertAllToGlbWithAnimation(
                                merged.Mesh, motMs, motlistPath, listWorkDir,
                                (current, total) =>
                                {
                                    var overall = prepareBase + current;
                                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                        UpdateCountProgress(progress, overall, totalWork,
                                            $"准备列表 {listNumber}/{paths.Length} · {stem} {current}/{total}"));
                                });

                            RunBlenderBatch("export_animations_fbx.py", blender, listWorkDir, finalDir,
                                (current, total) =>
                                {
                                    var overall = totalAnimations + exportBase + current;
                                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                        UpdateCountProgress(progress, overall, totalWork,
                                            $"导出列表 {listNumber}/{paths.Length} · {stem} {current}/{total}"));
                                }, exportFps.ToString());
                            exportedFbx += motionCount;
                            successfulLists++;
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"{motlistPath.Split('/')[^1]}：{ex.Message}");
                        }
                        finally
                        {
                            preparedOffset += motionCount;
                            exportedOffset += motionCount;
                        }
                    }
                }
                finally { TryDeleteDirectory(workRoot); }

                return (exportedFbx, successfulLists, failures, batchRoot);
            });

            foreach (var failure in result.failures) AppendLog("批量动画跳过：" + failure);
            ActionStatus.Text = result.failures.Count == 0
                ? $"批量动画导出完成：{result.successfulLists} 个 MotionList，共 {result.exportedFbx} 个 FBX | {result.batchRoot}"
                : $"批量动画导出完成：{result.successfulLists}/{paths.Length} 个 MotionList，{result.exportedFbx} 个 FBX；跳过 {result.failures.Count} 项，详情见日志 | {result.batchRoot}";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "批量动画导出失败：" + ex.Message;
        }
        finally
        {
            ExportAnimButton.IsEnabled = true;
            ExportAnimScopeCombo.IsEnabled = true;
            EndProgress(progress);
        }
    }

    private static bool SameSkeleton(ViewportMesh a, ViewportMesh b)
        => ViewportMesh.TryGetMergeCompatibility([a, b], out _);

    private async void OnMergeLoadClicked(object? sender, RoutedEventArgs e)
    {
        if (_mergeQueue.Count == 0) { ActionStatus.Text = "合并队列为空"; return; }
        if (_pak == null) { ActionStatus.Text = "请先加载 PAK"; return; }
        // Joining the queue starts from selected rows, and row selection also starts an
        // asynchronous single-model preview. Give this merge operation ownership of the
        // scene so an older preview cannot overwrite the merged result after it completes.
        InvalidateTextureLoad();
        var sceneOperation = ++_previewSeq;
        var progress = BeginProgress($"正在加载模型 0/{_mergeQueue.Count}", indeterminate: false,
            maximum: _mergeQueue.Count);
        ActionStatus.Text = $"合并加载中（{_mergeQueue.Count} 个模型）…";
        try
        {
            var paths = _mergeQueue.ToArray();
            var meshes = new List<ViewportMesh>(paths.Length);
            var loadedPaths = new List<string>(paths.Length);
            var skipped = new List<string>();
            for (var i = 0; i < paths.Length; i++)
            {
                var index = i;
                var mesh = await Task.Run(() =>
                {
                    using var ms = _pak.ReadFile(paths[index]);
                    return ViewportDataLoader.LoadMesh(ms, paths[index], 1, OpenResource, loadTextures: false);
                });
                if (mesh.VertexCount == 0 || mesh.FaceCount == 0)
                {
                    skipped.Add(paths[index]);
                    AppendLog($"合并加载已跳过无可视几何的模型：{paths[index]}");
                }
                else
                {
                    meshes.Add(mesh);
                    loadedPaths.Add(paths[index]);
                }
                UpdateProgress(progress, index + 1,
                    $"正在加载模型 {index + 1}/{paths.Length}");
            }
            if (sceneOperation != _previewSeq) return;
            if (meshes.Count == 0)
                throw new InvalidDataException("队列中的模型均不包含可显示的几何数据");

            // Bone names alone are not a safe merge key. MHRise parts can have the same named
            // bones but different bind-space origins; sharing one armature then separates the
            // body and clothing as soon as a motion is applied.
            var mergeReason = string.Empty;
            var shouldMerge = meshes.Count > 1 &&
                              ViewportMesh.TryGetMergeCompatibility(meshes, out mergeReason);
            ShowViewport();
            SetPreviewMeshPaths(loadedPaths.ToArray());
            ClearMotionState();
            if (shouldMerge)
            {
                var merged = ViewportMesh.Merge(meshes);
                Viewport.SetMesh(merged);
                RefreshVisconGroups();
                ActionStatus.Text = $"已几何合并 {meshes.Count} 个模型（未加载贴图，同骨骼）→ 预览 1 个整体" +
                    (skipped.Count > 0 ? $"；跳过 {skipped.Count} 个无几何占位模型" : "");
            }
            else
            {
                Viewport.SetMesh(meshes[0]);
                RefreshVisconGroups();
                for (var i = 1; i < meshes.Count; i++)
                    Viewport.AddMesh(meshes[i], loadedPaths[i].Split('/')[^1]);
                var verb = meshes.Count > 1
                    ? $"已加载 {meshes.Count} 个模型（保持独立骨架：{mergeReason} → 按主模型同步动画）"
                    : "模型已加载（未加载贴图）";
                ActionStatus.Text = $"{verb} | 导出包含 {loadedPaths.Count} 个有效源模型" +
                    (skipped.Count > 0 ? $" | 跳过 {skipped.Count} 个无几何占位模型" : "") +
                    $" | {Viewport.StatusInfo}";
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


    private void OpenOutputFolder()
    {
        var outDir = CurrentOutputDirectory;
        Directory.CreateDirectory(outDir);
        Process.Start("explorer.exe", $"\"{outDir}\"");
        ActionStatus.Text = $"已打开导出文件夹: {outDir}";
    }

    private void OnOpenOutputFolderClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            OpenOutputFolder();
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "打开文件夹失败: " + ex.Message;
        }
    }
    private void OnCtxShowExplorer(object? sender, RoutedEventArgs e)
    {
        OnOpenOutputFolderClicked(sender, e);
    }
}

