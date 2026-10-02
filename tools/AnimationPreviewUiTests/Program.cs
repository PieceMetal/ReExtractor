using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
using Avalonia.Threading;
using ReExtractor.Gui;
using ReExtractor.Core;
using System.Reflection;
Environment.SetEnvironmentVariable("REEXTRACTOR_DATA_DIR",Path.GetFullPath("artifacts/re4-audit/ui-data"));
AppBuilder.Configure<Application>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).SetupWithoutStarting();
Application.Current!.Styles.Add(new FluentTheme());Application.Current.RequestedThemeVariant=ThemeVariant.Dark;
var main=new MainWindow();
if(args.Length == 1 && args[0] == "--export-hover")
{
    main.Show(); Dispatcher.UIThread.RunJobs();
    var buttons = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(main).OfType<Button>().Where(b => b.Classes.Contains("export-folder") || b.Name == "FeedbackLauncher").ToArray();
    if(buttons.Length == 0) throw new Exception("Export folder button missing");
    foreach(var button in buttons)
    {
        button.ApplyTemplate();
        var presenter = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(button).OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        foreach(var state in new[] { "normal", ":pointerover", ":pressed" })
        {
            var pseudo = (Avalonia.Controls.IPseudoClasses)button.Classes;
            pseudo.Set(":pointerover",state != "normal"); pseudo.Set(":pressed",state == ":pressed");
            Dispatcher.UIThread.RunJobs();
            if(presenter.Background is not Avalonia.Media.ISolidColorBrush brush || brush.Color.A != 255) throw new Exception("Transparent export button: " + state);
            if(button.Name == "FeedbackLauncher" && brush.Color != Avalonia.Media.Color.Parse(state == "normal" ? "#2379d9" : state == ":pointerover" ? "#348beb" : "#185ca8")) throw new Exception("Feedback button lost its blue background");
            Console.WriteLine(button.Name + " " + state + " " + brush.Color);
        }
    }
    main.Close(); Console.WriteLine("EXPORT_FOLDER_OPAQUE_STATES_PASS"); return;
}
if(args.Length == 2 && args[0] is "--session" or "--session-dropdowns")
{
    var task = (Task)typeof(MainWindow).GetMethod("LoadPreviewSessionAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,[args[1]])!;
    var limit = DateTime.UtcNow.AddMinutes(3);
    while(!task.IsCompleted && DateTime.UtcNow < limit) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    if(!task.IsCompleted) throw new Exception("Session preload timed out");
    task.GetAwaiter().GetResult();
    using var result=System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[1]+".result.json"));
    if(!result.RootElement.GetProperty("Ready").GetBoolean())throw new Exception(result.RootElement.ToString());
    if(args[0] == "--session-dropdowns")
    {
        var combo = main.FindControl<ComboBox>("MotionCombo")!;
        var addCombo = main.FindControl<ComboBox>("AdditiveMotionCombo")!;
        var rawToggle = main.FindControl<CheckBox>("ShowRawMotionTracks")!;
        var binding = BindingFlags.NonPublic | BindingFlags.Instance;
        var choices = (IReadOnlyList<MotionInfo>)typeof(MainWindow).GetField("_visiblePreviewMotions",binding)!.GetValue(main)!;
        var allMotions = (IReadOnlyList<MotionInfo>)typeof(MainWindow).GetField("_previewMotions",binding)!.GetValue(main)!;
        var currentPath = (string)typeof(MainWindow).GetField("_currentMotlistPath",binding)!.GetValue(main)!;
        var livePairs = (IReadOnlyList<AnimationLayerPair>)typeof(MainWindow).GetField("_previewLayerPairs",binding)!.GetValue(main)!;
        var supported = choices.SelectMany(b => AnimationLayerCatalog.Find(currentPath,b,allMotions,livePairs)
            .Select(a => (b.MotionNumber,a.Motion.MotionNumber))).ToArray();
        if(supported.Length == 0)throw new Exception("Live list lost supported states");
        foreach (var pair in supported)
        {
            combo.SelectedIndex = choices.ToList().FindIndex(m => m.MotionNumber == pair.Item1);
            var source = choices[combo.SelectedIndex].SourceIndex;
            var until = DateTime.UtcNow.AddSeconds(20);
            do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            while ((int)typeof(MainWindow).GetField("_loadedMotionSource",binding)!.GetValue(main)! != source && DateTime.UtcNow < until);
            var candidates = (IReadOnlyList<MotionInfo>)typeof(MainWindow).GetField("_relatedPreviewMotions",binding)!.GetValue(main)!;
            Console.WriteLine($"LIVE_SELECTION {pair.Item1} raw={rawToggle.IsChecked} items={addCombo.ItemCount}: "
                + string.Join(" | ",addCombo.Items.Cast<ComboBoxItem>().Select(c=>c.Content)));
            if(candidates.Count != 1 || candidates[0].MotionNumber != pair.Item2 || addCombo.ItemCount != 2 || addCombo.SelectedIndex != 1)
                throw new Exception("Live selection lost paired motion");
            until = DateTime.UtcNow.AddSeconds(20);
            do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            while ((int)typeof(MainWindow).GetField("_loadedAdditiveSource",binding)!.GetValue(main)! != candidates[0].SourceIndex && DateTime.UtcNow < until);
            if((int)typeof(MainWindow).GetField("_loadedAdditiveSource",binding)!.GetValue(main)! != candidates[0].SourceIndex)
                throw new Exception("Live paired selection did not load");
        }
        Console.WriteLine("LIVE_DROPDOWN_AND_PAIR_PLAYBACK_PASS");
        var liveViewport = main.FindControl<Control>("Viewport")!;
        var liveType = liveViewport.GetType();
        foreach (var selectedMotion in choices)
        {
            combo.SelectedIndex = choices.ToList().FindIndex(m => m.SourceIndex == selectedMotion.SourceIndex);
            var until = DateTime.UtcNow.AddSeconds(20);
            do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            while ((int)typeof(MainWindow).GetField("_loadedMotionSource",binding)!.GetValue(main)! != selectedMotion.SourceIndex && DateTime.UtcNow < until);
            if((int)typeof(MainWindow).GetField("_loadedMotionSource",binding)!.GetValue(main)! != selectedMotion.SourceIndex)
                throw new Exception("Motion failed to load: " + selectedMotion.MotionNumber);
            var activeClip = (AnimationClip)liveType.GetField("_clip",binding)!.GetValue(liveViewport)!;
            var liveModels = new[]{liveType.GetField("_primary",binding)!.GetValue(liveViewport)!}
                .Concat(((System.Collections.IEnumerable)liveType.GetField("_extras",binding)!.GetValue(liveViewport)!).Cast<object>()).ToArray();
            foreach(var time in new[]{0f,activeClip.Duration/2,activeClip.Duration})
            {
                liveType.GetMethod("EvaluateAllPose",binding)!.Invoke(liveViewport,[time]);
                foreach(var model in liveModels)
                {
                    var points = (System.Numerics.Vector3[])model.GetType().GetField("Posed")!.GetValue(model)!;
                    if(points.Any(p=>!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z)))
                        throw new Exception("Non-finite vertices: " + selectedMotion.MotionNumber);
                }
            }
            var expectedCandidates = AnimationLayerCatalog.Find(currentPath,selectedMotion,allMotions,livePairs);
            if(addCombo.ItemCount != expectedCandidates.Count+1 || addCombo.SelectedIndex != (expectedCandidates.Count>0?1:0))
                throw new Exception("Unexpected automatic pair: " + selectedMotion.MotionNumber);
            Console.WriteLine($"LIVE_ALL_MOTIONS {selectedMotion.MotionNumber} candidates={expectedCandidates.Count} clip={activeClip.Name}");
        }
        Console.WriteLine($"LIVE_ALL_{choices.Count}_BASES_REAL_MODELS_AND_AUTO_SELECTION_PASS");
        return;
    }
    var viewport=main.FindControl<Control>("Viewport")!;
    var vt=viewport.GetType();
    var flags=BindingFlags.NonPublic|BindingFlags.Instance;
    var primary=vt.GetField("_primary",flags)!.GetValue(viewport)!;
    var extras=((System.Collections.IEnumerable)vt.GetField("_extras",flags)!.GetValue(viewport)!).Cast<object>();
    var driver=vt.GetMethod("GetPoseDriver",flags)!.Invoke(viewport,null);
    vt.GetMethod("EvaluateAllPose",flags)!.Invoke(viewport,[.29f]);
    foreach(var model in new[]{primary}.Concat(extras))
    {
        var mt=model.GetType();var mesh=(ViewportMesh)mt.GetField("Mesh")!.GetValue(model)!;
        var posed=(System.Numerics.Vector3[])mt.GetField("Posed")!.GetValue(model)!;
        Console.WriteLine($"POSE_MODEL driver={ReferenceEquals(driver,model)} bones={mesh.Bones.Length} deform={mesh.DeformToBone.Length} vertices={mesh.VertexCount} y={posed.Min(v=>v.Y)}..{posed.Max(v=>v.Y)}");
        var globals=(System.Numerics.Matrix4x4[])mt.GetField("BoneGlobals")!.GetValue(model)!;
        foreach(var n in new[]{"root","Hip","Spine_0","Spine_1","Spine_2","Neck_0","Head"}){
            var ix=Array.FindIndex(mesh.Bones,b=>b.Name==n);if(ix<0)continue;var bone=mesh.Bones[ix];
            Console.WriteLine($"  {n} helper={bone.IsMotionHelper} parent={bone.ParentIndex} local={bone.LocalBind.Translation} posed={globals[ix].Translation}");
        }
    }
    var models=new[]{primary}.Concat(extras).ToArray();
    ViewportMesh Mesh(object model)=>(ViewportMesh)model.GetType().GetField("Mesh")!.GetValue(model)!;
    var body=models.Single(m=>Mesh(m).Bones.Any(b=>b.Name=="Hip"&&!b.IsMotionHelper));
    if(!ReferenceEquals(driver,body))throw new Exception("Partial head rig selected as full-body pose driver");
    var poseDirectory=Path.Combine("artifacts/preview-fix/native-render-r10",Path.GetFileNameWithoutExtension(args[1]));Directory.CreateDirectory(poseDirectory);
    var renderJobs=new List<object>();
    var originalClip=(AnimationClip)vt.GetField("_clip",flags)!.GetValue(viewport)!;
    for(int frame=0;frame<=35;frame++)
    {
        var time=frame/60f;vt.GetMethod("EvaluateAllPose",flags)!.Invoke(viewport,[time]);
        var bodyGlobals=(System.Numerics.Matrix4x4[])body.GetType().GetField("BoneGlobals")!.GetValue(body)!;
        foreach(var model in models)
        {
            var mesh=Mesh(model);var globals=(System.Numerics.Matrix4x4[])model.GetType().GetField("BoneGlobals")!.GetValue(model)!;
            var head=Array.FindIndex(mesh.Bones,b=>b.Name=="Head");
            if(globals[head].Translation.Y<1.4f)throw new Exception("Head collapsed below shoulders");
            foreach(var name in new[]{"Spine_2","Neck_0","Head"})
            {
                var mi=Array.FindIndex(mesh.Bones,b=>b.Name==name);var bi=Array.FindIndex(Mesh(body).Bones,b=>b.Name==name);
                if(System.Numerics.Vector3.Distance(globals[mi].Translation,bodyGlobals[bi].Translation)>1e-5f)
                    throw new Exception("Independent part disconnected at "+name);
            }
        }
        if(frame is 0 or 17 or 35)
        {
            var posedModels=new List<(ViewportMesh Mesh,IReadOnlySet<int> VisibleGroups)>();
            foreach(var model in models)
            {
                var mesh=Mesh(model);var posed=mesh.WithTransform(System.Numerics.Matrix4x4.Identity);
                posed.Vertices=((System.Numerics.Vector3[])model.GetType().GetField("Posed")!.GetValue(model)!).ToArray();
                posed.Normals=((System.Numerics.Vector3[])model.GetType().GetField("PosedNormals")!.GetValue(model)!).ToArray();
                posed.Bones=[];posed.DeformToBone=[];posed.Weights=posed.Vertices.Select(_=>Array.Empty<(int,float)>()).ToArray();
                posedModels.Add((posed,mesh.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet()));
            }
            var file=Path.GetFullPath($"{poseDirectory}/frame-{frame}.glb");
            new ViewportExportService().ConvertMergedToGlb(posedModels,file);
            renderJobs.Add(new{label=$"Native viewport frame {frame}",file,duration=0f,variant=$"native-{frame}",bid=result.RootElement.GetProperty("BaseId").GetInt32(),aid=result.RootElement.GetProperty("AdditiveId").GetInt32()});
        }
    }
    // Reordering independent parts must not change any skinned vertex.
    var expected=models.ToDictionary(m=>Mesh(m),m=>((System.Numerics.Vector3[])m.GetType().GetField("Posed")!.GetValue(m)!).ToArray());
    var meshes=models.Select(Mesh).ToArray();
    foreach(var order in new[]{new[]{1,0,2},new[]{2,1,0}})
    {
        if(!ReferenceEquals(AnimationSkeleton.SelectDriver(order.Select(i=>meshes[i]).ToArray()),Mesh(body)))throw new Exception("Driver depends on load order");
        var canonical=AnimationSkeleton.PreviewBones(order.Select(i=>meshes[i]).ToArray());
        var spine=canonical.Single(b=>b.Name=="Spine_2");
        if(spine.ParentIndex<0||canonical[spine.ParentIndex].Name!="Spine_1"||spine.LocalBind.Translation.Y>.2f)throw new Exception("Preview reference used a truncated head root");
        vt.GetMethod("SetMesh")!.Invoke(viewport,[meshes[order[0]]]);
        foreach(var i in order.Skip(1))vt.GetMethod("AddMesh")!.Invoke(viewport,[meshes[i],"part"]);
        vt.GetMethod("SetAnimation")!.Invoke(viewport,[originalClip]);
        vt.GetMethod("EvaluateAllPose",flags)!.Invoke(viewport,[35/60f]);
        var reordered=new[]{vt.GetField("_primary",flags)!.GetValue(viewport)!}.Concat(((System.Collections.IEnumerable)vt.GetField("_extras",flags)!.GetValue(viewport)!).Cast<object>());
        foreach(var model in reordered)
        {
            var values=(System.Numerics.Vector3[])model.GetType().GetField("Posed")!.GetValue(model)!;
            if(values.Zip(expected[Mesh(model)],(a,b)=>System.Numerics.Vector3.Distance(a,b)<1e-5f).Any(equal=>!equal))throw new Exception("Part load order changes native skinning");
        }
    }
    // Replay the user's candidate switches through the real window loader, then
    // evaluate the native multi-part skinning for the entire resulting clips.
    var fullMotions = (IReadOnlyList<MotionInfo>)typeof(MainWindow).GetField("_previewMotions",flags)!.GetValue(main)!;
    var fullPath = (string)typeof(MainWindow).GetField("_currentMotlistPath",flags)!.GetValue(main)!;
    var fullPairs = (IReadOnlyList<AnimationLayerPair>)typeof(MainWindow).GetField("_previewLayerPairs",flags)!.GetValue(main)!;
    foreach(var pair in fullMotions.SelectMany(b => AnimationLayerCatalog.Find(fullPath,b,fullMotions,fullPairs)
        .Select(a => (b.MotionNumber,a.Motion.MotionNumber))))
    {
        var mainType=typeof(MainWindow);
        var sync=mainType.GetField("_syncingMotionUi",flags)!;
        sync.SetValue(main,true);
        var visible=(IReadOnlyList<MotionInfo>)mainType.GetField("_visiblePreviewMotions",flags)!.GetValue(main)!;
        main.FindControl<ComboBox>("MotionCombo")!.SelectedIndex=visible.ToList().FindIndex(m=>m.MotionNumber==pair.Item1);
        mainType.GetMethod("ResetAdditiveChoices",flags)!.Invoke(main,null);
        var related=(IReadOnlyList<MotionInfo>)mainType.GetField("_relatedPreviewMotions",flags)!.GetValue(main)!;
        var pairIndex=related.ToList().FindIndex(m=>m.MotionNumber==pair.Item2);
        if(pairIndex<0||related.Count!=1)throw new Exception("Confirmed pair list is incorrect");
        main.FindControl<ComboBox>("AdditiveMotionCombo")!.SelectedIndex=pairIndex+1;
        sync.SetValue(main,false);
        mainType.GetMethod("UpdateBlendPreviewDescription",flags)!.Invoke(main,null);
        var pending=(Task)mainType.GetMethod("LoadSelectedMotionAsync",flags)!.Invoke(main,new object?[]{null})!;
        var deadline=DateTime.UtcNow.AddMinutes(1);
        while(!pending.IsCompleted&&DateTime.UtcNow<deadline){Dispatcher.UIThread.RunJobs();Thread.Sleep(10);}
        if(!pending.IsCompleted)throw new Exception("Candidate replay timed out");
        pending.GetAwaiter().GetResult();
        var clip=(AnimationClip)vt.GetField("_clip",flags)!.GetValue(viewport)!;
        if(!clip.Name.Contains($"mot_{pair.Item1}")||!clip.Name.Contains($"mot_{pair.Item2}")||!clip.Name.Contains("游戏分层"))
            throw new Exception("Candidate did not load using the base layer context: "+clip.Name);
        var activeModels=new[]{vt.GetField("_primary",flags)!.GetValue(viewport)!}.Concat(((System.Collections.IEnumerable)vt.GetField("_extras",flags)!.GetValue(viewport)!).Cast<object>()).ToArray();
        var activeBody=activeModels.Single(m=>Mesh(m).Bones.Any(b=>b.Name=="Hip"&&!b.IsMotionHelper));
        int frames=(int)MathF.Ceiling(clip.Duration*60);
        for(int frame=0;frame<=frames;frame++)
        {
            vt.GetMethod("EvaluateAllPose",flags)!.Invoke(viewport,[Math.Min(frame/60f,clip.Duration)]);
            var bg=(System.Numerics.Matrix4x4[])activeBody.GetType().GetField("BoneGlobals")!.GetValue(activeBody)!;
            foreach(var model in activeModels)
            {
                var mesh=Mesh(model);
                var globals=(System.Numerics.Matrix4x4[])model.GetType().GetField("BoneGlobals")!.GetValue(model)!;
                foreach(var name in new[]{"Spine_2","Neck_0","Head"})
                {
                    int mi=Array.FindIndex(mesh.Bones,b=>b.Name==name),bi=Array.FindIndex(Mesh(activeBody).Bones,b=>b.Name==name);
                    if(System.Numerics.Vector3.Distance(globals[mi].Translation,bg[bi].Translation)>1e-5f)throw new Exception("Candidate disconnected part");
                }
                var vertices=(System.Numerics.Vector3[])model.GetType().GetField("Posed")!.GetValue(model)!;
                if(vertices.Any(v=>!float.IsFinite(v.X)||!float.IsFinite(v.Y)||!float.IsFinite(v.Z)))throw new Exception("Invalid native vertices");
            }
            if((pair.Item1 is 156 or 805 or 850) && (frame == frames / 2 || frame == frames))
            {
                var posedModels=new List<(ViewportMesh Mesh,IReadOnlySet<int> VisibleGroups)>();
                foreach(var model in activeModels)
                {
                    var mesh=Mesh(model);var posed=mesh.WithTransform(System.Numerics.Matrix4x4.Identity);
                    posed.Vertices=((System.Numerics.Vector3[])model.GetType().GetField("Posed")!.GetValue(model)!).ToArray();
                    posed.Normals=((System.Numerics.Vector3[])model.GetType().GetField("PosedNormals")!.GetValue(model)!).ToArray();
                    posed.Bones=[];posed.DeformToBone=[];posed.Weights=posed.Vertices.Select(_=>Array.Empty<(int,float)>()).ToArray();
                    posedModels.Add((posed,mesh.Groups.Where(g=>g.DefaultVisible).Select(g=>g.Key).ToHashSet()));
                }
                var file=Path.GetFullPath($"{poseDirectory}/{pair.Item1}-{pair.Item2}-{frame}.glb");
                new ViewportExportService().ConvertMergedToGlb(posedModels,file);
                renderJobs.Add(new{label=$"{pair.Item1}+{pair.Item2} native frame {frame}",file,duration=0f,variant=$"native-{frame}",bid=pair.Item1,aid=pair.Item2});
            }
        }
        Console.WriteLine($"NATIVE_CONFIRMED_SWITCH_{pair.Item1}_{pair.Item2}_{frames+1}_FRAMES_PASS");
    }
    File.WriteAllText(poseDirectory+"/jobs.json",System.Text.Json.JsonSerializer.Serialize(renderJobs));
    Console.WriteLine("NATIVE_SKINNING_36_FRAMES_SHARED_HEAD_AND_3_LOAD_ORDERS_PASS");
    Console.WriteLine("HEADLESS_REAL_MODEL_AND_ANIMATION_SESSION_PASS "+result.RootElement);
    main.Close();return;
}
var motions=new[]{new MotionInfo(16,"walk（编号 400）",400),new MotionInfo(52,"reload（编号 810）",810),new MotionInfo(53,"reload_add（编号 811）",811),new MotionInfo(44,"put_out（编号 541）",541),new MotionInfo(45,"put_out_add（编号 542）",542),new MotionInfo(99,"unknown_add（编号 812）",812)};
void Load(string path,IReadOnlyList<MotionInfo> list)=>typeof(MainWindow).GetMethod("SetMotionListUi",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,[path,list,0]);
const string re4="natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663";
MotionInfo[] realMotions;
using(var inventory=System.Text.Json.JsonDocument.Parse(File.ReadAllText("artifacts/re4-audit/motions.json")))
{
    var real=inventory.RootElement.EnumerateArray().Where(e=>e.GetProperty("path").GetString()==re4)
        .Select(e=>new MotionInfo(e.GetProperty("index").GetInt32(),e.GetProperty("name").GetString()!,e.GetProperty("id").GetInt32())).ToArray();
    realMotions=real;
    if(real.Count(AnimationLayerCatalog.IsAdditive)!=23)throw new Exception("Real ADD inventory lost");
    if(real.Count(m=>AnimationLayerCatalog.IsAdditive(re4,m))!=28)throw new Exception("Configured additive inventory lost");
    foreach(var id in new[]{161,520})
    {
        var item=real.Single(m=>m.MotionNumber==id);
        if(!AnimationLayerCatalog.IsAdditive(re4,item)||AnimationLayerCatalog.IsAdditive("other.motlist.663",item))
            throw new Exception("FSM-derived classification missing or leaked to another list");
        if(AnimationLayerCatalog.IsAdditive(re4,new MotionInfo(item.SourceIndex,"unrelated",id)))throw new Exception("Classification ignored resource identity");
    }
    var bases=real.Where(m=>!AnimationLayerCatalog.IsAdditive(re4,m)).ToArray();
    if(bases.Count(b=>AnimationLayerCatalog.Find(re4,b,real).Count>0)!=17)throw new Exception("Configured states missing from paired previews");
    var reload=AnimationLayerCatalog.Find(re4,real.Single(m=>m.MotionNumber==810),real);
    if(reload.Count!=1||reload[0].Motion.MotionNumber!=811||!reload[0].PreviewSupported||!reload[0].Confirmed)
        throw new Exception("Unverified variant exposed as pairing");
    if(AnimationLayerCatalog.Find(re4,real.Single(m=>m.MotionNumber==400),real).Count!=0)throw new Exception("Unrelated walking action matched");
    var other=AnimationLayerCatalog.Find("other.motlist.663",real.Single(m=>m.MotionNumber==810),real);
    if(other.Any(c=>c.Confirmed||c.PreviewSupported))throw new Exception("Preview profile leaked into other list");
}
Load(re4,motions);
var panel=main.FindControl<Border>("BlendPreviewPanel")!;
var additive=main.FindControl<ComboBox>("AdditiveMotionCombo")!;
if(main.FindControl<ComboBox>("BlendWeightCombo") != null) throw new Exception("Percentage selector must be removed");
var motion=main.FindControl<ComboBox>("MotionCombo")!;
var raw=main.FindControl<CheckBox>("ShowRawMotionTracks")!;
var advanced=main.FindControl<Expander>("AdvancedMotionOptions")!;
if(advanced.IsExpanded || raw.IsChecked == true)throw new Exception("Advanced raw mode must start closed");
if(!panel.IsVisible||motion.ItemCount!=3||additive.ItemCount!=1)throw new Exception("Base list separation");
motion.SelectedIndex=1;
if(additive.ItemCount!=2||additive.SelectedIndex!=1)throw new Exception("Related layer missing or not automatically enabled");
additive.SelectedIndex=1;
if(motion.SelectedIndex!=1)throw new Exception("Additive replaced base");
var indices=(int[])typeof(MainWindow).GetField("_currentMotionIndices",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
if(indices[motion.SelectedIndex]!=52)throw new Exception("Current export no longer points to base");
additive.SelectedIndex=0;
if(motion.SelectedIndex!=1)throw new Exception("Disable changed base");
motion.SelectedIndex=2;
if(additive.ItemCount!=2||!((ComboBoxItem)additive.Items[1]!).IsEnabled)throw new Exception("Manual layer unavailable");
additive.SelectedIndex=1;
if(motion.SelectedIndex!=2)throw new Exception("Manual layer replaced base");
advanced.IsExpanded=true;raw.IsChecked=true;
if(additive.IsEnabled)throw new Exception("Raw mode must disable composition");
if(motion.ItemCount!=6)throw new Exception("Raw tracks missing");
motion.SelectedIndex=2;
if(additive.ItemCount!=1)throw new Exception("Raw mode offered composition");
advanced.IsExpanded=false;
if(motion.ItemCount!=3||additive.SelectedIndex!=1)throw new Exception("Reset filter or confirmed pair not restored");
if(raw.IsChecked==true||motion.SelectedIndex!=2||!additive.IsEnabled)throw new Exception("Closing advanced must restore prior base");
Load("natives/stm/sf6/test.motlist.653",motions);
if(!panel.IsVisible||motion.ItemCount!=6)throw new Exception("Other games must retain raw indices");
Load(re4,[motions[1]]);
if(additive.ItemCount!=1)throw new Exception("Absent layer offered");
Load(re4,realMotions);
var realBases=realMotions.Where(m=>!AnimationLayerCatalog.IsAdditive(re4,m)).ToArray();
if(realBases.Any(m=>m.MotionNumber is 161 or 520))throw new Exception("ADD remains in main-motion list");
foreach(var id in new[]{161,520})
{
    advanced.IsExpanded=true;raw.IsChecked=true;
    motion.SelectedIndex=Array.FindIndex(realMotions,m=>m.MotionNumber==id);
    var mapping=(int[])typeof(MainWindow).GetField("_currentMotionIndices",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
    if(mapping[motion.SelectedIndex]!=realMotions[motion.SelectedIndex].SourceIndex)
        throw new Exception("Original export index lost");
    advanced.IsExpanded=false;
    if(realBases[motion.SelectedIndex].MotionNumber is 161 or 520)throw new Exception("Leaving raw mode restored ADD as main motion");
}
Console.WriteLine("FSM_161_520_FILTER_RAW_WARNING_AND_EXPORT_PASS");
for(int i=0;i<realBases.Length;i++)
{
    motion.SelectedIndex=i;
    var mapping=(int[])typeof(MainWindow).GetField("_currentMotionIndices",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
    if(mapping[i]!=realBases[i].SourceIndex)throw new Exception("Real export mapping changed");
    var choices=AnimationLayerCatalog.Find(re4,realBases[i],realMotions).Where(c=>c.Confirmed).ToArray();
    if(additive.ItemCount!=choices.Length+1)throw new Exception("UI includes an unverified candidate or lost a confirmed pair");
    for(int j=0;j<choices.Length;j++)
        {
            if(!((ComboBoxItem)additive.Items[j+1]!).IsEnabled)throw new Exception("Manual candidate disabled");
            additive.SelectedIndex=j+1;
            if(motion.SelectedIndex!=i)throw new Exception("Manual candidate changed base");
        }
    advanced.IsExpanded=true;raw.IsChecked=true;
    motion.SelectedIndex=Array.FindIndex(realMotions,m=>AnimationLayerCatalog.IsAdditive(m));
    advanced.IsExpanded=false;
    if(motion.SelectedIndex!=i)throw new Exception("Real base not restored after ADD inspection");
}
Console.WriteLine($"REAL_UI_ALL_{realBases.Length}_BASES_AND_EXPORT_MAPPING_PASS");
foreach(var id in new[]{810,910,1810,1910}) {
    motion.SelectedIndex=Array.FindIndex(realBases,m=>m.MotionNumber==id); additive.SelectedIndex=1;
    if(!(bool)typeof(MainWindow).GetProperty("UseVerifiedReloadMask",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!)throw new Exception("UI mask missing "+id);
}
motion.SelectedIndex=Array.FindIndex(realBases,m=>m.MotionNumber==160);
if(additive.ItemCount!=1)
    throw new Exception("Unverified shoot pairing shown in normal preview");
advanced.IsExpanded=true;raw.IsChecked=true;
motion.SelectedIndex=Array.FindIndex(realMotions,m=>m.MotionNumber==500);
advanced.IsExpanded=false;
Console.WriteLine("RELOAD_MASK_ROUTING_AND_SHOOT_UI_PASS");
motion.SelectedIndex=Array.FindIndex(realBases,m=>m.MotionNumber==551);additive.SelectedIndex=1;
var layerProfile=typeof(MainWindow).GetProperty("SelectedLayerProfile",BindingFlags.NonPublic|BindingFlags.Instance)!;
if(layerProfile.GetValue(main) is not AnimationLayerProfile {BaseMaskId:3,AdditiveMaskId:4}
    || !main.FindControl<TextBlock>("BlendPreviewDescription")!.Text!.Contains("游戏配置配对"))throw new Exception("Holster layer order missing");
additive.SelectedIndex=0;
if(layerProfile.GetValue(main)==null)throw new Exception("Disabling ADD changed upper-body reference");
advanced.IsExpanded=true;raw.IsChecked=true;
if(layerProfile.GetValue(main)!=null)throw new Exception("Raw mode unexpectedly applies masks");
advanced.IsExpanded=false;
Console.WriteLine("HOLSTER_ORDER_DISABLE_AND_RAW_MODE_PASS");
foreach(var (bid,aid) in new[]{(541,542),(543,544),(551,552),(810,811)})
{
    motion.SelectedIndex=Array.FindIndex(realBases,m=>m.MotionNumber==bid);
    var baseline=layerProfile.GetValue(main);
    var related=(IReadOnlyList<MotionInfo>)typeof(MainWindow).GetField("_relatedPreviewMotions",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
    if(additive.ItemCount!=2||related.Count!=1||related[0].MotionNumber!=aid)throw new Exception("Misleading pair shown for "+bid);
    additive.SelectedIndex=1;
    if(!Equals(baseline,layerProfile.GetValue(main)))throw new Exception("Changing ADD lost base layer context");
}
if(main.FindControl<TextBlock>("MotionLabel")!.Text!="动作轨道")throw new Exception("Unknown tracks mislabeled main motion");
Console.WriteLine("ONLY_CONFIRMED_PAIRS_AND_TRACK_USAGE_LABEL_PASS");
// Real evidence from different banks/lists must not be inferred from numeric IDs.
var nearMiss=new MotionInfo(0,"cha0_general2_0140_nearmiss_L",140);
var generalPath="natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_general2.motlist.663";
if(AnimationUsageCatalog.Get(generalPath,nearMiss).Kind!=AnimationUsageKind.Additive)throw new Exception("Unmarked near-miss ADD missed");
if(AnimationUsageCatalog.Get("other",nearMiss).Kind!=AnimationUsageKind.Unknown)throw new Exception("Bank context leaked");
Load(generalPath,[nearMiss]);
if(raw.IsChecked!=true||raw.IsEnabled||motion.ItemCount!=1||additive.IsEnabled)throw new Exception("ADD-only list silently shown as main animation");
advanced.IsExpanded=false;
if(raw.IsChecked!=true||motion.ItemCount!=1)throw new Exception("Collapsing raw-only list lost selection");
Load(re4,realMotions);
motion.SelectedIndex=Array.FindIndex(realBases,m=>m.MotionNumber==400);
Console.WriteLine("CROSS_BANK_CONTEXT_UNKNOWN_OVERRIDE_AND_RAW_ONLY_PASS");
// v1.4.4 restores the previously loaded selection when a load fails. Verify
// that filtering and additive choices are restored together with that base.
motion.SelectedIndex=Array.FindIndex(realBases,m=>m.MotionNumber==910);additive.SelectedIndex=1;
typeof(MainWindow).GetMethod("RememberLoadedMotionUi",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,null);
advanced.IsExpanded=true;raw.IsChecked=true;
motion.SelectedIndex=Array.FindIndex(realMotions,m=>m.MotionNumber==500);
typeof(MainWindow).GetMethod("RestoreLoadedMotionUi",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,null);
if(raw.IsChecked==true||realBases[motion.SelectedIndex].MotionNumber!=910||additive.SelectedIndex!=1)
    throw new Exception("Failed load did not restore base, overlay and mode");
Console.WriteLine("V144_FAILED_LOAD_SELECTION_RESTORE_PASS");
var scaledTrack=new BoneTrack {ScaleTimes=[0,1],Scales=[System.Numerics.Vector3.One,new(2,3,4)]};
var evaluator=typeof(MainWindow).Assembly.GetType("ReExtractor.Gui.GlViewport")!.GetMethod("EvaluateLocal",BindingFlags.NonPublic|BindingFlags.Static)!;
var evaluated=(System.Numerics.Matrix4x4)evaluator.Invoke(null,[scaledTrack,.5f,System.Numerics.Matrix4x4.Identity])!;
var point=System.Numerics.Vector3.Transform(System.Numerics.Vector3.One,evaluated);
if(System.Numerics.Vector3.Distance(point,new(1.5f,2,2.5f))>1e-6)throw new Exception("v1.4.4 viewport scale regression");
Console.WriteLine("V144_VIEWPORT_SCALE_EVALUATOR_PASS");
motion.SelectedIndex=Array.FindIndex(realBases,m=>m.MotionNumber==810);additive.SelectedIndex=1;
((Panel)panel.Parent!).Children.Remove(panel);((Panel)motion.Parent!).Children.Remove(motion);
motion.Width=300;motion.HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Left;
var content=new StackPanel{Spacing=8,Children={new TextBlock{Text=main.FindControl<TextBlock>("MotionLabel")!.Text},motion,panel}};
var window=new Window{Width=840,Height=290,Content=new Border{Padding=new Thickness(12),Child=content}};window.Show();Dispatcher.UIThread.RunJobs();
window.CaptureRenderedFrame()!.Save("artifacts/re4-audit/layers-wide.png");
window.Width=420;window.Height=390;Dispatcher.UIThread.RunJobs();
window.CaptureRenderedFrame()!.Save("artifacts/re4-audit/layers-narrow.png");
window.Close();main.Close();Console.WriteLine("BASE_LAYER_SEPARATION_SELECTION_EXPORT_MAPPING_AND_LAYOUT_PASS");


