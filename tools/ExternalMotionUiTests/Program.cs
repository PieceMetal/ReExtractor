using System.Reflection;
using System.Numerics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ReExtractor.Core;
using ReExtractor.Gui;

Environment.SetEnvironmentVariable("REEXTRACTOR_DATA_DIR", Path.GetFullPath("artifacts/oni-external-motion/ui-data"));
AppBuilder.Configure<Application>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Application.Current!.Styles.Add(new FluentTheme());
var window = new MainWindow();
var flags = BindingFlags.NonPublic | BindingFlags.Instance;
var session = args.Single();
var load = (Task)typeof(MainWindow).GetMethod("LoadPreviewSessionAsync", flags)!.Invoke(window, [session])!;
Wait(load);
using var result = JsonDocument.Parse(File.ReadAllText(session + ".result.json"));
if (!result.RootElement.GetProperty("Ready").GetBoolean()) throw new Exception(result.RootElement.ToString());
var combo = window.FindControl<ComboBox>("MotionCombo")!;
var motions = (IReadOnlyList<MotionInfo>)typeof(MainWindow).GetField("_visiblePreviewMotions", flags)!.GetValue(window)!;
if (motions.Count != 148 || combo.ItemCount != 148) throw new Exception("Inherited dropdown incomplete");
var viewport = window.FindControl<Control>("Viewport")!;
foreach (var index in new[] { 0, 73, 147, 0 })
{
    typeof(MainWindow).GetField("_syncingMotionUi", flags)!.SetValue(window, true);
    combo.SelectedIndex = index;
    typeof(MainWindow).GetField("_syncingMotionUi", flags)!.SetValue(window, false);
    Wait((Task)typeof(MainWindow).GetMethod("LoadSelectedMotionAsync", flags)!.Invoke(window, [null])!);
    if ((int)typeof(MainWindow).GetField("_loadedMotionSource", flags)!.GetValue(window)! != motions[index].SourceIndex)
        throw new Exception("Wrong selected motion");
    var clip = (AnimationClip)viewport.GetType().GetField("_clip", flags)!.GetValue(viewport)!;
    if (clip.NamedTracks.Count == 0 || clip.Duration <= 0) throw new Exception("Empty loaded motion");
    var model = viewport.GetType().GetField("_primary", flags)!.GetValue(viewport)!;
    foreach (var time in new[] { 0f, clip.Duration / 2, clip.Duration })
    {
        viewport.GetType().GetMethod("EvaluateAllPose", flags)!.Invoke(viewport, [time]);
        var points = (Vector3[])model.GetType().GetField("Posed")!.GetValue(model)!;
        if (points.Length == 0 || points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)))
            throw new Exception("Invalid posed vertices");
    }
    Console.WriteLine($"GUI_SWITCH_PASS index={index} id={motions[index].MotionNumber} tracks={clip.NamedTracks.Count}");
}
window.Close();
Console.WriteLine("HEADLESS_INHERITED_PREVIEW_PASS 148 dropdown entries, 4 motion switches, 12 finite skinned poses");

void Wait(Task task)
{
    var deadline = DateTime.UtcNow.AddMinutes(3);
    while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    if (!task.IsCompleted) throw new TimeoutException("Preview timed out");
    task.GetAwaiter().GetResult();
}
