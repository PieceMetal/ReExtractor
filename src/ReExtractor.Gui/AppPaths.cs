using System;
using System.IO;

namespace ReExtractor.Gui;

public static class AppPaths
{
    public static string WorkDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "ReExtractor-tools");
    public static string DataDirectory => Path.Combine(WorkDirectory, "data");
    public static string FileListsDirectory => Path.Combine(WorkDirectory, "filelists");
    public static string LogsDirectory => Path.Combine(WorkDirectory, "logs");
    public static string ExecutableDirectory { get; } = AppContext.BaseDirectory;
    public static string LegacyOutputDirectory => Path.Combine(WorkDirectory, "output");
    public static string OutputDirectory => Path.Combine(ExecutableDirectory, "output");
    public static string TempDirectory => Path.Combine(WorkDirectory, "temp");
    public static string ToolsDirectory => Path.Combine(WorkDirectory, "tools");
}
