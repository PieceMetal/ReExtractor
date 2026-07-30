using System;
using System.IO;
using System.Text.Json;

namespace ReExtractor.Gui;

public sealed class AppSettings
{
    public string BlenderPath { get; set; } = "";
    public string OutputDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "output");
    public string LastGameDirectory { get; set; } = "";
    public string LastListPath { get; set; } = "";
}

public static class AppSettingsService
{
    private static readonly string SettingsDirectory = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? CreateDefault();
        }
        catch { }
        return CreateDefault();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static AppSettings CreateDefault()
    {
        var settings = new AppSettings();
        var knownBlender = @"E:\Steam\steamapps\common\Blender\blender.exe";
        if (File.Exists(knownBlender)) settings.BlenderPath = knownBlender;
        return settings;
    }
}
