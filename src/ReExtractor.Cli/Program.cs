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

if (gameDir == null || listFile == null || command == null)
{
    Console.WriteLine("Usage: ReExtractor.Cli --game-dir <dir> --list <listfile> <stats|find <substr>|extract <path> --out <dir>>");
    return 1;
}

var pak = new PakService();
var pakCount = pak.AddPaksFromGameDir(gameDir);
var listCount = pak.LoadListFile(listFile);
Console.WriteLine($"[init] PAKs: {pakCount}, list paths: {listCount}");

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
            Console.WriteLine($"  mat '{mat.Name}' textures={mat.Textures.Count}");
            foreach (var t in mat.Textures.Take(8))
                Console.WriteLine($"    [{t.texType}] {t.texPath}");
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
    default:
        Console.WriteLine($"Unknown command: {command}");
        return 1;
}
