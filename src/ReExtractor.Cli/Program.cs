using System.Numerics;
using ReExtractor.Core;

// ReExtractor.Cli — milestone 0 harness
// Usage:
//   ReExtractor.Cli --game-dir <dir> --list <listfile> stats
//   ReExtractor.Cli --game-dir <dir> --list <listfile> find <substr>
//   ReExtractor.Cli --game-dir <dir> --list <listfile> extract <nativePath> --out <dir>

var argsList = args.ToList();
string? GetOpt(string name)
{
    var i = argsList.IndexOf(name);
    return i >= 0 && i + 1 < argsList.Count ? argsList[i + 1] : null;
}
var gameDir = GetOpt("--game-dir");
var folderDir = GetOpt("--folder");
var listFile = GetOpt("--list");
var command = argsList.FirstOrDefault(a => !a.StartsWith("--") && argsList.IndexOf(a) != 0
    || !a.StartsWith("--") && (argsList.IndexOf(a) == 0));

// simpler: command = first non-option arg not consumed as option value
var consumed = new HashSet<int>();
for (var i = 0; i < argsList.Count; i++)
{
    if (argsList[i].StartsWith("--")) { consumed.Add(i); consumed.Add(i + 1); }
}
var positional = argsList.Where((a, i) => !consumed.Contains(i)).ToList();
command = positional.ElementAtOrDefault(0);

if ((gameDir == null && folderDir == null) || command == null)
{
    Console.WriteLine("Usage: ReExtractor.Cli (--game-dir <dir> --list <listfile> | --folder <dir>) <stats|find <substr>|extract <path> --out <dir>>");
    return 1;
}

var pak = new PakService();
if (folderDir != null)
{
    pak.AddFolder(folderDir);
    Console.WriteLine($"[init] folder: {folderDir}, files: {pak.EnumerateFiles().Count}");
}
else
{
    var pakCount = pak.AddPaksFromGameDir(gameDir!);
    var listCount = pak.LoadListFile(listFile!);
    Console.WriteLine($"[init] PAKs: {pakCount}, list paths: {listCount}");
}

switch (command)
{
    case "stats":
    {
        var files = pak.EnumerateFiles();
        Console.WriteLine($"[stats] resolved entries: {files.Count}");
        var byExt = files.GroupBy(f => Path.GetExtension(f.Path)).OrderByDescending(g => g.Count());
        foreach (var g in byExt.Take(15))
            Console.WriteLine($"  {g.Key,-12} {g.Count()}");
        return 0;
    }
    case "find":
    {
        var substr = positional.ElementAtOrDefault(1);
        if (substr == null) { Console.WriteLine("find requires a substring"); return 1; }
        var files = pak.EnumerateFiles();
        var hits = files.Where(f => f.Path.Contains(substr, StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"[find] '{substr}': {hits.Count} hits");
        foreach (var h in hits.Take(50))
            Console.WriteLine($"  {h.Path}  ({h.DecompressedSize:N0} B, {h.SourcePak})");
        if (hits.Count > 50) Console.WriteLine($"  ... and {hits.Count - 50} more");
        return 0;
    }
    case "extract":
    {
        var nativePath = positional.ElementAtOrDefault(1);
        var outDir = GetOpt("--out") ?? "output";
        if (nativePath == null) { Console.WriteLine("extract requires a native path"); return 1; }
        var outPath = pak.ExtractFile(nativePath, outDir);
        Console.WriteLine($"[extract] -> {outPath}");
        return 0;
    }
    case "tex2png":
    {
        var nativePath = positional.ElementAtOrDefault(1);
        var outDir = GetOpt("--out") ?? "output";
        if (nativePath == null) { Console.WriteLine("tex2png requires a native path"); return 1; }
        using var ms = pak.ReadFile(nativePath);
        var pngName = Path.GetFileNameWithoutExtension(nativePath) + ".png";
        var outPath = Path.Combine(outDir, pngName);
        var tex = new TexService();
        tex.ConvertToPng(ms, nativePath, outPath);
        Console.WriteLine($"[tex2png] -> {outPath}");
        return 0;
    }
    case "texinfo":
    {
        var nativePath = positional.ElementAtOrDefault(1);
        if (nativePath == null) { Console.WriteLine("texinfo requires a native path"); return 1; }
        using var ms = pak.ReadFile(nativePath);
        var texFile = new ReeLib.TexFile(new ReeLib.FileHandler(ms, nativePath));
        if (!texFile.Read()) { Console.WriteLine("[texinfo] read failed"); return 1; }
        var h = texFile.Header;
        Console.WriteLine($"[texinfo] {h.width}x{h.height} format={h.format} ({(int)h.format}) mips={h.mipCount} images={h.imageCount} swizzleControl={h.swizzleControl} swizzleWidth={h.swizzleWidth} swizzleHeightDepth={h.swizzleHeightDepth}");
        var service = new TexService();
        var (_, diag) = service.DecodeToImageDiag(ms, nativePath);
        Console.WriteLine($"[texinfo] branch={diag.Branch} stats={diag.PixelStats} notes={diag.Notes}");
        return 0;
    }
    case "mesh2glb":
    {
        var nativePath = positional.ElementAtOrDefault(1);
        var outDir = GetOpt("--out") ?? "output";
        if (nativePath == null) { Console.WriteLine("mesh2glb requires a native path"); return 1; }
        using var ms = pak.ReadFile(nativePath);
        var glbName = Path.GetFileNameWithoutExtension(nativePath) + ".glb";
        var outPath = Path.Combine(outDir, glbName);
        var mesh = new MeshService();
        mesh.ConvertToGlb(ms, nativePath, outPath);
        Console.WriteLine($"[mesh2glb] -> {outPath}");
        return 0;
    }
    case "meshinfo":
    {
        var nativePath = positional.ElementAtOrDefault(1);
        if (nativePath == null) { Console.WriteLine("meshinfo requires a native path"); return 1; }
        using var ms = pak.ReadFile(nativePath);
        var viewportMesh = ViewportDataLoader.LoadMesh(ms, nativePath, 0,
            path => pak.ReadFile(path), loadTextures: false);
        Console.WriteLine($"[meshinfo] viewport V={viewportMesh.VertexCount} T={viewportMesh.FaceCount} Bones={viewportMesh.Bones.Length} Viscon={viewportMesh.VisconInfo}");
        return 0;
        /*
        using var mesh = new ReeLib.MeshFile(new ReeLib.FileHandler(ms, nativePath));
        mesh.Read();
        Console.WriteLine($"[meshinfo] RequiresStreamingData={mesh.RequiresStreamingData} BufferCount={mesh.Header.BufferCount} LODs={mesh.MeshData?.LODs.Count ?? 0} V={mesh.TotalVertexCount} T={mesh.TotalTriangleCount} Bones={mesh.BoneData?.Bones.Count ?? 0}");
        var lod = mesh.MeshData!.LODs[0];
        var gi = 0;
        foreach (var group in lod.MeshGroups)
        {
            foreach (var sub in group.Submeshes)
            {
                var sampleIdx = new List<int>();
                for (var k = 0; k < Math.Min(6, sub.indicesCount); k++)
                    sampleIdx.Add(sub.Buffer.IntegerFaces != null ? sub.IntegerIndices[k] : sub.Indices[k]);
                Console.WriteLine($"  g{group.groupId} sub#{gi}: vOff={sub.vertsIndexOffset} vCnt={sub.vertCount} iOff={sub.facesIndexOffset} iCnt={sub.indicesCount} mat={sub.materialIndex} idx[0..5]={string.Join(",", sampleIdx)}");
                gi++;
            }
        }
        return 0;
        */
    }
    case "anim2glb":
    {
        var meshPath = positional.ElementAtOrDefault(1);
        var motlistPath = positional.ElementAtOrDefault(2);
        var outDir = GetOpt("--out") ?? "output";
        var motionIndex = GetOpt("--mot") is { } motStr && int.TryParse(motStr, out var mi) ? mi : 0;
        if (meshPath == null || motlistPath == null) { Console.WriteLine("anim2glb requires <mesh> <motlist>"); return 1; }
        using var meshMs = pak.ReadFile(meshPath);
        using var motMs = pak.ReadFile(motlistPath);
        var glbName = Path.GetFileNameWithoutExtension(motlistPath) + $".mot{motionIndex}.glb";
        var outPath = Path.Combine(outDir, glbName);
        var anim = new AnimationService();
        anim.ConvertToGlbWithAnimation(meshMs, meshPath, motMs, motlistPath, outPath, motionIndex);
        Console.WriteLine($"[anim2glb] -> {outPath}");
        return 0;
    }
    case "motinfo":
    {
        var motlistPath = positional.ElementAtOrDefault(1);
        if (motlistPath == null) { Console.WriteLine("motinfo requires a motlist path"); return 1; }
        using var motMs = pak.ReadFile(motlistPath);
        using var motlist = new ReeLib.MotlistFile(new ReeLib.FileHandler(motMs, motlistPath));
        motlist.Read();
        Console.WriteLine($"[motinfo] motions={motlist.Motions.Count} motfiles={motlist.MotFiles.Count}");
        var motion = motlist.Motions.FirstOrDefault();
        if (motion?.MotFile is ReeLib.MotFile mot)
        {
            Console.WriteLine($"[motinfo] first motion motNumber={motion.motNumber} boneClips={mot.BoneClips.Count} bones={mot.Bones.Count}");
            foreach (var clip in mot.BoneClips.Take(5))
            {
                var h = clip.ClipHeader;
                Console.WriteLine($"  clip bone={h.boneName ?? "?"} idx={h.boneIndex} hash={h.boneHash:X8} flags={h.trackFlags}");
                foreach (var (label, tr) in new[] { ("T", clip.Translation), ("R", clip.Rotation), ("S", clip.Scale) })
                {
                    if (tr == null) continue;
                    Console.WriteLine($"    {label}: keyCount={tr.keyCount} frameRate={tr.frameRate} maxFrame={tr.maxFrame} frameIdxLen={tr.frameIndexes?.Length ?? -1} values={tr.rotations?.Length ?? tr.translations?.Length ?? tr.floats?.Length ?? -1}");
                }
            }
        }
        return 0;
    }
    case "motinspect":
    {
        var motlistPath = positional.ElementAtOrDefault(1);
        var motionIndexText = positional.ElementAtOrDefault(2);
        if (motlistPath == null || !int.TryParse(motionIndexText, out var motionIndex))
        {
            Console.WriteLine("motinspect requires <motlist path> <motion index>");
            return 1;
        }

        using var motMs = pak.ReadFile(motlistPath);
        using var motlist = new ReeLib.MotlistFile(new ReeLib.FileHandler(motMs, motlistPath));
        motlist.Read();
        if (motionIndex < 0 || motionIndex >= motlist.Motions.Count)
        {
            Console.WriteLine($"[motinspect] index {motionIndex} out of range ({motlist.Motions.Count})");
            return 1;
        }

        var motion = motlist.Motions[motionIndex];
        Console.WriteLine($"[motinspect] index={motionIndex} id={motion.motNumber} type={motion.MotFile?.GetType().Name}");
        if (motion.MotFile is not ReeLib.MotFile mot)
            return 0;

        Console.WriteLine($"[motinspect] name={mot.Header.motName} frame={mot.Header.frameCount} fps={mot.Header.FrameRate} boneClips={mot.BoneClips.Count}");
        foreach (var clip in mot.BoneClips)
        {
            var h = clip.ClipHeader;
            var t = clip.Translation;
            var r = clip.Rotation;
            var s = clip.Scale;
            Console.WriteLine(
                $"  bone={h.boneName ?? "?"} idx={h.boneIndex} hash={h.boneHash:X8} flags={h.trackFlags} " +
                $"T={t?.keyCount ?? 0} R={r?.keyCount ?? 0} S={s?.keyCount ?? 0}");
        }
        return 0;
    }
    case "motmatch":
    {
        var meshPath = positional.ElementAtOrDefault(1);
        var motlistPath = positional.ElementAtOrDefault(2);
        var motionIndexText = positional.ElementAtOrDefault(3);
        if (meshPath == null || motlistPath == null ||
            !int.TryParse(motionIndexText, out var motionIndex))
        {
            Console.WriteLine("motmatch requires <mesh path> <motlist path> <motion index>");
            return 1;
        }

        using var meshStream = pak.ReadFile(meshPath);
        var viewportMesh = ViewportDataLoader.LoadMesh(
            meshStream, meshPath, 0, path =>
            {
                try { return pak.ReadFile(path); }
                catch { return null; }
            }, loadTextures: false);
        using var motStream = pak.ReadFile(motlistPath);
        var clip = ViewportDataLoader.LoadAnimation(
            motStream, motlistPath, motionIndex, viewportMesh.Bones.Select(b => b.Name).ToArray());

        var names = new HashSet<string>(
            viewportMesh.Bones.Select(b => b.Name),
            StringComparer.OrdinalIgnoreCase);
        var handNames = clip.NamedTracks.Keys
            .Where(name => name.Contains("Hand", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Finger", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Thumb", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("IndexF", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("MiddleF", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("RingF", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("PinkyF", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Console.WriteLine($"[motmatch] meshBones={viewportMesh.Bones.Length} clipTracks={clip.NamedTracks.Count} handTracks={handNames.Length}");
        foreach (var name in handNames)
        {
            var track = clip.NamedTracks[name];
            Console.WriteLine($"  {name} T={track.Translations?.Length ?? 0} R={track.Rotations?.Length ?? 0}");
        }
        return 0;
    }
    case "mottrackdiag":
    {
        var motlistPath = positional.ElementAtOrDefault(1);
        var motionIndexText = positional.ElementAtOrDefault(2);
        var boneName = positional.ElementAtOrDefault(3);
        if (motlistPath == null || !int.TryParse(motionIndexText, out var motionIndex) ||
            string.IsNullOrWhiteSpace(boneName))
        {
            Console.WriteLine("mottrackdiag requires <motlist path> <motion index> <bone name>");
            return 1;
        }

        using var motMs = pak.ReadFile(motlistPath);
        using var motlist = new ReeLib.MotlistFile(new ReeLib.FileHandler(motMs, motlistPath));
        motlist.Read();
        if (motionIndex < 0 || motionIndex >= motlist.Motions.Count ||
            motlist.Motions[motionIndex].MotFile is not ReeLib.MotFile mot)
        {
            Console.WriteLine("motion index is invalid or has no embedded mot");
            return 1;
        }

        var clip = mot.BoneClips.FirstOrDefault(item =>
            string.Equals(item.ClipHeader.boneName, boneName, StringComparison.OrdinalIgnoreCase));
        if (clip == null)
        {
            var hash = ReeLib.Common.MurMur3HashUtils.GetHash(boneName);
            clip = mot.BoneClips.FirstOrDefault(item => item.ClipHeader.boneHash == hash);
        }
        if (clip == null)
        {
            Console.WriteLine($"track not found: {boneName}");
            return 1;
        }

        Console.WriteLine($"[mottrackdiag] motion={motionIndex} id={motlist.Motions[motionIndex].motNumber} bone={clip.ClipHeader.boneName ?? boneName}");
        if (clip.Rotation?.rotations is { Length: > 0 } rotations)
        {
            var frames = clip.Rotation.frameIndexes;
            var fps = clip.Rotation.frameRate > 0 ? clip.Rotation.frameRate : 60u;
            Quaternion? previous = null;
            for (var i = 0; i < rotations.Length; i++)
            {
                var q = Quaternion.Normalize(rotations[i]);
                var dot = previous.HasValue ? Quaternion.Dot(previous.Value, q) : 1;
                var frame = frames != null && i < frames.Length ? frames[i] : i;
                Console.WriteLine($"  R[{i}] frame={frame} t={frame / (float)fps:F4} q=({q.X:F5},{q.Y:F5},{q.Z:F5},{q.W:F5}) dotPrev={dot:F5} compression=0x{clip.Rotation.Compression:X}");
                previous = q;
            }
        }
        if (clip.Translation?.translations is { Length: > 0 } translations)
        {
            var frames = clip.Translation.frameIndexes;
            var fps = clip.Translation.frameRate > 0 ? clip.Translation.frameRate : 60u;
            for (var i = 0; i < translations.Length; i++)
            {
                var frame = frames != null && i < frames.Length ? frames[i] : i;
                var v = translations[i];
                Console.WriteLine($"  T[{i}] frame={frame} t={frame / (float)fps:F4} v=({v.X:F5},{v.Y:F5},{v.Z:F5}) compression=0x{clip.Translation.Compression:X}");
            }
        }
        return 0;
    }
    case "motcandidates":
    {
        var motlistPath = positional.ElementAtOrDefault(1);
        if (motlistPath == null) { Console.WriteLine("motcandidates requires a motlist path"); return 1; }
        using var motMs = pak.ReadFile(motlistPath);
        using var motlist = new ReeLib.MotlistFile(new ReeLib.FileHandler(motMs, motlistPath));
        motlist.Read();

        static float RotationSpread(Quaternion[]? values)
        {
            if (values is not { Length: > 1 }) return 0;
            var first = Quaternion.Normalize(values[0]);
            var max = 0f;
            foreach (var value in values)
            {
                var q = Quaternion.Normalize(value);
                var dot = Math.Clamp(MathF.Abs(Quaternion.Dot(first, q)), 0, 1);
                max = Math.Max(max, 2 * MathF.Acos(dot) * 180 / MathF.PI);
            }
            return max;
        }

        static float TranslationSpread(Vector3[]? values)
        {
            if (values is not { Length: > 1 }) return 0;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var value in values)
            {
                min = Vector3.Min(min, value);
                max = Vector3.Max(max, value);
            }
            return (max - min).Length();
        }

        var rows = new List<(int Index, int Id, float Score, float COG, float Legs, float Hands, float Frame, int Clips)>();
        for (var index = 0; index < motlist.Motions.Count; index++)
        {
            if (motlist.Motions[index].MotFile is not ReeLib.MotFile mot) continue;
            var byName = mot.BoneClips
                .Where(clip => !string.IsNullOrWhiteSpace(clip.ClipHeader.boneName))
                .GroupBy(clip => clip.ClipHeader.boneName!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            float GetRot(string name) => byName.TryGetValue(name, out var clip)
                ? RotationSpread(clip.Rotation?.rotations)
                : 0;
            float GetTrans(string name) => byName.TryGetValue(name, out var clip)
                ? TranslationSpread(clip.Translation?.translations)
                : 0;
            var cog = GetTrans("COG");
            var legs = GetRot("L_Thigh") + GetRot("R_Thigh") +
                       GetRot("L_Knee") + GetRot("R_Knee") +
                       GetRot("L_Shin") + GetRot("R_Shin");
            var hands = GetRot("L_Hand") + GetRot("R_Hand") +
                        GetRot("L_Thumb1") + GetRot("R_Thumb1") +
                        GetRot("L_IndexF1") + GetRot("R_IndexF1") +
                        GetRot("L_MiddleF1") + GetRot("R_MiddleF1");
            // A kneeling/grounded item action tends to have both pronounced
            // lower-body motion and hand motion; COG translation helps break
            // ties with ordinary standing item-use actions.
            var score = legs + hands * 0.35f + cog * 100f;
            rows.Add((index, motlist.Motions[index].motNumber, score, cog, legs, hands,
                mot.Header.frameCount, mot.BoneClips.Count));
        }

        Console.WriteLine("[motcandidates] sorted by crouch/item-use motion score");
        foreach (var row in rows.OrderByDescending(row => row.Score).Take(80))
            Console.WriteLine(
                $"  index={row.Index} id={row.Id} score={row.Score:F1} cog={row.COG:F3} legs={row.Legs:F1} hands={row.Hands:F1} frame={row.Frame:F0} clips={row.Clips}");
        return 0;
    }
    case "uvinfo":
    {
        var nativePath = positional.ElementAtOrDefault(1);
        if (nativePath == null) { Console.WriteLine("uvinfo requires a mesh path"); return 1; }
        using var ms = pak.ReadFile(nativePath);
        using var mesh = new ReeLib.MeshFile(new ReeLib.FileHandler(ms, nativePath));
        mesh.Read();
        var lod = mesh.MeshData!.LODs[0];
        foreach (var group in lod.MeshGroups)
        foreach (var sub in group.Submeshes)
        {
            static string Range(Span<ReeLib.MplyMesh.HFloat2> uv)
            {
                if (uv.Length == 0) return "empty";
                var values = uv.ToArray().Select(v => v.AsVector2).ToArray();
                return $"({values.Min(v => v.X):G4},{values.Min(v => v.Y):G4})..({values.Max(v => v.X):G4},{values.Max(v => v.Y):G4})";
            }
            var name = sub.materialIndex < mesh.MaterialNames.Count ? mesh.MaterialNames[sub.materialIndex] : "?";
            var uv1 = sub.Buffer.UV1.Length >= sub.vertsIndexOffset + sub.vertCount ? Range(sub.UV1) : "empty";
            var uv2 = sub.Buffer.UV2.Length >= sub.vertsIndexOffset + sub.vertCount ? Range(sub.UV2) : "empty";
            Console.WriteLine($"g{group.groupId} mat={sub.materialIndex} {name} vertices={sub.vertCount} UV0={Range(sub.UV0)} UV1={uv1} UV2={uv2}");
        }
        return 0;
    }
    case "animall2glb":
    {
        var meshPath = positional.ElementAtOrDefault(1);
        var motlistPath = positional.ElementAtOrDefault(2);
        var outDir = GetOpt("--out") ?? "output";
        if (meshPath == null || motlistPath == null) { Console.WriteLine("animall2glb requires <mesh> <motlist>"); return 1; }
        using var meshMs = pak.ReadFile(meshPath);
        using var motMs = pak.ReadFile(motlistPath);
        var outputs = new AnimationService().ConvertAllToGlbWithAnimation(
            meshMs, meshPath, motMs, motlistPath, outDir);
        Console.WriteLine($"[animall2glb] exported={outputs.Count} first={outputs.FirstOrDefault()} last={outputs.LastOrDefault()}");
        return 0;
    }
    case "motembedded":
    {
        var motlistPath = positional.ElementAtOrDefault(1);
        if (motlistPath == null) { Console.WriteLine("motembedded requires a motlist path"); return 1; }
        using var motMs = pak.ReadFile(motlistPath);
        var motions = ViewportDataLoader.ListMotions(motMs, motlistPath);
        Console.WriteLine($"[motembedded] exportable={motions.Count}");
        foreach (var motion in motions.Take(10))
            Console.WriteLine($"  source={motion.SourceIndex} id={motion.MotionNumber} {motion.DisplayName}");
        return 0;
    }
    case "mdfinfo":
    {
        var mdfPath = positional.ElementAtOrDefault(1);
        var meshPath = GetOpt("--mesh");
        if (mdfPath == null) { Console.WriteLine("mdfinfo requires a native path"); return 1; }
        using var ms = pak.ReadFile(mdfPath);
        using var mdf = new ReeLib.MdfFile(new ReeLib.FileHandler(ms, mdfPath));
        var ok = mdf.Read();
        Console.WriteLine($"[mdfinfo] read={ok} materials={mdf.Materials.Count}");
        foreach (var mat in mdf.Materials.Take(20))
        {
            Console.WriteLine($"  mat '{mat.Name}' textures={mat.Textures.Count} params={mat.Parameters.Count}");
            foreach (var t in mat.Textures)
                Console.WriteLine($"    [{t.texType}] {t.texPath}");
            foreach (var p in mat.Parameters)
                Console.WriteLine($"    param [{p.paramName}] components={p.componentCount} value=({p.parameter.X:G6}, {p.parameter.Y:G6}, {p.parameter.Z:G6}, {p.parameter.W:G6})");
        }
        if (meshPath != null)
        {
            using var meshMs = pak.ReadFile(meshPath);
            using var mesh = new ReeLib.MeshFile(new ReeLib.FileHandler(meshMs, meshPath));
            mesh.Read();
            Console.WriteLine($"[mdfinfo] mesh MaterialNames ({mesh.MaterialNames.Count}): {string.Join(" | ", mesh.MaterialNames.Take(20))}");
        }
        return 0;
    }
    case "meshinfo2":
    {
        var nativePath = positional.ElementAtOrDefault(1);
        if (nativePath == null) { Console.WriteLine("meshinfo2 requires a native path"); return 1; }
        using var ms = pak.ReadFile(nativePath);
        var vm = ReExtractor.Core.ViewportDataLoader.LoadMesh(ms, nativePath, 0,
            p => { try { return pak.ReadFile(p); } catch { return null; } });
        Console.WriteLine($"[meshinfo2] verts={vm.VertexCount} faces={vm.FaceCount} bones={vm.Bones.Length} textures={vm.Textures.Length}");
        foreach (var t in vm.Textures)
            Console.WriteLine($"  tex {t.Width}x{t.Height} {t.Name}");
        return 0;
    }
    case "boneparents":
    {
        var nativePath = positional.ElementAtOrDefault(1);
        if (nativePath == null)
        {
            Console.WriteLine("boneparents requires <mesh path> [bone name]");
            return 1;
        }

        using var ms = pak.ReadFile(nativePath);
        var vm = ViewportDataLoader.LoadMesh(ms, nativePath, 0,
            path => { try { return pak.ReadFile(path); } catch { return null; } },
            loadTextures: false);
        var wanted = positional.ElementAtOrDefault(2);
        var indices = string.IsNullOrWhiteSpace(wanted)
            ? Enumerable.Range(0, vm.Bones.Length)
            : Enumerable.Range(0, vm.Bones.Length)
                .Where(index => vm.Bones[index].Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        foreach (var index in indices)
        {
            var bone = vm.Bones[index];
            var parent = bone.ParentIndex >= 0 && bone.ParentIndex < vm.Bones.Length
                ? vm.Bones[bone.ParentIndex].Name
                : "<root>";
            var children = vm.Bones
                .Select((candidate, childIndex) => (candidate, childIndex))
                .Where(item => item.candidate.ParentIndex == index)
                .Select(item => item.candidate.Name)
                .Take(12);
            Console.WriteLine($"[{index}] {bone.Name} parent={parent} children={string.Join(",", children)}");
        }
        return 0;
    }
    case "bonepose":
    {
        var nativePath = positional.ElementAtOrDefault(1);
        var wanted = positional.ElementAtOrDefault(2);
        if (nativePath == null || string.IsNullOrWhiteSpace(wanted))
        {
            Console.WriteLine("bonepose requires <mesh path> <bone name>");
            return 1;
        }

        using var ms = pak.ReadFile(nativePath);
        var vm = ViewportDataLoader.LoadMesh(ms, nativePath, 0,
            path => { try { return pak.ReadFile(path); } catch { return null; } },
            loadTextures: false);
        foreach (var (bone, index) in vm.Bones.Select((bone, index) => (bone, index))
                     .Where(item => item.bone.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)))
        {
            var q = Quaternion.CreateFromRotationMatrix(bone.LocalBind);
            var t = bone.LocalBind.Translation;
            Console.WriteLine($"[{index}] {bone.Name} parent={bone.ParentIndex} localT=({t.X:F5},{t.Y:F5},{t.Z:F5}) localQ=({q.X:F5},{q.Y:F5},{q.Z:F5},{q.W:F5})");
        }
        return 0;
    }
    case "boneusage":
    {
        var nativePath = positional.ElementAtOrDefault(1);
        var wanted = positional.ElementAtOrDefault(2);
        if (nativePath == null)
        {
            Console.WriteLine("boneusage requires <mesh path> [name filter]");
            return 1;
        }

        using var ms = pak.ReadFile(nativePath);
        var vm = ViewportDataLoader.LoadMesh(ms, nativePath, 0,
            path => { try { return pak.ReadFile(path); } catch { return null; } },
            loadTextures: false);
        var deformSet = vm.DeformToBone.ToHashSet();
        var usage = new int[vm.Bones.Length];
        foreach (var weights in vm.Weights)
            foreach (var (joint, weight) in weights)
                if (weight > 0 && joint >= 0 && joint < vm.DeformToBone.Length)
                    usage[vm.DeformToBone[joint]]++;

        foreach (var (bone, index) in vm.Bones.Select((bone, index) => (bone, index))
                     .Where(item => string.IsNullOrWhiteSpace(wanted) ||
                                    item.bone.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"[{index}] {bone.Name} parent={bone.ParentIndex} deform={deformSet.Contains(index)} weightedVertices={usage[index]}");
        }
        return 0;
    }
    case "motcoverage":
    {
        var meshPath = positional.ElementAtOrDefault(1);
        var motlistPath = positional.ElementAtOrDefault(2);
        var motionIndexText = positional.ElementAtOrDefault(3);
        if (meshPath == null || motlistPath == null ||
            !int.TryParse(motionIndexText, out var motionIndex))
        {
            Console.WriteLine("motcoverage requires <mesh path> <motlist path> <motion index>");
            return 1;
        }

        using var meshMs = pak.ReadFile(meshPath);
        var mesh = ViewportDataLoader.LoadMesh(meshMs, meshPath, 0,
            path => { try { return pak.ReadFile(path); } catch { return null; } },
            loadTextures: false);
        var meshNames = mesh.Bones.Select(bone => bone.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var motMs = pak.ReadFile(motlistPath);
        using var motlist = new ReeLib.MotlistFile(new ReeLib.FileHandler(motMs, motlistPath));
        motlist.Read();
        if ((uint)motionIndex >= (uint)motlist.Motions.Count ||
            motlist.Motions[motionIndex].MotFile is not ReeLib.MotFile mot)
        {
            Console.WriteLine("motion index is invalid or has no embedded mot");
            return 1;
        }

        var clipNames = mot.BoneClips
            .Select(clip => clip.ClipHeader.boneName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var matched = clipNames.Where(meshNames.Contains).ToArray();
        var missing = clipNames.Where(name => !meshNames.Contains(name!)).ToArray();
        Console.WriteLine($"[motcoverage] meshBones={mesh.Bones.Length} motBones={mot.Bones.Count} clipNames={clipNames.Length} matched={matched.Length} missing={missing.Length}");
        Console.WriteLine("[motcoverage] missing tracks:");
        foreach (var name in missing)
            Console.WriteLine($"  {name}");
        return 0;
    }
    case "bindcompare":
    {
        var meshPath = positional.ElementAtOrDefault(1);
        var motlistPath = positional.ElementAtOrDefault(2);
        var motionIndexText = positional.ElementAtOrDefault(3);
        if (meshPath == null || motlistPath == null ||
            !int.TryParse(motionIndexText, out var motionIndex))
        {
            Console.WriteLine("bindcompare requires <mesh path> <motlist path> <motion index>");
            return 1;
        }

        using var meshMs = pak.ReadFile(meshPath);
        var mesh = ViewportDataLoader.LoadMesh(meshMs, meshPath, 0,
            path => { try { return pak.ReadFile(path); } catch { return null; } },
            loadTextures: false);
        var meshByName = mesh.Bones
            .GroupBy(bone => bone.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        using var motMs = pak.ReadFile(motlistPath);
        using var motlist = new ReeLib.MotlistFile(new ReeLib.FileHandler(motMs, motlistPath));
        motlist.Read();
        if ((uint)motionIndex >= (uint)motlist.Motions.Count ||
            motlist.Motions[motionIndex].MotFile is not ReeLib.MotFile mot)
        {
            Console.WriteLine("motion index is invalid or has no embedded mot");
            return 1;
        }

        var filters = positional.Skip(4).ToArray();
        var motByName = mot.Bones
            .GroupBy(bone => bone.boneName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var pair in motByName)
        {
            if (filters.Length > 0 &&
                !filters.Any(filter => pair.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                continue;
            var m = pair.Value;
            var parent = m.Parent?.boneName ?? "<root>";
            var mt = m.translation;
            var mq = Quaternion.Normalize(m.quaternion);
            if (!meshByName.TryGetValue(pair.Key, out var meshBone))
            {
                Console.WriteLine($"MISSING mesh {pair.Key} motParent={parent} motT=({mt.X:F4},{mt.Y:F4},{mt.Z:F4}) motQ=({mq.X:F4},{mq.Y:F4},{mq.Z:F4},{mq.W:F4})");
                continue;
            }
            var lt = meshBone.LocalBind.Translation;
            var lq = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(meshBone.LocalBind));
            Console.WriteLine($"{pair.Key} motParent={parent} meshParent={mesh.Bones[meshBone.ParentIndex >= 0 ? meshBone.ParentIndex : 0].Name} motT=({mt.X:F4},{mt.Y:F4},{mt.Z:F4}) meshT=({lt.X:F4},{lt.Y:F4},{lt.Z:F4}) motQ=({mq.X:F4},{mq.Y:F4},{mq.Z:F4},{mq.W:F4}) meshQ=({lq.X:F4},{lq.Y:F4},{lq.Z:F4},{lq.W:F4})");
        }
        return 0;
    }
    case "preview2glb":
    {
        var nativePath = positional.ElementAtOrDefault(1);
        var outDir = GetOpt("--out") ?? "output";
        if (nativePath == null) { Console.WriteLine("preview2glb requires a mesh path"); return 1; }
        using var ms = pak.ReadFile(nativePath);
        var vm = ViewportDataLoader.LoadMesh(ms, nativePath, 1,
            path => { try { return pak.ReadFile(path); } catch { return null; } });
        var visible = vm.Groups.Where(group => group.DefaultVisible).Select(group => group.Key).ToHashSet();
        var output = Path.Combine(outDir, Path.GetFileNameWithoutExtension(nativePath) + ".preview.glb");
        new ViewportExportService().ConvertToGlb(vm, visible, output);
        Console.WriteLine($"[preview2glb] -> {output} visibleGroups={visible.Count}/{vm.Groups.Length} textures={vm.Textures.Length}");
        return 0;
    }
    default:
        Console.WriteLine($"Unknown command: {command}");
        return 1;
}

