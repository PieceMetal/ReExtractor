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
            g.Submeshes.All(s => s.materialIndex < mesh.MaterialNames.Count &&
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
                .Where(s => s.indicesCount >= 3 && s.materialIndex >= 0)
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
                .Where(s => s.indicesCount >= 3 && s.materialIndex < mesh.MaterialNames.Count)
                .Select(s => mesh.MaterialNames[s.materialIndex])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new ViewportGroup
            {
                Key = g.groupId,
                Id = g.groupId,
                Name = $"组 {g.groupId}",
                Materials = materials,
                FaceCount = g.Submeshes.Sum(s => s.indicesCount / 3),
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
                var positions = sub.Positions;
                if (positions.Length == 0) continue;
                var vBase = verts.Count;
                var w = sub.Weights;
                var norTan = sub.NormalsTangents;
                var uv0 = sub.UV0;
                var hasWeights = w.Length >= positions.Length;
                var hasNormals = norTan.Length >= positions.Length;
                var hasUvs = uv0.Length >= positions.Length;

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
                    exportHidden = IsUnsupportedEyeOverlayMaterial(matName) &&
                        !materialTextures.ContainsKey(sub.materialIndex);
                    if (materialTextures.TryGetValue(sub.materialIndex, out var previewMaterial))
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
            var baseColor = mat.Parameters.FirstOrDefault(parameter =>
                parameter.paramName.Equals("BaseColor", StringComparison.OrdinalIgnoreCase))?.parameter ?? Vector4.One;
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

            using var texStream = fallbackTexture ? null : OpenNormalized(openResource, albedoPath!, meshPath);
            if (!fallbackTexture && texStream == null) continue;
            try
            {
                var isEyeMaterial = mat.Name.Equals("m_eye", StringComparison.OrdinalIgnoreCase) ||
                    mat.Name.Contains("eyeball", StringComparison.OrdinalIgnoreCase);
                var flags = mat.Header.Flags;
                var alphaCutout = (flags & (MaterialFlags.BaseAlphaTestEnable |
                    MaterialFlags.ForcedAlphaTestEnable | MaterialFlags.AlphaTestEnable)) != 0;
                // Some older assets expose the convention only through their material name.
                // Keep that as a compatibility fallback, but prefer the MDF's authoritative flags.
                alphaCutout |= mat.Name.Contains("ALP", StringComparison.OrdinalIgnoreCase) ||
                    IsAlphaCutoutMaterial(mat);

                var texture = fallbackTexture
                    ? CreateSolidTexture(baseColor, mat.Name)
                    : DecodeTexture(texStream!, albedoPath!, atlasQuadrant);
                ApplyBaseColor(texture, baseColor);
                ApplyCustomizeColorMasks(texture, mat, openResource, meshPath);
                var eyeColor = mat.Parameters.FirstOrDefault(parameter =>
                    parameter.paramName.Equals("Eye_ColorChange", StringComparison.OrdinalIgnoreCase))?.parameter;
                if (eyeColor.HasValue) ApplyEyeColorChange(texture, eyeColor.Value);
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
        BuildMipChain(texture);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static bool IsNullTexture(string path)
        => path.Contains("NullWhite", StringComparison.OrdinalIgnoreCase)
           || path.Contains("NullBlack", StringComparison.OrdinalIgnoreCase)
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
        BuildMipChain(texture);
    }

    private static void ForceOpaqueAlpha(ViewportTexture texture)
    {
        for (var i = 0; i < texture.Pixels.Length; i++)
            texture.Pixels[i] = texture.Pixels[i] | 0xFF000000u;
        BuildMipChain(texture);
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
        BuildMipChain(texture);
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
        BuildMipChain(texture);
    }
    private static bool IsAlphaCutoutMaterial(MaterialData mat)
    {
        if (mat.Name.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
            mat.Name.Contains("eyebrow", StringComparison.OrdinalIgnoreCase) ||
            mat.Name.Contains("eyelash", StringComparison.OrdinalIgnoreCase) ||
            mat.Name.Contains("eyeduct", StringComparison.OrdinalIgnoreCase) ||
            mat.Name.Contains("eyeshadow", StringComparison.OrdinalIgnoreCase) ||
            mat.Name.Contains("beard", StringComparison.OrdinalIgnoreCase))
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
               type.Equals("AlbedoMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseAlbedoMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseColor", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseMetalMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseShiftMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseAnisoShiftMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("BaseAlphaMap", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("ALBD", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] MdfNameSuffixCandidates = ["", "_v00"];

    private static readonly string[] MdfVersionCandidates =
        [".mdf2.51", ".mdf2.50", ".mdf2.40", ".mdf2.34", ".mdf2.32", ".mdf2.31",
         ".mdf2.23", ".mdf2.21", ".mdf2.19", ".mdf2.13", ".mdf2.10", ".mdf2.6"];
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
        var raw = texPath.Replace('\\', '/').TrimStart('/');
        if (raw.StartsWith("natives/", StringComparison.OrdinalIgnoreCase))
            return ResolveVersionedPath(open, raw);

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
            return exact == null ? null : p;
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
            }
        }
        foreach (var ver in TexVersionCandidates)
        {
            var candidate = p + ver;
            using var stream = open(candidate);
            if (stream != null) return candidate;
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
        BuildMipChain(texture);
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
        BuildMipChain(tex);
        return tex;
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
           || name.Contains("VFXEmitter", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnsupportedEyeOverlayMaterial(string name)
        => name.Contains("cornea", StringComparison.OrdinalIgnoreCase)
           || name.Contains("eyeouter", StringComparison.OrdinalIgnoreCase)
           || name.Contains("eyewet", StringComparison.OrdinalIgnoreCase)
           || name.Contains("eye_shadow", StringComparison.OrdinalIgnoreCase)
           || name.Contains("human_tear", StringComparison.OrdinalIgnoreCase);

    private static float GetGroupsBoundsDiagonal(IEnumerable<MeshGroup> groups)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var found = false;
        foreach (var position in groups.SelectMany(g => g.Submeshes).SelectMany(s => s.Positions.ToArray()))
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
            .Where(s => s.Positions.Length > 0)
            .GroupBy(s => s.materialIndex)
            .ToArray();
        if (candidateParts.Length == 0) return false;

        foreach (var part in candidateParts)
        {
            var candidateVertices = part.SelectMany(s => s.Positions.ToArray()).ToArray();
            var duplicate = kept.Any(previous =>
            {
                var previousVertices = previous.Submeshes
                    .Where(s => s.materialIndex == part.Key)
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

        // Noesis adds bones that exist in the MOT header but are absent from
        // an individual mesh part before it builds the keyframe animation.
        // MH Wilds stores leg/hand IK and helper bones this way. Keep the
        // original weighted bone indices intact and append only the missing
        // animation bones.
        if (sceneMeshes is { Count: > 0 })
        {
            foreach (var sceneMesh in sceneMeshes)
                AddAnimationBones(sceneMesh, mot.Bones);

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

        return new AnimationClip
        {
            Name = $"mot_{motion.motNumber}", Duration = duration,
            FrameRate = sourceFrameRate, FrameCount = sourceFrameCount,
            Tracks = tracks, NamedTracks = namedTracks,
        };
    }

    private static void AddAnimationBones(ViewportMesh mesh, IReadOnlyList<MotBone> motionBones)
    {
        if (motionBones.Count == 0) return;

        var byName = mesh.Bones
            .Select((bone, index) => (bone, index))
            .ToDictionary(item => item.bone.Name, item => item.index,
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

        for (var index = 0; index < mesh.Bones.Length; index++)
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
