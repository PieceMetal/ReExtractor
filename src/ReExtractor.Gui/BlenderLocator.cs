using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace ReExtractor.Gui;

/// <summary>
/// 在常见安装位置中自动查找 blender.exe。
/// 给别人试用时工具不再依赖本机硬编码路径，装了 Blender 就能自动填好。
/// 搜索顺序即优先级：Steam 库（含多库）→ Program Files 常见布局 → Microsoft Store → PATH。
/// </summary>
public static class BlenderLocator
{
    public static string? Detect()
    {
        var candidates = new List<string>();

        // 1. Steam 库（主库 + libraryfolders.vdf 里的额外库）
        candidates.AddRange(SteamCandidates());

        // 2. Program Files 常见布局（含版本子目录）
        candidates.AddRange(ProgramFilesCandidates());

        // 3. Microsoft Store 转发 stub
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            candidates.Add(Path.Combine(local, "Microsoft", "WindowsApps", "blender.exe"));

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // 4. PATH 环境变量
        return FindInPath();
    }

    private static IEnumerable<string> SteamCandidates()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        string? steamPath = null;
        try
        {
            steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        }
        catch { }
        if (string.IsNullOrEmpty(steamPath)) yield break;

        // 主库
        yield return Path.Combine(steamPath, "steamapps", "common", "Blender", "blender.exe");

        // 额外库（libraryfolders.vdf 中的 "path" 字段）
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        string[] lines;
        try { lines = File.ReadAllLines(vdf); }
        catch { yield break; }
        foreach (var line in lines)
        {
            var idx = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var rest = line.Substring(idx + 6).Trim();
            if (!rest.StartsWith("\"")) continue;
            rest = rest.Substring(1);
            var end = rest.IndexOf('"');
            if (end <= 0) continue;
            var libPath = rest.Substring(0, end);
            if (!string.IsNullOrEmpty(libPath))
                yield return Path.Combine(libPath, "steamapps", "common", "Blender", "blender.exe");
        }
    }

    private static IEnumerable<string> ProgramFilesCandidates()
    {
        var roots = new List<string?>();
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)); } catch { }
        roots.Add(@"C:\Program Files");
        roots.Add(@"C:\Program Files (x86)");

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parents = new[]
            {
                Path.Combine(root!, "Blender Foundation"),
                Path.Combine(root!, "Programs", "Blender Foundation"),
                Path.Combine(root!, "Programs", "Blender Foundation", "Blender"),
            };
            foreach (var parent in parents.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(parent)) continue;
                IEnumerable<string> dirs;
                try { dirs = Directory.GetDirectories(parent); }
                catch { continue; }

                // Official installers use either Blender Foundation\\Blender <version>
                // or Blender Foundation\\Blender\\<version>; support both layouts.
                yield return Path.Combine(parent, "blender.exe");
                foreach (var dir in dirs)
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith("Blender", StringComparison.OrdinalIgnoreCase) ||
                        parent.EndsWith(Path.Combine("Blender Foundation", "Blender"), StringComparison.OrdinalIgnoreCase))
                        yield return Path.Combine(dir, "blender.exe");
                }
            }
        }
    }

    private static string? FindInPath()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), "blender.exe");
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}
