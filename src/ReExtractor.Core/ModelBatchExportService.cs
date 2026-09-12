namespace ReExtractor.Core;

public sealed record ModelBatchExportResult(
    IReadOnlyList<string> ExportedSources, IReadOnlyList<string> OutputFiles, IReadOnlyList<string> Failures);

public static class ModelBatchExportService
{
    public static ModelBatchExportResult Export(PakService pak, IReadOnlyList<string> paths,
        string outputRoot, string temporaryRoot, Action<string, string> convertToFbx,
        Action<int, int>? progress = null)
    {
        var exported = new List<string>();
        var outputs = new List<string>();
        var failures = new List<string>();
        var modelsRoot = Path.GetFullPath(Path.Combine(outputRoot, "models"));
        var sources = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Stream? Open(string path)
        {
            try { return pak.ReadFile(path); }
            catch (FileNotFoundException) { return null; }
        }
        for (var index = 0; index < sources.Length; index++)
        {
            var path = sources[index];
            string? workDirectory = null;
            try
            {
                // Keep source directories and the mesh version so equally named models
                // from different folders/versions do not overwrite each other.
                var relative = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(p => p is ".." or "."))
                    throw new InvalidDataException("模型资源路径无效");
                var output = Path.GetFullPath(Path.Combine(modelsRoot, relative + ".fbx"));
                if (!output.StartsWith(modelsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("模型导出路径超出输出目录");
                using var stream = pak.ReadFile(path);
                var mesh = ViewportDataLoader.LoadMesh(stream, path, 1, Open, loadTextures: true);
                if (mesh.FaceCount == 0) throw new InvalidDataException("模型没有可导出的面");
                workDirectory = Path.Combine(temporaryRoot, "model_batch_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workDirectory);
                new ViewportExportService().ConvertToGlb(mesh,
                    mesh.Groups.Where(group => group.DefaultVisible).Select(group => group.Key).ToHashSet(),
                    Path.Combine(workDirectory, "model.glb"));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                convertToFbx(workDirectory, output);
                if (!File.Exists(output) || new FileInfo(output).Length == 0)
                    throw new InvalidDataException("转换程序没有生成 FBX 文件");
                exported.Add(path);
                outputs.Add(output);
                // Preserve every MDF map beside batch results too, including packed
                // normal/mask maps that cannot be represented by FBX materials.
                var references = ViewportDataLoader.ListReferencedTexturePaths(path, Open);
                var textures = TextureExportService.ExportTextureFiles(pak, references, outputRoot);
                failures.AddRange(textures.failures.Select(failure => $"{path} 关联贴图: {failure}"));
            }
            catch (Exception exception) { failures.Add($"{path}: {exception.Message}"); }
            finally
            {
                if (workDirectory != null)
                {
                    try { Directory.Delete(workDirectory, recursive: true); }
                    catch { /* A retained temporary file does not invalidate an exported FBX. */ }
                }
                progress?.Invoke(index + 1, sources.Length);
            }
        }
        return new(exported, outputs, failures);
    }
}
