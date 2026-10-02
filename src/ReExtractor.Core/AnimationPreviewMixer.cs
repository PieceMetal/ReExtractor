using System.Numerics;
namespace ReExtractor.Core;

public readonly record struct PreviewJointWeight(float Translation, float Rotation);

/// <summary>Creates an isolated preview clip. Source clips and export data are never changed.</summary>
public static class AnimationPreviewMixer
{
    public const string ResourceGuide = """
动画资源使用说明
“当前动画（含预览叠加）”导出已加载的预览结果，作为普通动画播放，不要再次叠加。“全部动画”和批量导出保留原轨道；需要原始二进制文件时使用资源提取。
文件名中的 motion 是源列表索引，id 是游戏动作编号。附加轨道不能作为完整动作直接播放；识别同时参考名称和已核实的游戏层配置，161持枪危险状态循环、520空弹动作及500/501/502射击轨道虽然没有add后缀，也属于附加轨道。确认轨道类型不代表已确认可配对的主动作。
RE4 cha0_wp4000h 支持17组配置配对，涵盖持枪起止、拔枪收枪、换弹起手与收尾及装填爆炸物。选择这些动作会自动启用对应附加层，也可选择“不叠加”作对比。组合使用站姿参考，先应用附加层3，再用上半身层4覆盖；按各状态及其父状态读取游戏 JMAP 遮罩，包括普通3/4、换弹3/14、持枪结束10/11。被上层完全覆盖的通道不会重复叠加。不叠加和0%使用同一参考姿势；查看源动作请启用“单独检查原始轨道”。
普通预览的附加列表仅显示游戏配置已确认的配对；同名或编号相近的轨道不会作为可选配对。尚未核实的主动作只提供“不叠加”，不代表游戏没有附加层。高级选项仅支持单独检查原始轨道，尚不支持自由组合或N层编辑。界面“主动作”指所选动作，底层移动姿势当前以静态站姿代替。预览按原速从零时刻同步，以主动作时长为准，短轨道保持末帧。
未模拟底层移动动作、状态切换、动态权重或 IK，站姿参考预览不等于游戏运行效果。全部动画及批量导出保留所选基础/附加资源原轨道。在目标引擎中需要按层序、遮罩、参考姿势和权重重新组合，不能把所有 add 都加在最终动作之上。
RE4 部分列表引用 BaseMotListPath；当前导出只包含本列表内嵌动作，引用内容不保证完整，请另外导出基础列表。
""";
    public static AnimationClip Compose(AnimationClip basis, AnimationClip overlay,
        IReadOnlyList<ViewportBone> bones, float weight = 1,
        IReadOnlyDictionary<string, PreviewJointWeight>? mask = null)
    {
        weight = Math.Clamp(weight, 0, 1);
        if (weight == 0) return Copy(basis);
        // Preserve the base clock. A static hold pose can support a moving overlay.
        var duration = basis.Duration > 0 ? basis.Duration : overlay.Duration;
        var fps = Math.Clamp(Math.Max(basis.FrameRate, overlay.FrameRate), 1, 240);
        var count = Math.Max(1, (int)MathF.Ceiling(duration * fps));
        var times = Enumerable.Range(0, count + 1).Select(i => Math.Min(i / (float)fps, duration)).ToArray();
        var result = new AnimationClip { Name = basis.Name + " + " + overlay.Name,
            Duration = duration, FrameRate = fps, FrameCount = count, Tracks = new() };
        for (var i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            basis.NamedTracks.TryGetValue(bone.Name, out var b);
            overlay.NamedTracks.TryGetValue(bone.Name, out var a);
            var jointWeight = mask == null ? new PreviewJointWeight(1, 1) : mask.GetValueOrDefault(bone.Name);
            var translationWeight = weight * Math.Clamp(jointWeight.Translation, 0, 1);
            var rotationWeight = weight * Math.Clamp(jointWeight.Rotation, 0, 1);
            var bind = bone.LocalBind;
            Matrix4x4.Decompose(bind, out _, out var bindQ, out var bindT);
            var track = new BoneTrack { TransTimes = times, RotTimes = times,
                Translations = new Vector3[times.Length], Rotations = new Quaternion[times.Length] };
            // Preserve the base scale channel added in v1.4.4. RE4 overlay scale
            // semantics have not been established; do not invent a scale delta.
            track.ScaleTimes = b?.ScaleTimes?.ToArray();
            track.Scales = b?.Scales?.ToArray();
            for (var f = 0; f < times.Length; f++)
            {
                // Sample both clips in seconds; shorter tracks hold their final pose.
                var bt = times[f];
                var baseT = Translation(b, bt, bindT);
                var baseQ = Rotation(b, bt, bindQ);
                // RE4 ADD stores translation deltas already. Root travel belongs to the base.
                var addT = bone.ParentIndex < 0 ? Vector3.Zero : TranslationDelta(a, times[f]);
                // RE4 additive samples encode rotation deltas around identity, not an absolute bind pose.
                // Missing channels must contribute identity, never inverse(bind).
                var addQ = RotationDelta(a, times[f]);
                track.Translations[f] = baseT + translationWeight * addT;
                track.Rotations[f] = Quaternion.Normalize(baseQ * Quaternion.Slerp(Quaternion.Identity, addQ, rotationWeight));
            }
            result.Tracks[i] = track;
            result.NamedTracks[bone.Name] = track;
        }
        return result;
    }

    /// <summary>
    /// RE4 upper-body stack: standing reference -> masked ADD (layer 3) ->
    /// masked absolute upper body (layer 4). The full source base clip must NOT
    /// be used as the lower-layer reference: it already contains the authored
    /// body movement, and applying its paired delta again doubles that movement.
    /// Zero weight is the same masked upper-body preview as disabling ADD.
    /// Raw-track mode and exports continue to use the original clip.
    /// </summary>
    public static AnimationClip ComposeUpperBody(AnimationClip basis, AnimationClip? overlay,
        IReadOnlyList<ViewportBone> bones,
        IReadOnlyDictionary<string, PreviewJointWeight> upperBodyMask,
        IReadOnlyDictionary<string, PreviewJointWeight> additiveMask, float weight = 1)
    {
        weight = Math.Clamp(weight, 0, 1);
        var duration = basis.Duration > 0 ? basis.Duration : overlay?.Duration ?? 0;
        var fps = Math.Clamp(Math.Max(basis.FrameRate, overlay?.FrameRate ?? basis.FrameRate), 1, 240);
        var count = Math.Max(1, (int)MathF.Ceiling(duration * fps));
        var times = Enumerable.Range(0, count + 1).Select(i => Math.Min(i / (float)fps, duration)).ToArray();
        var result = new AnimationClip { Name = basis.Name + (overlay == null ? "" : " + " + overlay.Name),
            Duration = duration, FrameRate = fps, FrameCount = count, Tracks = new() };
        for (var i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            basis.NamedTracks.TryGetValue(bone.Name, out var b);
            BoneTrack? a = null;
            overlay?.NamedTracks.TryGetValue(bone.Name, out a);
            Matrix4x4.Decompose(bone.LocalBind, out _, out var bindQ, out var bindT);
            var upper = upperBodyMask.GetValueOrDefault(bone.Name);
            var add = additiveMask.GetValueOrDefault(bone.Name);
            var track = new BoneTrack { TransTimes = times, RotTimes = times,
                Translations = new Vector3[times.Length], Rotations = new Quaternion[times.Length],
                ScaleTimes = b?.ScaleTimes?.ToArray(), Scales = b?.Scales?.ToArray() };
            for (var f = 0; f < times.Length; f++)
            {
                var time = times[f];
                var lowerT = bindT + TranslationDelta(a, time) * (weight * Math.Clamp(add.Translation, 0, 1));
                var lowerQ = Quaternion.Normalize(bindQ * Quaternion.Slerp(Quaternion.Identity,
                    RotationDelta(a, time), weight * Math.Clamp(add.Rotation, 0, 1)));
                // A missing base channel does not overwrite a lower layer.
                var transWeight = b?.Translations is { Length: > 0 } ? Math.Clamp(upper.Translation, 0, 1) : 0;
                var rotWeight = b?.Rotations is { Length: > 0 } ? Math.Clamp(upper.Rotation, 0, 1) : 0;
                track.Translations[f] = Vector3.Lerp(lowerT, Translation(b, time, bindT), transWeight);
                track.Rotations[f] = Quaternion.Normalize(Quaternion.Slerp(lowerQ, Rotation(b, time, bindQ), rotWeight));
            }
            result.Tracks[i] = track;
            result.NamedTracks[bone.Name] = track;
        }
        return result;
    }
    private static AnimationClip Copy(AnimationClip source)
    {
        BoneTrack Clone(BoneTrack t) => new() { IsAdditive = t.IsAdditive,
            TransTimes = t.TransTimes?.ToArray(), Translations = t.Translations?.ToArray(),
            RotTimes = t.RotTimes?.ToArray(), Rotations = t.Rotations?.ToArray(),
            ScaleTimes = t.ScaleTimes?.ToArray(), Scales = t.Scales?.ToArray() };
        return new AnimationClip { Name = source.Name, Duration = source.Duration,
            FrameRate = source.FrameRate, FrameCount = source.FrameCount,
            Tracks = source.Tracks.ToDictionary(kv => kv.Key, kv => Clone(kv.Value)),
            NamedTracks = source.NamedTracks.ToDictionary(kv => kv.Key, kv => Clone(kv.Value), StringComparer.OrdinalIgnoreCase) };
    }
    private static (int A,int B,float T) Segment(float[] times, float t)
    {
        if (times.Length < 2 || t <= times[0]) return (0,0,0);
        for (var i=1;i<times.Length;i++) if(t<=times[i]) return(i-1,i, times[i]>times[i-1] ? (t-times[i-1])/(times[i]-times[i-1]):0);
        return(times.Length-1,times.Length-1,0);
    }
    private static Vector3 Translation(BoneTrack? track,float t,Vector3 bind)
    {
        if(track?.Translations is not {Length:>0} v || track.TransTimes is not {Length:>0} ts)return bind;
        var (a,b,w)=Segment(ts,t);var value=Vector3.Lerp(v[Math.Min(a,v.Length-1)],v[Math.Min(b,v.Length-1)],w);
        return track.IsAdditive ? bind+value : value;
    }
    private static Vector3 TranslationDelta(BoneTrack? track, float t)
    {
        if (track?.Translations is not { Length: > 0 } values || track.TransTimes is not { Length: > 0 } times)
            return Vector3.Zero;
        var (a, b, w) = Segment(times, t);
        return Vector3.Lerp(values[Math.Min(a, values.Length - 1)], values[Math.Min(b, values.Length - 1)], w);
    }
    private static Quaternion RotationDelta(BoneTrack? track, float t)
    {
        if (track?.Rotations is not { Length: > 0 } values || track.RotTimes is not { Length: > 0 } times)
            return Quaternion.Identity;
        var (a, b, w) = Segment(times, t);
        return Quaternion.Normalize(Quaternion.Slerp(values[Math.Min(a, values.Length - 1)],
            values[Math.Min(b, values.Length - 1)], w));
    }
    private static Quaternion Rotation(BoneTrack? track,float t,Quaternion bind)
    {
        if(track?.Rotations is not {Length:>0} v || track.RotTimes is not {Length:>0} ts)return bind;
        var (a,b,w)=Segment(ts,t);var value=Quaternion.Slerp(v[Math.Min(a,v.Length-1)],v[Math.Min(b,v.Length-1)],w);
        return Quaternion.Normalize(track.IsAdditive ? bind*value : value);
    }
}
