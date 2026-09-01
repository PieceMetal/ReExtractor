using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReExtractor.Gui;

public sealed record UpdateRelease(Version Version, string Tag, string Name,
    string Notes, string DownloadUrl, long DownloadSize, string WebUrl);

public sealed record PreparedUpdate(string NewExecutable, string PackageDirectory, string StagingDirectory);

public sealed class UpdateService
{
    private const string LatestReleaseUrl =
        "https://github.com/PieceMetal/ReExtractor/releases/latest";
    private static readonly HttpClient Http = CreateHttpClient();

    public Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        // The GitHub REST endpoint has a small unauthenticated hourly quota.
        // The public /releases/latest redirect provides the same stable tag
        // without consuming API rate limit, so desktop clients can check freely.
        using var response = await Http.GetAsync(LatestReleaseUrl,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var releasePage = response.RequestMessage?.RequestUri?.ToString() ?? LatestReleaseUrl;
        const string tagMarker = "/releases/tag/";
        var markerIndex = releasePage.IndexOf(tagMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            throw new InvalidDataException("无法从 GitHub 最新发布页识别版本号");
        var tag = Uri.UnescapeDataString(releasePage[(markerIndex + tagMarker.Length)..])
            .TrimEnd('/');
        if (!TryParseVersion(tag, out var version) || version <= CurrentVersion) return null;

        var expectedName = $"ReExtractor-v{version.ToString(3)}-win-x64.zip";
        var downloadUrl =
            $"https://github.com/PieceMetal/ReExtractor/releases/download/{Uri.EscapeDataString(tag)}/{expectedName}";
        return new UpdateRelease(
            version,
            tag,
            $"ReExtractor {tag}",
            $"发现新版本 {tag}。下载完成后将自动替换当前程序并重启。\n\n完整更新说明：{releasePage}",
            downloadUrl,
            0,
            releasePage);
    }

    public async Task<PreparedUpdate> DownloadAsync(UpdateRelease release,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var staging = Path.Combine(Path.GetTempPath(),
            $"ReExtractor-update-{release.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var archivePath = Path.Combine(staging, "update.zip");
        try
        {
            using var response = await Http.GetAsync(release.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.DownloadSize;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (total > 0) progress?.Report(Math.Clamp(received / (double)total, 0, 1));
                }
            }

            var extracted = Path.Combine(staging, "extracted");
            ZipFile.ExtractToDirectory(archivePath, extracted);
            var newExe = Directory.EnumerateFiles(extracted, "ReExtractor-v*.exe",
                    SearchOption.AllDirectories).FirstOrDefault()
                // Accept legacy packages so installations can still recover from
                // a manually downloaded pre-v1.3.3 build.
                ?? Directory.EnumerateFiles(extracted, "ReExtractor.Gui.exe",
                    SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidDataException("更新包内没有带版本号的 ReExtractor 可执行文件");
            var fileVersionText = FileVersionInfo.GetVersionInfo(newExe).FileVersion;
            if (!Version.TryParse(fileVersionText, out var fileVersion) || fileVersion < release.Version)
                throw new InvalidDataException(
                    $"更新包版本校验失败：{fileVersionText ?? "未知"}");
            progress?.Report(1);
            return new PreparedUpdate(newExe, Path.GetDirectoryName(newExe)!, staging);
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch { }
            throw;
        }
    }

    public void LaunchInstaller(PreparedUpdate update)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("当前自动更新仅支持 Windows");

        static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
        var currentDirectory = Path.GetDirectoryName(currentExe)!;
        var versionedExe = Path.Combine(currentDirectory, Path.GetFileName(update.NewExecutable));
        var scriptPath = Path.Combine(Path.GetTempPath(),
            $"ReExtractor-updater-{Guid.NewGuid():N}.ps1");
        var script = string.Join(Environment.NewLine,
        [
            "$ErrorActionPreference = 'Stop'",
            $"$oldPid = {Environment.ProcessId}",
            "while (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }",
            // Keep the version in the executable file name. Previous versions stay
            // available as a rollback option instead of being overwritten in place.
            $"Copy-Item -LiteralPath {PsQuote(update.NewExecutable)} -Destination {PsQuote(versionedExe)} -Force",
            // The portable package has a native GDeflate decoder beside the EXE.
            // Keep the current app usable when updating from a pre-GDeflate build.
            $"$newNative = Join-Path {PsQuote(update.PackageDirectory)} 'libGDeflate.dll'",
            $"$currentNative = Join-Path {PsQuote(currentDirectory)} 'libGDeflate.dll'",
            "if (Test-Path -LiteralPath $newNative) { Copy-Item -LiteralPath $newNative -Destination $currentNative -Force }",
            $"Start-Process -FilePath {PsQuote(versionedExe)} -WorkingDirectory {PsQuote(currentDirectory)}",
            $"Remove-Item -LiteralPath {PsQuote(update.StagingDirectory)} -Recurse -Force -ErrorAction SilentlyContinue",
            "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue",
        ]);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var value = tag.Trim().TrimStart('v', 'V');
        var suffix = value.IndexOfAny(['-', '+']);
        if (suffix >= 0) value = value[..suffix];
        return Version.TryParse(value, out version!);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ReExtractor", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
