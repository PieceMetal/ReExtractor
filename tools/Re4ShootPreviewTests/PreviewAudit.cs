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

static class PreviewAudit
{
    const BindingFlags Flags=BindingFlags.NonPublic|BindingFlags.Instance;
    public static void Run(string root, bool neutralOnly=false)
    {
        var outRoot=Path.Combine(root,"preview");Directory.CreateDirectory(outRoot);
        Environment.SetEnvironmentVariable("REEXTRACTOR_DATA_DIR",Path.Combine(outRoot,"ui-data"));
        AppBuilder.Configure<Application>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).SetupWithoutStarting();
        Application.Current!.Styles.Add(new FluentTheme());
        var main=new MainWindow();
        object? Field(string name)=>typeof(MainWindow).GetField(name,Flags)!.GetValue(main);
        object? Call(string name,params object?[] values)=>typeof(MainWindow).GetMethod(name,Flags)!.Invoke(main,values);
        void Check(bool good,string message){if(!good)throw new InvalidDataException(message);}
        void Pump(Task task)
        {
            var until=DateTime.UtcNow.AddMinutes(3);
            while(!task.IsCompleted&&DateTime.UtcNow<until){Dispatcher.UIThread.RunJobs();Thread.Sleep(5);}
            Check(task.IsCompleted,"GUI loading timeout");task.GetAwaiter().GetResult();
        }
        var initialSession=Path.Combine(root,"initial-preview.json");
        var body="natives/stm/_chainsaw/character/ch/cha0/cha000/00/cha000_00.mesh.221108797";
        var head="natives/stm/_chainsaw/character/ch/cha0/cha000/10/cha000_10.mesh.221108797";
        var hair="natives/stm/_chainsaw/character/ch/cha0/cha000/20/cha000_20.mesh.221108797";
        Shared.Save(root,"initial-preview",new {ListPath=Shared.List,GameDirectory=Shared.Game,MeshPaths=new[]{body,head,hair},
            MotionListPath="natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_tram.motlist.663",BaseId=101,AdditiveId=130,PauseAtSeconds=.12});
        Pump((Task)Call("LoadPreviewSessionAsync",initialSession)!);
        using(var d=JsonDocument.Parse(File.ReadAllText(initialSession+".result.json")))Check(d.RootElement.GetProperty("Ready").GetBoolean(),d.RootElement.ToString());
        var pak=(PakService)Field("_pak")!;var resolved=Re4AnimationLayerResolver.Resolve(pak);
        ConfigurationGuards.Run(pak);
        var view=main.FindControl<GlViewport>("Viewport")!;
        var combo=main.FindControl<ComboBox>("MotionCombo")!;var addCombo=main.FindControl<ComboBox>("AdditiveMotionCombo")!;
        var advanced=main.FindControl<Expander>("AdvancedMotionOptions")!;var rawToggle=main.FindControl<CheckBox>("ShowRawMotionTracks")!;
        var sync=typeof(MainWindow).GetField("_syncingMotionUi",Flags)!;
        var vt=view.GetType();
        object[] Models()=>new[]{vt.GetField("_primary",Flags)!.GetValue(view)!}
            .Concat(((IEnumerable)vt.GetField("_extras",Flags)!.GetValue(view)!).Cast<object>()).ToArray();
        ViewportMesh Mesh(object model)=>(ViewportMesh)model.GetType().GetField("Mesh")!.GetValue(model)!;
        Vector3[] Pose(float time)
        {
            vt.GetMethod("EvaluateAllPose",Flags)!.Invoke(view,[time]);
            var models=Models();var driver=models.First(m=>Mesh(m).Bones.Any(b=>b.Name=="Hip"&&!b.IsMotionHelper));
            var driverGlobals=(Matrix4x4[])driver.GetType().GetField("BoneGlobals")!.GetValue(driver)!;
            foreach(var model in models)
            {
                var globals=(Matrix4x4[])model.GetType().GetField("BoneGlobals")!.GetValue(model)!;
                foreach(var name in new[]{"Spine_2","Neck_0","Head"})
                {
                    int bi=Array.FindIndex(Mesh(driver).Bones,b=>b.Name==name),mi=Array.FindIndex(Mesh(model).Bones,b=>b.Name==name);
                    Check(Vector3.Distance(driverGlobals[bi].Translation,globals[mi].Translation)<1e-4f,"Disconnected model part: "+name);
                }
            }
            var points=models.SelectMany(m=>(Vector3[])m.GetType().GetField("Posed")!.GetValue(m)!).ToArray();
            Check(points.All(v=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z)),"Non-finite skinned vertex");
            return points;
        }
        void Select(int id,int? add,bool raw=false)
        {
            sync.SetValue(main,true);
            try {
                advanced.IsExpanded=raw;rawToggle.IsChecked=raw;
                // Choice population is the GUI's implementation, including its usage filter.
                Call("PopulatePreviewMotionChoices",-1);
                var visible=(IReadOnlyList<MotionInfo>)Field("_visiblePreviewMotions")!;
                var index=visible.ToList().FindIndex(m=>m.MotionNumber==id);
                Check(index>=0,"Selected ID filtered out: "+id);combo.SelectedIndex=index;
                Call("ResetAdditiveChoices");
                if(add.HasValue){var candidates=(IReadOnlyList<MotionInfo>)Field("_relatedPreviewMotions")!;
                    var ai=candidates.ToList().FindIndex(m=>m.MotionNumber==add);Check(ai>=0,"No confirmed ADD choice: "+add);addCombo.SelectedIndex=ai+1;}
                else addCombo.SelectedIndex=0;
            }finally{sync.SetValue(main,false);}
            Call("UpdateBlendPreviewDescription");Pump((Task)Call("LoadSelectedMotionAsync",(object?)null)!);
            var current=(IReadOnlyList<MotionInfo>)Field("_visiblePreviewMotions")!;
            Check((int)Field("_loadedMotionSource")! == current[combo.SelectedIndex].SourceIndex,"GUI load failed or restored a different motion");
            Check((int)Field("_loadedAdditiveSource")! == (add.HasValue ? ((IReadOnlyList<MotionInfo>)Field("_relatedPreviewMotions")!).Single(m=>m.MotionNumber==add).SourceIndex : -1),
                "GUI overlay load failed: "+main.FindControl<TextBlock>("ActionStatus")!.Text);
            view.PausePlayback();
        }
        Check(resolved.Diagnostics.Count==0,string.Join("; ",resolved.Diagnostics));
        Check(resolved.Usages?.Count==66,"Expected 66 resource-scoped ordinary shoot usages");
        Check(resolved.Pairs.Count(p=>p.Composition==AnimationLayerComposition.PlayerShootEndpoint)==198,"Expected 22 weapon lists with nine fixed input combinations each");
        Check(resolved.Sources?.Count(s=>s.ListPath!=s.SourcePath)==33,"Expected 11 weapon lists inheriting three base inputs each");
        Shared.Save(root,"runtime-catalog",resolved);
        var paths=resolved.Pairs.Select(p=>p.Path).Where(p=>!neutralOnly||p.EndsWith("cha0_wp4410h.motlist.663")).Distinct().Order().ToArray();
        using var pairWriter=new StreamWriter(Path.Combine(outRoot,"pairs.jsonl")){AutoFlush=true};
        using var rawWriter=new StreamWriter(Path.Combine(outRoot,"shoot-raw.jsonl")){AutoFlush=true};
        var exporter=new ViewportExportService();
        int pairsDone=0,pairFailures=0,rawDone=0,rawFailures=0,zeroDelta=0,missing=0;
        var missingPairs=new List<object>();
        foreach(var path in paths)
        {
            IReadOnlyList<MotionInfo> motions;
            try {using var s=pak.ReadFile(path);motions=resolved.PreviewMotions(path,ViewportDataLoader.ListMotions(s,path));Call("SetMotionListUi",path,motions,0);}
            catch(Exception e){pairWriter.WriteLine(JsonSerializer.Serialize(new {Path=path,Status="LIST_ERROR",Error=e.Message}));pairFailures++;continue;}
            var pairs=resolved.ForList(path,motions);
            foreach(var def in resolved.Pairs.Where(p=>p.Path==path).Except(pairs)) {missing++;missingPairs.Add(new {Path=path,def.BaseId,def.AdditiveId,Reason="Missing, duplicated or external source clip"});}
            foreach(var pair in pairs)
            {
                try
                {
                    Select(pair.BaseId,pair.AdditiveId);
                    if(pair.Composition==AnimationLayerComposition.PlayerShootEndpoint)
                    {
                        Check(addCombo.ItemCount==4,"Shoot must expose three confirmed variants plus off");
                        var visible=(IReadOnlyList<MotionInfo>)Field("_visiblePreviewMotions")!;
                        Check(!visible.Any(m=>m.MotionNumber is 500 or 501 or 502),"Raw shoot leaked into ordinary complete motion list");
                        var usage=AnimationUsageCatalog.Get(path,motions.Single(m=>m.MotionNumber==pair.AdditiveId),pairs,resolved.Usages);
                        Check(usage.Kind==AnimationUsageKind.Additive,"Shoot usage not recorded");
                        Check(!AnimationLayerCatalog.IsAdditive("other.motlist.663",motions.Single(m=>m.MotionNumber==pair.AdditiveId),pairs,resolved.Usages),"Shoot usage leaked to a different resource");
                        Check(main.FindControl<TextBlock>("BlendPreviewDescription")!.Text!.Contains("固定输入"),"Fixed-input scope missing");
                    }
                    var on=view.ExportAnimationClip!;
                    Check(on.NamedTracks.Values.All(t=>(t.Translations??[]).All(v=>float.IsFinite(v.LengthSquared()))
                        &&(t.Rotations??[]).All(q=>float.IsFinite(q.LengthSquared())&&Math.Abs(q.LengthSquared()-1)<.001f)),"Invalid combined keyframe");
                    var shotDuration=pair.Composition==AnimationLayerComposition.PlayerShootEndpoint
                        ? Re4AnimationResourceCatalog.LoadPreview(pak,path,motions.Single(m=>m.MotionNumber==pair.AdditiveId).SourceIndex,
                            resolved,meshes:view.SceneMeshes).Duration : on.Duration;
                    var times=Enumerable.Range(0,9).Select(i=>on.Duration*i/8)
                        .Concat(Enumerable.Range(0,9).Select(i=>Math.Min(on.Duration,shotDuration*i/8))).Distinct().Order().ToArray();
                    var onPoses=times.Select(Pose).ToArray();
                    var maxExtent=onPoses.Select(points=>Vector3.Distance(new Vector3(points.Min(v=>v.X),points.Min(v=>v.Y),points.Min(v=>v.Z)),
                        new Vector3(points.Max(v=>v.X),points.Max(v=>v.Y),points.Max(v=>v.Z)))).Max();
                    Select(pair.BaseId,null);var off=view.ExportAnimationClip!;
                    Check(Math.Abs(on.Duration-off.Duration)<1e-6,"On/off duration differs");
                    var delta=times.Select((t,i)=>Pose(t).Zip(onPoses[i],Vector3.Distance).Max()).Max();
                    if(delta<1e-5)
                    {
                        zeroDelta++;
                        Check(resolved.Usages?.Any(u=>u.Path==path&&u.Id==pair.AdditiveId&&u.IsNeutralDelta)==true,
                            "No on/off delta from a non-neutral source");
                        Select(pair.BaseId,pair.AdditiveId);
                        Check(main.FindControl<TextBlock>("BlendPreviewDescription")!.Text!.Contains("零增量"),"Neutral source explanation missing");
                        Select(pair.BaseId,null);
                    }
                    var bones=AnimationSkeleton.PreviewBones(view.SceneMeshes);
                    AnimationClip Load(int id)=>Re4AnimationResourceCatalog.LoadPreview(pak,path,motions.Single(m=>m.MotionNumber==id).SourceIndex,
                        resolved,meshes:view.SceneMeshes);
                    var basis=Load(pair.BaseId);var add=Load(pair.AdditiveId);
                    var bm=AnimationPreviewProfiles.ReadRe4Mask(pak,bones,pair.BaseMaskId,pair.JointMapPath);
                    var am=AnimationPreviewProfiles.ReadRe4Mask(pak,bones,pair.AdditiveMaskId,pair.JointMapPath);
                    var zero=pair.Composition switch {
                        AnimationLayerComposition.MotionTreeEndpoint=>AnimationPreviewMixer.ComposeMotionTreeEndpoint(basis,add,add,bones,bm,am,0),
                        AnimationLayerComposition.PlayerShootEndpoint=>AnimationPreviewMixer.ComposePlayerShootEndpoint(basis,add,bones,bm,am,0),
                        _=>AnimationPreviewMixer.ComposeUpperBody(basis,add,bones,bm,am,0)};
                    Check(Math.Abs(zero.Duration-off.Duration)<1e-6,"Zero-weight duration differs");
                    foreach(var (name,track) in zero.NamedTracks)
                    {
                        var other=off.NamedTracks[name];
                        Check(track.Translations!.Zip(other.Translations!,Vector3.Distance).All(d=>d<1e-5)
                            &&track.Rotations!.Zip(other.Rotations!, (a,b)=>1-Math.Abs(Quaternion.Dot(a,b))).All(d=>d<1e-5),"Zero does not match overlay off: "+name);
                    }
                    // This is the current composed export path, checked against the selected preview's duration.
                    var merged=exporter.BuildMergedExportModel(view.ExportModels.Select(m=>(m.Mesh,m.VisibleGroups)).ToArray());
                    var glb=Path.Combine(outRoot,"current-glb",Path.GetFileName(path)+"-"+pair.BaseId+"-"+pair.AdditiveId+".glb");
                    exporter.ConvertToAnimatedGlb(merged.Mesh,merged.VisibleGroups,on,glb);
                    var saved=ModelRoot.Load(glb);
                    Check(saved.LogicalAnimations.Count==1&&Math.Abs(saved.LogicalAnimations[0].Duration-on.Duration)<1e-4,"Current export animation/time differs");
                    pairWriter.WriteLine(JsonSerializer.Serialize(new {Path=path,pair.BaseId,pair.AdditiveId,pair.Composition,pair.BaseMaskId,pair.AdditiveMaskId,
                        Status="PASS_NUMERIC_GUI_SELECTION_SKINNING_EXPORT",on.Duration,KeyFrames=on.FrameCount+1,NativePoseSamples=times.Length,OnOffDelta=delta,
                        MaxExtent=maxExtent,CurrentGlb=glb,VisualAcceptance=false}));pairsDone++;
                }
                catch(Exception e){pairFailures++;pairWriter.WriteLine(JsonSerializer.Serialize(new {Path=path,pair.BaseId,pair.AdditiveId,Status="ERROR",Error=e.ToString()}));}
            }
            foreach(var motion in motions.Where(m=>m.DisplayName.Contains("shoot",StringComparison.OrdinalIgnoreCase)))
            {
                try {
                    Select(motion.MotionNumber,null,true);var clip=view.ExportAnimationClip!;
                    var poses=Enumerable.Range(0,9).Select(i=>Pose(clip.Duration*i/8)).ToArray();
                    rawWriter.WriteLine(JsonSerializer.Serialize(new {Path=path,Id=motion.MotionNumber,Name=motion.DisplayName,Status="PASS_RAW_GUI_LOAD_AND_SKINNING",
                        clip.Duration,NativePoseSamples=9,AnimationName=clip.Name,Usage=AnimationUsageCatalog.Get(path,motion,pairs),
                        HasComposition=pairs.Any(p=>p.AdditiveId==motion.MotionNumber),VisualAcceptance=false}));rawDone++;
                } catch(Exception e){rawFailures++;rawWriter.WriteLine(JsonSerializer.Serialize(new {Path=path,Id=motion.MotionNumber,Name=motion.DisplayName,Status="ERROR",Error=e.ToString()}));}
            }
            Console.WriteLine($"PREVIEW {Path.GetFileName(path)} pairs={pairs.Length} total={pairsDone} errors={pairFailures} rawShoot={rawDone} rawErrors={rawFailures}");
        }
        Check(pairFailures==0&&rawFailures==0,"Preview failures recorded; inspect pairs.jsonl/shoot-raw.jsonl");
        Shared.Save(outRoot,"missing-source-pairs",missingPairs);
        Shared.Save(outRoot,"summary",new {Scope="Loaded Leon body/head/hair; fixed input previews sampled across base/shot windows; neutral sources explicitly identified; not gameplay or exhaustive visual acceptance",
            Paths=paths.Length,Definitions=resolved.Pairs.Count,PairsPassed=pairsDone,PairsFailed=pairFailures,PairsWithNoVisibleSampleDelta=zeroDelta,
            MissingOrExternalPairs=missing,RawShootsPassed=rawDone,RawShootsFailed=rawFailures});
        if(neutralOnly)ResultVerifier.Run(Path.GetDirectoryName(root)!,pak,resolved);
        main.Close();Console.WriteLine("PREVIEW_AUDIT_COMPLETE");
    }
}
