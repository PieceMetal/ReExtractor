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
    public required ViewportTexture[] Textures;
    public required (int Joint, float Weight)[][] Weights; // per-vertex; joints index into DeformBones
    public required ViewportBone[] Bones;            // full bone list
    public required int[] DeformToBone;              // deform joint -> bone index
    public int VertexCount => Vertices.Length;
    public int FaceCount => Faces.Length;
    /// <summary>viscon group filter summary, e.g. "viscon 7/11（隐藏 9,10,131,250）".</summary>
    public string VisconInfo = "";
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
    public required Dictionary<int, BoneTrack> Tracks; // bone index -> track
}

public sealed class BoneTrack
{
    public float[]? TransTimes;
    public Vector3[]? Translations;
    public float[]? RotTimes;
    public Quaternion[]? Rotations;
}

/// <summary>Loads ViewportMesh / AnimationClip from RE files (parse only, no rendering deps).</summary>
public static class ViewportDataLoader
{
    public static ViewportMesh LoadMesh(Stream meshStream, string nativePath, int lodIndex = 0, Func<string, Stream?>? openResource = null)
    {
        using var mesh = MeshService.LoadMesh(meshStream, nativePath);
        var meshData = mesh.MeshData ?? throw new InvalidDataException("MeshData missing");
        var lod = meshData.LODs[Math.Min(lodIndex, meshData.LODs.Count - 1)];

        // resolve material textures via the model's .mdf2 (best effort)
        var materialTextures = openResource == null
            ? new Dictionary<int, ViewportTexture>()
            : ResolveMaterialTextures(mesh, nativePath, openResource);

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var faces = new List<(int, int, int)>();
        var faceTex = new List<int>();
        var weights = new List<(int, float)[]>();
        var textureList = new List<ViewportTexture>();
        var textureIndex = new Dictionary<string, int>(); // material name -> texture slot

        // --- viscon alternate-state filter (bit-exact duplicate detection, probe9-verified) ---
        // The mesh ships alternate-state shells (e.g. jacket variants 9/10) that are
        // bit-exact coplanar duplicates of each other; rendering both z-fights. Noesis
        // imports ALL groups and lets the user toggle — for a single-view preview we keep
        // the first group of each duplicate set and drop a group only when >50% of its
        // faces are bit-exact duplicates of already-kept groups. Everything else is kept
        // (group 250 = the body, 130/131 = footwear — all distinct geometry).
        var allGroups = lod.MeshGroups.OrderBy(g => g.groupId).ToList();
        var keptFaceKeys = new HashSet<ulong>();
        var keptGroups = new List<MeshGroup>();
        var droppedGroups = new List<int>();
        foreach (var g in allGroups)
        {
            var keys = new List<ulong>();
            foreach (var sub in g.Submeshes)
            {
                var pos = sub.Positions;
                for (var i = 0; i + 2 < sub.indicesCount; i += 3)
                {
                    int i0 = GetIndex(sub, i), i1 = GetIndex(sub, i + 1), i2 = GetIndex(sub, i + 2);
                    if ((uint)i0 >= (uint)pos.Length || (uint)i1 >= (uint)pos.Length || (uint)i2 >= (uint)pos.Length) continue;
                    keys.Add(FaceKey(pos[i0], pos[i1], pos[i2]));
                }
            }
            var dupCount = keys.Count(k => keptFaceKeys.Contains(k));
            if (keptGroups.Count > 0 && keys.Count > 0 && dupCount * 2 > keys.Count)
            {
                droppedGroups.Add(g.groupId); // >50% bit-exact duplicate of already-kept groups
                continue;
            }
            keptGroups.Add(g);
            foreach (var k in keys) keptFaceKeys.Add(k);
        }
        var droppedDesc = string.Join(",", droppedGroups);
        var visconInfo = droppedDesc.Length > 0
            ? $"viscon {keptGroups.Count}/{allGroups.Count}（隐藏组 {droppedDesc}）"
            : $"viscon {keptGroups.Count}/{allGroups.Count}";

        foreach (var group in keptGroups)
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
                if (sub.materialIndex < mesh.MaterialNames.Count)
                {
                    var matName = mesh.MaterialNames[sub.materialIndex];
                    if (materialTextures.TryGetValue(sub.materialIndex, out var vt))
                    {
                        if (!textureIndex.TryGetValue(matName, out texSlot))
                        {
                            texSlot = textureList.Count;
                            textureList.Add(vt);
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
    private static Dictionary<int, ViewportTexture> ResolveMaterialTextures(
        MeshFile mesh, string meshPath, Func<string, Stream?> openResource)
    {
        var result = new Dictionary<int, ViewportTexture>();

        // mesh path: .../ch001_00_00.mesh.251215606 -> .../ch001_00_00.mdf2.50
        var dotMesh = meshPath.IndexOf(".mesh.", StringComparison.OrdinalIgnoreCase);
        if (dotMesh < 0) return result;
        var mdfPath = meshPath[..dotMesh] + ".mdf2.50";

        using var mdfStream = openResource(mdfPath);
        if (mdfStream == null) return result;

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

            // albedo slot priority: BaseDielectricMap > texType*ALBD > path*_albd
            var albedo = mat.Textures.FirstOrDefault(t => t.texType.Equals("BaseDielectricMap", StringComparison.OrdinalIgnoreCase))
                ?? mat.Textures.FirstOrDefault(t => t.texType.Contains("ALBD", StringComparison.OrdinalIgnoreCase))
                ?? mat.Textures.FirstOrDefault(t => t.texPath.Contains("_albd", StringComparison.OrdinalIgnoreCase));
            if (albedo == null || string.IsNullOrEmpty(albedo.texPath)) continue;

            using var texStream = OpenNormalized(openResource, albedo.texPath);
            if (texStream == null) continue;
            try
            {
                result[meshMatIndex] = DecodeTexture(texStream, albedo.texPath);
            }
            catch { /* skip undecodable textures */ }
        }
        return result;
    }

    /// <summary>Known .tex version suffixes, most recent RE games first (OWOTS/Pragmata era).</summary>
    private static readonly string[] TexVersionCandidates =
        [".251111100", ".241106027", ".250813143", ".240701001", ".240606151", ".760230703", ".143221013", ".35", ".34", ".30", ".28", ".190820018"];

    /// <summary>
    /// mdf texPath is relative ("Art/Model/.../x_ALBD.tex") and lacks natives prefix + version suffix.
    /// PAK hash is case-insensitive, so only prefix/suffix need fixing.
    /// Streaming variants (natives/stm/streaming/...) hold the full-res textures; the plain path
    /// only has 256px stubs whose BC grain renders as speckle at preview zoom. fmt_RE_MESH
    /// resolves streaming first — do the same.
    /// </summary>
    private static Stream? OpenNormalized(Func<string, Stream?> open, string texPath)
    {
        var p = texPath.Replace('\\', '/').TrimStart('/');
        if (!p.StartsWith("natives/", StringComparison.OrdinalIgnoreCase))
            p = "natives/stm/" + p;

        // already has numeric version suffix?
        var lastDot = p.LastIndexOf('.');
        if (lastDot > 0 && p[(lastDot + 1)..].All(char.IsDigit))
            return open(p);

        // streaming first (full-res), then the plain stub path
        var streaming = p.Replace("natives/stm/", "natives/stm/streaming/");
        foreach (var ver in TexVersionCandidates)
        {
            var s = open(streaming + ver);
            if (s != null) return s;
        }
        foreach (var ver in TexVersionCandidates)
        {
            var s = open(p + ver);
            if (s != null) return s;
        }
        return null;
    }

    private const int MaxTextureSize = 2048;

    private static ViewportTexture DecodeTexture(Stream texStream, string texPath)
    {
        using var img = new TexService().DecodeToImage(texStream, texPath);
        var w = img.Width;
        var h = img.Height;

        // DEBUG: dump first texture to disk so we can verify GUI-side decoding
        var debugDir = @"D:\texdump\gui_debug";
        if (!Directory.Exists(debugDir))
            Directory.CreateDirectory(debugDir);
        var safeName = Path.GetFileName(texPath).Replace('.', '_');
        var debugPath = Path.Combine(debugDir, $"{safeName}.png");
        if (!File.Exists(debugPath))
        {
            try { img.SaveAsPng(debugPath); } catch { /* ignore */ }
            // Also dump raw pixel stats
            var sample = new Rgba32[w * h];
            img.CopyPixelDataTo(sample);
            var sum = 0L;
            for (var i = 0; i < Math.Min(100, sample.Length); i++)
                sum += sample[i].R + sample[i].G + sample[i].B;
            Console.WriteLine($"[TEX-DEBUG] {texPath} -> {debugPath}  size={w}x{h}  first100avg={sum / 100f / 3f:F1}  px[0]=({sample[0].R},{sample[0].G},{sample[0].B},{sample[0].A})");
        }

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
                // framebuffer is BGRA: uint = A<<24 | R<<16 | G<<8 | B
                pixels[y * w + x] = 0xFF000000u | ((uint)p.R << 16) | ((uint)p.G << 8) | p.B;
                }
            }
        });
        var tex = new ViewportTexture { Width = w, Height = h, Pixels = pixels, Name = Path.GetFileName(texPath) };
        BuildMipChain(tex);
        Console.WriteLine($"[TEX-DECODED] {texPath} -> {w}x{h}  px[0]=0x{pixels[0]:X8}  px[last]=0x{pixels[^1]:X8}");
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
                uint sb = 0, sg = 0, sr = 0, cnt = 0;
                for (var dy = 0; dy < 2; dy++)
                for (var dx = 0; dx < 2; dx++)
                {
                    var sx = Math.Min(cw - 1, x * 2 + dx);
                    var sy = Math.Min(ch - 1, y * 2 + dy);
                    var p = cur[sy * cw + sx];
                    sb += p & 0xFF; sg += (p >> 8) & 0xFF; sr += (p >> 16) & 0xFF; cnt++;
                }
                next[y * nw + x] = 0xFF000000u | ((sr / cnt) << 16) | ((sg / cnt) << 8) | (sb / cnt);
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

    /// <summary>Lists all motions in a .motlist with readable labels (file base name + mot id).</summary>
    public static IReadOnlyList<string> ListMotionNames(Stream motlistStream, string motlistPath)
    {
        using var motlist = new MotlistFile(new FileHandler(motlistStream, motlistPath));
        if (!motlist.Read())
            throw new InvalidDataException($"Failed to parse .motlist: {motlistPath}");
        var fileName = Path.GetFileName(motlistPath);
        var cut = fileName.IndexOf(".motlist", StringComparison.OrdinalIgnoreCase);
        var baseName = cut > 0 ? fileName[..cut] : fileName;
        return motlist.Motions.Select((m, i) => $"{baseName} #{i} (ID {m.motNumber})").ToList();
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
    public static AnimationClip LoadAnimation(Stream motlistStream, string motlistPath, int motionIndex = 0, IReadOnlyList<string>? meshBoneNames = null)
    {
        using var motlist = new MotlistFile(new FileHandler(motlistStream, motlistPath));
        if (!motlist.Read())
            throw new InvalidDataException($"Failed to parse .motlist: {motlistPath}");

        var motion = motlist.Motions.ElementAtOrDefault(motionIndex)
            ?? throw new InvalidDataException($"Motion index {motionIndex} out of range");
        if (motion.MotFile is not MotFile mot)
            throw new NotSupportedException("Motion has no embedded .mot data");

        // map bone-name hash -> mesh bone index (case-sensitive MurMur3, as RE uses)
        Dictionary<uint, int>? hashToBone = null;
        if (meshBoneNames != null)
        {
            hashToBone = new Dictionary<uint, int>(meshBoneNames.Count);
            for (var i = 0; i < meshBoneNames.Count; i++)
                hashToBone.TryAdd(MurMur3HashUtils.GetHash(meshBoneNames[i]), i);
        }

        var tracks = new Dictionary<int, BoneTrack>();
        var duration = 0f;
        foreach (var clip in mot.BoneClips)
        {
            int boneIndex = clip.ClipHeader.boneIndex;
            if (hashToBone != null)
            {
                if (!hashToBone.TryGetValue(clip.ClipHeader.boneHash, out boneIndex))
                    continue; // helper/twist bone not present in the mesh skeleton -> skip track
            }
            var track = new BoneTrack();

            if (clip.HasTranslation && clip.Translation!.translations is { Length: > 0 } tr)
            {
                var fps = clip.Translation.frameRate > 0 ? clip.Translation.frameRate : 30u;
                var frames = clip.Translation.frameIndexes;
                track.TransTimes = BuildTimes(frames, tr.Length, fps);
                track.Translations = tr;
                duration = Math.Max(duration, track.TransTimes[^1]);
            }
            if (clip.HasRotation && clip.Rotation!.rotations is { Length: > 0 } ro)
            {
                var fps = clip.Rotation.frameRate > 0 ? clip.Rotation.frameRate : 30u;
                var frames = clip.Rotation.frameIndexes;
                track.RotTimes = BuildTimes(frames, ro.Length, fps);
                track.Rotations = ro.Select(q => q.W < 0 ? new Quaternion(-q.X, -q.Y, -q.Z, -q.W) : q)
                                    .Select(Quaternion.Normalize).ToArray();
                duration = Math.Max(duration, track.RotTimes[^1]);
            }

            tracks[boneIndex] = track;
        }

        return new AnimationClip { Name = $"mot_{motion.motNumber}", Duration = duration, Tracks = tracks };
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
