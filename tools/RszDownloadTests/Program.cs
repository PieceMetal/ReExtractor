using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReeLib;

// Publish this runner as a single file as well as running it normally.
var cache = Path.Combine(Path.GetTempPath(), "ReExtractor-RszTests", Guid.NewGuid().ToString("N"));
ResourceRepository.LocalResourceRepositoryFolder = cache;
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var baseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
var metadata = JsonSerializer.Serialize(new RemoteResourceConfig {
    Resources = new() { ["re4"] = new() { RszPatchFiles = [baseUrl + "/rsz.json"] } }
});
var requests = new List<string>();
var server = Task.Run(async () => {
    for (int i = 0; i < 3; i++) {
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(20));
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        var first = await reader.ReadLineAsync();
        string? line;
        string? userAgent = null;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) {
            if (line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)) userAgent = line[11..].Trim();
        }
        if (userAgent == null || !userAgent.StartsWith("REE-Lib/") || userAgent.Contains(' '))
            throw new Exception("Missing or invalid User-Agent: " + userAgent);
        requests.Add(first!);
        var body = Encoding.UTF8.GetBytes(first!.Contains("/metadata") ? metadata : "{}");
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(body);
    }
});
ResourceRepository.MetadataRemoteSource = baseUrl + "/metadata";
var resources = ResourceRepository.UpdateAndGet("re4");
if (!resources.TryGetRszFiles(out var paths) || paths.Length != 1 || !File.Exists(paths[0]))
    throw new Exception("Cold cache metadata/RSZ download failed");
Console.WriteLine("PASS cold cache: metadata and RSZ downloaded");
ResourceRepository.AllowThreadedFetch = false;
using (var content = ResourceRepository.FetchContentFromUrlOrFile(baseUrl + "/sync")) {
    if (content == null || new StreamReader(content).ReadToEnd() != "{}") throw new Exception("Sync HTTP failed");
}
await server;
listener.Stop();
ResourceRepository.DisableOnlineUpdater = true;
ResourceRepository.Initialize(forceRefresh: true);
if (!ResourceRepository.UpdateAndGet("re4").TryGetRszFiles(out var warm) || !warm.SequenceEqual(paths))
    throw new Exception("Offline cache reload failed");
Console.WriteLine("PASS synchronous HTTP and offline cache reload; requests=" + requests.Count);
#pragma warning disable IL3000 // Assert the deployment mode in the regression runner.
var bundled = string.IsNullOrEmpty(typeof(ResourceRepository).Assembly.Location);
#pragma warning restore IL3000
if (args.Contains("--require-single-file") && !bundled) throw new Exception("Runner was not bundled");
Console.WriteLine("PASS single-file=" + bundled + "; isolated cache=" + cache);
