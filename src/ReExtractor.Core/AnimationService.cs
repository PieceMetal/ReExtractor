using System.Numerics;
using ReeLib;
using ReeLib.Mesh;
using ReeLib.Mot;
using SharpGLTF.Scenes;

namespace ReExtractor.Core;

/// <summary>
/// Converts RE Engine .motlist animations to glTF animation tracks on top of a mesh skeleton.
/// </summary>
public sealed class AnimationService
{
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

        var scene = new SceneBuilder();
        var skeleton = MeshService.BuildSkeletonInternal(mesh.BoneData);
        MeshService.ExportGeometry(scene, mesh, skeleton, lodIndex: 0);

        var animName = $"mot_{motion.motNumber}";
        var applied = 0;
        foreach (var clip in mot.BoneClips)
        {
            var node = ResolveBoneNode(skeleton, mesh, clip);
            if (node == null) continue;

            if (clip.HasRotation && clip.Rotation!.rotations is { Length: > 0 } rotations)
            {
                var frames = clip.Rotation.frameIndexes;
                var fps = clip.Rotation.frameRate > 0 ? clip.Rotation.frameRate : 30u;
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
                var fps = clip.Translation.frameRate > 0 ? clip.Translation.frameRate : 30u;
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

        Console.WriteLine($"[anim] motion #{motion.motNumber}: {applied}/{mot.BoneClips.Count} bone clips applied");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        scene.ToGltf2().SaveGLB(outputPath);
        return outputPath;
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
