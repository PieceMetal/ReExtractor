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
        PakService pak, IEnumerable<string> paths, string outputRoot, Action<int, int>? progress = null)
    {
        var textures = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var exported = 0;
        var failures = new List<string>();
        for (var index = 0; index < textures.Length; index++)
        {
            var path = textures[index];
            try
            {
                using var stream = pak.ReadPreferredTextureFile(path, out var resolvedPath);
                new TexService().ConvertToPng(stream, resolvedPath, TextureExportPath(outputRoot, path));
                exported++;
            }
            catch (Exception ex)
            {
                failures.Add($"{path}：{ex.Message}");
            }
            progress?.Invoke(index + 1, textures.Length);
        }
        return (exported, failures);
    }

}
