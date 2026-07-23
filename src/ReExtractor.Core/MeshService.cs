using System.Numerics;
using ReeLib;
using ReeLib.Mesh;
using ReeLib.MplyMesh;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace ReExtractor.Core;

/// <summary>
/// Converts RE Engine .mesh files to glTF/GLB via REE-Lib parsing + SharpGLTF.
/// Supports static geometry and skinned meshes (skeleton + weights).
/// </summary>
public sealed class MeshService
{
    /// <summary>
    /// Parse a .mesh stream and export LOD 0 as GLB.
    /// Includes skeleton + skin weights when bone data is present.
    /// </summary>
    public string ConvertToGlb(Stream meshStream, string nativePath, string outputPath, int lodIndex = 0)
    {
        using var mesh = LoadMesh(meshStream, nativePath);
        var scene = new SceneBuilder();
        var skeleton = BuildSkeleton(mesh.BoneData);
        ExportGeometry(scene, mesh, skeleton, lodIndex);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        scene.ToGltf2().SaveGLB(outputPath);
        return outputPath;
    }

    internal static MeshFile LoadMesh(Stream meshStream, string nativePath)
    {
        var mesh = new MeshFile(new FileHandler(meshStream, nativePath));
        if (!mesh.Read())
            throw new InvalidDataException($"Failed to parse .mesh: {nativePath}");
        return mesh;
    }

    internal static SkeletonData BuildSkeletonInternal(MeshBoneHierarchy? boneData) => BuildSkeleton(boneData);

    internal static void ExportGeometry(SceneBuilder scene, MeshFile mesh, SkeletonData skeleton, int lodIndex)
    {
        var meshData = mesh.MeshData ?? throw new InvalidDataException("MeshData missing");
        if (meshData.LODs.Count == 0)
            throw new InvalidDataException("No LODs in mesh");
        var lod = meshData.LODs[Math.Min(lodIndex, meshData.LODs.Count - 1)];

        var hasAnyPrimitive = false;

        foreach (var group in lod.MeshGroups)
        {
            foreach (var sub in group.Submeshes)
            {
                var positions = sub.Positions;
                if (positions.Length == 0) continue;

                var normals = sub.NormalsTangents;
                var uvs = sub.UV0;
                var weights = sub.Weights;
                var hasNormals = normals.Length >= positions.Length;
                var hasUvs = uvs.Length >= positions.Length;
                var hasSkin = skeleton.Joints.Length > 0 && weights.Length >= positions.Length && weights[0] != null;

                var materialName = sub.materialIndex < mesh.MaterialNames.Count
                    ? mesh.MaterialNames[sub.materialIndex]
                    : $"mat_{sub.materialIndex}";
                var material = new MaterialBuilder(materialName).WithDoubleSide(true);

                if (hasSkin)
                {
                    hasAnyPrimitive |= ExportSkinnedSubmesh(scene, group, sub, positions, normals, uvs, weights, hasNormals, hasUvs, material, skeleton);
                }
                else
                {
                    hasAnyPrimitive |= ExportStaticSubmesh(scene, group, sub, positions, normals, uvs, hasNormals, hasUvs, material);
                }
            }
        }

        if (!hasAnyPrimitive)
            throw new InvalidDataException("No exportable geometry found (streaming mesh?)");
    }

    private static bool ExportStaticSubmesh(
        SceneBuilder scene, MeshGroup group, Submesh sub,
        Span<Vector3> positions, Span<QuantizedNorTan> normals, Span<HFloat2> uvs,
        bool hasNormals, bool hasUvs, MaterialBuilder material)
    {
        var mb = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>($"g{group.groupId}_s{sub.materialIndex}");
        var prim = mb.UsePrimitive(material);

        for (var i = 0; i + 2 < sub.indicesCount; i += 3)
        {
            int i0 = GetIndex(sub, i), i1 = GetIndex(sub, i + 1), i2 = GetIndex(sub, i + 2);
            if ((uint)i0 >= (uint)positions.Length || (uint)i1 >= (uint)positions.Length || (uint)i2 >= (uint)positions.Length)
                continue;

            prim.AddTriangle(
                MakeVertex(positions, normals, uvs, hasNormals, hasUvs, i0),
                MakeVertex(positions, normals, uvs, hasNormals, hasUvs, i1),
                MakeVertex(positions, normals, uvs, hasNormals, hasUvs, i2));
        }

        if (prim.Vertices.Count == 0) return false;
        scene.AddRigidMesh(mb, Matrix4x4.Identity);
        return true;
    }

    private static bool ExportSkinnedSubmesh(
        SceneBuilder scene, MeshGroup group, Submesh sub,
        Span<Vector3> positions, Span<QuantizedNorTan> normals, Span<HFloat2> uvs,
        Span<VertexBoneWeights> weights,
        bool hasNormals, bool hasUvs, MaterialBuilder material, SkeletonData skeleton)
    {
        var mb = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>($"g{group.groupId}_s{sub.materialIndex}_sk");
        var prim = mb.UsePrimitive(material);

        for (var i = 0; i + 2 < sub.indicesCount; i += 3)
        {
            int i0 = GetIndex(sub, i), i1 = GetIndex(sub, i + 1), i2 = GetIndex(sub, i + 2);
            if ((uint)i0 >= (uint)positions.Length || (uint)i1 >= (uint)positions.Length || (uint)i2 >= (uint)positions.Length)
                continue;

            prim.AddTriangle(
                MakeSkinnedVertex(positions, normals, uvs, weights, hasNormals, hasUvs, i0, skeleton.DeformBoneCount),
                MakeSkinnedVertex(positions, normals, uvs, weights, hasNormals, hasUvs, i1, skeleton.DeformBoneCount),
                MakeSkinnedVertex(positions, normals, uvs, weights, hasNormals, hasUvs, i2, skeleton.DeformBoneCount));
        }

        if (prim.Vertices.Count == 0) return false;
        scene.AddSkinnedMesh(mb, skeleton.Joints);
        return true;
    }

    internal sealed record SkeletonData(
        (NodeBuilder Joint, Matrix4x4 InverseBindMatrix)[] Joints,
        int DeformBoneCount,
        NodeBuilder[] AllNodes,
        MeshBoneHierarchy? Hierarchy);

    private static SkeletonData BuildSkeleton(MeshBoneHierarchy? boneData)
    {
        if (boneData == null || boneData.Bones.Count == 0)
            return new SkeletonData([], 0, [], null);

        var nodes = new NodeBuilder[boneData.Bones.Count];
        for (var i = 0; i < boneData.Bones.Count; i++)
        {
            var bone = boneData.Bones[i];
            var name = string.IsNullOrEmpty(bone.name) ? $"bone_{i}" : bone.name;
            nodes[i] = new NodeBuilder(name) { LocalMatrix = ToMatrix(bone.localTransform) };
        }

        var roots = new List<NodeBuilder>();
        for (var i = 0; i < boneData.Bones.Count; i++)
        {
            var parentIndex = boneData.Bones[i].parentIndex;
            if (parentIndex >= 0 && parentIndex < boneData.Bones.Count && parentIndex != i)
                nodes[parentIndex].AddNode(nodes[i]);
            else
                roots.Add(nodes[i]);
        }

        // glTF joints = deform bones in remap order; vertex weight indices reference this list
        var deform = boneData.DeformBones.Count > 0 ? boneData.DeformBones : boneData.Bones;
        var joints = new (NodeBuilder, Matrix4x4)[deform.Count];
        for (var i = 0; i < deform.Count; i++)
            joints[i] = (nodes[deform[i].index], ToMatrix(deform[i].inverseGlobalTransform));

        return new SkeletonData(joints, deform.Count, nodes, boneData);
    }

    private static Matrix4x4 ToMatrix(ReeLib.via.mat4 m) => new(
        m.m00, m.m01, m.m02, m.m03,
        m.m10, m.m11, m.m12, m.m13,
        m.m20, m.m21, m.m22, m.m23,
        m.m30, m.m31, m.m32, m.m33);

    private static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> MakeVertex(
        Span<Vector3> positions, Span<QuantizedNorTan> normals, Span<HFloat2> uvs,
        bool hasNormals, bool hasUvs, int i)
    {
        var (geometry, uv) = MakeGeometryAndUv(positions, normals, uvs, hasNormals, hasUvs, i);
        return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(geometry, new VertexTexture1(uv));
    }

    private static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> MakeSkinnedVertex(
        Span<Vector3> positions, Span<QuantizedNorTan> normals, Span<HFloat2> uvs,
        Span<VertexBoneWeights> weights,
        bool hasNormals, bool hasUvs, int i, int deformBoneCount)
    {
        var (geometry, uv) = MakeGeometryAndUv(positions, normals, uvs, hasNormals, hasUvs, i);

        // collect (joint, weight) pairs, keep top 4
        var vw = weights[i];
        var pairs = new List<(int joint, float weight)>(8);
        if (vw != null)
        {
            var count = Math.Min(vw.IndexCount, 8);
            for (var k = 0; k < count; k++)
            {
                var w = vw.GetWeight(k);
                var j = vw.GetIndex(k);
                if (w <= 0 || j < 0 || j >= deformBoneCount) continue;
                pairs.Add((j, w));
            }
        }
        pairs.Sort((a, b) => b.weight.CompareTo(a.weight));
        if (pairs.Count > 4) pairs.RemoveRange(4, pairs.Count - 4);
        if (pairs.Count == 0) pairs.Add((0, 1f));

        var sum = pairs.Sum(p => p.weight);
        var bindings = pairs.Select(p => (p.joint, p.weight / sum)).ToArray();
        return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(
            geometry, new VertexTexture1(uv), new VertexJoints4(bindings));
    }

    private static (VertexPositionNormal geometry, Vector2 uv) MakeGeometryAndUv(
        Span<Vector3> positions, Span<QuantizedNorTan> normals, Span<HFloat2> uvs,
        bool hasNormals, bool hasUvs, int i)
    {
        var pos = positions[i];
        var nor = hasNormals ? normals[i].Normal : Vector3.UnitZ;
        nor = nor.LengthSquared() < 1e-6f ? Vector3.UnitZ : Vector3.Normalize(nor);
        var uv = hasUvs ? uvs[i].AsVector2 : Vector2.Zero;
        return (new VertexPositionNormal(pos, nor), uv);
    }

    private static int GetIndex(Submesh sub, int i)
    {
        return sub.Buffer.IntegerFaces != null ? sub.IntegerIndices[i] : sub.Indices[i];
    }
}
