using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ReExtractor.Core;

/// <summary>Exports exactly what the interactive viewport currently shows.</summary>
public sealed class ViewportExportService
{
    public string ConvertToGlb(ViewportMesh mesh, IReadOnlySet<int> visibleGroups, string outputPath)
    {
        var (scene, _) = BuildScene(mesh, visibleGroups);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        scene.ToGltf2().SaveGLB(outputPath);
        return outputPath;
    }

    public string ConvertToAnimatedGlb(ViewportMesh mesh, IReadOnlySet<int> visibleGroups, AnimationClip clip, string outputPath)
    {
        var (scene, nodes) = BuildScene(mesh, visibleGroups);
        ApplyAnimation(nodes, clip);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        scene.ToGltf2().SaveGLB(outputPath);
        return outputPath;
    }

    private static (SceneBuilder Scene, NodeBuilder[] Nodes) BuildScene(ViewportMesh mesh, IReadOnlySet<int> visibleGroups)
    {
        var scene = new SceneBuilder();
        var materials = BuildMaterials(mesh);
        var skeleton = BuildSkeleton(mesh);
        if (skeleton.Joints.Length > 0)
        {
            // One MeshBuilder with multiple material primitives becomes ONE mesh object in
            // Blender/FBX/UE. A builder per material would produce scattered mesh objects.
            var builder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>("合并模型");
            var hasGeometry = false;
            for (var slot = -1; slot < Math.Max(1, mesh.Textures.Length); slot++)
            {
                var material = materials.TryGetValue(slot, out var found) ? found : materials[-1];
                var primitive = builder.UsePrimitive(material);
                for (var face = 0; face < mesh.Faces.Length; face++)
                {
                    if (!IsVisible(mesh, visibleGroups, face) || IsExportHidden(mesh, face) || TextureSlot(mesh, face) != slot) continue;
                    var (a, b, c) = mesh.Faces[face];
                    primitive.AddTriangle(SkinnedVertex(mesh, a), SkinnedVertex(mesh, b), SkinnedVertex(mesh, c));
                    hasGeometry = true;
                }
            }
            if (hasGeometry) scene.AddSkinnedMesh(builder, skeleton.Joints);
        }
        else
        {
            var builder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>("合并模型");
            var hasGeometry = false;
            for (var slot = -1; slot < Math.Max(1, mesh.Textures.Length); slot++)
            {
                var material = materials.TryGetValue(slot, out var found) ? found : materials[-1];
                var primitive = builder.UsePrimitive(material);
                for (var face = 0; face < mesh.Faces.Length; face++)
                {
                    if (!IsVisible(mesh, visibleGroups, face) || IsExportHidden(mesh, face) || TextureSlot(mesh, face) != slot) continue;
                    var (a, b, c) = mesh.Faces[face];
                    primitive.AddTriangle(Vertex(mesh, a), Vertex(mesh, b), Vertex(mesh, c));
                    hasGeometry = true;
                }
            }
            if (hasGeometry) scene.AddRigidMesh(builder, Matrix4x4.Identity);
        }
        return (scene, skeleton.Nodes);
    }

    /// <summary>Combines every visible preview part into one geometry and one union skeleton.</summary>
    public string ConvertMergedToGlb(
        IReadOnlyList<(ViewportMesh Mesh, IReadOnlySet<int> VisibleGroups)> models,
        string outputPath)
    {
        if (models.Count == 0) throw new ArgumentException("至少需要一个模型", nameof(models));
        var merged = BuildMergedExportModel(models);
        return ConvertToGlb(merged.Mesh, merged.VisibleGroups, outputPath);
    }

    public string ConvertMergedToAnimatedGlb(
        IReadOnlyList<(ViewportMesh Mesh, IReadOnlySet<int> VisibleGroups)> models,
        AnimationClip clip,
        string outputPath)
    {
        var merged = BuildMergedExportModel(models);
        return ConvertToAnimatedGlb(merged.Mesh, merged.VisibleGroups, clip, outputPath);
    }

    public (ViewportMesh Mesh, IReadOnlySet<int> VisibleGroups) BuildMergedExportModel(
        IReadOnlyList<(ViewportMesh Mesh, IReadOnlySet<int> VisibleGroups)> models)
    {
        if (models.Count == 0) throw new ArgumentException("need at least one model", nameof(models));
        if (models.Count == 1) return (models[0].Mesh, models[0].VisibleGroups);

        var visibleMeshes = models.Select(model => VisibleCopy(model.Mesh, model.VisibleGroups)).ToArray();
        var merged = ViewportMesh.Merge(visibleMeshes);
        return (merged, merged.Groups.Select(group => group.Key).ToHashSet());
    }

    private static ViewportMesh VisibleCopy(ViewportMesh mesh, IReadOnlySet<int> visibleGroups)
    {
        var faces = new List<(int A, int B, int C)>();
        var faceTexture = new List<int>();
        var faceAlpha = new List<bool>();
        var faceExportHidden = new List<bool>();
        var faceGroups = new List<int>();
        for (var face = 0; face < mesh.Faces.Length; face++)
        {
            if (!IsVisible(mesh, visibleGroups, face)) continue;
            faces.Add(mesh.Faces[face]);
            faceTexture.Add(TextureSlot(mesh, face));
            faceAlpha.Add(face < mesh.FaceAlphaCutout.Length && mesh.FaceAlphaCutout[face]);
            faceExportHidden.Add(face < mesh.FaceExportHidden.Length && mesh.FaceExportHidden[face]);
            faceGroups.Add(face < mesh.FaceGroups.Length ? mesh.FaceGroups[face] : -1);
        }

        return new ViewportMesh
        {
            Vertices = mesh.Vertices,
            Normals = mesh.Normals,
            Uvs = mesh.Uvs,
            Faces = faces.ToArray(),
            FaceTexture = faceTexture.ToArray(),
            FaceAlphaCutout = faceAlpha.ToArray(),
            FaceExportHidden = faceExportHidden.ToArray(),
            FaceGroups = faceGroups.ToArray(),
            Groups = mesh.Groups.Where(group => visibleGroups.Contains(group.Key)).Select(group => new ViewportGroup
            {
                Key = group.Key,
                Id = group.Id,
                Name = group.Name,
                Materials = group.Materials,
                FaceCount = group.FaceCount,
                DefaultVisible = true,
                IsHelper = group.IsHelper,
            }).ToArray(),
            Textures = mesh.Textures,
            Weights = mesh.Weights,
            Bones = mesh.Bones,
            DeformToBone = mesh.DeformToBone,
            VisconInfo = mesh.VisconInfo,
        };
    }

    private static int TextureSlot(ViewportMesh mesh, int face)
        => face < mesh.FaceTexture.Length ? mesh.FaceTexture[face] : -1;

    private static bool IsExportHidden(ViewportMesh mesh, int face)
        => face < mesh.FaceExportHidden.Length && mesh.FaceExportHidden[face];

    private sealed record SkeletonBuild(
        (NodeBuilder Joint, Matrix4x4 InverseBindMatrix)[] Joints,
        NodeBuilder[] Nodes);

    private static SkeletonBuild BuildSkeleton(ViewportMesh mesh)
    {
        if (mesh.Bones.Length == 0 || mesh.DeformToBone.Length == 0)
            return new SkeletonBuild([], []);
        var nodes = mesh.Bones.Select(bone => new NodeBuilder(bone.Name) { LocalMatrix = bone.LocalBind }).ToArray();
        for (var i = 0; i < mesh.Bones.Length; i++)
        {
            var parent = mesh.Bones[i].ParentIndex;
            if (parent >= 0 && parent < nodes.Length && parent != i) nodes[parent].AddNode(nodes[i]);
        }
        var joints = mesh.DeformToBone.Select(boneIndex =>
            (nodes[boneIndex], mesh.Bones[boneIndex].InverseGlobalBind)).ToArray();
        return new SkeletonBuild(joints, nodes);
    }

    private static void ApplyAnimation(NodeBuilder[] nodes, AnimationClip clip)
    {
        var animName = string.IsNullOrWhiteSpace(clip.Name) ? "mot" : clip.Name;
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            if (!clip.NamedTracks.TryGetValue(node.Name, out var track) &&
                !clip.Tracks.TryGetValue(i, out track))
                continue;

            if (track.RotTimes is { Length: > 0 } rotTimes &&
                track.Rotations is { Length: > 0 } rotations)
            {
                var keys = new Dictionary<float, Quaternion>(Math.Min(rotTimes.Length, rotations.Length));
                for (var k = 0; k < rotTimes.Length && k < rotations.Length; k++)
                    keys[rotTimes[k]] = rotations[k];
                node.WithLocalRotation(animName, keys);
            }

            if (track.TransTimes is { Length: > 0 } transTimes &&
                track.Translations is { Length: > 0 } translations)
            {
                var keys = new Dictionary<float, Vector3>(Math.Min(transTimes.Length, translations.Length));
                for (var k = 0; k < transTimes.Length && k < translations.Length; k++)
                    keys[transTimes[k]] = translations[k];
                node.WithLocalTranslation(animName, keys);
            }
        }
    }

    private static bool IsVisible(ViewportMesh mesh, IReadOnlySet<int> visibleGroups, int face)
    {
        if (mesh.Groups.Length == 0 || mesh.FaceGroups.Length != mesh.Faces.Length) return true;
        return visibleGroups.Contains(mesh.FaceGroups[face]);
    }

    private static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> Vertex(ViewportMesh mesh, int index)
    {
        var normal = index < mesh.Normals.Length ? mesh.Normals[index] : Vector3.UnitZ;
        if (normal.LengthSquared() < 1e-6f) normal = Vector3.UnitZ;
        var uv = index < mesh.Uvs.Length ? mesh.Uvs[index] : Vector2.Zero;
        return new(new VertexPositionNormal(mesh.Vertices[index], Vector3.Normalize(normal)), new VertexTexture1(uv));
    }

    private static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> SkinnedVertex(ViewportMesh mesh, int index)
    {
        var normal = index < mesh.Normals.Length ? mesh.Normals[index] : Vector3.UnitZ;
        if (normal.LengthSquared() < 1e-6f) normal = Vector3.UnitZ;
        var uv = index < mesh.Uvs.Length ? mesh.Uvs[index] : Vector2.Zero;
        var weights = index < mesh.Weights.Length ? mesh.Weights[index] : [];
        var top = weights.Where(weight => weight.Item2 > 0 && weight.Item1 >= 0 && weight.Item1 < mesh.DeformToBone.Length)
            .OrderByDescending(weight => weight.Item2).Take(4).ToArray();
        if (top.Length == 0) top = [(0, 1f)];
        var sum = top.Sum(weight => weight.Item2);
        var bindings = top.Select(weight => (weight.Item1, weight.Item2 / sum)).ToArray();
        return new(new VertexPositionNormal(mesh.Vertices[index], Vector3.Normalize(normal)),
            new VertexTexture1(uv), new VertexJoints4(bindings));
    }

    private static Dictionary<int, MaterialBuilder> BuildMaterials(ViewportMesh mesh)
    {
        var result = new Dictionary<int, MaterialBuilder>
        {
            [-1] = new MaterialBuilder("无贴图材质").WithMetallicRoughnessShader().WithDoubleSide(true),
        };
        for (var slot = 0; slot < mesh.Textures.Length; slot++)
        {
            var texture = mesh.Textures[slot];
            var material = new MaterialBuilder(texture.Name).WithMetallicRoughnessShader().WithDoubleSide(true);
            material.WithChannelImage(KnownChannel.BaseColor, ImageBuilder.From(ToPng(texture), texture.Name));
            var alphaCutout = Enumerable.Range(0, Math.Min(mesh.Faces.Length, mesh.FaceTexture.Length))
                .Any(face => mesh.FaceTexture[face] == slot &&
                    face < mesh.FaceAlphaCutout.Length && mesh.FaceAlphaCutout[face]);
            if (alphaCutout) material.WithAlpha(AlphaMode.MASK, 0.5f);
            result[slot] = material;
        }
        return result;
    }

    private static MemoryImage ToPng(ViewportTexture texture)
    {
        using var image = new Image<Rgba32>(texture.Width, texture.Height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < texture.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < texture.Width; x++)
                {
                    var pixel = texture.Pixels[y * texture.Width + x];
                    row[x] = new Rgba32((byte)(pixel >> 16), (byte)(pixel >> 8),
                        (byte)pixel, (byte)(pixel >> 24));
                }
            }
        });
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return new MemoryImage(stream.ToArray());
    }
}
