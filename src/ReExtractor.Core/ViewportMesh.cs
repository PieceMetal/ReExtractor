using System.Numerics;
using ReeLib;
using ReeLib.Common;
using ReeLib.Mdf;
using ReeLib.Mesh;
using ReeLib.Mot;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ReExtractor.Core;

/// <summary>Unified geometry + skeleton for real-time viewport rendering (CPU side).</summary>
public sealed class ViewportMesh
{
    public required Vector3[] Vertices;              // bind pose
    public required Vector3[] Normals;               // bind pose (dequantized)
    public required Vector2[] Uvs;                   // TEXCOORD_0
    public required (int A, int B, int C)[] Faces;
    public required int[] FaceTexture;               // per-face index into Textures (-1 = untextured)
    /// <summary>Faces using RE-only transparent eye shaders that must not become opaque white in FBX.</summary>
    public bool[] FaceExportHidden = [];
    /// <summary>True only for materials authored as alpha-cutout surfaces (for example *_ALP_*).</summary>
    public bool[] FaceAlphaCutout = [];
    /// <summary>Per-face key into <see cref="Groups"/>. Empty for synthetic/legacy meshes.</summary>
    public int[] FaceGroups = [];
    /// <summary>All VISCON groups in the source LOD, including default-hidden alternatives/helpers.</summary>
    public ViewportGroup[] Groups = [];
    public required ViewportTexture[] Textures;
    public required (int Joint, float Weight)[][] Weights; // per-vertex; joints index into DeformBones
    public required ViewportBone[] Bones;            // full bone list
    public required int[] DeformToBone;              // deform joint -> bone index
    public int VertexCount => Vertices.Length;
    public int FaceCount => Faces.Length;
    /// <summary>viscon group filter summary, e.g. "viscon 7/11（隐藏 9,10,131,250）".</summary>
    public string VisconInfo = "";

    /// <summary>Bake a rigid scene-node transform into a mesh instance.</summary>
    public ViewportMesh WithTransform(Matrix4x4 transform)
    {
        Matrix4x4.Invert(transform, out var inverse);
        var normalMatrix = Matrix4x4.Transpose(inverse);
        return new ViewportMesh
        {
            Vertices = Vertices.Select(vertex => Vector3.Transform(vertex, transform)).ToArray(),
            Normals = Normals.Select(normal => Vector3.Normalize(Vector3.TransformNormal(normal, normalMatrix))).ToArray(),
            Uvs = Uvs,
            Faces = Faces,
            FaceTexture = FaceTexture,
            FaceExportHidden = FaceExportHidden,
            FaceAlphaCutout = FaceAlphaCutout,
            FaceGroups = FaceGroups,
            Groups = Groups,
            Textures = Textures,
            Weights = Weights,
            Bones = Bones,
            DeformToBone = DeformToBone,
            VisconInfo = VisconInfo,
        };
    }

    /// <summary>
    /// Merge multiple meshes into ONE unified mesh (geometry + skeleton + textures).
    /// Bones are unified by NAME: parts sharing a skeleton (e.g. one character's
    /// body/hair/weapon .mesh files) collapse onto a single skeleton; bones unique to a
    /// source are appended. Per-vertex skin weights and the DeformToBone array are remapped
    /// through the per-source bone map so the merged result skins correctly as a single unit.
    /// Vertex positions are concatenated as-is (each source is already in its own bind space),
    /// which is correct for parts of one character and acceptable for overlaying distinct characters.
    /// </summary>
    public static ViewportMesh Merge(IReadOnlyList<ViewportMesh> meshes)
    {
        if (meshes == null || meshes.Count == 0)
            throw new ArgumentException("need at least one mesh", nameof(meshes));
        if (meshes.Count == 1) return meshes[0];
        if (!TryGetMergeCompatibility(meshes, out var incompatibility))
            throw new InvalidOperationException($"这些模型不能共享一副骨架：{incompatibility}");

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var faces = new List<(int, int, int)>();
        var faceTex = new List<int>();
        var faceAlphaCutout = new List<bool>();
        var faceExportHidden = new List<bool>();
        var faceGroups = new List<int>();
        var weights = new List<(int, float)[]>();
        var textures = new List<ViewportTexture>();
        var texByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // --- unify skeleton by bone name ---
        var bones = new List<ViewportBone>();
        var boneSources = new List<(int Mesh, int Bone)>();
        var boneNameToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perMeshBoneMap = new List<int[]>(); // [m][srcBoneIdx] -> unified bone idx
        var perMeshGroupMap = new List<Dictionary<int, int>>();
        var groups = new List<ViewportGroup>();
        var nextGroupKey = 0;
        // Use the most complete source skeleton as the canonical bind pose.
        // The merge queue often starts with head/hair parts; taking bind
        // matrices from whichever part appears first can combine a parent
        // hierarchy from one part with inverse-bind data from another and
        // makes the whole lower body visibly vibrate after animation export.
        var canonicalMeshIndex = meshes
            .Select((mesh, index) => (mesh, index))
            .OrderByDescending(item => item.mesh.Bones.Length)
            .ThenBy(item => item.index)
            .First().index;
        for (var m = 0; m < meshes.Count; m++)
        {
            var mb = meshes[m].Bones;
            var map = new int[mb.Length];
            for (var b = 0; b < mb.Length; b++)
            {
                var name = mb[b].Name;
                if (!boneNameToIdx.TryGetValue(name, out var ui))
                {
                    ui = bones.Count;
                    boneNameToIdx[name] = ui;
                    bones.Add(mb[b]);
                    boneSources.Add((m, b));
                }
                map[b] = ui;
            }
            perMeshBoneMap.Add(map);

            var groupMap = new Dictionary<int, int>();
            foreach (var group in meshes[m].Groups)
            {
                var key = nextGroupKey++;
                groupMap[group.Key] = key;
                groups.Add(new ViewportGroup
                {
                    Key = key,
                    Id = group.Id,
                    Name = meshes.Count > 1 ? $"{m + 1}: {group.Name}" : group.Name,
                    Materials = group.Materials,
                    FaceCount = group.FaceCount,
                    DefaultVisible = group.DefaultVisible,
                    IsHelper = group.IsHelper,
                });
            }
            perMeshGroupMap.Add(groupMap);
        }

        var canonicalBoneIndexByName = meshes[canonicalMeshIndex].Bones
            .Select((bone, index) => (bone.Name, index))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index,
                StringComparer.OrdinalIgnoreCase);
        for (var unifiedIndex = 0; unifiedIndex < bones.Count; unifiedIndex++)
        {
            var name = bones[unifiedIndex].Name;
            if (canonicalBoneIndexByName.TryGetValue(name, out var canonicalBoneIndex))
                boneSources[unifiedIndex] = (canonicalMeshIndex, canonicalBoneIndex);
        }

        // ParentIndex belongs to each source skeleton and cannot be copied verbatim into
        // the union skeleton. A character part may expose C_Hip as a top-level deform bone
        // while another part contains the complete Root -> C_Hip hierarchy. Prefer any
        // valid parent supplied by the merged sources instead of freezing the hierarchy to
        // whichever part happened to be added first.
        var preferredParents = Enumerable.Repeat(-1, bones.Count).ToArray();
        var preferredBindSources = Enumerable.Repeat((-1, -1), bones.Count).ToArray();
        var sourceOrder = new[] { canonicalMeshIndex }
            .Concat(Enumerable.Range(0, meshes.Count).Where(index => index != canonicalMeshIndex));
        foreach (var sourceMesh in sourceOrder)
        {
            var sourceBones = meshes[sourceMesh].Bones;
            var map = perMeshBoneMap[sourceMesh];
            for (var sourceBone = 0; sourceBone < sourceBones.Length; sourceBone++)
            {
                var sourceParent = sourceBones[sourceBone].ParentIndex;
                if (sourceParent < 0 || sourceParent >= map.Length) continue;
                var unifiedBone = map[sourceBone];
                var unifiedParent = map[sourceParent];
                if (unifiedParent != unifiedBone && preferredParents[unifiedBone] < 0)
                {
                    preferredParents[unifiedBone] = unifiedParent;
                    preferredBindSources[unifiedBone] = (sourceMesh, sourceBone);
                }
            }
        }

        for (var unifiedIndex = 0; unifiedIndex < bones.Count; unifiedIndex++)
        {
            var (sourceMesh, sourceBone) = boneSources[unifiedIndex];
            var source = meshes[sourceMesh].Bones[sourceBone];
            var remappedParent = preferredParents[unifiedIndex];
            var bindSource = preferredBindSources[unifiedIndex];
            if (bindSource.Item1 >= 0)
            {
                source = meshes[bindSource.Item1].Bones[bindSource.Item2];
            }
            bones[unifiedIndex] = new ViewportBone
            {
                Name = source.Name,
                ParentIndex = remappedParent == unifiedIndex ? -1 : remappedParent,
                LocalBind = source.LocalBind,
                InverseGlobalBind = source.InverseGlobalBind,
            };
        }

        // --- merge geometry + textures + weights + deform map ---
        var deformToBone = new List<int>();
        var deformJointByBone = new Dictionary<int, int>();
        for (var m = 0; m < meshes.Count; m++)
        {
            var mm = meshes[m];
            var map = perMeshBoneMap[m];
            var vBase = verts.Count;
            verts.AddRange(mm.Vertices);
            normals.AddRange(mm.Normals);
            uvs.AddRange(mm.Uvs);

            // Weights[].Joint is a DEFORM JOINT index (into DeformToBone), NOT a bone index.
            // Merge deform joints by their unified bone index. Duplicating the same bone in the
            // glTF skin joint list is legal-looking but fragile in Blender/UE, and can make
            // merged parts skin against subtly different joint slots.
            var sourceDeformToMerged = new int[mm.DeformToBone.Length];
            for (var j = 0; j < mm.DeformToBone.Length; j++)
            {
                var sourceBone = mm.DeformToBone[j];
                if (sourceBone < 0 || sourceBone >= map.Length)
                {
                    sourceDeformToMerged[j] = 0;
                    continue;
                }

                var unifiedBone = map[sourceBone];
                if (!deformJointByBone.TryGetValue(unifiedBone, out var mergedJoint))
                {
                    mergedJoint = deformToBone.Count;
                    deformJointByBone[unifiedBone] = mergedJoint;
                    deformToBone.Add(unifiedBone);
                }
                sourceDeformToMerged[j] = mergedJoint;
            }

            foreach (var w in mm.Weights)
            {
                var remapped = new (int, float)[w.Length];
                for (var k = 0; k < w.Length; k++)
                {
                    var sourceJoint = w[k].Item1;
                    var mergedJoint = sourceJoint >= 0 && sourceJoint < sourceDeformToMerged.Length
                        ? sourceDeformToMerged[sourceJoint]
                        : 0;
                    remapped[k] = (mergedJoint, w[k].Item2);
                }
                weights.Add(remapped);
            }

            foreach (var (a, b, c) in mm.Faces)
                faces.Add((a + vBase, b + vBase, c + vBase));

            // textures: dedupe by name, remap per-face texture slots
            var localTexToGlobal = new int[mm.Textures.Length];
            for (var t = 0; t < mm.Textures.Length; t++)
            {
                var tex = mm.Textures[t];
                if (!texByName.TryGetValue(tex.Name, out var gslot))
                {
                    gslot = textures.Count;
                    texByName[tex.Name] = gslot;
                    textures.Add(tex);
                }
                localTexToGlobal[t] = gslot;
            }
            foreach (var slot in mm.FaceTexture)
                faceTex.Add(slot >= 0 && slot < localTexToGlobal.Length ? localTexToGlobal[slot] : -1);
            for (var f = 0; f < mm.Faces.Length; f++)
                faceAlphaCutout.Add(f < mm.FaceAlphaCutout.Length && mm.FaceAlphaCutout[f]);
            for (var f = 0; f < mm.Faces.Length; f++)
                faceExportHidden.Add(f < mm.FaceExportHidden.Length && mm.FaceExportHidden[f]);
            var groupMap = perMeshGroupMap[m];
            for (var f = 0; f < mm.Faces.Length; f++)
            {
                var sourceKey = f < mm.FaceGroups.Length ? mm.FaceGroups[f] : -1;
                faceGroups.Add(groupMap.TryGetValue(sourceKey, out var mergedKey) ? mergedKey : -1);
            }
        }

        return new ViewportMesh
        {
            Vertices = verts.ToArray(),
            Normals = normals.ToArray(),
            Uvs = uvs.ToArray(),
            Faces = faces.ToArray(),
            FaceTexture = faceTex.ToArray(),
            FaceAlphaCutout = faceAlphaCutout.ToArray(),
            FaceExportHidden = faceExportHidden.ToArray(),
            FaceGroups = faceGroups.ToArray(),
            Groups = groups.ToArray(),
            Textures = textures.ToArray(),
            Weights = weights.ToArray(),
            Bones = bones.ToArray(),
            DeformToBone = deformToBone.ToArray(),
            VisconInfo = $"merged {meshes.Count} meshes",
        };
    }

    /// <summary>
    /// Determines whether parts can safely be collapsed onto one armature.  Bone names alone
    /// are not sufficient: some RE Engine character parts use the same names but a different
    /// bind-space origin (for example, a waist-relative accessory alongside a ground-rooted
    /// body).  Rebinding such a part by name makes it explode as soon as animation is applied.
    /// </summary>
    public static bool TryGetMergeCompatibility(IReadOnlyList<ViewportMesh> meshes, out string reason)
    {
        reason = string.Empty;
        if (meshes == null || meshes.Count == 0)
        {
            reason = "没有可合并的模型";
            return false;
        }

        var reference = meshes[0];
        if (reference.Bones.Length == 0 || reference.DeformToBone.Length == 0)
        {
            reason = "主模型没有可用骨架";
            return false;
        }

        var referenceByName = UniqueBonesByName(reference, out var referenceDuplicates);
        if (referenceDuplicates != null)
        {
            reason = $"主模型存在重复骨骼名：{referenceDuplicates}";
            return false;
        }

        for (var meshIndex = 1; meshIndex < meshes.Count; meshIndex++)
        {
            var candidate = meshes[meshIndex];
            if (candidate.Bones.Length != reference.Bones.Length ||
                candidate.DeformToBone.Length == 0)
            {
                reason = $"第 {meshIndex + 1} 个分件的骨架数量不同";
                return false;
            }

            var candidateByName = UniqueBonesByName(candidate, out var candidateDuplicates);
            if (candidateDuplicates != null)
            {
                reason = $"第 {meshIndex + 1} 个分件存在重复骨骼名：{candidateDuplicates}";
                return false;
            }

            foreach (var (name, referenceIndex) in referenceByName)
            {
                if (!candidateByName.TryGetValue(name, out var candidateIndex))
                {
                    reason = $"第 {meshIndex + 1} 个分件缺少骨骼 {name}";
                    return false;
                }

                var a = reference.Bones[referenceIndex];
                var b = candidate.Bones[candidateIndex];
                var aParent = a.ParentIndex >= 0 && a.ParentIndex < reference.Bones.Length
                    ? reference.Bones[a.ParentIndex].Name : null;
                var bParent = b.ParentIndex >= 0 && b.ParentIndex < candidate.Bones.Length
                    ? candidate.Bones[b.ParentIndex].Name : null;
                if (!string.Equals(aParent, bParent, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"第 {meshIndex + 1} 个分件的 {name} 父级不同";
                    return false;
                }

                if (!NearlyEqual(a.LocalBind, b.LocalBind) ||
                    !NearlyEqual(a.InverseGlobalBind, b.InverseGlobalBind))
                {
                    reason = $"第 {meshIndex + 1} 个分件的 {name} 静止绑定矩阵不同";
                    return false;
                }
            }
        }

        return true;
    }

    private static Dictionary<string, int> UniqueBonesByName(ViewportMesh mesh, out string? duplicate)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (bone, index) in mesh.Bones.Select((bone, index) => (bone, index)))
        {
            if (!result.TryAdd(bone.Name, index))
            {
                duplicate = bone.Name;
                return result;
            }
        }
        duplicate = null;
        return result;
    }

    private static bool NearlyEqual(Matrix4x4 a, Matrix4x4 b)
    {
        const float relativeTolerance = 1e-5f;
        return NearlyEqual(a.M11, b.M11) && NearlyEqual(a.M12, b.M12) && NearlyEqual(a.M13, b.M13) && NearlyEqual(a.M14, b.M14) &&
               NearlyEqual(a.M21, b.M21) && NearlyEqual(a.M22, b.M22) && NearlyEqual(a.M23, b.M23) && NearlyEqual(a.M24, b.M24) &&
               NearlyEqual(a.M31, b.M31) && NearlyEqual(a.M32, b.M32) && NearlyEqual(a.M33, b.M33) && NearlyEqual(a.M34, b.M34) &&
               NearlyEqual(a.M41, b.M41) && NearlyEqual(a.M42, b.M42) && NearlyEqual(a.M43, b.M43) && NearlyEqual(a.M44, b.M44);

        static bool NearlyEqual(float left, float right)
            => MathF.Abs(left - right) <= relativeTolerance * MathF.Max(1f, MathF.Max(MathF.Abs(left), MathF.Abs(right)));
    }
}

/// <summary>One source VISCON visibility group exposed to the interactive viewport.</summary>
public sealed class ViewportGroup
{
    /// <summary>Unique key inside this ViewportMesh (Id may repeat after mesh merge).</summary>
    public required int Key;
    /// <summary>Original RE Engine groupId.</summary>
    public required int Id;
    public required string Name;
    public required string[] Materials;
    public required int FaceCount;
    public bool DefaultVisible;
    public bool IsHelper;
}

/// <summary>Decoded RGBA texture for software sampling (top-left origin, row-major).</summary>
public sealed class ViewportTexture
{
    public required int Width;
    public required int Height;
    public required uint[] Pixels; // BGRA, same layout as framebuffer (mip level 0)
    public required string Name;
    // mip chain: Mips[0] == Pixels (level 0); Mips[i] is 1/2^i resolution. Prevents minification aliasing.
    public uint[][] Mips = [];
    public int[] MipW = [];
    public int[] MipH = [];
}

public sealed class ViewportBone
{
    public required string Name;
    public required int ParentIndex;                 // -1 for root
    public required Matrix4x4 LocalBind;
    public required Matrix4x4 InverseGlobalBind;
}

/// <summary>One parsed motion: per-bone keyframe tracks.</summary>
public sealed class AnimationClip
{
    public required string Name;
    public required float Duration;
    public required int FrameRate;
    public required int FrameCount;
    public required Dictionary<int, BoneTrack> Tracks; // bone index -> track
    /// <summary>Tracks keyed by target skeleton bone name, used to animate every merged/overlaid model.</summary>
    public Dictionary<string, BoneTrack> NamedTracks = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BoneTrack
{
    public float[]? TransTimes;
    public Vector3[]? Translations;
    public float[]? RotTimes;
    public Quaternion[]? Rotations;
}

/// <summary>One embedded, directly readable motion in a MotionList.</summary>
public sealed record MotionInfo(int SourceIndex, string DisplayName, int MotionNumber);

/// <summary>Loads ViewportMesh / AnimationClip from RE files (parse only, no rendering deps).</summary>
public static class ViewportDataLoader
{
    private sealed record PreviewMaterial(ViewportTexture Texture, bool AlphaCutout);

    /// <summary>Resolve every non-placeholder TEX resource referenced by the mesh's sibling MDF.</summary>
    public static IReadOnlyList<string> ListReferencedTexturePaths(
        string meshPath, Func<string, Stream?> openResource)
    {
        var dotMesh = meshPath.IndexOf(".mesh.", StringComparison.OrdinalIgnoreCase);
        if (dotMesh < 0) return [];
        var meshBasePath = meshPath[..dotMesh];
        foreach (var nameSuffix in MdfNameSuffixCandidates)
        foreach (var versionSuffix in MdfVersionCandidates)
        {
            var mdfPath = meshBasePath + nameSuffix + versionSuffix;
            using var mdfStream = openResource(mdfPath);
            if (mdfStream == null) continue;
            try
            {
                var mdf = new MdfFile(new FileHandler(mdfStream, mdfPath));
                if (!mdf.Read()) continue;
                return mdf.Materials.SelectMany(material => material.Textures)
                    .Select(texture => texture.texPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path) && !IsNullTexture(path))
                    .Select(path => ResolveNormalizedPath(openResource, path, meshPath))
                    .Where(path => path != null)
                    .Select(path => path!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch { continue; }
        }
        return [];
    }

    public static ViewportMesh LoadMesh(Stream meshStream, string nativePath, int lodIndex = 0, Func<string, Stream?>? openResource = null, bool loadTextures = true)
    {
        using var mesh = MeshService.LoadMesh(meshStream, nativePath, openResource);
        var meshData = mesh.MeshData ?? throw new InvalidDataException("MeshData missing");
        var lod = meshData.LODs[Math.Min(lodIndex, meshData.LODs.Count - 1)];

        // resolve material textures via the model's .mdf2 (best effort)
        var materialTextures = openResource == null || !loadTextures
            ? new Dictionary<int, PreviewMaterial>()
            : ResolveMaterialTextures(mesh, nativePath, openResource);

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var faces = new List<(int, int, int)>();
        var faceTex = new List<int>();
        var faceAlphaCutout = new List<bool>();
        var faceExportHidden = new List<bool>();
        var faceGroups = new List<int>();
        var weights = new List<(int, float)[]>();
        var textureList = new List<ViewportTexture>();
        var textureIndex = new Dictionary<string, int>(); // material name -> texture slot

        // --- default VISCON state ---------------------------------------------------------
        // Mesh groups are visibility alternatives, not layers that may all be flattened into
        // one render.  A common character layout is:
        //   primary groups introduce Body/Hand/Cloth/... materials;
        //   later groups reuse one of those materials for an alternate/damage/proxy shell.
        // Drawing every group lets the later shell cover the real multi-material character
        // (ch001 group 250 reuses Cloth_Top over the whole body, turning skin red/grey).
        //
        // Build a deterministic default state from geometry, not just material reuse. VISCON
        // groups frequently split one material across several real body/garment pieces (ch001
        // group 9 is the missing left-abdomen piece), so "material already seen" is not enough
        // to call a group an alternative. Hide a later group only when its same-material bounds
        // and vertex counts closely match geometry already retained. IDs 250+ are reserved by
        // these assets for broad override/proxy states and remain opt-in in the inspector.
        var allGroups = lod.MeshGroups.OrderBy(g => g.groupId).ToList();
        // VFX emitter/helper shells are authoring-time/runtime support geometry, not part of
        // the visible model surface. They often envelop the entire garment and use NullGray;
        // flattening them into the preview makes the real albedo look missing or corrupted.
        // Keep the source mesh/export untouched and exclude only from the interactive preview.
        var helperGroups = allGroups.Where(g => g.Submeshes.Count > 0 &&
            g.Submeshes.Where(IsSubmeshRangeValid).Any() &&
            g.Submeshes.Where(IsSubmeshRangeValid).All(s => s.materialIndex < mesh.MaterialNames.Count &&
                IsPreviewHelperMaterial(mesh.MaterialNames[s.materialIndex])))
            .ToList();
        var visualGroups = allGroups.Except(helperGroups).ToList();
        var modelDiagonal = GetGroupsBoundsDiagonal(visualGroups);
        var representedMaterials = new HashSet<ushort>();
        var keptGroups = new List<MeshGroup>();
        var alternateGroups = new List<int>();
        foreach (var g in visualGroups)
        {
            var materials = g.Submeshes
                .Where(s => IsSubmeshRangeValid(s) && s.indicesCount >= 3 && s.materialIndex >= 0)
                .Select(s => s.materialIndex)
                .Distinct()
                .ToArray();
            var isReservedOverride = g.groupId >= 250 && visualGroups.Count > 1;
            var isGeometryDuplicate = keptGroups.Count > 0 && materials.Length > 0 &&
                materials.All(representedMaterials.Contains) &&
                IsNearDuplicateGroup(g, keptGroups, modelDiagonal);
            if (isReservedOverride || isGeometryDuplicate)
            {
                alternateGroups.Add(g.groupId);
                continue;
            }
            keptGroups.Add(g);
            foreach (var material in materials) representedMaterials.Add(material);
        }
        var hiddenParts = new List<string>();
        if (alternateGroups.Count > 0) hiddenParts.Add($"替代组 {string.Join(",", alternateGroups)}");
        if (helperGroups.Count > 0) hiddenParts.Add($"辅助组 {string.Join(",", helperGroups.Select(g => g.groupId))}");
        var visconInfo = $"viscon {keptGroups.Count}/{allGroups.Count}" +
            (hiddenParts.Count > 0 ? $"（隐藏{string.Join("；", hiddenParts)}）" : "");

        var keptGroupIds = keptGroups.Select(g => (int)g.groupId).ToHashSet();
        var helperGroupIds = helperGroups.Select(g => (int)g.groupId).ToHashSet();
        var viewportGroups = allGroups.Select(g =>
        {
            var materials = g.Submeshes
                .Where(s => IsSubmeshRangeValid(s) && s.indicesCount >= 3 &&
                            s.materialIndex < mesh.MaterialNames.Count)
                .Select(s => mesh.MaterialNames[s.materialIndex])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new ViewportGroup
            {
                Key = g.groupId,
                Id = g.groupId,
                Name = $"组 {g.groupId}",
                Materials = materials,
                FaceCount = g.Submeshes.Where(IsSubmeshRangeValid).Sum(s => s.indicesCount / 3),
                DefaultVisible = keptGroupIds.Contains(g.groupId),
                IsHelper = helperGroupIds.Contains(g.groupId),
            };
        }).ToArray();

        // Preserve every source group in the viewport mesh. Rendering starts with the safe
        // default set above, while the GUI can toggle alternatives instantly without
        // reparsing the mesh or decoding textures again.
        foreach (var group in allGroups)
        {
            foreach (var sub in group.Submeshes)
            {
                // Some Wilds costume parts reference optional streaming buffers that are
                // absent from an extracted package. ReeLib exposes the declared range, but
                // slicing it would throw. Preserve the rest of the mesh and skip only the
                // unavailable submesh.
                if (!IsSubmeshRangeValid(sub)) continue;
                var positions = sub.Positions;
                if (positions.Length == 0) continue;
                var vBase = verts.Count;
                // Some MHR helper/debris meshes declare a vertex range but do not
                // contain skin-weight data. Accessing Submesh.Weights blindly makes
                // Span slicing throw before the unskinned fallback can be used.
                var hasWeights = HasBufferRange(sub.Buffer.Weights.Length, sub.vertsIndexOffset, sub.vertCount);
                var hasNormals = HasBufferRange(sub.Buffer.NormalsTangents.Length, sub.vertsIndexOffset, sub.vertCount);
                var hasUvs = HasBufferRange(sub.Buffer.UV0.Length, sub.vertsIndexOffset, sub.vertCount);
                var w = hasWeights ? sub.Weights : default;
                var norTan = hasNormals ? sub.NormalsTangents : default;
                var uv0 = hasUvs ? sub.UV0 : default;

                for (var i = 0; i < positions.Length; i++)
                {
                    verts.Add(positions[i]);
                    normals.Add(hasNormals ? SafeNormal(norTan[i].Normal) : Vector3.UnitZ);
                    uvs.Add(hasUvs ? uv0[i].AsVector2 : Vector2.Zero);
                    weights.Add(hasWeights && w[i] != null ? ExtractWeights(w[i]) : [(0, 1f)]);
                }

                var texSlot = -1;
                var alphaCutout = false;
                var exportHidden = false;
                if (sub.materialIndex < mesh.MaterialNames.Count)
                {
                    var matName = mesh.MaterialNames[sub.materialIndex];
                    // RE renders these with dedicated transparent/refraction shaders. FBX
                    // cannot reproduce them faithfully; without a color material they otherwise
                    // become an opaque white shell that hides the real eyeball in UE.
                    // The RE eye lens is a refractive shell that requires the game's
                    // specialised shader. Rendering it as an ordinary opaque albedo
                    // layer sits in front of the eyeball and makes the iris look shifted.
                    // Keep the actual eye mesh; hide only these unsupported overlays.
                    exportHidden = IsUnsupportedEyeOverlayMaterial(matName) ||
                        IsPreviewHelperMaterial(matName);
                    if (!exportHidden && materialTextures.TryGetValue(sub.materialIndex, out var previewMaterial))
                    {
                        alphaCutout = previewMaterial.AlphaCutout;
                        if (!textureIndex.TryGetValue(matName, out texSlot))
                        {
                            texSlot = textureList.Count;
                            textureList.Add(previewMaterial.Texture);
                            textureIndex[matName] = texSlot;
                        }
                    }
                }

                for (var i = 0; i + 2 < sub.indicesCount; i += 3)
                {
                    int i0 = GetIndex(sub, i), i1 = GetIndex(sub, i + 1), i2 = GetIndex(sub, i + 2);
                    if ((uint)i0 >= (uint)positions.Length || (uint)i1 >= (uint)positions.Length || (uint)i2 >= (uint)positions.Length)
                        continue;
                    faces.Add((vBase + i0, vBase + i1, vBase + i2));
                    faceTex.Add(texSlot);
                    faceAlphaCutout.Add(alphaCutout);
                    faceExportHidden.Add(exportHidden);
                    faceGroups.Add(group.groupId);
                }
            }
        }

        var (bones, deformToBone) = BuildBones(mesh.BoneData);
        return new ViewportMesh
        {
            Vertices = verts.ToArray(),
            Normals = normals.ToArray(),
            Uvs = uvs.ToArray(),
            Faces = faces.ToArray(),
            FaceTexture = faceTex.ToArray(),
            FaceAlphaCutout = faceAlphaCutout.ToArray(),
            FaceExportHidden = faceExportHidden.ToArray(),
            FaceGroups = faceGroups.ToArray(),
            Groups = viewportGroups,
            Textures = textureList.ToArray(),
            Weights = weights.ToArray(),
            Bones = bones,
            DeformToBone = deformToBone,
            VisconInfo = visconInfo,
        };
    }

    /// <summary>
    /// Parse the sibling .mdf2 of the mesh and decode each material's albedo texture.
    /// Returns materialIndex -> decoded texture (best effort; missing pieces are skipped).
    /// </summary>
    private static Dictionary<int, PreviewMaterial> ResolveMaterialTextures(
        MeshFile mesh, string meshPath, Func<string, Stream?> openResource)
    {
        var result = new Dictionary<int, PreviewMaterial>();
        // Several material variants (cloth/metal/pants, for example) often share
        // one large source albedo. Decode that immutable source once, then give
        // each material its own writable pixel copy for tint/mask processing.
        var decodedBaseTextures = new Dictionary<string, ViewportTexture>(StringComparer.OrdinalIgnoreCase);

        // MDF naming varies by game. OWOTS uses the mesh basename directly with
        // .mdf2.50, while SF6 commonly adds _v00 and uses .mdf2.31.
        var dotMesh = meshPath.IndexOf(".mesh.", StringComparison.OrdinalIgnoreCase);
        if (dotMesh < 0) return result;
        var meshBasePath = meshPath[..dotMesh];
        Stream? foundMdfStream = null;
        string? mdfPath = null;
        foreach (var nameSuffix in MdfNameSuffixCandidates)
        {
            foreach (var versionSuffix in MdfVersionCandidates)
            {
                var candidate = meshBasePath + nameSuffix + versionSuffix;
                foundMdfStream = openResource(candidate);
                if (foundMdfStream == null) continue;
                mdfPath = candidate;
                break;
            }
            if (foundMdfStream != null) break;
        }
        if (foundMdfStream == null || mdfPath == null) return result;
        using var mdfStream = foundMdfStream;
        MdfFile mdf;
        try
        {
            mdf = new MdfFile(new FileHandler(mdfStream, mdfPath));
            if (!mdf.Read()) return result;
        }
        catch { return result; }

        // material name -> index (mesh-side order)
        var nameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < mesh.MaterialNames.Count; i++)
            nameToIndex.TryAdd(mesh.MaterialNames[i], i);

        for (var m = 0; m < mdf.Materials.Count; m++)
        {
            var mat = mdf.Materials[m];
            var meshMatIndex = nameToIndex.TryGetValue(mat.Name, out var idx) ? idx : (m < mesh.MaterialNames.Count ? m : -1);
            if (meshMatIndex < 0 || result.ContainsKey(meshMatIndex)) continue;

            // Albedo slot priority: the material's authored base-color channel first.
            // Do not treat wrinkle/expression/VFX ALBD maps as a base texture. Skin shaders
            // blend several of those maps at runtime; projecting one over the whole head
            // produces the characteristic repeated-face corruption seen on ch001_00_10.
            var atlasQuadrant = false;
            string? albedoPath = GetMaterialBaseTexturePath(mat);
            if (!string.IsNullOrEmpty(albedoPath) && IsNullTexture(albedoPath))
                albedoPath = FindFamilyBaseTexturePath(mdf.Materials, mat);
            if (string.IsNullOrEmpty(albedoPath) && mat.Name.Contains("skin", StringComparison.OrdinalIgnoreCase))
            {
                var wrinkle = mat.Textures.FirstOrDefault(t =>
                    t.texType.Equals("Wrinkle_ALBMap01", StringComparison.OrdinalIgnoreCase));
                if (wrinkle != null && !string.IsNullOrEmpty(wrinkle.texPath))
                {
                    // OWOTS head skin stores four complete expression variants in one 2x2 atlas.
                    // The neutral companion is not exposed as a regular MDF texture slot; derive
                    // it from the wrinkle map name and crop the default (top-left) quadrant.
                    albedoPath = wrinkle.texPath
                        .Replace("_skin_FW_01_ALBD.tex", "_skin_neutral_FW_01_ALB.tex", StringComparison.OrdinalIgnoreCase);
                    atlasQuadrant = !albedoPath.Equals(wrinkle.texPath, StringComparison.OrdinalIgnoreCase);
                }
            }
            // Wilds materials use ColorParam for the tint used by eye lines and
            // eyelashes, while several older games use BaseColor. Rise eyelashes
            // use their own Eyelash_Color parameter; its Face material's Hair_Color
            // must not be applied to the whole baked face atlas.
            var baseColor = GetPreviewBaseColor(mat);
            var fallbackTexture = false;
            if (string.IsNullOrEmpty(albedoPath))
            {
                if (!mat.Name.Contains("eyelash", StringComparison.OrdinalIgnoreCase)) continue;
                fallbackTexture = true;
            }
            else if (IsNullTexture(albedoPath))
            {
                if (!mat.Name.Contains("eyelash", StringComparison.OrdinalIgnoreCase)) continue;
                fallbackTexture = true;
            }

            try
            {
                var isMhs3Pupil = mat.Name.EndsWith("_pupil", StringComparison.OrdinalIgnoreCase);
                var isEyeMaterial = mat.Name.Equals("m_eye", StringComparison.OrdinalIgnoreCase) ||
                    mat.Name.Equals("EyeL", StringComparison.OrdinalIgnoreCase) ||
                    mat.Name.Equals("EyeR", StringComparison.OrdinalIgnoreCase) ||
                    mat.Name.Contains("eyeball", StringComparison.OrdinalIgnoreCase) ||
                    isMhs3Pupil ||
                    mat.Name.EndsWith("_white", StringComparison.OrdinalIgnoreCase);
                var flags = mat.Header.Flags;
                var alphaCutout = (flags & (MaterialFlags.BaseAlphaTestEnable |
                    MaterialFlags.ForcedAlphaTestEnable | MaterialFlags.AlphaTestEnable)) != 0;
                // Some older assets expose the convention only through their material name.
                // Keep that as a compatibility fallback, but prefer the MDF's authoritative flags.
                alphaCutout |= mat.Name.Contains("ALP", StringComparison.OrdinalIgnoreCase) ||
                    IsAlphaCutoutMaterial(mat);
                // Wilds eye-line/eyeshadow cards store their coverage in BaseAlphaMap;
                // it is not an opaque base-color texture.
                alphaCutout |= mat.Textures.Any(texture =>
                    texture.texType.Equals("BaseAlphaMap", StringComparison.OrdinalIgnoreCase) &&
                    !IsNullTexture(texture.texPath));

                var isHairCard = IsHairCardMaterial(mat);
                ViewportTexture texture;
                if (fallbackTexture)
                {
                    texture = CreateSolidTexture(baseColor, mat.Name);
                }
                else
                {
                    var cacheKey = $"{albedoPath}|crop={atlasQuadrant}";
                    if (!decodedBaseTextures.TryGetValue(cacheKey, out var sourceTexture))
                    {
                        using var texStream = OpenNormalized(openResource, albedoPath!, meshPath);
                        if (texStream == null) continue;
                        sourceTexture = DecodeTexture(texStream, albedoPath!, atlasQuadrant);
                        decodedBaseTextures[cacheKey] = sourceTexture;
                    }
                    texture = CloneTexture(sourceTexture, mat.Name);
                }
                // Wilds hair cards use BaseAlphaMap as a coverage field. Its RGB
                // is a neutral grey mask, not the strand colour; using it directly
                // produces the white eyebrows/lashes seen in the basic preview.
                if (isHairCard && mat.Textures.Any(candidate =>
                        candidate.texType.Equals("BaseAlphaMap", StringComparison.OrdinalIgnoreCase) &&
                        !IsNullTexture(candidate.texPath)))
                    ApplyHairCardTint(texture, baseColor);
                else
                    ApplyBaseColor(texture, baseColor);
                ApplyCustomizeColorMasks(texture, mat, openResource, meshPath);
                var eyeColor = mat.Parameters.FirstOrDefault(parameter =>
                        parameter.paramName.Equals("Eye_ColorChange", StringComparison.OrdinalIgnoreCase))?.parameter
                    ?? mat.Parameters.FirstOrDefault(parameter =>
                        parameter.paramName.Equals("Eye_Color", StringComparison.OrdinalIgnoreCase))?.parameter;
                if (eyeColor.HasValue) ApplyEyeColorChange(texture, eyeColor.Value);
                if (isMhs3Pupil)
                    ApplyMhs3EyeColorLayers(texture, mat, openResource, meshPath);
                if (isEyeMaterial)
                {
                    // DMC5 eye ALBM stores color in RGB while its alpha channel is zero
                    // throughout; the game eye shader treats that channel as an internal
                    // mask, not surface coverage. Leaving it in the generic alpha-cutout
                    // path discards the entire eyeball in the preview.
                    ForceOpaqueAlpha(texture);
                    alphaCutout = false;
                }
                // Material color parameters can differ even when multiple materials reuse the
                // same source ALBD image. Keep those baked variants distinct during scene merge.
                texture.Name = fallbackTexture
                    ? $"{mat.Name}_fallback"
                    : $"{mat.Name}_{Path.GetFileName(albedoPath!)}";

                // RE hair cards commonly store coverage in a separate AlphaMap. Folding that
                // mask into the exported/preview RGBA texture prevents eyebrow and beard cards
                // from appearing as solid white or grey polygons. Do not use the packed
                // AlphaTranslucentOcclusionSSSMap as coverage; its red channel makes DMC5
                // hair sparse and makes the eye texture disappear in patches.
                var alphaHeader = mat.Textures.FirstOrDefault(candidate =>
                    candidate.texType.Equals("AlphaMap", StringComparison.OrdinalIgnoreCase) &&
                    !IsNullTexture(candidate.texPath));
                if (alphaHeader != null)
                {
                    using var alphaStream = OpenNormalized(openResource, alphaHeader.texPath, meshPath);
                    if (alphaStream != null)
                    {
                        var alphaAdjust = mat.Parameters.FirstOrDefault(parameter =>
                            parameter.paramName.Equals("AlphaAdjust", StringComparison.OrdinalIgnoreCase))?.parameter.X ?? 1f;
                        ApplyAlphaMap(texture, alphaStream, alphaHeader.texPath, alphaAdjust);
                        alphaCutout = true;
                    }
                }
                // Decode and material customization may each alter the pixels. Build the
                // GPU mip chain once from the final result, not after every sub-step.
                BuildMipChain(texture);
                result[meshMatIndex] = new PreviewMaterial(texture, alphaCutout);
            }
            catch { /* skip undecodable textures */ }
        }
        return result;
    }



    private static string? GetMaterialBaseTexturePath(MaterialData mat)
    {
        var albedo = mat.Textures.FirstOrDefault(t =>
                t.texType.Equals("BaseDielectricMap", StringComparison.OrdinalIgnoreCase) && !IsNullTexture(t.texPath))
            ?? mat.Textures.FirstOrDefault(t => IsBaseColorSlot(t.texType, t.texPath) && !IsNullTexture(t.texPath))
            ?? mat.Textures.FirstOrDefault(t => t.texType.Equals("BaseDielectricMap", StringComparison.OrdinalIgnoreCase))
            ?? mat.Textures.FirstOrDefault(t => IsBaseColorSlot(t.texType, t.texPath));
        return albedo?.texPath;
    }

    private static Vector4 GetPreviewBaseColor(MaterialData mat)
    {
        var authored = mat.Parameters.FirstOrDefault(parameter =>
                parameter.paramName.Equals("BaseColor", StringComparison.OrdinalIgnoreCase))?.parameter
            ?? mat.Parameters.FirstOrDefault(parameter =>
                parameter.paramName.Equals("ColorParam", StringComparison.OrdinalIgnoreCase))?.parameter;
        if (authored.HasValue) return authored.Value;

        // MHS3's BCLO hair map is a brightness/occlusion texture rather than the
        // final strand colour.  Its active colour swatch is stored separately in
        // SymbolColor_Default (or SymbolColor_R for a selected colour variant).
        // FixedHairColor is only active when its matching flag is enabled.
        if (IsHairCardMaterial(mat))
        {
            var useFixedHairColor = mat.Parameters.FirstOrDefault(parameter =>
                parameter.paramName.Equals("UseFixedHairColor", StringComparison.OrdinalIgnoreCase))?.parameter.X > 0.5f;
            if (useFixedHairColor)
            {
                var fixedHairColor = mat.Parameters.FirstOrDefault(parameter =>
                    parameter.paramName.Equals("FixedHairColor", StringComparison.OrdinalIgnoreCase))?.parameter;
                if (fixedHairColor.HasValue) return fixedHairColor.Value;
            }

            var useRedVariant = mat.Parameters.FirstOrDefault(parameter =>
                parameter.paramName.Equals("UseColorChange_R", StringComparison.OrdinalIgnoreCase))?.parameter.X > 0.5f;
            var symbolColor = mat.Parameters.FirstOrDefault(parameter =>
                    parameter.paramName.Equals(useRedVariant ? "SymbolColor_R" : "SymbolColor_Default", StringComparison.OrdinalIgnoreCase))?.parameter;
            if (symbolColor.HasValue) return symbolColor.Value;
        }

        // MHRise keeps lash coverage in AlphaMap and its strand colour in this
        // separate field. Face.Hair_Color is deliberately excluded: it affects
        // only a shader layer over FaceBaseMap, not the complete skin texture.
        if (mat.Name.Contains("eyelash", StringComparison.OrdinalIgnoreCase))
            return mat.Parameters.FirstOrDefault(parameter =>
                    parameter.paramName.Equals("Eyelash_Color", StringComparison.OrdinalIgnoreCase))?.parameter
                ?? Vector4.One;

        if (mat.Name.Contains("tooth", StringComparison.OrdinalIgnoreCase))
            return mat.Parameters.FirstOrDefault(parameter =>
                    parameter.paramName.Equals("Adjuatment_color", StringComparison.OrdinalIgnoreCase))?.parameter
                ?? Vector4.One;

        return Vector4.One;
    }

    private static string? FindFamilyBaseTexturePath(IEnumerable<MaterialData> materials, MaterialData mat)
    {
        var family = MaterialFamilyName(mat.Name);
        if (string.IsNullOrWhiteSpace(family)) return null;
        foreach (var other in materials)
        {
            if (ReferenceEquals(other, mat)) continue;
            if (!MaterialFamilyName(other.Name).Equals(family, StringComparison.OrdinalIgnoreCase)) continue;
            var path = GetMaterialBaseTexturePath(other);
            if (!string.IsNullOrEmpty(path) && !IsNullTexture(path)) return path;
        }
        return null;
    }

    private static string MaterialFamilyName(string name)
    {
        var end = name.Length;
        while (end > 0 && char.IsDigit(name[end - 1])) end--;
        return name[..end];
    }
    private static void ApplyCustomizeColorMasks(ViewportTexture texture, MaterialData mat,
        Func<string, Stream?> openResource, string meshPath)
    {
        ApplyCustomizeColorMask(texture, mat, openResource, meshPath, "CustomizeColor_Mask", 0);
        ApplyCustomizeColorMask(texture, mat, openResource, meshPath, "CustomizeColor_Mask2", 4);
    }

    private static void ApplyCustomizeColorMask(ViewportTexture texture, MaterialData mat,
        Func<string, Stream?> openResource, string meshPath, string slotName, int colorOffset)
    {
        var maskHeader = mat.Textures.FirstOrDefault(candidate =>
            candidate.texType.Equals(slotName, StringComparison.OrdinalIgnoreCase) &&
            !IsNullTexture(candidate.texPath));
        if (maskHeader == null) return;

        var colors = new Vector4[4];
        var rates = new float[4];
        var hasAnyColor = false;
        for (var i = 0; i < 4; i++)
        {
            colors[i] = mat.Parameters.FirstOrDefault(parameter =>
                parameter.paramName.Equals($"CustomizeColor_{colorOffset + i}", StringComparison.OrdinalIgnoreCase))?.parameter ?? Vector4.One;
            rates[i] = mat.Parameters.FirstOrDefault(parameter =>
                parameter.paramName.Equals($"CustomizeColor_{colorOffset + i}_BlendRate", StringComparison.OrdinalIgnoreCase))?.parameter.X ?? 1f;
            if (Math.Abs(colors[i].X - 1f) > 0.001f || Math.Abs(colors[i].Y - 1f) > 0.001f ||
                Math.Abs(colors[i].Z - 1f) > 0.001f)
                hasAnyColor = true;
        }
        if (!hasAnyColor) return;

        using var maskStream = OpenNormalized(openResource, maskHeader.texPath, meshPath);
        if (maskStream == null) return;
        using var mask = new TexService().DecodeToImage(maskStream, maskHeader.texPath);
        if (mask.Width != texture.Width || mask.Height != texture.Height)
            mask.Mutate(operation => operation.Resize(texture.Width, texture.Height));

        mask.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < texture.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < texture.Width; x++)
                {
                    var maskPixel = row[x];
                    var maskWeights = new[]
                    {
                        maskPixel.R / 255f,
                        maskPixel.G / 255f,
                        maskPixel.B / 255f,
                        maskPixel.A / 255f,
                    };
                    var index = y * texture.Width + x;
                    var pixel = texture.Pixels[index];
                    var r = ((pixel >> 16) & 0xFF) / 255f;
                    var g = ((pixel >> 8) & 0xFF) / 255f;
                    var b = (pixel & 0xFF) / 255f;
                    for (var i = 0; i < 4; i++)
                    {
                        var w = Math.Clamp(maskWeights[i] * rates[i], 0f, 1f);
                        if (w <= 0.001f) continue;
                        r = Lerp(r, r * colors[i].X, w);
                        g = Lerp(g, g * colors[i].Y, w);
                        b = Lerp(b, b * colors[i].Z, w);
                    }
                    var rr = (uint)Math.Clamp((int)MathF.Round(r * 255f), 0, 255);
                    var gg = (uint)Math.Clamp((int)MathF.Round(g * 255f), 0, 255);
                    var bb = (uint)Math.Clamp((int)MathF.Round(b * 255f), 0, 255);
                    texture.Pixels[index] = (pixel & 0xFF000000) | (rr << 16) | (gg << 8) | bb;
                }
            }
        });
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static bool IsNullTexture(string path)
        => path.Contains("NullWhite", StringComparison.OrdinalIgnoreCase)
           || path.Contains("NullBlack", StringComparison.OrdinalIgnoreCase)
           || path.Contains("NullGray", StringComparison.OrdinalIgnoreCase)
           || path.Contains("NullTexture", StringComparison.OrdinalIgnoreCase);

    private static void ApplyBaseColor(ViewportTexture texture, Vector4 color)
    {
        var red = Math.Clamp(color.X, 0f, 1f);
        var green = Math.Clamp(color.Y, 0f, 1f);
        var blue = Math.Clamp(color.Z, 0f, 1f);
        for (var i = 0; i < texture.Pixels.Length; i++)
        {
            var pixel = texture.Pixels[i];
            var r = (uint)Math.Clamp((int)MathF.Round(((pixel >> 16) & 0xFF) * red), 0, 255);
            var g = (uint)Math.Clamp((int)MathF.Round(((pixel >> 8) & 0xFF) * green), 0, 255);
            var b = (uint)Math.Clamp((int)MathF.Round((pixel & 0xFF) * blue), 0, 255);
            texture.Pixels[i] = (pixel & 0xFF000000) | (r << 16) | (g << 8) | b;
        }
    }

    /// <summary>Apply the authored hair colour while retaining an ALBA map's coverage.</summary>
    private static void ApplyHairCardTint(ViewportTexture texture, Vector4 color)
    {
        var red = (uint)Math.Clamp((int)MathF.Round(Math.Clamp(color.X, 0f, 1f) * 255f), 0, 255);
        var green = (uint)Math.Clamp((int)MathF.Round(Math.Clamp(color.Y, 0f, 1f) * 255f), 0, 255);
        var blue = (uint)Math.Clamp((int)MathF.Round(Math.Clamp(color.Z, 0f, 1f) * 255f), 0, 255);
        var opacity = Math.Clamp(color.W, 0f, 1f);
        for (var i = 0; i < texture.Pixels.Length; i++)
        {
            var coverage = (uint)Math.Clamp((int)MathF.Round(((texture.Pixels[i] >> 24) & 0xFF) * opacity), 0, 255);
            texture.Pixels[i] = (coverage << 24) | (red << 16) | (green << 8) | blue;
        }
    }

    private static void ForceOpaqueAlpha(ViewportTexture texture)
    {
        for (var i = 0; i < texture.Pixels.Length; i++)
            texture.Pixels[i] = texture.Pixels[i] | 0xFF000000u;
    }

    private static void ApplyEyeColorChange(ViewportTexture texture, Vector4 eyeColor)
    {
        var tintR = Math.Clamp(eyeColor.X, 0f, 1f);
        var tintG = Math.Clamp(eyeColor.Y, 0f, 1f);
        var tintB = Math.Clamp(eyeColor.Z, 0f, 1f);
        for (var i = 0; i < texture.Pixels.Length; i++)
        {
            var pixel = texture.Pixels[i];
            // The eyeball ALBD alpha is an iris mask, not surface transparency.
            var iris = 1f - ((pixel >> 24) & 0xFF) / 255f;
            if (iris <= 0f) continue;
            var rScale = 1f + (tintR - 1f) * iris;
            var gScale = 1f + (tintG - 1f) * iris;
            var bScale = 1f + (tintB - 1f) * iris;
            var r = (uint)Math.Clamp((int)MathF.Round(((pixel >> 16) & 0xFF) * rScale), 0, 255);
            var g = (uint)Math.Clamp((int)MathF.Round(((pixel >> 8) & 0xFF) * gScale), 0, 255);
            var b = (uint)Math.Clamp((int)MathF.Round((pixel & 0xFF) * bScale), 0, 255);
            texture.Pixels[i] = (pixel & 0xFF000000) | (r << 16) | (g << 8) | b;
        }
    }

    /// <summary>
    /// MHS3 stores its iris as a grayscale ALBD plus a ColorLayerReflectionMap whose
    /// R/G channels select SymbolColor_R/G.  The game shader combines them at render
    /// time; reproducing that compact composition keeps the iris detail while applying
    /// the character's selected colour.
    /// </summary>
    private static void ApplyMhs3EyeColorLayers(ViewportTexture texture, MaterialData mat,
        Func<string, Stream?> openResource, string meshPath)
    {
        var colorLayer = mat.Textures.FirstOrDefault(candidate =>
            candidate.texType.Equals("ColorLayerReflectionMap", StringComparison.OrdinalIgnoreCase) &&
            !IsNullTexture(candidate.texPath));
        if (colorLayer == null) return;

        var useRed = mat.Parameters.FirstOrDefault(parameter =>
            parameter.paramName.Equals("UseColorChange_R", StringComparison.OrdinalIgnoreCase))?.parameter.X > 0.5f;
        var useGreen = mat.Parameters.FirstOrDefault(parameter =>
            parameter.paramName.Equals("UseColorChange_G", StringComparison.OrdinalIgnoreCase))?.parameter.X > 0.5f;
        if (!useRed && !useGreen) return;

        var redColor = mat.Parameters.FirstOrDefault(parameter =>
                parameter.paramName.Equals("SymbolColor_R", StringComparison.OrdinalIgnoreCase))?.parameter
            ?? Vector4.One;
        var greenColor = mat.Parameters.FirstOrDefault(parameter =>
                parameter.paramName.Equals("SymbolColor_G", StringComparison.OrdinalIgnoreCase))?.parameter
            ?? Vector4.One;

        using var colorLayerStream = OpenNormalized(openResource, colorLayer.texPath, meshPath);
        if (colorLayerStream == null) return;
        using var colorMask = new TexService().DecodeToImage(colorLayerStream, colorLayer.texPath);
        if (colorMask.Width != texture.Width || colorMask.Height != texture.Height)
            colorMask.Mutate(operation => operation.Resize(texture.Width, texture.Height));

        colorMask.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < texture.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < texture.Width; x++)
                {
                    var mask = row[x];
                    var redWeight = useRed ? mask.R / 255f : 0f;
                    var greenWeight = useGreen ? mask.G / 255f : 0f;
                    var weight = Math.Clamp(redWeight + greenWeight, 0f, 1f);
                    if (weight <= 0.001f) continue;

                    var tint = redWeight + greenWeight <= 0.001f
                        ? Vector4.One
                        : (redColor * redWeight + greenColor * greenWeight) /
                          Math.Max(0.001f, redWeight + greenWeight);
                    var index = y * texture.Width + x;
                    var pixel = texture.Pixels[index];
                    var brightness = Math.Max((pixel >> 16) & 0xFF,
                        Math.Max((pixel >> 8) & 0xFF, pixel & 0xFF)) / 255f;
                    var targetR = brightness * Math.Clamp(tint.X, 0f, 1f);
                    var targetG = brightness * Math.Clamp(tint.Y, 0f, 1f);
                    var targetB = brightness * Math.Clamp(tint.Z, 0f, 1f);
                    var sourceR = ((pixel >> 16) & 0xFF) / 255f;
                    var sourceG = ((pixel >> 8) & 0xFF) / 255f;
                    var sourceB = (pixel & 0xFF) / 255f;
                    var r = (uint)Math.Clamp((int)MathF.Round(Lerp(sourceR, targetR, weight) * 255f), 0, 255);
                    var g = (uint)Math.Clamp((int)MathF.Round(Lerp(sourceG, targetG, weight) * 255f), 0, 255);
                    var b = (uint)Math.Clamp((int)MathF.Round(Lerp(sourceB, targetB, weight) * 255f), 0, 255);
                    texture.Pixels[index] = (pixel & 0xFF000000) | (r << 16) | (g << 8) | b;
                }
            }
        });
    }

    private static void ApplyAlphaMap(ViewportTexture texture, Stream alphaStream, string alphaPath, float adjust)
    {
        using var alpha = new TexService().DecodeToImage(alphaStream, alphaPath);
        if (alpha.Width != texture.Width || alpha.Height != texture.Height)
            alpha.Mutate(operation => operation.Resize(texture.Width, texture.Height));
        adjust = Math.Clamp(adjust, 0.01f, 8f);
        alpha.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < texture.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < texture.Width; x++)
                {
                    var source = row[x];
                    var coverage = (uint)Math.Clamp((int)MathF.Round(source.R * adjust), 0, 255);
                    var index = y * texture.Width + x;
                    texture.Pixels[index] = (texture.Pixels[index] & 0x00FFFFFF) | (coverage << 24);
                }
            }
        });
    }
    private static bool IsHairCardMaterial(MaterialData mat)
    {
        return mat.Name.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
               mat.Name.Contains("eyebrow", StringComparison.OrdinalIgnoreCase) ||
               mat.Name.Contains("eyelash", StringComparison.OrdinalIgnoreCase) ||
               mat.Name.Contains("eyeduct", StringComparison.OrdinalIgnoreCase) ||
               mat.Name.Contains("beard", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAlphaCutoutMaterial(MaterialData mat)
    {
        if (IsHairCardMaterial(mat) || mat.Name.Contains("eyeshadow", StringComparison.OrdinalIgnoreCase))
            return true;

        return mat.Textures.Any(texture =>
            texture.texType.Equals("BaseShiftMap", StringComparison.OrdinalIgnoreCase) ||
            texture.texType.Equals("BaseAlphaMap", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBaseColorSlot(string type, string path)
    {
        var derived = type.Contains("Wrinkle", StringComparison.OrdinalIgnoreCase) ||
                      type.Contains("EyeAwake", StringComparison.OrdinalIgnoreCase) ||
                      type.Contains("Gradient", StringComparison.OrdinalIgnoreCase) ||
                      type.Contains("VFX", StringComparison.OrdinalIgnoreCase) ||
                      path.Contains("_FW_", StringComparison.OrdinalIgnoreCase) ||
                      path.Contains("_EAW_", StringComparison.OrdinalIgnoreCase) ||
                      path.Contains("/VFX/", StringComparison.OrdinalIgnoreCase);
        if (derived) return false;
        return type.Equals("BaseColorMap", StringComparison.OrdinalIgnoreCase) ||
               // Monster Hunter Stories 3's toon/PBR character materials use
               // ALBR (albedo + reflection) as their authored base surface.
               type.Equals("BaseColorReflectionMap", StringComparison.OrdinalIgnoreCase) ||
               // MHS3 hair uses BCLO rather than an ALBD slot.  It contains the
               // authored strand colour plus baked occlusion, so skipping it leaves
               // the entire hair mesh untextured and grey/white in the preview.
               type.Equals("BrightnessColorLayerOcclusionMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("AlbedoMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseAlbedoMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseColor", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseMetalMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseShiftMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseAnisoShiftMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseAlphaMap", StringComparison.OrdinalIgnoreCase) ||
               // MHRise's player shaders predate the generic Base* slot names.
               // FaceBaseMap is the complete baked skin albedo; BaseMap is used
               // for teeth; AlphaMap is the grayscale lash coverage/color map.
               type.Equals("FaceBaseMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("AlphaMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("ALBD", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] MdfNameSuffixCandidates = ["", "_v00"];

    private static readonly string[] MdfVersionCandidates =
        [".mdf2.51", ".mdf2.50", ".mdf2.49", ".mdf2.45", ".mdf2.40", ".mdf2.34", ".mdf2.32", ".mdf2.31",
         ".mdf2.23", ".mdf2.21", ".mdf2.19", ".mdf2.13", ".mdf2.10", ".mdf2.6"];

    private static bool HasBufferRange(int bufferLength, int offset, int count)
        => offset >= 0 && count >= 0 && offset <= bufferLength && count <= bufferLength - offset;

    private static bool IsSubmeshRangeValid(Submesh sub)
    {
        var integerFaces = sub.Buffer.IntegerFaces;
        var faceLength = integerFaces != null ? integerFaces.Length : sub.Buffer.Faces?.Length ?? 0;
        return HasBufferRange(sub.Buffer.Positions.Length, sub.vertsIndexOffset, sub.vertCount) &&
               HasBufferRange(faceLength, sub.facesIndexOffset, sub.indicesCount);
    }
    /// <summary>Known .tex version suffixes, most recent RE games first (OWOTS/Pragmata era).</summary>
    private static readonly string[] TexVersionCandidates =
        [".251111100", ".241106027", ".241101895", ".250813143", ".240701001", ".240606151", ".760230703", ".143230113", ".143221013", ".35", ".34", ".30", ".28", ".190820018", ".11", ".10"];

    /// <summary>
    /// mdf texPath is relative ("Art/Model/.../x_ALBD.tex") and lacks natives prefix + version suffix.
    /// PAK hash is case-insensitive, so only prefix/suffix need fixing.
    /// Streaming variants (natives/stm/streaming/...) hold the full-res textures; the plain path
    /// only has 256px stubs whose BC grain renders as speckle at preview zoom. fmt_RE_MESH
    /// resolves streaming first — do the same.
    /// </summary>
    private static Stream? OpenNormalized(Func<string, Stream?> open, string texPath, string? meshPath = null)
    {
        var path = ResolveNormalizedPath(open, texPath, meshPath);
        return path == null ? null : open(path);
    }

    private static string? ResolveNormalizedPath(Func<string, Stream?> open, string texPath, string? meshPath = null)
    {
        // Some MHR MDF files prefix resource references with '@'. It is a
        // resource marker, not part of the on-disk path.
        var raw = texPath.Replace('\\', '/').TrimStart('/', '@');
        if (raw.StartsWith("natives/", StringComparison.OrdinalIgnoreCase))
            return ResolveVersionedPath(open, raw);

        // Convenience extraction sets often start directly at enemy/player/weapon
        // instead of preserving the natives/stm prefix. Try that layout first.
        var direct = ResolveVersionedPath(open, raw);
        if (direct != null) return direct;

        // MDF texture paths are relative and the native root is game-specific. DMC5
        // stores them under natives/x64, while RE Engine STM games use natives/stm.
        // Derive the root from the mesh first, then keep stm/x64 fallbacks for mixed PAKs.
        var roots = new List<string>();
        if (meshPath is { Length: > 0 })
        {
            var normalizedMesh = meshPath.Replace('\\', '/');
            var nativeMarker = normalizedMesh.IndexOf("natives/", StringComparison.OrdinalIgnoreCase);
            if (nativeMarker >= 0)
            {
                var rootEnd = normalizedMesh.IndexOf('/', nativeMarker + "natives/".Length);
                if (rootEnd > nativeMarker)
                    roots.Add(normalizedMesh[..(rootEnd + 1)]);
            }
        }
        roots.Add("natives/stm/");
        roots.Add("natives/x64/");

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = root + raw;
            var resolved = ResolveVersionedPath(open, candidate);
            if (resolved != null) return resolved;
        }
        return null;
    }

    private static Stream? OpenVersioned(Func<string, Stream?> open, string path)
    {
        var resolved = ResolveVersionedPath(open, path);
        return resolved == null ? null : open(resolved);
    }

    private static string? ResolveVersionedPath(Func<string, Stream?> open, string path)
    {
        var p = path.Replace('\\', '/');
        var lastDot = p.LastIndexOf('.');
        if (lastDot > 0 && p[(lastDot + 1)..].All(char.IsDigit))
        {
            using var exact = open(p);
            if (exact != null) return p;
            using var exactStreaming = open(p + ".STM");
            return exactStreaming == null ? null : p + ".STM";
        }

        var roots = new[] { "natives/stm/", "natives/x64/" };
        foreach (var root in roots)
        {
            var streaming = p.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? root + "streaming/" + p[root.Length..]
                : p;
            foreach (var ver in TexVersionCandidates)
            {
                var candidate = streaming + ver;
                using var stream = open(candidate);
                if (stream != null) return candidate;
                using var stmStream = open(candidate + ".STM");
                if (stmStream != null) return candidate + ".STM";
            }
        }
        foreach (var ver in TexVersionCandidates)
        {
            var candidate = p + ver;
            using var stream = open(candidate);
            if (stream != null) return candidate;
            using var stmStream = open(candidate + ".STM");
            if (stmStream != null) return candidate + ".STM";
        }
        return null;
    }

    private const int MaxTextureSize = 2048;

    private static ViewportTexture CreateSolidTexture(Vector4 color, string name)
    {
        var r = (uint)Math.Clamp((int)MathF.Round(color.X * 255f), 0, 255);
        var g = (uint)Math.Clamp((int)MathF.Round(color.Y * 255f), 0, 255);
        var b = (uint)Math.Clamp((int)MathF.Round(color.Z * 255f), 0, 255);
        var texture = new ViewportTexture
        {
            Width = 1,
            Height = 1,
            Pixels = [0xFF000000u | (r << 16) | (g << 8) | b],
            Name = name,
        };
        return texture;
    }

    private static ViewportTexture DecodeTexture(Stream texStream, string texPath, bool cropTopLeftQuadrant = false)
    {
        using var img = new TexService().DecodeToImage(texStream, texPath);
        if (cropTopLeftQuadrant && img.Width >= 2 && img.Height >= 2)
            img.Mutate(operation => operation.Crop(new Rectangle(0, 0, img.Width / 2, img.Height / 2)));
        var w = img.Width;
        var h = img.Height;

        // downscale (nearest) to keep sampling fast
        if (w > MaxTextureSize || h > MaxTextureSize)
        {
            var scale = MathF.Max(w / (float)MaxTextureSize, h / (float)MaxTextureSize);
            w = Math.Max(1, (int)(w / scale));
            h = Math.Max(1, (int)(h / scale));
            img.Mutate(x => x.Resize(w, h));
        }

        var pixels = new uint[w * h];
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < w; x++)
                {
                var p = row[x];
                // Keep the source alpha channel. The GPU only consumes it for materials
                // explicitly marked ALP; ordinary skin/cloth albedo alpha may carry packed
                // material data and must remain visually opaque.
                pixels[y * w + x] = ((uint)p.A << 24) | ((uint)p.R << 16) | ((uint)p.G << 8) | p.B;
                }
            }
        });
        var tex = new ViewportTexture { Width = w, Height = h, Pixels = pixels, Name = Path.GetFileName(texPath) };
        return tex;
    }

    private static ViewportTexture CloneTexture(ViewportTexture source, string name)
    {
        var pixels = new uint[source.Pixels.Length];
        Array.Copy(source.Pixels, pixels, pixels.Length);
        return new ViewportTexture
        {
            Width = source.Width,
            Height = source.Height,
            Pixels = pixels,
            Name = name,
        };
    }

    /// <summary>Generate a full mip chain (box-filter 2x2 per level) so the rasterizer can pick a mip that matches the pixel footprint, eliminating minification aliasing.</summary>
    private static void BuildMipChain(ViewportTexture tex)
    {
        var mips = new List<uint[]> { tex.Pixels };
        var mws = new List<int> { tex.Width };
        var mhs = new List<int> { tex.Height };
        var cur = tex.Pixels;
        int cw = tex.Width, ch = tex.Height;
        while (cw > 1 || ch > 1)
        {
            var nw = Math.Max(1, cw / 2);
            var nh = Math.Max(1, ch / 2);
            var next = new uint[nw * nh];
            for (var y = 0; y < nh; y++)
            for (var x = 0; x < nw; x++)
            {
                uint sb = 0, sg = 0, sr = 0, sa = 0, cnt = 0;
                for (var dy = 0; dy < 2; dy++)
                for (var dx = 0; dx < 2; dx++)
                {
                    var sx = Math.Min(cw - 1, x * 2 + dx);
                    var sy = Math.Min(ch - 1, y * 2 + dy);
                    var p = cur[sy * cw + sx];
                    sb += p & 0xFF; sg += (p >> 8) & 0xFF; sr += (p >> 16) & 0xFF;
                    sa += (p >> 24) & 0xFF; cnt++;
                }
                next[y * nw + x] = ((sa / cnt) << 24) | ((sr / cnt) << 16) | ((sg / cnt) << 8) | (sb / cnt);
            }
            mips.Add(next); mws.Add(nw); mhs.Add(nh);
            cur = next; cw = nw; ch = nh;
        }
        tex.Mips = mips.ToArray();
        tex.MipW = mws.ToArray();
        tex.MipH = mhs.ToArray();
    }

    private static Vector3 SafeNormal(Vector3 n)
        => n.LengthSquared() < 1e-6f ? Vector3.UnitZ : Vector3.Normalize(n);

    private static bool IsPreviewHelperMaterial(string name)
        => name.Contains("EffectEmitter", StringComparison.OrdinalIgnoreCase)
           || name.Contains("VFXEmitter", StringComparison.OrdinalIgnoreCase)
           // MHR NPC body meshes can carry item/prop geometry in the same mesh
           // (for example npc102_00_body's nitem_028_002 group).  These are
           // not character surfaces and otherwise appear as large grey slabs
           // in the generic preview.
           || name.StartsWith("nitem_", StringComparison.OrdinalIgnoreCase)
           // Wilds character assets retain a pair of DCC/runtime volume passes. They use
           // Simple_VolumeBlend with no authored colour texture, so the generic preview
           // paints them as solid grey over the actual garment. They are not renderable
           // character surfaces and must stay out of the preview (and preview export).
           || name.Equals("fakeShade", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("_mesh_lambert1", StringComparison.OrdinalIgnoreCase)
           // Stories 3 uses an untextured *_outline shell for the in-game toon outline.
           // It is not an albedo surface; drawing it as an opaque material covers the
           // face with a blue-grey mask and leaves fragments of the real skin visible.
           || name.EndsWith("_outline", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnsupportedEyeOverlayMaterial(string name)
        => name.Contains("cornea", StringComparison.OrdinalIgnoreCase)
           || name.Contains("eyelens", StringComparison.OrdinalIgnoreCase)
           || name.Contains("eyeouter", StringComparison.OrdinalIgnoreCase)
           || name.Contains("eyewet", StringComparison.OrdinalIgnoreCase)
           || name.Contains("eye_shadow", StringComparison.OrdinalIgnoreCase)
           || name.Contains("human_tear", StringComparison.OrdinalIgnoreCase)
           // MHS3 names its refractive eye shells chXX_..._L_lens/R_lens.
           // They have only AlphaMap, so a generic preview turns them into opaque
           // grey discs over the properly textured pupil and sclera.
           || name.EndsWith("_L_lens", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("_R_lens", StringComparison.OrdinalIgnoreCase);

    private static float GetGroupsBoundsDiagonal(IEnumerable<MeshGroup> groups)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var found = false;
        foreach (var position in groups.SelectMany(g => g.Submeshes)
                     .Where(IsSubmeshRangeValid)
                     .SelectMany(s => s.Positions.ToArray()))
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
            found = true;
        }
        return found ? MathF.Max(1e-4f, Vector3.Distance(min, max)) : 1f;
    }

    private static bool IsNearDuplicateGroup(MeshGroup candidate, IReadOnlyList<MeshGroup> kept, float modelDiagonal)
    {
        var candidateParts = candidate.Submeshes
            .Where(s => IsSubmeshRangeValid(s) && s.Positions.Length > 0)
            .GroupBy(s => s.materialIndex)
            .ToArray();
        if (candidateParts.Length == 0) return false;

        foreach (var part in candidateParts)
        {
            var candidateVertices = part.SelectMany(s => s.Positions.ToArray()).ToArray();
            var duplicate = kept.Any(previous =>
            {
                var previousVertices = previous.Submeshes
                    .Where(s => IsSubmeshRangeValid(s) && s.materialIndex == part.Key)
                    .SelectMany(s => s.Positions.ToArray())
                    .ToArray();
                return GeometryBoundsMatch(candidateVertices, previousVertices, modelDiagonal);
            });
            if (!duplicate) return false;
        }
        return true;
    }

    private static bool GeometryBoundsMatch(Vector3[] a, Vector3[] b, float modelDiagonal)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        var countRatio = a.Length / (float)b.Length;
        if (countRatio is < 0.85f or > 1.18f) return false;

        static (Vector3 Center, Vector3 Size) Bounds(Vector3[] vertices)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var position in vertices)
            {
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }
            return ((min + max) * 0.5f, max - min);
        }

        var ba = Bounds(a);
        var bb = Bounds(b);
        return Vector3.Distance(ba.Center, bb.Center) <= modelDiagonal * 0.006f &&
               Vector3.Distance(ba.Size, bb.Size) <= modelDiagonal * 0.012f;
    }

    /// <summary>Lists all motions in a .motlist with readable labels (file base name + mot id).</summary>
    public static IReadOnlyList<string> ListMotionNames(Stream motlistStream, string motlistPath)
        => ListMotions(motlistStream, motlistPath).Select(motion => motion.DisplayName).ToList();

    /// <summary>Lists motions that contain embedded animation data, preserving their source indices.</summary>
    public static IReadOnlyList<MotionInfo> ListMotions(Stream motlistStream, string motlistPath)
    {
        using var motlist = new MotlistFile(new FileHandler(motlistStream, motlistPath));
        if (!motlist.Read())
            throw new InvalidDataException($"Failed to parse .motlist: {motlistPath}");
        var fileName = Path.GetFileName(motlistPath);
        var cut = fileName.IndexOf(".motlist", StringComparison.OrdinalIgnoreCase);
        var baseName = cut > 0 ? fileName[..cut] : fileName;
        return motlist.Motions.Select((motion, index) => (motion, index))
            .Where(item => item.motion.MotFile is MotFile)
            .Select(item => new MotionInfo(item.index,
                $"{baseName} #{item.index}（编号 {item.motion.motNumber}）",
                item.motion.motNumber))
            .ToList();
    }

    private static (int Joint, float Weight)[] ExtractWeights(VertexBoneWeights vw)
    {
        var count = Math.Min(vw.IndexCount, 8);
        var pairs = new List<(int, float)>(count);
        for (var k = 0; k < count; k++)
        {
            var wt = vw.GetWeight(k);
            var j = vw.GetIndex(k);
            if (wt > 0 && j >= 0) pairs.Add((j, wt));
        }
        if (pairs.Count == 0) pairs.Add((0, 1f));
        var sum = pairs.Sum(p => p.Item2);
        return pairs.Select(p => (p.Item1, p.Item2 / sum)).ToArray();
    }

    private static (ViewportBone[] bones, int[] deformToBone) BuildBones(MeshBoneHierarchy? hierarchy)
    {
        if (hierarchy == null || hierarchy.Bones.Count == 0)
            return ([], []);

        var bones = hierarchy.Bones.Select(b => new ViewportBone
        {
            Name = string.IsNullOrEmpty(b.name) ? $"bone_{b.index}" : b.name,
            ParentIndex = b.parentIndex >= 0 && b.parentIndex < hierarchy.Bones.Count && b.parentIndex != b.index ? b.parentIndex : -1,
            LocalBind = ToMatrix(b.localTransform),
            InverseGlobalBind = ToMatrix(b.inverseGlobalTransform),
        }).ToArray();

        var deform = hierarchy.DeformBones.Count > 0 ? hierarchy.DeformBones : hierarchy.Bones;
        var deformToBone = deform.Select(b => b.index).ToArray();
        return (bones, deformToBone);
    }

    /// <summary>
    /// Parse one motion from a .motlist into per-bone keyframe tracks.
    /// .mot tracks address bones by NAME HASH (boneHash), NOT by the mesh bone index, so when
    /// <paramref name="meshBoneNames"/> is supplied the tracks are remapped onto the mesh skeleton
    /// via MurMur3 name hash (matches fmt_RE_MESH). Tracks for bones the mesh lacks are skipped.
    /// </summary>
    public static AnimationClip LoadAnimation(
        Stream motlistStream,
        string motlistPath,
        int motionIndex = 0,
        IReadOnlyList<string>? meshBoneNames = null,
        IReadOnlyList<ViewportMesh>? sceneMeshes = null)
    {
        using var motlist = new MotlistFile(new FileHandler(motlistStream, motlistPath));
        if (!motlist.Read())
            throw new InvalidDataException($"Failed to parse .motlist: {motlistPath}");
        var motion = motlist.Motions.ElementAtOrDefault(motionIndex)
            ?? throw new InvalidDataException($"Motion index {motionIndex} out of range");
        if (motion.MotFile is not MotFile mot)
            throw new NotSupportedException("Motion has no embedded .mot data");
        var usesReferencePoseTracks = (int)mot.Header.version == 892;

        // Noesis adds bones that exist in the MOT header but are absent from
        // an individual mesh part before it builds the keyframe animation.
        // MH Wilds stores leg/hand IK and helper bones this way. Keep the
        // original weighted bone indices intact and append only the missing
        // animation bones.
        if (sceneMeshes is { Count: > 0 })
        {
            foreach (var sceneMesh in sceneMeshes)
            {
                AddAnimationBones(sceneMesh, mot.Bones);
            }

            meshBoneNames = sceneMeshes
                .SelectMany(mesh => mesh.Bones)
                .Select(bone => bone.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // map bone-name hash -> mesh bone index (case-sensitive MurMur3, as RE uses)
        Dictionary<uint, (int Index, string Name)>? hashToBone = null;
        if (meshBoneNames != null)
        {
            hashToBone = new Dictionary<uint, (int, string)>(meshBoneNames.Count);
            for (var i = 0; i < meshBoneNames.Count; i++)
                hashToBone.TryAdd(MurMur3HashUtils.GetHash(meshBoneNames[i]), (i, meshBoneNames[i]));
        }

        var tracks = new Dictionary<int, BoneTrack>();
        var namedTracks = new Dictionary<string, BoneTrack>(StringComparer.OrdinalIgnoreCase);
        var duration = 0f;
        var sourceFrameRate = 60;
        var sourceFrameCount = 0;
        var targetBoneNames = meshBoneNames;
        foreach (var clip in mot.BoneClips)
        {
            int boneIndex = clip.ClipHeader.boneIndex;
            string? boneName = null;
            if (hashToBone != null)
            {
                if (hashToBone.TryGetValue(clip.ClipHeader.boneHash, out var target))
                {
                    boneIndex = target.Index;
                    boneName = target.Name;
                }
                else
                {
                    var fallbackIndex = clip.ClipHeader.boneIndex;
                    if (clip.ClipHeader.boneHash != 0 ||
                        fallbackIndex < 0 ||
                        targetBoneNames == null ||
                        fallbackIndex >= targetBoneNames.Count)
                        continue; // helper/twist bone not present in the mesh skeleton -> skip track
                    boneIndex = fallbackIndex;
                    boneName = targetBoneNames[fallbackIndex];
                }
            }
            var track = new BoneTrack();

            if (clip.HasTranslation && clip.Translation!.translations is { Length: > 0 } tr)
            {
                var fps = TrackFrameRate(clip.Translation);
                var frames = clip.Translation.frameIndexes;
                var maxFrame = TrackMaxFrame(clip.Translation, tr.Length);
                track.TransTimes = BuildTimes(frames, tr.Length, fps);
                track.Translations = tr;
                sourceFrameRate = Math.Max(sourceFrameRate, (int)fps);
                sourceFrameCount = Math.Max(sourceFrameCount, (int)MathF.Round(maxFrame));
                duration = Math.Max(duration, maxFrame / fps);
            }
            if (clip.HasRotation && clip.Rotation!.rotations is { Length: > 0 } ro)
            {
                var fps = TrackFrameRate(clip.Rotation);
                var frames = clip.Rotation.frameIndexes;
                var maxFrame = TrackMaxFrame(clip.Rotation, ro.Length);
                track.RotTimes = BuildTimes(frames, ro.Length, fps);
                track.Rotations = ro.Select(q => q.W < 0 ? new Quaternion(-q.X, -q.Y, -q.Z, -q.W) : q)
                                    .Select(Quaternion.Normalize).ToArray();
                sourceFrameRate = Math.Max(sourceFrameRate, (int)fps);
                sourceFrameCount = Math.Max(sourceFrameCount, (int)MathF.Round(maxFrame));
                duration = Math.Max(duration, maxFrame / fps);
            }
            tracks[boneIndex] = track;
            if (boneName != null) namedTracks[boneName] = track;
        }

        if (usesReferencePoseTracks)
        {
            RetargetMot892Tracks(mot, namedTracks,
                sceneMeshes?.OrderByDescending(mesh => mesh.Bones.Length).FirstOrDefault());
            if (hashToBone != null)
                foreach (var (name, track) in namedTracks)
                    if (hashToBone.TryGetValue(MurMur3HashUtils.GetHash(name), out var target))
                        tracks[target.Index] = track;
        }
        return new AnimationClip
        {
            Name = $"mot_{motion.motNumber}", Duration = duration,
            FrameRate = sourceFrameRate, FrameCount = sourceFrameCount,
            Tracks = tracks, NamedTracks = namedTracks,
        };
    }

    /// <summary>
    /// Onimusha 2 MOT 892 stores limb translations as model-space IK targets,
    /// while rotation channels remain local bone rotations. The MOT bone table
    /// supplies each target's reference position. Retarget those target deltas
    /// onto the mesh bind pose and solve the two-bone chains; treating the target
    /// coordinates as local transforms makes hands and feet fly off the model.
    /// </summary>
    internal static void RetargetMot892Tracks(
        MotFile mot,
        IDictionary<string, BoneTrack> targetTracks,
        ViewportMesh? targetSkeleton)
    {
        if (targetSkeleton == null || targetTracks.Count == 0) return;
        var referenceBones = mot.Bones
            .Where(bone => !string.IsNullOrWhiteSpace(bone.boneName))
            .GroupBy(bone => bone.boneName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var boneIndexes = targetSkeleton.Bones
            .Select((bone, index) => (bone.Name, index))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index,
                StringComparer.OrdinalIgnoreCase);
        var rawTracks = targetTracks.ToDictionary(item => item.Key, item => item.Value,
            StringComparer.OrdinalIgnoreCase);
        var xzAxisTargets = mot.BoneClips
            .Where(clip => clip.Translation?.TranslationCompressionType is
                           Vector3Decompression.LoadVector3sXZAxis8Bit or
                           Vector3Decompression.LoadVector3sXZAxis12Bit or
                           Vector3Decompression.LoadVector3sXZAxis16Bit &&
                           !string.IsNullOrWhiteSpace(clip.ClipHeader.boneName))
            .Select(clip => clip.ClipHeader.boneName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ikChains = new[]
        {
            (Root: "R_upperarm", Mid: "R_lowerarmX", End: "R_hand", Ball: (string?)null, MirrorPole: false),
            (Root: "L_upperarm", Mid: "L_lowerarmX", End: "L_hand", Ball: (string?)null, MirrorPole: true),
            (Root: "R_thigh", Mid: "R_calf", End: "R_foot", Ball: (string?)"R_ball", MirrorPole: false),
            (Root: "L_thigh", Mid: "L_calf", End: "L_foot", Ball: (string?)"L_ball", MirrorPole: true),
        }.Where(chain => boneIndexes.ContainsKey(chain.Root) &&
                         boneIndexes.ContainsKey(chain.Mid) &&
                         boneIndexes.ContainsKey(chain.End) &&
                         rawTracks.TryGetValue(chain.End, out var endTrack) &&
                         endTrack.Translations is { Length: > 0 })
         .ToArray();
        foreach (var chain in ikChains)
        {
            if (!targetTracks.ContainsKey(chain.Root)) targetTracks[chain.Root] = new BoneTrack();
            if (!targetTracks.ContainsKey(chain.Mid)) targetTracks[chain.Mid] = new BoneTrack();
            if (chain.Ball != null && boneIndexes.ContainsKey(chain.Ball) &&
                !targetTracks.ContainsKey(chain.Ball))
                targetTracks[chain.Ball] = new BoneTrack();
            if (chain.Root.EndsWith("upperarm", StringComparison.OrdinalIgnoreCase))
            {
                var lowerYName = chain.Root.StartsWith("L_", StringComparison.OrdinalIgnoreCase)
                    ? "L_lowerarmY"
                    : "R_lowerarmY";
                if (boneIndexes.ContainsKey(lowerYName) && !targetTracks.ContainsKey(lowerYName))
                    targetTracks[lowerYName] = new BoneTrack();
            }
        }
        var fps = mot.Header.FrameRate > 0 ? mot.Header.FrameRate : 60;
        var lastFrame = (int)MathF.Ceiling(MathF.Max(mot.Header.frameCount, mot.Header.endFrame));
        var times = Enumerable.Range(0, Math.Max(1, lastFrame + 1))
            .Select(frame => frame / (float)fps)
            .ToArray();
        var baked = targetTracks.Keys
            .Where(name => boneIndexes.ContainsKey(name) && referenceBones.ContainsKey(name))
            .ToDictionary(name => name, _ => (Translations: new Vector3[times.Length],
                Rotations: new Quaternion[times.Length]), StringComparer.OrdinalIgnoreCase);

        var bindGlobals = new Matrix4x4[targetSkeleton.Bones.Length];
        var bindComputed = new bool[bindGlobals.Length];
        for (var index = 0; index < bindGlobals.Length; index++) ComputeBindGlobal(index);

        for (var frame = 0; frame < times.Length; frame++)
        {
            var globals = new Matrix4x4[targetSkeleton.Bones.Length];
            var locals = new Matrix4x4[targetSkeleton.Bones.Length];
            var computed = new bool[globals.Length];
            for (var index = 0; index < globals.Length; index++) ComputeAnimatedGlobal(index);
            foreach (var chain in ikChains) SolveIkChain(chain);

            foreach (var (name, data) in baked)
            {
                var index = boneIndexes[name];
                var local = globals[index];
                var parent = targetSkeleton.Bones[index].ParentIndex;
                if (parent >= 0 && parent < globals.Length &&
                    Matrix4x4.Invert(globals[parent], out var inverseParent))
                    local *= inverseParent;
                if (!Matrix4x4.Decompose(local, out _, out var rotation, out var translation))
                {
                    rotation = Quaternion.Identity;
                    translation = local.Translation;
                }
                data.Translations[frame] = translation;
                data.Rotations[frame] = NormalizeOrIdentity(rotation);
            }

            void ComputeAnimatedGlobal(int index)
            {
                if (computed[index]) return;
                var bone = targetSkeleton.Bones[index];
                var parent = bone.ParentIndex;
                var parentGlobal = Matrix4x4.Identity;
                if (parent >= 0 && parent < globals.Length)
                {
                    ComputeAnimatedGlobal(parent);
                    parentGlobal = globals[parent];
                }

                Matrix4x4.Decompose(bone.LocalBind, out var scale, out var rotation, out var translation);
                if (rawTracks.TryGetValue(bone.Name, out var raw) &&
                    referenceBones.TryGetValue(bone.Name, out var reference))
                {
                    // Root/mid rotations carry axial twist and can be applied
                    // before the positional solve. End rotations are expressed
                    // in model space (or disabled for hands), never as local bone
                    // rotations.
                    var ikEnd = ikChains.Any(chain =>
                        bone.Name.Equals(chain.End, StringComparison.OrdinalIgnoreCase));
                    if (!ikEnd && raw.Rotations is { Length: > 0 } rotations &&
                        raw.RotTimes is { Length: > 0 } rotationTimes)
                    {
                        rotation = NormalizeOrIdentity(
                            SampleTrack(rotations, rotationTimes, times[frame]));

                        // JointData type 7 is not an ordinary child rotation.
                        // Onimusha 2 folds the spine controller into the breast
                        // joint before applying the breast's own X rotation.
                        // Applying only the breast channel loses the torso lean
                        // that the original runtime visibly preserves.
                        if (bone.Name.Equals("breast", StringComparison.OrdinalIgnoreCase) &&
                            rawTracks.TryGetValue("spine", out var spineTrack) &&
                            spineTrack.Rotations is { Length: > 0 } spineRotations &&
                            spineTrack.RotTimes is { Length: > 0 } spineRotationTimes)
                            rotation = NormalizeOrIdentity(
                                SampleTrack(spineRotations, spineRotationTimes, times[frame]) * rotation);
                    }
                }
                locals[index] = Matrix4x4.CreateScale(scale) *
                                Matrix4x4.CreateFromQuaternion(rotation) *
                                Matrix4x4.CreateTranslation(translation);
                globals[index] = locals[index] * parentGlobal;

                // MOT 892 emits model-space IK targets on limb bones (hands,
                // feet, thighs and upper arms). Applying those targets as bone
                // translations stretches the skinned chain. Only the pelvis
                // channel is a deform-pose translation. Limb targets are consumed
                // by SolveIkChain below and must not also become local positions.
                if (bone.Name.Equals("pelvis", StringComparison.OrdinalIgnoreCase) &&
                    rawTracks.TryGetValue(bone.Name, out var positionTrack) &&
                    referenceBones.TryGetValue(bone.Name, out var positionReference) &&
                    positionTrack.Translations is { Length: > 0 } translations &&
                    positionTrack.TransTimes is { Length: > 0 } translationTimes)
                {
                    globals[index].Translation =
                        SampleTrack(translations, translationTimes, times[frame]);
                }
                computed[index] = true;
            }

            void SolveIkChain((string Root, string Mid, string End, string? Ball, bool MirrorPole) chain)
            {
                var rootIndex = boneIndexes[chain.Root];
                var midIndex = boneIndexes[chain.Mid];
                var endIndex = boneIndexes[chain.End];
                if (!rawTracks.TryGetValue(chain.End, out var targetTrack) ||
                    targetTrack.Translations is not { Length: > 0 } targetValues ||
                    targetTrack.TransTimes is not { Length: > 0 } targetTimes ||
                    !referenceBones.TryGetValue(chain.End, out var targetReference))
                    return;

                var rootPosition = globals[rootIndex].Translation;
                var currentMid = globals[midIndex].Translation;
                var currentEnd = globals[endIndex].Translation;
                var upperLength = Vector3.Distance(rootPosition, currentMid);
                var lowerLength = Vector3.Distance(currentMid, currentEnd);
                if (upperLength < 1e-5f || lowerLength < 1e-5f) return;

                // The translation on a JointData 3/4 thigh is the signed side
                // axis of the IK basis. Transform it by the pelvis controller;
                // type 4 uses the mirrored sign. This axis remains stable through
                // large pelvis turns, unlike a screen/model-space pole guess.
                var planeNormal = Vector3.UnitZ;
                if (rawTracks.TryGetValue(chain.Root, out var poleTrack) &&
                    poleTrack.Translations is { Length: > 0 } poleValues &&
                    poleTrack.TransTimes is { Length: > 0 } poleTimes)
                {
                    planeNormal = SampleTrack(poleValues, poleTimes, times[frame]);
                    var legacyLeftArmAxis = chain.MirrorPole &&
                                            chain.Root.EndsWith("upperarm", StringComparison.OrdinalIgnoreCase) &&
                                            xzAxisTargets.Contains(chain.Root);
                    if (legacyLeftArmAxis)
                    {
                        // Type 6 stores only the X/Z controller pair. Reconstruct
                        // the runtime shoulder side axis from that normalized pair;
                        // these coefficients come from the game's type-6 basis
                        // conversion, not from a screen-space mirror.
                        var encoded = Vector3.Normalize(planeNormal);
                        planeNormal = new Vector3(
                            0.18400343f * encoded.X - 0.04845674f * encoded.Z + 0.88369350f,
                           -0.29193612f * encoded.X - 0.00575891f * encoded.Z + 0.17522209f,
                           -0.17461551f * encoded.X + 0.05430577f * encoded.Z + 0.41430242f);
                    }
                    else if (chain.MirrorPole)
                        planeNormal = -planeNormal;
                    if (planeNormal.LengthSquared() > 1e-8f)
                    {
                        if (!legacyLeftArmAxis)
                        {
                            var poleParent = targetSkeleton.Bones[rootIndex].ParentIndex;
                            if (poleParent >= 0 && poleParent < globals.Length &&
                                Matrix4x4.Decompose(globals[poleParent], out _, out var parentRotation, out _))
                                planeNormal = Vector3.Transform(planeNormal, parentRotation);
                        }
                        planeNormal = Vector3.Normalize(planeNormal);
                    }
                }

                // MOT 892 limb positions are already targets in the motion's
                // model space. Hand tracks target the hand joint. Foot tracks
                // target the ball joint, so convert that point back to the ankle
                // using the requested global foot orientation and the mesh bind
                // offset from foot to ball.
                var requested = SampleTrack(targetValues, targetTimes, times[frame]);
                Quaternion? requestedEndRotation = null;
                Quaternion? requestedBallRotation = null;
                int? requestedBallIndex = null;
                if (rawTracks.TryGetValue(chain.End, out var endTrack) &&
                    endTrack.Rotations is { Length: > 0 } endRotations &&
                    endTrack.RotTimes is { Length: > 0 } endRotationTimes)
                    requestedEndRotation = NormalizeOrIdentity(
                        SampleTrack(endRotations, endRotationTimes, times[frame]));

                if (chain.Ball != null)
                {
                    if (requestedEndRotation is Quaternion solvedBallRotation &&
                        boneIndexes.TryGetValue(chain.Ball, out var ballIndex))
                    {
                        requestedBallIndex = ballIndex;
                        requestedBallRotation = solvedBallRotation;
                        var ballLocalMatrix = targetSkeleton.Bones[ballIndex].LocalBind;
                        var ballLocal = ballLocalMatrix.Translation;
                        var bindBallRotation = NormalizeOrIdentity(
                            Quaternion.CreateFromRotationMatrix(ballLocalMatrix));

                        // The controller quaternion is the requested ball-joint
                        // orientation. JointData type 1 applies the inverse of the
                        // serialized ball bind locally, so recover the parent foot
                        // orientation before converting the ball position to ankle.
                        var solvedFootRotation = NormalizeOrIdentity(
                            bindBallRotation * solvedBallRotation);
                        requestedEndRotation = solvedFootRotation;
                        var ballOffset = Vector3.Transform(ballLocal, solvedFootRotation);

                        requested -= ballOffset;
                    }
                }
                var toTarget = requested - rootPosition;
                var distance = toTarget.Length();
                if (distance < 1e-5f) return;
                var targetDirection = toTarget / distance;
                var solvedDistance = Math.Clamp(distance,
                    MathF.Abs(upperLength - lowerLength) + 1e-4f,
                    upperLength + lowerLength - 1e-4f);
                var cosine = Math.Clamp((upperLength * upperLength + solvedDistance * solvedDistance -
                                         lowerLength * lowerLength) /
                                        (2f * upperLength * solvedDistance), -1f, 1f);
                var sine = MathF.Sqrt(MathF.Max(0f, 1f - cosine * cosine));
                // pJnt_info 3/4/5/6 stores the opposite of the chain's geometric
                // normal (the left controller uses the mirrored convention).
                // normal x target therefore points towards the real knee/elbow.
                    var bendDirection = Vector3.Cross(planeNormal, targetDirection);
                bendDirection -= targetDirection * Vector3.Dot(bendDirection, targetDirection);
                if (bendDirection.LengthSquared() < 1e-8f)
                    bendDirection = Vector3.UnitY - targetDirection * Vector3.Dot(Vector3.UnitY, targetDirection);
                bendDirection = Vector3.Normalize(bendDirection);
                var desiredMid = rootPosition +
                    (targetDirection * cosine + bendDirection * sine) * upperLength;
                var desiredEnd = rootPosition + targetDirection * solvedDistance;

                var solvedUpperDirection = Vector3.Normalize(desiredMid - rootPosition);
                if (chain.Root.EndsWith("upperarm", StringComparison.OrdinalIgnoreCase))
                {
                    // JointData types 5/6 derive the shoulder basis from the solved
                    // chain itself. The split lowerarmX channel is a pure axial
                    // rotation: its inverse is retained on the upper arm, while the
                    // channel itself remains on lowerarmX. This is the exact layout
                    // exposed by the game's JNT_WORK matrices.
                    var isLeft = chain.Root.StartsWith("L_", StringComparison.OrdinalIgnoreCase);
                    var xAxis = isLeft ? solvedUpperDirection : -solvedUpperDirection;
                    var yAxis = isLeft ? planeNormal : -planeNormal;
                    var solvedRotation = RotationFromAxes(
                        xAxis, yAxis, Vector3.Cross(xAxis, yAxis));
                    var type6LeftArm = isLeft && xzAxisTargets.Contains(chain.Root);
                    if (!type6LeftArm &&
                        rawTracks.TryGetValue(chain.Root, out var upperArmController) &&
                        upperArmController.Rotations is { Length: > 0 } upperArmRotations &&
                        upperArmController.RotTimes is { Length: > 0 } upperArmRotationTimes)
                    {
                        var upperArmRotation = NormalizeOrIdentity(SampleTrack(
                            upperArmRotations, upperArmRotationTimes, times[frame]));
                        var axialTwist = NormalizeOrIdentity(new Quaternion(
                            upperArmRotation.X, 0f, 0f, upperArmRotation.W));
                        var twistedBasis = Matrix4x4.CreateFromQuaternion(axialTwist) *
                                           Matrix4x4.CreateFromQuaternion(solvedRotation);
                        solvedRotation = NormalizeOrIdentity(Quaternion.CreateFromRotationMatrix(twistedBasis));
                    }
                    SetGlobalRotation(rootIndex, solvedRotation);
                }
                else
                {
                    // JointData types 3/4 use -Y for the thigh direction and the
                    // controller normal for the side axis.
                    var yAxis = -solvedUpperDirection;
                    var xAxis = -planeNormal;
                    xAxis -= yAxis * Vector3.Dot(xAxis, yAxis);
                    SetGlobalRotation(rootIndex, RotationFromAxes(xAxis, yAxis, Vector3.Cross(xAxis, yAxis)));
                }
                currentMid = globals[midIndex].Translation;
                currentEnd = globals[endIndex].Translation;
                var solvedLowerDirection = Vector3.Normalize(desiredEnd - currentMid);
                if (chain.Root.EndsWith("thigh", StringComparison.OrdinalIgnoreCase))
                {
                    var yAxis = -solvedLowerDirection;
                    var xAxis = -planeNormal;
                    xAxis -= yAxis * Vector3.Dot(xAxis, yAxis);
                    SetGlobalRotation(midIndex, RotationFromAxes(xAxis, yAxis, Vector3.Cross(xAxis, yAxis)));
                }
                else
                {
                    // The authored upper-arm controller supplies the base axial
                    // twist on lowerarmX. JointData then adds the remaining
                    // twist needed to bring the solved forearm direction into
                    // lowerarmY's X/Z bend plane. Dropping that local Y component
                    // works only for near-planar clips and leaves the left hand
                    // visibly short of its target in motions such as mot_3.
                    var lowerXBaseRotation = Quaternion.Identity;
                    if (rawTracks.TryGetValue(chain.Root, out var armController) &&
                        armController.Rotations is { Length: > 0 } controllerRotations &&
                        armController.RotTimes is { Length: > 0 } controllerRotationTimes)
                    {
                        lowerXBaseRotation = Quaternion.Inverse(NormalizeOrIdentity(
                            SampleTrack(controllerRotations, controllerRotationTimes, times[frame])));
                        SetLocalRotation(midIndex, lowerXBaseRotation);
                    }
                    var lowerYName = chain.Root.StartsWith("L_", StringComparison.OrdinalIgnoreCase)
                        ? "L_lowerarmY"
                        : "R_lowerarmY";
                    var aimIndex = boneIndexes.TryGetValue(lowerYName, out var lowerYIndex)
                        ? lowerYIndex
                        : midIndex;
                    if (aimIndex != midIndex &&
                        Matrix4x4.Decompose(globals[midIndex], out _, out var lowerXGlobalRotation, out _))
                    {
                        var localDirection = Vector3.Transform(solvedLowerDirection,
                            Quaternion.Inverse(NormalizeOrIdentity(lowerXGlobalRotation)));
                        if (localDirection.Y * localDirection.Y + localDirection.Z * localDirection.Z > 1e-10f)
                        {
                            // For System.Numerics' row-vector convention,
                            // inverse(Rx(a)) maps y to y*cos(a)+z*sin(a).
                            // Choose a so that the direction expressed below
                            // lowerarmX has y=0 and can be represented exactly
                            // by lowerarmY's single Y-axis rotation.
                            var axialCorrection = MathF.Atan2(-localDirection.Y, localDirection.Z);
                            lowerXBaseRotation = NormalizeOrIdentity(
                                Quaternion.CreateFromAxisAngle(Vector3.UnitX, axialCorrection) *
                                lowerXBaseRotation);
                            SetLocalRotation(midIndex, lowerXBaseRotation);
                            if (Matrix4x4.Decompose(globals[midIndex], out _, out lowerXGlobalRotation, out _))
                                localDirection = Vector3.Transform(solvedLowerDirection,
                                    Quaternion.Inverse(NormalizeOrIdentity(lowerXGlobalRotation)));
                        }
                        localDirection.Y = 0;
                        if (localDirection.LengthSquared() > 1e-10f)
                        {
                            localDirection = Vector3.Normalize(localDirection);
                            var bindDirection = chain.Root.StartsWith("L_", StringComparison.OrdinalIgnoreCase)
                                ? Vector3.UnitX
                                : -Vector3.UnitX;
                            var angle = MathF.Atan2(
                                Vector3.Dot(Vector3.UnitY, Vector3.Cross(bindDirection, localDirection)),
                                Math.Clamp(Vector3.Dot(bindDirection, localDirection), -1f, 1f));
                            SetLocalRotation(aimIndex,
                                Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle));
                        }
                    }
                    else
                    {
                        currentMid = globals[aimIndex].Translation;
                        currentEnd = globals[endIndex].Translation;
                        RotateGlobalDirection(aimIndex, currentEnd - currentMid, desiredEnd - currentMid);
                    }
                }

                if (requestedEndRotation is Quaternion endRotation)
                    SetGlobalRotation(endIndex, endRotation);
                if (requestedBallIndex is int solvedBallIndex &&
                    requestedBallRotation is Quaternion ballRotation)
                    SetGlobalRotation(solvedBallIndex, ballRotation);
            }

            void SetGlobalRotation(int index, Quaternion rotation)
            {
                Matrix4x4.Decompose(globals[index], out var scale, out _, out var translation);
                var adjustedGlobal = Matrix4x4.CreateScale(scale) *
                                     Matrix4x4.CreateFromQuaternion(NormalizeOrIdentity(rotation)) *
                                     Matrix4x4.CreateTranslation(translation);
                var parent = targetSkeleton.Bones[index].ParentIndex;
                locals[index] = adjustedGlobal;
                if (parent >= 0 && parent < globals.Length &&
                    Matrix4x4.Invert(globals[parent], out var inverseParent))
                    locals[index] *= inverseParent;
                RecomputeSubtree(index);
            }

            void SetLocalRotation(int index, Quaternion rotation)
            {
                Matrix4x4.Decompose(locals[index], out var scale, out _, out var translation);
                locals[index] = Matrix4x4.CreateScale(scale) *
                                Matrix4x4.CreateFromQuaternion(NormalizeOrIdentity(rotation)) *
                                Matrix4x4.CreateTranslation(translation);
                RecomputeSubtree(index);
            }

            void RotateGlobalDirection(int index, Vector3 from, Vector3 to)
            {
                if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f) return;
                var delta = RotationBetween(Vector3.Normalize(from), Vector3.Normalize(to));
                Matrix4x4.Decompose(globals[index], out var scale, out var rotation, out var translation);
                var adjustedGlobal = Matrix4x4.CreateScale(scale) *
                                     Matrix4x4.CreateFromQuaternion(NormalizeOrIdentity(delta * rotation)) *
                                     Matrix4x4.CreateTranslation(translation);
                var parent = targetSkeleton.Bones[index].ParentIndex;
                locals[index] = adjustedGlobal;
                if (parent >= 0 && parent < globals.Length &&
                    Matrix4x4.Invert(globals[parent], out var inverseParent))
                    locals[index] *= inverseParent;
                RecomputeSubtree(index);
            }

            static Quaternion RotationFromAxes(Vector3 xAxis, Vector3 yAxis, Vector3 zAxis)
            {
                if (xAxis.LengthSquared() < 1e-10f || yAxis.LengthSquared() < 1e-10f ||
                    zAxis.LengthSquared() < 1e-10f)
                    return Quaternion.Identity;
                xAxis = Vector3.Normalize(xAxis);
                zAxis = Vector3.Normalize(zAxis - xAxis * Vector3.Dot(zAxis, xAxis));
                yAxis = Vector3.Normalize(Vector3.Cross(zAxis, xAxis));
                var matrix = new Matrix4x4(
                    xAxis.X, xAxis.Y, xAxis.Z, 0,
                    yAxis.X, yAxis.Y, yAxis.Z, 0,
                    zAxis.X, zAxis.Y, zAxis.Z, 0,
                    0, 0, 0, 1);
                return NormalizeOrIdentity(Quaternion.CreateFromRotationMatrix(matrix));
            }

            void RecomputeSubtree(int index)
            {
                var parent = targetSkeleton.Bones[index].ParentIndex;
                globals[index] = parent >= 0 && parent < globals.Length
                    ? locals[index] * globals[parent]
                    : locals[index];
                for (var child = 0; child < targetSkeleton.Bones.Length; child++)
                    if (targetSkeleton.Bones[child].ParentIndex == index)
                        RecomputeSubtree(child);
            }
        }

        foreach (var (name, track) in targetTracks)
        {
            if (!baked.TryGetValue(name, out var data)) continue;
            track.TransTimes = times;
            track.Translations = data.Translations;
            track.RotTimes = times;
            track.Rotations = EnsureQuaternionContinuity(data.Rotations);
        }

        void ComputeBindGlobal(int index)
        {
            if (bindComputed[index]) return;
            var bone = targetSkeleton.Bones[index];
            var parent = bone.ParentIndex;
            if (parent >= 0 && parent < bindGlobals.Length)
            {
                ComputeBindGlobal(parent);
                bindGlobals[index] = bone.LocalBind * bindGlobals[parent];
            }
            else bindGlobals[index] = bone.LocalBind;
            bindComputed[index] = true;
        }
    }

    private static Vector3 SampleTrack(Vector3[] values, float[] times, float time)
    {
        if (values.Length == 1 || time <= times[0]) return values[0];
        if (time >= times[^1]) return values[^1];
        var next = Array.BinarySearch(times, time);
        if (next >= 0) return values[Math.Min(next, values.Length - 1)];
        next = ~next;
        var previous = next - 1;
        var amount = (time - times[previous]) / Math.Max(1e-6f, times[next] - times[previous]);
        return Vector3.Lerp(values[Math.Min(previous, values.Length - 1)],
            values[Math.Min(next, values.Length - 1)], amount);
    }

    private static Quaternion SampleTrack(Quaternion[] values, float[] times, float time)
    {
        if (values.Length == 1 || time <= times[0]) return values[0];
        if (time >= times[^1]) return values[^1];
        var next = Array.BinarySearch(times, time);
        if (next >= 0) return values[Math.Min(next, values.Length - 1)];
        next = ~next;
        var previous = next - 1;
        var amount = (time - times[previous]) / Math.Max(1e-6f, times[next] - times[previous]);
        return Quaternion.Slerp(values[Math.Min(previous, values.Length - 1)],
            values[Math.Min(next, values.Length - 1)], amount);
    }

    private static Quaternion[] EnsureQuaternionContinuity(Quaternion[] values)
    {
        var result = new Quaternion[values.Length];
        Quaternion? previous = null;
        for (var i = 0; i < values.Length; i++)
        {
            var current = NormalizeOrIdentity(values[i]);
            if (previous is Quaternion prior && Quaternion.Dot(prior, current) < 0)
                current = new Quaternion(-current.X, -current.Y, -current.Z, -current.W);
            result[i] = current;
            previous = current;
        }
        return result;
    }

    private static Quaternion NormalizeOrIdentity(Quaternion value)
        => value.LengthSquared() > 1e-12f ? Quaternion.Normalize(value) : Quaternion.Identity;

    private static Quaternion RotationBetween(Vector3 from, Vector3 to)
    {
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.999999f) return Quaternion.Identity;
        if (dot < -0.999999f)
        {
            var axis = Vector3.Cross(from, Vector3.UnitX);
            if (axis.LengthSquared() < 1e-8f) axis = Vector3.Cross(from, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var cross = Vector3.Cross(from, to);
        return NormalizeOrIdentity(new Quaternion(cross, 1f + dot));
    }

    private static void AddAnimationBones(ViewportMesh mesh, IReadOnlyList<MotBone> motionBones)
    {
        if (motionBones.Count == 0) return;

        var originalBoneCount = mesh.Bones.Length;
        var byName = mesh.Bones
            .Select((bone, index) => (bone, index))
            // Some Rise/Sunbreak NPC meshes legitimately repeat helper-bone names
            // (npc615_00 contains two wst_chain_end entries).  They are separate source
            // indices but either occurrence proves the named MOT helper already exists.
            .GroupBy(item => item.bone.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index,
                StringComparer.OrdinalIgnoreCase);
        var sourceByName = motionBones
            .Where(bone => !string.IsNullOrWhiteSpace(bone.boneName))
            .GroupBy(bone => bone.boneName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var adding = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int Ensure(string name)
        {
            if (byName.TryGetValue(name, out var existing)) return existing;
            if (!sourceByName.TryGetValue(name, out var source)) return -1;
            if (!adding.Add(name)) return -1;

            var parentName = source.Parent?.boneName;
            var parentIndex = !string.IsNullOrWhiteSpace(parentName)
                ? Ensure(parentName!)
                : -1;
            var local = Matrix4x4.CreateFromQuaternion(
                            Quaternion.Normalize(source.quaternion)) *
                        Matrix4x4.CreateTranslation(source.translation);
            var index = mesh.Bones.Length;
            Array.Resize(ref mesh.Bones, index + 1);
            mesh.Bones[index] = new ViewportBone
            {
                Name = source.boneName,
                ParentIndex = parentIndex,
                LocalBind = local,
                InverseGlobalBind = Matrix4x4.Identity,
            };
            byName[name] = index;
            adding.Remove(name);
            return index;
        }

        foreach (var source in motionBones)
            if (!string.IsNullOrWhiteSpace(source.boneName))
                Ensure(source.boneName);

        var globals = new Matrix4x4[mesh.Bones.Length];
        var computed = new bool[mesh.Bones.Length];
        for (var index = 0; index < mesh.Bones.Length; index++)
            Compute(index);

        // The mesh inverse-bind matrices are authoritative. Rebuilding them from
        // LocalBind changes the bind space for formats whose serialized local and
        // inverse-global transforms are not exact mathematical inverses (notably
        // Onimusha 2), which makes skinned vertices explode as soon as animation is
        // applied. Only appended MOT-only helper bones need a synthesized inverse.
        for (var index = originalBoneCount; index < mesh.Bones.Length; index++)
            if (Matrix4x4.Invert(globals[index], out var inverse))
                mesh.Bones[index].InverseGlobalBind = inverse;

        void Compute(int index)
        {
            if (computed[index]) return;
            var parent = mesh.Bones[index].ParentIndex;
            if (parent >= 0 && parent < mesh.Bones.Length && parent != index)
            {
                Compute(parent);
                globals[index] = mesh.Bones[index].LocalBind * globals[parent];
            }
            else
            {
                globals[index] = mesh.Bones[index].LocalBind;
            }
            computed[index] = true;
        }
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

    internal static Matrix4x4 ToMatrix(ReeLib.via.mat4 m) => new(
        m.m00, m.m01, m.m02, m.m03,
        m.m10, m.m11, m.m12, m.m13,
        m.m20, m.m21, m.m22, m.m23,
        m.m30, m.m31, m.m32, m.m33);

    private static int GetIndex(Submesh sub, int i)
        => sub.Buffer.IntegerFaces != null ? sub.IntegerIndices[i] : sub.Indices[i];

    /// <summary>
    /// Bit-exact face key: the three vertices' float bits (sorted) hashed together.
    /// Used to detect coplanar-duplicate faces across viscon groups (probe9).
    /// </summary>
    private static ulong FaceKey(Vector3 a, Vector3 b, Vector3 c)
    {
        var ka = VKey(a); var kb = VKey(b); var kc = VKey(c);
        // sort the three vertex keys lexicographically
        if (ka.CompareTo(kb) > 0) (ka, kb) = (kb, ka);
        if (kb.CompareTo(kc) > 0) (kb, kc) = (kc, kb);
        if (ka.CompareTo(kb) > 0) (ka, kb) = (kb, ka);
        var h = new HashCode();
        h.Add(ka); h.Add(kb); h.Add(kc);
        return (ulong)h.ToHashCode();

        static (uint, uint, uint) VKey(Vector3 v)
            => (BitConverter.SingleToUInt32Bits(v.X),
                BitConverter.SingleToUInt32Bits(v.Y),
                BitConverter.SingleToUInt32Bits(v.Z));
    }
}
