namespace ReExtractor.Core;

public static class TextureExportService
{
    public static string TextureExportPath(string outputRoot, string nativePath)
    {
        var normalized = nativePath.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
            throw new InvalidDataException($"无效资源路径: {nativePath}");

        var invalid = Path.GetInvalidFileNameChars();
        static string SafeSegment(string value, char[] invalidChars) =>
            new(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());

        var safeParts = parts.Select(part => SafeSegment(part, invalid)).ToArray();
        var fileName = safeParts[^1];
        var texMarker = fileName.LastIndexOf(".tex.", StringComparison.OrdinalIgnoreCase);
        if (texMarker < 0)
            throw new InvalidDataException($"不是可导出的 TEX 资源: {nativePath}");
        safeParts[^1] = fileName[..texMarker] + ".png";

        var root = Path.GetFullPath(Path.Combine(outputRoot, "textures"));
        var result = Path.GetFullPath(Path.Combine(new[] { root }.Concat(safeParts).ToArray()));
        if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"资源路径超出导出目录: {nativePath}");
        return result;
    }

    public static (int exported, List<string> failures) ExportTextureFiles(
        PakService pak, IEnumerable<string> paths, string outputRoot, Action<int, int>? progress = null,
        Action<string>? log = null)
    {
        var textures = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var exported = 0;
        var failures = new List<string>();
        var directory = Path.GetFullPath(Path.Combine(outputRoot, "textures"));
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, $"export-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        using var report = new StreamWriter(reportPath, false, new System.Text.UTF8Encoding(true)) { AutoFlush = true };
        void Report(string message) { report.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"); log?.Invoke(message); }
        Report($"关联贴图输出目录：{directory}；待导出 {textures.Length} 张；记录：{reportPath}");
        if (textures.Length == 0) Report("未找到可导出的关联贴图，请检查模型对应的 MDF 及其贴图引用。");
        for (var index = 0; index < textures.Length; index++)
        {
            var path = textures[index];
            try
            {
                using var stream = pak.ReadPreferredTextureFile(path, out var resolvedPath);
                var outputPath = TextureExportPath(outputRoot, path);
                new TexService().ConvertToPng(stream, resolvedPath, outputPath);
                exported++;
                Report($"贴图成功 {index + 1}/{textures.Length}：{resolvedPath} → {outputPath}");
            }
            catch (Exception ex)
            {
                failures.Add($"{path}：{ex.Message}");
                Report($"贴图失败 {index + 1}/{textures.Length}：{path}：{ex.Message}");
            }
            progress?.Invoke(index + 1, textures.Length);
        }
        Report($"关联贴图导出结束：成功 {exported}/{textures.Length}，失败 {failures.Count}");
        return (exported, failures);
    }

}
