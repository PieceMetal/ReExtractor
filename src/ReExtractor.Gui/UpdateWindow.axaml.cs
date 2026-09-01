using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ReExtractor.Gui;

public partial class UpdateWindow : Window
{
    private readonly UpdateService _service;
    private readonly UpdateRelease _release;
    public PreparedUpdate? PreparedUpdate { get; private set; }

    public UpdateWindow() : this(new UpdateService(),
        new UpdateRelease(new Version(0, 0), "", "", "", "", 0, "")) { }

    public UpdateWindow(UpdateService service, UpdateRelease release)
    {
        InitializeComponent();
        _service = service;
        _release = release;
        TitleText.Text = string.IsNullOrWhiteSpace(release.Name) ? "发现新版本" : release.Name;
        VersionText.Text = $"当前 {_service.CurrentVersion.ToString(3)}  →  新版本 {release.Version.ToString(3)}";
        NotesText.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? "该版本暂无发布说明。"
            : release.Notes;
    }

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        DownloadProgress.IsVisible = true;
        StatusText.Text = "正在下载更新包…";
        try
        {
            var progress = new Progress<double>(value =>
            {
                DownloadProgress.Value = value * 100;
                StatusText.Text = $"正在下载更新包… {value:P0}";
            });
            PreparedUpdate = await _service.DownloadAsync(_release, progress);
            StatusText.Text = "下载和校验完成，正在重启…";
            Close(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "更新失败：" + ex.Message;
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Close(false);
}
