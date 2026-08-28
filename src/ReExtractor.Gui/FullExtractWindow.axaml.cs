using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ReExtractor.Gui;

public sealed record FullExtractRequest(string ListFile, string PakDirectory,
    string OutputDirectory, string ListTitle, int PakCount);

public partial class FullExtractWindow : Window
{
    private IReadOnlyList<ManagedFileList> _lists;
    private int _pakCount;
    private readonly Func<ManagedFileList, Task<string?>>? _locateGameDirectory;
    private bool _initializing = true;

    public FullExtractWindow() : this([], null, "", "") { }

    public FullExtractWindow(IReadOnlyList<ManagedFileList> lists, ManagedFileList? selected,
        string sourceDirectory, string outputDirectory,
        Func<ManagedFileList, Task<string?>>? locateGameDirectory = null)
    {
        InitializeComponent();
        _locateGameDirectory = locateGameDirectory;
        _lists = lists;
        ListCombo.ItemsSource = lists;
        ListCombo.SelectedItem = selected == null
            ? lists.FirstOrDefault()
            : lists.FirstOrDefault(item => item.Identifier.Equals(selected.Identifier,
                StringComparison.OrdinalIgnoreCase)) ?? lists.FirstOrDefault();
        PakDirectoryBox.Text = Directory.Exists(sourceDirectory) ? sourceDirectory : "";
        OutputDirectoryBox.Text = outputDirectory;
        _initializing = false;
        RefreshSummary();
        Opened += async (_, _) => await AutoLocateGameDirectoryAsync();
    }

    private async void OnChoosePakDirectory(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 PAK 所在目录",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        PakDirectoryBox.Text = folders[0].Path.LocalPath;
        RefreshSummary();
    }

    private async void OnChooseOutputDirectory(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择全部解包的输出目录",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        OutputDirectoryBox.Text = folders[0].Path.LocalPath;
        RefreshSummary();
    }

    private async void OnListChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshSummary();
        if (!_initializing) await AutoLocateGameDirectoryAsync();
    }

    private async Task AutoLocateGameDirectoryAsync()
    {
        if (_locateGameDirectory == null || ListCombo.SelectedItem is not ManagedFileList list) return;
        var path = await _locateGameDirectory(list);
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            PakDirectoryBox.Text = path;
        else
            PakDirectoryBox.Text = "";
        RefreshSummary();
    }

    private async void OnManageLists(object? sender, RoutedEventArgs e)
    {
        var currentPath = (ListCombo.SelectedItem as ManagedFileList)?.FilePath;
        var selectedPath = await new FileListManagerWindow(currentPath).ShowDialog<string?>(this);
        _lists = new FileListManagerService().GetLocalLists();
        ListCombo.ItemsSource = _lists;
        ListCombo.SelectedItem = !string.IsNullOrWhiteSpace(selectedPath)
            ? _lists.FirstOrDefault(item => item.FilePath.Equals(selectedPath,
                StringComparison.OrdinalIgnoreCase)) ?? _lists.FirstOrDefault()
            : _lists.FirstOrDefault(item => item.FilePath.Equals(currentPath,
                StringComparison.OrdinalIgnoreCase)) ?? _lists.FirstOrDefault();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        if (SummaryText == null) return;
        var source = PakDirectoryBox.Text?.Trim() ?? "";
        _pakCount = Directory.Exists(source) ? FindPaks(source).Count() : 0;
        var list = ListCombo.SelectedItem as ManagedFileList;
        SummaryText.Text = list == null
            ? "暂无可用的路径列表，请点击右侧“管理…”下载或导入"
            : !Directory.Exists(source)
            ? "请选择有效的 PAK 所在目录"
            : _pakCount == 0
                ? "所选目录及四层子目录内没有找到 PAK 文件"
                : $"路径列表：{list?.Title ?? "未选择"}\n检测到 PAK：{_pakCount} 个\n输出：{OutputDirectoryBox.Text}";
        StartButton.IsEnabled = list != null && _pakCount > 0 &&
                                !string.IsNullOrWhiteSpace(OutputDirectoryBox.Text);
    }

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (ListCombo.SelectedItem is not ManagedFileList list || _pakCount == 0) return;
        Close(new FullExtractRequest(list.FilePath, PakDirectoryBox.Text!,
            OutputDirectoryBox.Text!, list.Title, _pakCount));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private static IEnumerable<string> FindPaks(string root) => FindPaks(root, 4);
    private static IEnumerable<string> FindPaks(string root, int depth)
    {
        if (depth < 0 || !Directory.Exists(root)) yield break;
        IEnumerable<string> files;
        IEnumerable<string> folders;
        try
        {
            files = Directory.EnumerateFiles(root, "*.pak", SearchOption.TopDirectoryOnly).ToArray();
            folders = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch { yield break; }
        foreach (var file in files) yield return file;
        foreach (var folder in folders)
        foreach (var file in FindPaks(folder, depth - 1)) yield return file;
    }
}
