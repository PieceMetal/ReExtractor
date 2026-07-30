using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ReExtractor.Gui;

public partial class EnvironmentWindow : Window
{
    public EnvironmentWindow() : this(AppSettingsService.Load()) { }

    public EnvironmentWindow(AppSettings settings)
    {
        InitializeComponent();
        Refresh(settings);
    }

    private void Refresh(AppSettings settings)
    {
        var blender = settings.BlenderPath?.Trim() ?? "";
        var hasBlender = File.Exists(blender);
        BlenderStatusText.Text = hasBlender
            ? $"已配置：{blender}"
            : "未配置。FBX 导出需要安装 Blender，并在设置里选择 blender.exe。";
        BlenderStatusText.Foreground = hasBlender ? Brushes.LightGreen : Brushes.Orange;
        OutputStatusText.Text = string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? AppPaths.OutputDirectory
            : settings.OutputDirectory;
    }

    private void OnOpenOutputClicked(object? sender, RoutedEventArgs e)
    {
        var settings = AppSettingsService.Load();
        var outDir = string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? AppPaths.OutputDirectory
            : settings.OutputDirectory;
        Directory.CreateDirectory(outDir);
        Process.Start("explorer.exe", $"\"{outDir}\"");
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e) => Refresh(AppSettingsService.Load());
    private void OnDownloadClicked(object? sender, RoutedEventArgs e) => Close("download");
    private void OnSettingsClicked(object? sender, RoutedEventArgs e) => Close("settings");
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close("close");
}