using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using ReExtractor.Gui;

// The same executable doubles as a deterministic Blender stand-in. No game assets
// or real FBX files are touched. Real long-running Blender exports remain manual QA.
if (args.Contains("--background"))
{
    var mode = args[^1];
    // Exercise simultaneous pipe draining well beyond OS pipe buffer capacity.
    for (var i = 0; i < 4000; i++)
    {
        Console.WriteLine($"stdout {i} " + new string('x', 100));
        Console.Error.WriteLine($"stderr {i} " + new string('y', 100));
    }
    Thread.Sleep(1200);
    Console.WriteLine("REEXTRACTOR_PROGRESS:1/1");
    Console.Error.WriteLine("STDERR_FINAL_SENTINEL");
    if (mode != "missing-marker") Console.WriteLine("REEXTRACTOR_OK:1");
    Environment.Exit(mode == "nonzero" ? 7 : 0);
    return;
}
var root = Path.GetFullPath(Path.Combine("artifacts", "issue3-tests", Guid.NewGuid().ToString("N")));
Environment.SetEnvironmentVariable("REEXTRACTOR_DATA_DIR", Path.Combine(root, "data"));
Directory.CreateDirectory(root);
var source = File.ReadAllText("src/ReExtractor.Gui/MainWindow.axaml.cs");
var start = source.IndexOf("private void RunBlenderBatch(", StringComparison.Ordinal);
var end = source.IndexOf("private async Task ExportPathAsync", start, StringComparison.Ordinal);
var methodSource = source[start..end];
if (methodSource.Contains("3_600_000") || methodSource.Contains("process.Kill") ||
    methodSource.Contains("TimeoutException") || !methodSource.Contains("process.WaitForExit();"))
    throw new Exception("Elapsed-time termination regression");
AppBuilder.Configure<Application>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Application.Current!.Styles.Add(new FluentTheme());
var window = new MainWindow();
var run = typeof(MainWindow).GetMethod("RunBlenderBatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
var output = Path.Combine(root, "output");
Directory.CreateDirectory(output);
var sentinel = Path.Combine(output, "already-exported.fbx");
File.WriteAllText(sentinel, "existing-output-must-survive");
foreach (var mode in new[] { "success", "missing-marker", "nonzero" })
{
    var progress = 0;
    Exception? failure = null;
    try
    {
        run.Invoke(window, new object?[] { "export_models_fbx.py", Environment.ProcessPath!, root, output,
            (Action<int, int>)((current, total) => { if (current == 1 && total == 1) progress++; }), new[] { mode } });
    }
    catch (TargetInvocationException e) { failure = e.InnerException; }
    if ((mode == "success") != (failure == null)) throw new Exception($"Wrong outcome: {mode}", failure);
    if (mode == "nonzero" && !failure!.Message.Contains("7")) throw new Exception("Exit code lost");
    if (progress != 1) throw new Exception("Final progress event lost");
    if (File.ReadAllText(sentinel) != "existing-output-must-survive") throw new Exception("Existing output changed");
    Console.WriteLine($"PASS {mode}: progress drained, output preserved, outcome correct");
}
var logs = Directory.GetFiles(root, "fbx-*.log", SearchOption.AllDirectories);
if (logs.Length != 3 || logs.Any(path => !File.ReadAllText(path).Contains("STDERR_FINAL_SENTINEL")))
    throw new Exception("Full conversion logs missing");
window.Close();
Console.WriteLine("PASS no elapsed-time kill; all 3 logs persisted with final stderr");
