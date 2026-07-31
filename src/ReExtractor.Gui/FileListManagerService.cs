using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace ReExtractor.Gui;

public sealed class ManagedFileList
{
    public required string Identifier { get; init; }
    public required string Title { get; init; }
    public required string FilePath { get; init; }
    public required string Source { get; init; }
    public required string Tags { get; init; }
    public DateTimeOffset? UpdateTime { get; init; }
    public string UpdateTimeText => UpdateTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";
}

public sealed class RemoteFileListInfo
{
    [JsonPropertyName("file_name")] public string FileName { get; set; } = "";
    [JsonPropertyName("tags")] public string[] Tags { get; set; } = [];
    [JsonPropertyName("update_time")] public string UpdateTimeRaw { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("git_blob_sha")] public string GitBlobSha { get; set; } = "";
    [JsonPropertyName("direct_url")] public string DirectUrl { get; set; } = "";
    [JsonPropertyName("source_name")] public string SourceName { get; set; } = "";
    [JsonIgnore] public string Identifier => FileListManagerService.IdentifierFromFileName(FileName);
    [JsonIgnore] public DateTimeOffset? UpdateTime => DateTimeOffset.TryParse(UpdateTimeRaw, out var time) ? time : null;
    [JsonIgnore] public string UpdateTimeText => UpdateTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";
    [JsonIgnore] public string SizeText => Size >= 1 << 20 ? $"{Size / 1048576d:F1} MB" : $"{Size / 1024d:F0} KB";
}

public sealed class FileListManifest
{
    [JsonPropertyName("base_urls")] public string[] BaseUrls { get; set; } = [];
    [JsonPropertyName("files")] public RemoteFileListInfo[] Files { get; set; } = [];
}

public sealed class FileListManagerService
{
    private static readonly string[] ManifestUrls =
    [
        "https://raw.githubusercontent.com/eigeen/ree-pak-gui-update/refs/heads/main/filelist_manifest.json",
        "https://gitee.com/eigeen/ree-pak-gui-update/raw/main/filelist_manifest.json",
    ];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public string LibraryDirectory { get; } = AppPaths.FileListsDirectory;
    public string RemoteDirectory => Path.Combine(LibraryDirectory, "remote");

    public FileListManagerService()
    {
        Directory.CreateDirectory(LibraryDirectory);
        Directory.CreateDirectory(RemoteDirectory);
    }

    public IReadOnlyList<ManagedFileList> GetLocalLists()
    {
        var local = Directory.EnumerateFiles(LibraryDirectory, "*.list", SearchOption.TopDirectoryOnly)
            .Select(path => ReadManaged(path, "本地导入"));
        var remote = Directory.EnumerateFiles(RemoteDirectory, "*.list", SearchOption.TopDirectoryOnly)
            .Select(path => ReadManaged(path, "在线下载"));
        return local.Concat(remote)
            .GroupBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.Source == "本地导入" ? 0 : 1).First())
            .OrderBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string> ImportAsync(string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("找不到列表文件", sourcePath);
        var fileName = Path.GetFileName(sourcePath);
        if (!fileName.EndsWith(".list", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .list 文件");
        var destination = Path.Combine(LibraryDirectory, fileName);
        if (File.Exists(destination) && !Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var index = 2;
            do destination = Path.Combine(LibraryDirectory, $"{stem}_{index++}.list");
            while (File.Exists(destination));
        }
        if (!Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            await using (var input = File.OpenRead(sourcePath))
            await using (var output = File.Create(destination))
                await input.CopyToAsync(output);
        return destination;
    }

    public async Task<FileListManifest> FetchManifestAsync(CancellationToken cancellationToken = default)
    {
        FileListManifest? onlineManifest = null;
        Exception? lastError = null;
        foreach (var url in ManifestUrls)
        {
            try
            {
                using var response = await Http.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                onlineManifest = await JsonSerializer.DeserializeAsync<FileListManifest>(stream, cancellationToken: cancellationToken)
                    ?? throw new InvalidDataException("在线清单内容为空");
                foreach (var item in onlineManifest.Files) item.SourceName = "Eigeen 更新源";
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { lastError = ex; }
        }

        FileListManifest curatedManifest;
        await using (var stream = AssetLoader.Open(
            new Uri("avares://ReExtractor.Gui/Assets/ekey_filelist_manifest.json")))
        {
            curatedManifest = await JsonSerializer.DeserializeAsync<FileListManifest>(stream, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("内置精选列表目录损坏");
        }

        if (onlineManifest == null && curatedManifest.Files.Length == 0)
            throw new HttpRequestException("所有在线列表源均连接失败", lastError);

        var merged = curatedManifest.Files
            .Concat(onlineManifest?.Files ?? [])
            .GroupBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new FileListManifest
        {
            BaseUrls = onlineManifest?.BaseUrls ?? [],
            Files = merged,
        };
    }

    public async Task<string> DownloadAsync(FileListManifest manifest, RemoteFileListInfo info,
        CancellationToken cancellationToken = default)
    {
        byte[]? data = null;
        Exception? lastError = null;
        var downloadUrls = string.IsNullOrWhiteSpace(info.DirectUrl)
            ? manifest.BaseUrls.Select(baseUrl => $"{baseUrl.TrimEnd('/')}/{info.FileName}")
            : [info.DirectUrl];
        foreach (var url in downloadUrls)
        {
            try
            {
                data = await Http.GetByteArrayAsync(url, cancellationToken);
                lastError = null;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { lastError = ex; }
        }
        if (data == null) throw new HttpRequestException("所有下载镜像均连接失败", lastError);
        var actualHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(info.Sha256) && !actualHash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载文件校验失败，请重新下载");
        if (!string.IsNullOrWhiteSpace(info.GitBlobSha))
        {
            using var blobHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            blobHash.AppendData(Encoding.ASCII.GetBytes($"blob {data.Length}\0"));
            blobHash.AppendData(data);
            var actualBlobHash = Convert.ToHexString(blobHash.GetHashAndReset()).ToLowerInvariant();
            if (!actualBlobHash.Equals(info.GitBlobSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载文件 Git 对象校验失败，请重新下载");
        }

        string targetPath;
        if (info.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(item => item.Name.EndsWith(".list", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("压缩包中没有 .list 文件");
            targetPath = Path.Combine(RemoteDirectory, Path.GetFileName(entry.Name));
            var tempPath = targetPath + ".download";
            await using (var input = entry.Open())
            await using (var output = File.Create(tempPath))
                await input.CopyToAsync(output, cancellationToken);
            File.Move(tempPath, targetPath, true);
        }
        else
        {
            targetPath = Path.Combine(RemoteDirectory, Path.GetFileName(info.FileName));
            await File.WriteAllBytesAsync(targetPath, data, cancellationToken);
        }
        return targetPath;
    }

    public void Delete(ManagedFileList item)
    {
        var path = Path.GetFullPath(item.FilePath);
        var root = Path.GetFullPath(LibraryDirectory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能删除列表库内的文件");
        File.Delete(path);
    }

    public static string IdentifierFromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);
        return name;
    }

    private static ManagedFileList ReadManaged(string path, string source)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var rawLine in File.ReadLines(path).Take(32))
            {
                var line = rawLine.TrimStart('\uFEFF');
                if (!line.StartsWith("#!", StringComparison.Ordinal)) break;
                var body = line[2..].Trim();
                var separator = body.IndexOf(':');
                if (separator <= 0) continue;
                var key = body[..separator].Trim().TrimStart('@');
                metadata[key] = body[(separator + 1)..].Trim();
            }
        }
        catch { }
        var identifier = Path.GetFileNameWithoutExtension(path);
        var updateTime = metadata.TryGetValue("update_time", out var value) && DateTimeOffset.TryParse(value, out var parsed)
            ? parsed : (DateTimeOffset?)File.GetLastWriteTimeUtc(path);
        return new ManagedFileList
        {
            Identifier = identifier,
            Title = metadata.GetValueOrDefault("title", identifier),
            FilePath = path,
            Source = source,
            Tags = metadata.GetValueOrDefault("tags", ""),
            UpdateTime = updateTime,
        };
    }
}
