using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ReExtractor.Gui;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = typeof(AboutWindow).Assembly.GetName().Version;
        VersionText.Text = $"版本 {version?.ToString(3) ?? "1.0.0"}";
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
