using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ReExtractor.Gui;

public partial class SettingsWindow : Window
{
    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        BlenderPathBox.Text = settings.BlenderPath;
        OutputDirectoryBox.Text = settings.OutputDirectory;
    }

    private async void OnSelectBlenderClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Blender 程序",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("程序") { Patterns = ["*.exe"] }],
        });
        if (files.Count > 0) BlenderPathBox.Text = files[0].Path.LocalPath;
    }

    private async void OnSelectOutputClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择导出文件夹",
            AllowMultiple = false,
        });
        if (folders.Count > 0) OutputDirectoryBox.Text = folders[0].Path.LocalPath;
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => Close(new AppSettings
    {
        BlenderPath = BlenderPathBox.Text?.Trim() ?? "",
        OutputDirectory = OutputDirectoryBox.Text?.Trim() ?? "",
    });

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close((AppSettings?)null);
}
