using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ReExtractor.Gui;

/// <summary>
/// A manually saved model assembly. Presets are grouped by game key and only
/// store the resource paths needed to rebuild the merge queue.
/// </summary>
public sealed class ModelAssemblyPreset
{
    public int Version { get; set; } = 1;
    public string GameKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
    public List<string> MeshPaths { get; set; } = new();
}

public sealed class ModelAssemblyPresetInfo
{
    public required string GameKey { get; init; }
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public int MeshCount { get; init; }
    public DateTimeOffset SavedAt { get; init; }
    public string Display => $"{Name}（{MeshCount} 个部件）";
}

public sealed class ModelAssemblyPresetService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string RootDirectory { get; } = AppPaths.PresetsDirectory;

    public ModelAssemblyPresetService()
    {
        Directory.CreateDirectory(RootDirectory);
        MigrateLegacyPresets();
    }

    public IReadOnlyList<ModelAssemblyPresetInfo> List(string gameKey)
    {
        var directory = GetGameDirectory(gameKey);
        if (!Directory.Exists(directory)) return Array.Empty<ModelAssemblyPresetInfo>();

        var result = new List<ModelAssemblyPresetInfo>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var preset = JsonSerializer.Deserialize<ModelAssemblyPreset>(
                    File.ReadAllText(file), JsonOptions);
                if (preset == null || string.IsNullOrWhiteSpace(preset.Name)) continue;
                result.Add(new ModelAssemblyPresetInfo
                {
                    GameKey = gameKey,
                    Name = preset.Name,
                    FilePath = file,
                    MeshCount = preset.MeshPaths?.Count ?? 0,
                    SavedAt = preset.SavedAt,
                });
            }
            catch
            {
                // Ignore a damaged/hand-edited preset instead of breaking the UI.
            }
        }

        return result
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.SavedAt)
            .ToArray();
    }

    public string Save(string gameKey, string name, string sourceFolder, IEnumerable<string> meshPaths)
    {
        if (string.IsNullOrWhiteSpace(gameKey)) throw new ArgumentException("游戏分类不能为空", nameof(gameKey));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("预设名称不能为空", nameof(name));

        var paths = meshPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0) throw new ArgumentException("合并队列为空，无法保存预设", nameof(meshPaths));

        var directory = GetGameDirectory(gameKey);
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, SanitizeFileName(name) + ".json");
        var preset = new ModelAssemblyPreset
        {
            GameKey = gameKey,
            Name = name.Trim(),
            SourceFolder = sourceFolder ?? "",
            SavedAt = DateTimeOffset.Now,
            MeshPaths = paths,
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(preset, JsonOptions));
        return filePath;
    }

    public ModelAssemblyPreset Load(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("找不到预设文件", filePath);
        var preset = JsonSerializer.Deserialize<ModelAssemblyPreset>(
            File.ReadAllText(filePath), JsonOptions);
        if (preset == null || string.IsNullOrWhiteSpace(preset.Name))
            throw new InvalidDataException("预设文件格式无效");
        preset.MeshPaths ??= new List<string>();
        return preset;
    }

    public void Delete(string filePath)
    {
        if (File.Exists(filePath)) File.Delete(filePath);
    }

    public string GetGameDirectory(string gameKey) =>
        Path.Combine(RootDirectory, SanitizeFileName(gameKey), "model-assemblies");

    private void MigrateLegacyPresets()
    {
        var legacyRoots = new[]
        {
            Path.Combine(AppPaths.PresetsDirectory, "model-assemblies"),
            Path.Combine(AppPaths.WorkDirectory, "presets", "model-assemblies"),
        };
        foreach (var legacyRoot in legacyRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(legacyRoot)) continue;
            foreach (var file in Directory.EnumerateFiles(legacyRoot, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var preset = JsonSerializer.Deserialize<ModelAssemblyPreset>(
                        File.ReadAllText(file), JsonOptions);
                    var gameKey = !string.IsNullOrWhiteSpace(preset?.GameKey)
                        ? preset.GameKey
                        : new DirectoryInfo(Path.GetDirectoryName(file)!).Name;
                    var destinationDirectory = GetGameDirectory(gameKey);
                    Directory.CreateDirectory(destinationDirectory);
                    var destination = Path.Combine(destinationDirectory, Path.GetFileName(file));
                    if (!File.Exists(destination)) File.Copy(file, destination);
                }
                catch
                {
                    // Keep damaged legacy files untouched instead of blocking startup.
                }
            }
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "未分类游戏" : result;
    }
}
