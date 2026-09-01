using System;
using System.IO;
using System.Reflection;

namespace ReExtractor.Gui;

/// <summary>
/// Keeps the portable release archive to one executable. GDeflateNet resolves
/// its native decoder from the application directory, so materialize the
/// embedded DLL before any texture decoder can be created.
/// </summary>
internal static class GDeflateNativeBootstrap
{
    private const string FileName = "libGDeflate.dll";

    public static void EnsureAvailable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var target = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(target)) return;

        var assembly = typeof(GDeflateNativeBootstrap).Assembly;
        var resourceName = Array.Find(assembly.GetManifestResourceNames(),
            name => name.EndsWith("EmbeddedNative.libGDeflate.dll", StringComparison.Ordinal));
        if (resourceName == null)
            throw new InvalidOperationException("未找到内置 GDeflate 解码库");

        using var input = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("无法读取内置 GDeflate 解码库");
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
        input.CopyTo(output);
    }
}
