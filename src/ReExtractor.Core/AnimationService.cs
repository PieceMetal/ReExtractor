using System.Numerics;
using ReeLib;
using ReeLib.Common;
using ReeLib.Mesh;
using ReeLib.Mot;
using SharpGLTF.Scenes;

namespace ReExtractor.Core;

/// <summary>
/// Converts RE Engine .motlist animations to glTF animation tracks on top of a mesh skeleton.
/// </summary>
public sealed class AnimationService
{

    public IReadOnlyList<string> ConvertOneToGlbWithAnimation(
        ViewportMesh skeletonMesh,
        Stream motlistStream, string motlistPath,
        int motionIndex,
        string outputDirectory,
        Action<int, int>? progress = null)
    {
        using var motlist = new MotlistFile(new FileHandler(motlistStream, motlistPath));
        if (!motlist.Read()) throw new InvalidDataException($"Failed to parse .motlist: {motlistPath}");
        if (motionIndex < 0 || motionIndex >= motlist.Motions.Count)
            throw new ArgumentOutOfRangeException(nameof(motionIndex), $"Motion index {motionIndex} out of range ({motlist.Motions.Count} motions)");
        var motion = motlist.Motions[motionIndex];
        if (motion.MotFile is not MotFile mot)
            throw new NotSupportedException($"Motion {motionIndex} has no embedded .mot data");

        Directory.CreateDirectory(outputDirectory);
        var stem = Path.GetFileName(motlistPath);
        var marker = stem.IndexOf(".motlist", StringComparison.OrdinalIgnoreCase);
        if (marker > 0) stem = stem[..marker];

        var outputPath = Path.Combine(outputDirectory,
            $"001_{SafeName(stem)}_motion{motionIndex:D3}_id{motion.motNumber}.glb");
        var boneNames = skeletonMesh.Bones.Select(bone => bone.Name).ToArray();
        var clip = BuildClip(mot, motion.motNumber, boneNames);
        var visibleGroups = skeletonMesh.Groups.Select(group => group.Key).ToHashSet();
        new ViewportExportService().ConvertToAnimatedGlb(skeletonMesh, visibleGroups, clip, outputPath);
        progress?.Invoke(1, 1);
        return new[] { outputPath };
    }
public IReadOnlyList<string> ConvertAllToGlbWithAnimation(
        ViewportMesh skeletonMesh,
        Stream motlistStream, string motlistPath,
        string outputDirectory,
        Action<int, int>? progress = null)
    {
        using var motlist = new MotlistFile(new FileHandler(motlistStream, motlistPath));
        if (!motlist.Read()) throw new InvalidDataException($"Failed to parse .motlist: {motlistPath}");
        Directory.CreateDirectory(outputDirectory);
        var stem = Path.GetFileName(motlistPath);
        var marker = stem.IndexOf(".motlist", StringComparison.OrdinalIgnoreCase);
        if (marker > 0) stem = stem[..marker];

        var outputs = new List<string>();
        var exportableCount = motlist.Motions.Count(motion => motion.MotFile is MotFile);
        var boneNames = skeletonMesh.Bones.Select(bone => bone.Name).ToArray();
        var exporter = new ViewportExportService();
        var visibleGroups = skeletonMesh.Groups.Select(group => group.Key).ToHashSet();
        for (var index = 0; index < motlist.Motions.Count; index++)
        {
            var motion = motlist.Motions[index];
            if (motion.MotFile is not MotFile mot) continue;
            var outputPath = Path.Combine(outputDirectory,
                $"{outputs.Count + 1:D3}_{SafeName(stem)}_motion{index:D3}_id{motion.motNumber}.glb");
            var clip = BuildClip(mot, motion.motNumber, boneNames);
            exporter.ConvertToAnimatedGlb(skeletonMesh, visibleGroups, clip, outputPath);
            outputs.Add(outputPath);
            progress?.Invoke(outputs.Count, exportableCount);
        }
        return outputs;
    }

    public IReadOnlyList<string> ConvertAllToGlbWithAnimation(
        Stream meshStream, string meshPath,
        Stream motlistStream, string motlistPath,
        string outputDirectory,
        Action<int, int>? progress = null)
    {
        using var mesh = MeshService.LoadMesh(meshStream, meshPath);
        using var motlist = new MotlistFile(new FileHandler(motlistStream, motlistPath));
        if (!motlist.Read()) throw new InvalidDataException($"无法解析动画列表：{motlistPath}");
        Directory.CreateDirectory(outputDirectory);
        var stem = Path.GetFileName(motlistPath);
        var marker = stem.IndexOf(".motlist", StringComparison.OrdinalIgnoreCase);
        if (marker > 0) stem = stem[..marker];

        var outputs = new List<string>();
        var exportableCount = motlist.Motions.Count(motion => motion.MotFile is MotFile);
        for (var index = 0; index < motlist.Motions.Count; index++)
        {
            var motion = motlist.Motions[index];
            if (motion.MotFile is not MotFile mot) continue;
            var outputPath = Path.Combine(outputDirectory,
                $"{outputs.Count + 1:D3}_{SafeName(stem)}_动作{index:D3}_编号{motion.motNumber}.glb");
            WriteMotionGlb(mesh, mot, motion.motNumber, outputPath);
            outputs.Add(outputPath);
            progress?.Invoke(outputs.Count, exportableCount);
        }
        return outputs;
    }

    /// <summary>
    /// Export mesh + skeleton + one motion from a .motlist as GLB with animation.
    /// </summary>
    public string ConvertToGlbWithAnimation(
        Stream meshStream, string meshPath,
        Stream motlistStream, string motlistPath,
        string outputPath, int motionIndex = 0)
    {
        using var mesh = MeshService.LoadMesh(meshStream, meshPath);
        using var motlist = new MotlistFile(new FileHandler(motlistStream, motlistPath));
        if (!motlist.Read())
            throw new InvalidDataException($"Failed to parse .motlist: {motlistPath}");

        var motion = motlist.Motions.ElementAtOrDefault(motionIndex)
            ?? throw new InvalidDataException($"Motion index {motionIndex} out of range ({motlist.Motions.Count} motions)");
        if (motion.MotFile is not MotFile mot)
            throw new NotSupportedException("Motion has no embedded .mot data (external mot link not supported yet)");

        return WriteMotionGlb(mesh, mot, motion.motNumber, outputPath);
    }

    private static string WriteMotionGlb(MeshFile mesh, MotFile mot, int motionNumber, string outputPath)
    {
        var scene = new SceneBuilder();
        var skeleton = MeshService.BuildSkeletonInternal(mesh.BoneData);
        MeshService.ExportGeometry(scene, mesh, skeleton, lodIndex: 0);

        var animName = $"mot_{motionNumber}";
        var applied = 0;
        foreach (var clip in mot.BoneClips)
        {
            var node = ResolveBoneNode(skeleton, mesh, clip);
            if (node == null) continue;

            if (clip.HasRotation && clip.Rotation!.rotations is { Length: > 0 } rotations)
            {
                var frames = clip.Rotation.frameIndexes;
                var fps = clip.Rotation.frameRate > 0 ? clip.Rotation.frameRate : 60u;
                var keys = new Dictionary<float, Quaternion>(rotations.Length);
                for (var i = 0; i < rotations.Length; i++)
                {
                    var t = frames != null && i < frames.Length ? frames[i] / (float)fps : i / (float)fps;
                    var q = rotations[i];
                    if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                    keys[t] = Quaternion.Normalize(q);
                }
                node.WithLocalRotation(animName, keys);
            }

            if (clip.HasTranslation && clip.Translation!.translations is { Length: > 0 } translations)
            {
                var frames = clip.Translation.frameIndexes;
                var fps = clip.Translation.frameRate > 0 ? clip.Translation.frameRate : 60u;
                var keys = new Dictionary<float, Vector3>(translations.Length);
                for (var i = 0; i < translations.Length; i++)
                {
                    var t = frames != null && i < frames.Length ? frames[i] / (float)fps : i / (float)fps;
                    keys[t] = translations[i];
                }
                node.WithLocalTranslation(animName, keys);
            }

            applied++;
        }

        Console.WriteLine($"[anim] motion #{motionNumber}: {applied}/{mot.BoneClips.Count} bone clips applied");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        scene.ToGltf2().SaveGLB(outputPath);
        return outputPath;
    }

    private static string SafeName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value.Length > 80 ? value[..80] : value;
    }

    private static AnimationClip BuildClip(MotFile mot, int motionNumber, IReadOnlyList<string> meshBoneNames)
    {
        var hashToBone = new Dictionary<uint, (int Index, string Name)>(meshBoneNames.Count);
        for (var i = 0; i < meshBoneNames.Count; i++)
            hashToBone.TryAdd(MurMur3HashUtils.GetHash(meshBoneNames[i]), (i, meshBoneNames[i]));

        var tracks = new Dictionary<int, BoneTrack>();
        var namedTracks = new Dictionary<string, BoneTrack>(StringComparer.OrdinalIgnoreCase);
        var duration = 0f;
        var sourceFrameRate = 60;
        var sourceFrameCount = 0;
        foreach (var clip in mot.BoneClips)
        {
            (int Index, string Name) target;
            if (!hashToBone.TryGetValue(clip.ClipHeader.boneHash, out target))
            {
                var fallbackIndex = clip.ClipHeader.boneIndex;
                if (clip.ClipHeader.boneHash != 0 ||
                    fallbackIndex < 0 ||
                    fallbackIndex >= meshBoneNames.Count)
                    continue;
                target = (fallbackIndex, meshBoneNames[fallbackIndex]);
            }

            var track = new BoneTrack();
            if (clip.HasTranslation && clip.Translation!.translations is { Length: > 0 } translations)
            {
                var fps = TrackFrameRate(clip.Translation);
                var frames = clip.Translation.frameIndexes;
                var maxFrame = TrackMaxFrame(clip.Translation, translations.Length);
                track.TransTimes = BuildTimes(frames, translations.Length, fps);
                track.Translations = translations;
                sourceFrameRate = Math.Max(sourceFrameRate, (int)fps);
                sourceFrameCount = Math.Max(sourceFrameCount, (int)MathF.Round(maxFrame));
                duration = Math.Max(duration, maxFrame / fps);
            }

            if (clip.HasRotation && clip.Rotation!.rotations is { Length: > 0 } rotations)
            {
                var fps = TrackFrameRate(clip.Rotation);
                var frames = clip.Rotation.frameIndexes;
                var maxFrame = TrackMaxFrame(clip.Rotation, rotations.Length);
                track.RotTimes = BuildTimes(frames, rotations.Length, fps);
                track.Rotations = rotations.Select(q => q.W < 0
                        ? new Quaternion(-q.X, -q.Y, -q.Z, -q.W)
                        : q)
                    .Select(Quaternion.Normalize)
                    .ToArray();
                sourceFrameRate = Math.Max(sourceFrameRate, (int)fps);
                sourceFrameCount = Math.Max(sourceFrameCount, (int)MathF.Round(maxFrame));
                duration = Math.Max(duration, maxFrame / fps);
            }

            tracks[target.Index] = track;
            namedTracks[target.Name] = track;
        }

        return new AnimationClip
        {
            Name = $"mot_{motionNumber}",
            Duration = duration,
            FrameRate = sourceFrameRate, FrameCount = sourceFrameCount,
            Tracks = tracks,
            NamedTracks = namedTracks,
        };
    }

    private static uint TrackFrameRate(Track track) => track.frameRate > 0 ? track.frameRate : 60u;

    private static float TrackMaxFrame(Track track, int count)
    {
        if (track.frameIndexes is { Length: > 0 } frames) return frames[^1];
        return Math.Max(0, count - 1);
    }

    private static float[] BuildTimes(int[]? frames, int count, uint fps)
    {
        var times = new float[count];
        for (var i = 0; i < count; i++)
            times[i] = frames != null && i < frames.Length ? frames[i] / (float)fps : i / (float)fps;
        return times;
    }

    private static SharpGLTF.Scenes.NodeBuilder? ResolveBoneNode(
        MeshService.SkeletonData skeleton, MeshFile mesh, BoneMotionClip clip)
    {
        var hierarchy = skeleton.Hierarchy;
        if (hierarchy == null || skeleton.AllNodes.Length == 0) return null;

        // 1) hash lookup (requires bone names on the mesh side)
        var header = clip.ClipHeader;
        MeshBone? bone = null;
        if (header.boneHash != 0)
            bone = hierarchy.GetByHash(header.boneHash);

        // 2) fall back to bone index into the mesh bone list
        bone ??= hierarchy.GetByIndex(header.boneIndex);

        if (bone == null || bone.index < 0 || bone.index >= skeleton.AllNodes.Length) return null;
        return skeleton.AllNodes[bone.index];
    }
}
