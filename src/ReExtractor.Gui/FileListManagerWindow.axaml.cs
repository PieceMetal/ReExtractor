using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace ReExtractor.Gui;

public partial class FileListManagerWindow : Window
{
    private sealed class RemoteRow
    {
        public required RemoteFileListInfo Info { get; init; }
        public required string Status { get; init; }
        public string Identifier => Info.Identifier;
        public string SourceName => Info.SourceName;
        public string UpdateTimeText => Info.UpdateTimeText;
        public string SizeText => Info.SizeText;
    }

    private readonly FileListManagerService _service = new();
    private readonly string? _currentPath;
    private FileListManifest? _manifest;

    public FileListManagerWindow() : this(null)
    {
    }

    public FileListManagerWindow(string? currentPath)
    {
        InitializeComponent();
        _currentPath = currentPath;
        RemoteGrid.SelectionChanged += (_, _) => UpdateRemoteSelection();
        Opened += async (_, _) =>
        {
            RefreshLocal();
            await FetchRemoteAsync();
        };
    }

    private void RefreshLocal(string? selectPath = null)
    {
        var lists = _service.GetLocalLists();
        LocalGrid.ItemsSource = lists;
        LocalCountText.Text = $"{lists.Count} 个";
        var wanted = selectPath ?? _currentPath;
        LocalGrid.SelectedItem = lists.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(wanted) &&
            Path.GetFullPath(item.FilePath).Equals(Path.GetFullPath(wanted), StringComparison.OrdinalIgnoreCase));
        RefreshRemoteRows();
    }

    private void RefreshRemoteRows()
    {
        if (_manifest == null) return;
        var local = _service.GetLocalLists().ToDictionary(item => item.Identifier, StringComparer.OrdinalIgnoreCase);
        RemoteGrid.ItemsSource = _manifest.Files
            .OrderBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(info => new RemoteRow { Info = info, Status = GetRemoteStatus(info, local) })
            .ToArray();
        RemoteCountText.Text = $"{_manifest.Files.Length} 个可用";
        UpdateRemoteSelection();
    }

    private static string GetRemoteStatus(RemoteFileListInfo info,
        System.Collections.Generic.IReadOnlyDictionary<string, ManagedFileList> local)
    {
        if (!local.TryGetValue(info.Identifier, out var installed)) return "可下载";
        if (installed.Source == "本地导入") return "本地同名";
        return info.UpdateTime > installed.UpdateTime ? "可更新" : "已是最新";
    }

    private async Task FetchRemoteAsync()
    {
        FetchRemoteButton.IsEnabled = false;
        ManagerStatusText.Text = "正在获取在线列表…";
        try
        {
            _manifest = await _service.FetchManifestAsync();
            RefreshRemoteRows();
            ManagerStatusText.Text = "在线列表已更新";
        }
        catch (Exception ex)
        {
            ManagerStatusText.Text = "获取在线列表失败：" + ex.Message;
        }
        finally
        {
            FetchRemoteButton.IsEnabled = true;
        }
    }

    private void UpdateRemoteSelection()
    {
        if (RemoteGrid.SelectedItem is not RemoteRow row)
        {
            RemoteDescriptionText.Text = "选择在线列表查看说明";
            DownloadButton.IsEnabled = false;
            return;
        }
        RemoteDescriptionText.Text = row.Info.SourceName == "Ekey/REE.PAK.Tool"
            ? "来源：Ekey/REE.PAK.Tool · PC 精选列表"
            : string.IsNullOrWhiteSpace(row.Info.Description)
                ? $"来源：{row.Info.SourceName}"
                : row.Info.Description;
        DownloadButton.Content = row.Status == "可更新" ? "更新选中列表" : "下载选中列表";
        DownloadButton.IsEnabled = row.Status is "可下载" or "可更新";
    }

    private async void OnImportClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入资源路径列表",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("RE Engine 路径列表") { Patterns = ["*.list"] }],
        });
        if (files.Count == 0) return;
        try
        {
            var path = await _service.ImportAsync(files[0].Path.LocalPath);
            RefreshLocal(path);
            ManagerStatusText.Text = "列表已导入";
        }
        catch (Exception ex)
        {
            ManagerStatusText.Text = "导入失败：" + ex.Message;
        }
    }

    private void OnOpenLibraryClicked(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(_service.LibraryDirectory) { UseShellExecute = true });
    }

    private void OnOpenFullRepositoryClicked(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://github.com/Ekey/REE.PAK.Tool/tree/main/Projects")
        {
            UseShellExecute = true,
        });
    }

    private void OnRefreshLocalClicked(object? sender, RoutedEventArgs e)
    {
        RefreshLocal((LocalGrid.SelectedItem as ManagedFileList)?.FilePath);
        ManagerStatusText.Text = "本地列表已刷新";
    }

    private async void OnDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (LocalGrid.SelectedItem is not ManagedFileList item)
        {
            ManagerStatusText.Text = "请先选择要删除的列表";
            return;
        }
        if (!await ConfirmDeleteAsync(item.Title)) return;
        try
        {
            _service.Delete(item);
            RefreshLocal();
            ManagerStatusText.Text = "列表已删除";
        }
        catch (Exception ex)
        {
            ManagerStatusText.Text = "删除失败：" + ex.Message;
        }
    }

    private async Task<bool> ConfirmDeleteAsync(string title)
    {
        var dialog = new Window
        {
            Title = "确认删除",
            Width = 420,
            Height = 175,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var cancel = new Button { Content = "取消", Padding = new Avalonia.Thickness(14, 6) };
        var remove = new Button { Content = "删除", Padding = new Avalonia.Thickness(14, 6), Margin = new Avalonia.Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => dialog.Close(false);
        remove.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Children =
            {
                new TextBlock { Text = $"确定删除“{title}”吗？", FontSize = 16, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "删除后需要重新导入或下载。", Foreground = Avalonia.Media.Brushes.Gray, Margin = new Avalonia.Thickness(0, 6, 0, 16) },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, remove } },
            },
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private async void OnFetchRemoteClicked(object? sender, RoutedEventArgs e) => await FetchRemoteAsync();

    private async void OnDownloadClicked(object? sender, RoutedEventArgs e)
    {
        if (_manifest == null || RemoteGrid.SelectedItem is not RemoteRow row) return;
        DownloadButton.IsEnabled = false;
        ManagerStatusText.Text = $"正在下载 {row.Identifier}…";
        try
        {
            var path = await _service.DownloadAsync(_manifest, row.Info);
            RefreshLocal(path);
            ManagerStatusText.Text = $"{row.Identifier} 已下载并校验";
        }
        catch (Exception ex)
        {
            ManagerStatusText.Text = "下载失败：" + ex.Message;
        }
        finally
        {
            UpdateRemoteSelection();
        }
    }

    private void OnUseSelectedClicked(object? sender, RoutedEventArgs e)
    {
        if (LocalGrid.SelectedItem is not ManagedFileList item)
        {
            ManagerStatusText.Text = "请先选择一个已安装列表";
            return;
        }
        Close(item.FilePath);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close((string?)null);
}
