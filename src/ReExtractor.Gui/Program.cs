using System;
using Avalonia;

namespace ReExtractor.Gui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new Win32PlatformOptions
            {
                // GPU composition (ANGLE/D3D) instead of CPU-skia — kills the ~70ms full-window composite per frame
                RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software },
            })
            .LogToTrace();
}
