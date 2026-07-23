using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
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
    private sealed record EntryRow(string Path, long Size, string SourcePak)
    {
        public string SizeText => Size >= 1 << 20 ? $"{Size / 1048576.0:F1} MB" : $"{Size / 1024.0:F1} KB";
    }

    private PakService? _pak;
    private List<EntryRow> _all = new();
    private readonly Dictionary<string, EntryRow> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedPath;
    private string? _lastMeshPath;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ReExtractor");

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_tempDir);
        OutDirBox.Text = Path.Combine(Directory.GetCurrentDirectory(), "output");

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
            var b = Viewport.IsVisible ? $"GPU vp {Viewport.Bounds.Width:F0}x{Viewport.Bounds.Height:F0}" : "";
            var f = Viewport.IsPlaying ? $"FPS: {Viewport.CurrentFps:F0}" : "";
            FpsText.Text = string.Join(' ', new[] { b, f }.Where(x => x != ""));
        };
        _fpsTimer.Start();

    }

    private Avalonia.Threading.DispatcherTimer? _fpsTimer;

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

    private async void OnLoadClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            LoadButton.IsEnabled = false;
            StatusText.Text = "加载中…";
            var gameDir = GameDirBox.Text.Trim();
            var listFile = ListFileBox.Text.Trim();

            var (pak, entries, roots) = await Task.Run(() =>
            {
                var p = new PakService();
                p.AddPaksFromGameDir(gameDir);
                p.LoadListFile(listFile);
                var list = p.EnumerateFiles()
                    .Select(f => new EntryRow(f.Path, f.DecompressedSize, f.SourcePak))
                    .ToList();
                var tree = BuildTree(list);
                return (p, list, tree);
            });

            _pak = pak;
            _all = entries;
            _byPath.Clear();
            foreach (var r in entries) _byPath[r.Path] = r;
            FileTree.ItemsSource = roots;
            ApplyFilter();
            StatusText.Text = $"已加载 {entries.Count:N0} 个文件";
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败: " + ex.Message;
        }
        finally
        {
            LoadButton.IsEnabled = true;
        }
    }

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
            SearchResults.ItemsSource = _all.Where(r => r.Path.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(500).ToList();
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

        // right-click selection updates info only — scene stays intact for multi-model assembly
        if (_suppressPreviewOnce)
        {
            _suppressPreviewOnce = false;
            return;
        }

        // single-click preview: fire and forget, only the latest selection wins
        if (kind is "tex" or "mesh" or "motlist")
            _ = PreviewPathAsync(path);
    }

    private bool _suppressPreviewOnce;

    private void OnListPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        // right-click selects an item (SelectionChanged follows) — mark it so we don't preview/replace the scene
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            _suppressPreviewOnce = true;
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
    }

    private void ShowViewport()
    {
        PreviewImage.IsVisible = false;
        Viewport.IsVisible = true;
        Viewport.Refresh();
    }

    private int _previewSeq;

    private async Task PreviewPathAsync(string path)
    {
        if (_pak == null) return;
        var seq = ++_previewSeq;
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
                    ActionStatus.Text = $"模型已加载 | {Viewport.StatusInfo} | 贴图 {viewportMesh.Textures.Length} 张 | {viewportMesh.VisconInfo} | 左键旋转 右键平移 滚轮缩放";
                    break;
                }
                case "motlist":
                {
                    if (_lastMeshPath == null) { ActionStatus.Text = "motlist 需要先选中一个 mesh 作为骨架载体"; return; }
                    var meshPath = _lastMeshPath;
                    _currentMotlistPath = path;
                    var (viewportMesh, clip, motionNames) = await Task.Run(() =>
                    {
                        using var meshMs = _pak.ReadFile(meshPath);
                        var vm = ViewportDataLoader.LoadMesh(meshMs, meshPath, 1, OpenResource);
                        using var motMs1 = _pak.ReadFile(path);
                        var names = ViewportDataLoader.ListMotionNames(motMs1, path);
                        using var motMs2 = _pak.ReadFile(path);
                        var c = ViewportDataLoader.LoadAnimation(motMs2, path, 0, Array.ConvertAll(vm.Bones, b => b.Name));
                        return (vm, c, names);
                    });
                    if (IsStale()) return;
                    ShowViewport();
                    Viewport.SetMesh(viewportMesh);
                    Viewport.SetAnimation(clip);
                    MotionCombo.ItemsSource = motionNames;
                    MotionCombo.SelectedIndex = 0;
                    MotionCombo.IsVisible = motionNames.Count > 1;
                    ShowTimeline(clip.Duration);
                    ActionStatus.Text = $"动画播放中: {clip.Name}（时长 {clip.Duration:F1}s，{clip.Tracks.Count} 轨道）| 左键旋转 右键平移 滚轮缩放";
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
    }

    private void OnPlaybackClicked(object? sender, RoutedEventArgs e)
    {
        if (!Viewport.HasAnimation) { ActionStatus.Text = "当前没有已加载的动画"; return; }
        Viewport.TogglePlayback();
    }

    // ---- viewport toolbar: skeleton / motion / timeline ----

    private string? _currentMotlistPath;
    private bool _suppressSlider;
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

    /// <summary>TEX_DEBUG 3D render-mode switch (0 normal / 1 solid / 2 raw texture / 3 UV) — live re-render.</summary>
    private void OnTexDebugModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        // fires during InitializeComponent (SelectedIndex=0 in XAML) before Viewport is wired — must guard
        if (Viewport is null || TexDebugCombo is null) return;
        Viewport.TexDebugMode = Math.Max(0, TexDebugCombo.SelectedIndex);
        Viewport.Refresh();
    }

    private void ShowTimeline(float duration)
    {
        TimelineSlider.Maximum = Math.Max(0.01, duration);
        TimelineSlider.Value = 0;
        TimelineSlider.IsVisible = true;
        TimelineText.Text = $"0.0 / {duration:F1}s";
        _playheadTimer ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
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
        if (_pak == null || _currentMotlistPath == null || MotionCombo.SelectedIndex < 0) return;
        var idx = MotionCombo.SelectedIndex;
        var motlistPath = _currentMotlistPath;
        try
        {
            var clip = await Task.Run(() =>
            {
                using var motMs = _pak.ReadFile(motlistPath);
                return ViewportDataLoader.LoadAnimation(motMs, motlistPath, idx, Viewport.MeshBoneNames);
            });
            Viewport.SetAnimation(clip);
            ShowTimeline(clip.Duration);
            ActionStatus.Text = $"动画播放中: {clip.Name}（时长 {clip.Duration:F1}s）";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "motion 加载失败: " + ex.Message;
        }
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
        if (_selectedPath == null) { ActionStatus.Text = "先选择文件"; return; }
        await ExportPathAsync(_selectedPath);
    }

    private async void OnExportAnimClicked(object? sender, RoutedEventArgs e)
    {
        if (_pak == null || _selectedPath == null) { ActionStatus.Text = "先加载并选择文件"; return; }
        if (KindOf(_selectedPath) != "motlist") { ActionStatus.Text = "请先选中一个 .motlist 文件"; return; }
        if (_lastMeshPath == null) { ActionStatus.Text = "请先选中过该角色的 .mesh（用于提供骨架）"; return; }

        var motlistPath = _selectedPath;
        var meshPath = _lastMeshPath;
        var outDir = OutDirBox.Text.Trim();
        ActionStatus.Text = "动画导出中…";
        try
        {
            var result = await Task.Run(() =>
            {
                using var meshMs = _pak.ReadFile(meshPath);
                using var motMs = _pak.ReadFile(motlistPath);
                var outPath = Path.Combine(outDir,
                    Path.GetFileNameWithoutExtension(motlistPath) + ".glb");
                new AnimationService().ConvertToGlbWithAnimation(meshMs, meshPath, motMs, motlistPath, outPath, 0);
                return outPath;
            });
            ActionStatus.Text = $"已导出: {result}";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "导出失败: " + ex.Message;
        }
    }

    private async Task ExportPathAsync(string path)
    {
        if (_pak == null) { ActionStatus.Text = "先加载 PAK"; return; }
        var kind = KindOf(path);
        var outDir = OutDirBox.Text.Trim();
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

    private static string? PathFromSender(object? sender)
    {
        if (sender is not MenuItem mi) return null;
        return mi.DataContext switch
        {
            FileTreeNode node => node.FilePath,
            EntryRow row => row.Path,
            _ => null,
        };
    }

    private async void OnCtxAddModel(object? sender, RoutedEventArgs e)
    {
        var path = PathFromSender(sender);
        if (_pak == null || path == null) return;
        if (KindOf(path) != "mesh") { ActionStatus.Text = "Additional model 需要选 .mesh 文件"; return; }
        ActionStatus.Text = "叠加模型加载中…";
        try
        {
            var vm = await Task.Run(() =>
            {
                using var ms = _pak.ReadFile(path);
                return ViewportDataLoader.LoadMesh(ms, path, 1, OpenResource);
            });
            ShowViewport();
            if (Viewport.HasMesh)
            {
                Viewport.AddMesh(vm, path.Split('/')[^1]);
                ActionStatus.Text = $"已叠加模型部件（共 {Viewport.ExtraModelCount + 1} 个）: {path.Split('/')[^1]}";
            }
            else
            {
                Viewport.SetMesh(vm);
                ActionStatus.Text = $"模型已加载 | {Viewport.StatusInfo}";
            }
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "叠加失败: " + ex.Message;
        }
    }

    private async void OnCtxAddAnim(object? sender, RoutedEventArgs e)
    {
        var path = PathFromSender(sender);
        if (_pak == null || path == null) return;
        if (KindOf(path) != "motlist") { ActionStatus.Text = "Additional Animations 需要选 .motlist 文件"; return; }
        if (!Viewport.HasMesh) { ActionStatus.Text = "请先在视口加载一个模型（点选 .mesh）"; return; }
        ActionStatus.Text = "动画加载中…";
        _currentMotlistPath = path;
        try
        {
            var (clip, motionNames) = await Task.Run(() =>
            {
                using var motMs1 = _pak.ReadFile(path);
                var names = ViewportDataLoader.ListMotionNames(motMs1, path);
                using var motMs2 = _pak.ReadFile(path);
                var c = ViewportDataLoader.LoadAnimation(motMs2, path, 0, Viewport.MeshBoneNames);
                return (c, names);
            });
            Viewport.SetAnimation(clip);
            MotionCombo.ItemsSource = motionNames;
            MotionCombo.SelectedIndex = 0;
            MotionCombo.IsVisible = motionNames.Count > 1;
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
        var outDir = OutDirBox.Text.Trim();
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
