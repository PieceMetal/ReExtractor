using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ReExtractor.Core;
using ReExtractor.Gui;
using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using SharpGLTF.Schema2;

if (args.Length != 2) throw new ArgumentException("Usage: session.json output-directory");
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
Environment.SetEnvironmentVariable("REEXTRACTOR_DATA_DIR", Path.Combine(output, "ui-data"));
AppBuilder.Configure<Application>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Application.Current!.Styles.Add(new FluentTheme());
var main = new MainWindow();
const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(main);
object? Call(string name, params object?[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(main, values);
void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
void Pump(Task pending)
{
    var limit = DateTime.UtcNow.AddMinutes(3);
    while (!pending.IsCompleted && DateTime.UtcNow < limit) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    Check(pending.IsCompleted, "Preview load timed out");
    pending.GetAwaiter().GetResult();
}
Pump((Task)Call("LoadPreviewSessionAsync", Path.GetFullPath(args[0]))!);
using var sessionResult = JsonDocument.Parse(File.ReadAllText(args[0] + ".result.json"));
Check(sessionResult.RootElement.GetProperty("Ready").GetBoolean(), sessionResult.RootElement.ToString());
var path = (string)Field("_currentMotlistPath")!;
var pak = (PakService)Field("_pak")!;
var motions = (IReadOnlyList<MotionInfo>)Field("_previewMotions")!;
var visible = (IReadOnlyList<MotionInfo>)Field("_visiblePreviewMotions")!;
var pairs = (IReadOnlyList<AnimationLayerPair>)Field("_previewLayerPairs")!;
var shoot = motions.Single(m => m.MotionNumber == 130);
var viewport = main.FindControl<GlViewport>("Viewport")!;
var combo = main.FindControl<ComboBox>("MotionCombo")!;
var addCombo = main.FindControl<ComboBox>("AdditiveMotionCombo")!;
var description = main.FindControl<TextBlock>("BlendPreviewDescription")!;
var resolved = Re4AnimationLayerResolver.Resolve(pak);
File.WriteAllText(Path.Combine(output, "runtime-catalog.json"), JsonSerializer.Serialize(new {
    resolved.Diagnostics, Pairs = pairs, Usage = AnimationUsageCatalog.Get(path, shoot, pairs)
}, new JsonSerializerOptions { WriteIndented = true }));
Check(resolved.Diagnostics.Count == 0, string.Join("; ", resolved.Diagnostics));
Check(pairs.Count == 3 && pairs.All(p => p.AdditiveId == 130 && p.Composition == AnimationLayerComposition.MotionTreeEndpoint),
    "Expected exactly three rail-car direction endpoints");
Check(pairs.Select(p => p.BaseId).Order().SequenceEqual(new[] { 101, 111, 121 }), "Wrong directional holds");
Check(!visible.Any(m => m.MotionNumber == 130) && AnimationLayerCatalog.IsAdditive(path, shoot, pairs), "Raw recoil exposed as complete motion");
Check(AnimationUsageCatalog.Get(path, shoot, pairs).Kind == AnimationUsageKind.Additive, "Runtime additive usage missing");
Check(!AnimationLayerCatalog.IsAdditive("other.motlist.663", shoot, pairs), "Rail-car usage leaked to another list");
Console.WriteLine("REAL_TREE_BANK_FSM_AND_USAGE_PASS");
using (var treeStream = pak.ReadFile(pairs[0].Context!.Split('#')[0]))
using (var trees = new ReeLib.MotlistFile(new ReeLib.FileHandler(treeStream, pairs[0].Context!.Split('#')[0])))
{
    Check(trees.Read(), "Tree reload failed");
    var tree = (ReeLib.MotTreeFile)trees.Motions.Single(m => m.motNumber == 1001).MotFile!;
    var root = tree.Nodes.Single(n => n.typeName == "AddBlendNode");
    var read = typeof(Re4RailCarAnimationResolver).GetMethod("ReadEndpoints", BindingFlags.NonPublic | BindingFlags.Static)!;
    foreach (var (name, invalid) in new (string, object)[] { ("WorldRotation", true), ("BlendRate", .5f), ("BaseNo", 1u) })
    {
        var parameter = root.Parameters.Single(p => p.name == name);
        var original = parameter.value;
        parameter.value = invalid;
        var rejected = false;
        try { read.Invoke(null, [tree]); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { rejected = true; }
        finally { parameter.value = original; }
        Check(rejected, "Unsupported tree blend was accepted: " + name);
    }
    Console.WriteLine("UNSUPPORTED_WORLD_ROTATION_WEIGHT_AND_BASE_INPUT_REJECTED_PASS");
}
AnimationClip LoadRaw(int id)
{
    using var stream = pak.ReadFile(path);
    return ViewportDataLoader.LoadAnimation(stream, path, motions.Single(m => m.MotionNumber == id).SourceIndex,
        meshBoneNames: viewport.AllMeshBoneNames, sceneMeshes: viewport.SceneMeshes);
}
var raw = LoadRaw(130);
var rawBefore = raw.NamedTracks.ToDictionary(k => k.Key,
    v => ((v.Value.Translations ?? []).ToArray(), (v.Value.Rotations ?? []).ToArray()));
var bones = AnimationSkeleton.PreviewBones(viewport.SceneMeshes);
void Select(int id, bool add)
{
    typeof(MainWindow).GetField("_syncingMotionUi", flags)!.SetValue(main, true);
    try
    {
        combo.SelectedIndex = visible.ToList().FindIndex(m => m.MotionNumber == id);
        Call("ResetAdditiveChoices");
        Check(addCombo.ItemCount == 2, "Direction did not expose shoot plus no-overlay choice");
        addCombo.SelectedIndex = add ? 1 : 0;
    }
    finally { typeof(MainWindow).GetField("_syncingMotionUi", flags)!.SetValue(main, false); }
    Call("UpdateBlendPreviewDescription");
    Pump((Task)Call("LoadSelectedMotionAsync", (object?)null)!);
    Check((int)Field("_loadedMotionSource")! == visible[combo.SelectedIndex].SourceIndex, "Direction failed to load");
    Check((int)Field("_loadedAdditiveSource")! == (add ? shoot.SourceIndex : -1), "Overlay failed to load");
    Check(description.Text!.Contains("固定方向"), "Missing fixed-direction limitation");
    Check(MathF.Abs(viewport.Duration - raw.Duration) < 1e-6f, "Toggle changed inspection clock");
}
var viewportType = viewport.GetType();
object[] Models() => new[] { viewportType.GetField("_primary", flags)!.GetValue(viewport)! }
    .Concat(((IEnumerable)viewportType.GetField("_extras", flags)!.GetValue(viewport)!).Cast<object>()).ToArray();
Vector3[] Pose(float time)
{
    viewportType.GetMethod("EvaluateAllPose", flags)!.Invoke(viewport, [time]);
    return Models().SelectMany(m => (Vector3[])m.GetType().GetField("Posed")!.GetValue(m)!).ToArray();
}
var reports = new List<object>();
var exporter = new ViewportExportService();
var merged = exporter.BuildMergedExportModel(viewport.ExportModels.Select(m => (m.Mesh, m.VisibleGroups)).ToArray());
foreach (var id in new[] { 101, 111, 121 })
{
    Select(id, false);
    var off = viewport.ExportAnimationClip!;
    var offPoses = Enumerable.Range(0, raw.FrameCount + 1).Select(frame => Pose(frame / (float)raw.FrameRate)).ToArray();
    Select(id, true);
    var on = viewport.ExportAnimationClip!;
    Check(on.Name.Contains("mot_130") && on.Name.Contains("固定方向"), "Wrong composition pipeline");
    var largestDelta = 0f;
    for (var frame = 0; frame <= raw.FrameCount; frame++)
    {
        var posed = Pose(frame / (float)raw.FrameRate);
        Check(posed.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)), "Invalid native skinning");
        largestDelta = Math.Max(largestDelta, posed.Zip(offPoses[frame], Vector3.Distance).Max());
    }
    Check(largestDelta > 1e-4f, "Recoil has no visible vertex movement");
    var profile = AnimationLayerCatalog.GetProfile(path, id, 130, pairs)!;
    var zero = AnimationPreviewMixer.ComposeMotionTreeEndpoint(LoadRaw(id), raw, raw, bones,
        AnimationPreviewProfiles.ReadRe4Mask(pak, bones, profile.BaseMaskId, profile.JointMapPath),
        AnimationPreviewProfiles.ReadRe4Mask(pak, bones, profile.AdditiveMaskId, profile.JointMapPath), 0);
    Check(zero.Duration == off.Duration && zero.FrameCount == off.FrameCount, "Zero changed clock");
    foreach (var (name, track) in zero.NamedTracks)
        Check(track.Translations!.SequenceEqual(off.NamedTracks[name].Translations!)
            && track.Rotations!.SequenceEqual(off.NamedTracks[name].Rotations!), "Zero differs from disabling overlay: " + name);
    var currentFile = Path.Combine(output, "current-glb", "hold-" + id + "-shoot-130.glb");
    exporter.ConvertToAnimatedGlb(merged.Mesh, merged.VisibleGroups, on, currentFile);
    var saved = ModelRoot.Load(currentFile);
    Check(MathF.Abs(saved.LogicalAnimations.Single().Duration - raw.Duration) < 1e-5f, "Current export retimed shot");
    reports.Add(new { BaseId = id, AdditiveId = 130, Duration = on.Duration,
        Frames = raw.FrameCount + 1, NativeVertexDelta = largestDelta, File = currentFile });
    Console.WriteLine($"DIRECTION_{id}_ON_OFF_ZERO_NATIVE_SKINNING_AND_CURRENT_GLB_PASS delta={largestDelta:F6} duration={on.Duration}");
}
foreach (var (name, value) in rawBefore)
    Check(value.Item1.SequenceEqual(raw.NamedTracks[name].Translations ?? [])
        && value.Item2.SequenceEqual(raw.NamedTracks[name].Rotations ?? []), "Source recoil was mutated");
// Exercise the same uncomposed conversion used by the all/batch export paths.
using (var stream = pak.ReadFile(path))
{
    var files = new AnimationService().ConvertAllToGlbWithAnimation(merged.Mesh, stream, path, Path.Combine(output, "raw-all-glb"));
    Check(files.Count == motions.Count, "All export lost raw motions");
    var shotFile = files.Single(f => Path.GetFileName(f).EndsWith("_id130.glb"));
    var exportedRaw = ModelRoot.Load(shotFile);
    Check(MathF.Abs(exportedRaw.LogicalAnimations.Single().Duration - raw.Duration) < 1e-5f, "Raw export changed shot clock");
    File.WriteAllText(Path.Combine(output, "raw-shoot-file.txt"), shotFile);
    Check(!exportedRaw.LogicalAnimations.Single().Name.Contains("固定方向"), "All export baked preview combination");
    Console.WriteLine($"ALL_{files.Count}_RAW_TRACKS_EXPORT_PASS");
}
var commonPath = "natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
using (var stream = pak.ReadFile(commonPath))
{
    var common = ViewportDataLoader.ListMotions(stream, commonPath);
    File.WriteAllText(Path.Combine(output, "wp4000h-ui-motions.json"), JsonSerializer.Serialize(common.Select(m => new {
        path = commonPath, index = m.SourceIndex, id = m.MotionNumber, name = m.DisplayName.Split('（')[0].Trim()
    })));
    Check(AnimationLayerCatalog.Find(commonPath, common.Single(m => m.MotionNumber == 160), common,
        resolved.ForList(commonPath, common)).Count == 3, "Loaded HOLD/SHOOT tree inputs were not resolved");
    Check(AnimationLayerCatalog.Find(commonPath, common.Single(m => m.MotionNumber == 160), common).Count == 0,
        "Offline fixture guessed a pairing without its actual loaded configuration");
    Check(AnimationLayerCatalog.GetProfile(commonPath, 541, 544, resolved.ForList(commonPath, common))?.Composition
        == AnimationLayerComposition.UpperBodyStack, "Existing upper-body pipeline changed");
}
Select(101, true);
var rawToggle = main.FindControl<CheckBox>("ShowRawMotionTracks")!;
var advanced = main.FindControl<Expander>("AdvancedMotionOptions")!;
advanced.IsExpanded = true;
rawToggle.IsChecked = true;
var rawVisible = (IReadOnlyList<MotionInfo>)Field("_visiblePreviewMotions")!;
combo.SelectedIndex = rawVisible.ToList().FindIndex(m => m.MotionNumber == 130);
Pump((Task)Call("LoadSelectedMotionAsync", (object?)null)!);
Check(viewport.ExportAnimationClip!.Name == "mot_130" && !addCombo.IsEnabled, "Raw inspection unexpectedly composed recoil");
advanced.IsExpanded = false;
Pump((Task)Call("LoadSelectedMotionAsync", (object?)null)!);
Check(rawToggle.IsChecked != true && (int)Field("_loadedMotionSource")! == motions.Single(m => m.MotionNumber == 101).SourceIndex
    && (int)Field("_loadedAdditiveSource")! == shoot.SourceIndex, "Leaving raw mode lost the confirmed hold/shoot selection");
Console.WriteLine("RAW_INSPECTION_AND_RETURN_TO_COMBINATION_PASS");
File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new {
    Scope = "Fixed directional endpoints, no runtime YawBlend/FSM/IK acceptance", Shots = reports,
    AllRawTracks = motions.Count, SourceTracksPreserved = true
}, new JsonSerializerOptions { WriteIndented = true }));
main.Close();
Console.WriteLine("RE4_TRAM_PREVIEW_REAL_RESOURCES_PASS");
