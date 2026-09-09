using System.Numerics;
using ReExtractor.Core;

// Exercise the same public API used by merged model export. No game installation,
// test framework, FBX converter, or GUI is required.
var tests = new (string Name, Action Run)[]
{
    ("identical skeletons retain geometry and skinning", IdenticalSkeletons),
    ("smaller part first merges with the complete body", () => SkeletonSubset(true)),
    ("complete body first merges with a smaller part", () => SkeletonSubset(false)),
    ("additional part bones and weights join the union", AdditionalBones),
    ("reordered bones and deform joints keep weight targets", ReorderedBones),
    ("bone names merge case insensitively", CaseInsensitiveBones),
    ("rootless subset reconnects to the complete parent chain", () => MissingParentChain(false)),
    ("parent chain can come from a smaller source", () => MissingParentChain(true)),
    ("different inverse global bind remains rejected", GlobalBindConflict),
    ("different local bind under the same parent remains rejected", LocalBindConflict),
    ("different named parents remain rejected", ParentConflict),
    ("bind conflicts between second and third parts are rejected", NonReferenceBindConflict),
    ("parent conflicts between second and third parts are rejected", NonReferenceParentConflict),
    ("compatible partial hierarchies cannot create a union cycle", UnionHierarchyCycle),
    ("static meshes merge without a skeleton", StaticMeshes),
    ("static and skinned parts remain independent when animated", () => MixedStaticAndSkinned(false)),
    ("static bind bone avoids a source bone name collision", () => MixedStaticAndSkinned(true)),
    ("single mesh retains the existing fast path", SingleMesh),
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine($"FAIL {name}: {exception.Message}");
    }
}
Console.WriteLine($"MERGE_REGRESSION_TESTS: {tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static void IdenticalSkeletons()
{
    var first = Mesh(Body(), ["Hip", "Hand"]);
    var second = Mesh(Body(), ["Hip", "Hand"]);
    var merged = MergeAndCheck(first, second);
    Equal(3, merged.Bones.Length, "unique bones");
    Equal(2, merged.DeformToBone.Length, "unique deform joints");
}

static void SkeletonSubset(bool smallerFirst)
{
    var body = Mesh(Body(), ["Hip", "Hand"]);
    var part = Mesh([Spec("Root", null, 2, 0, 0), Spec("Hip", "Root", 2, 3, 0)], ["Hip"]);
    var merged = smallerFirst ? MergeAndCheck(part, body) : MergeAndCheck(body, part);
    Equal(3, merged.Bones.Length, "union bone count");
    Equal("Root", ParentName(merged, "Hip"), "Hip parent");
    Equal("Hip", ParentName(merged, "Hand"), "Hand parent");
}

static void AdditionalBones()
{
    var body = Mesh(Body(), ["Hand", "Hip"]);
    var hair = Mesh([
        Spec("Root", null, 2, 0, 0),
        Spec("Hip", "Root", 2, 3, 0),
        Spec("Hair", "Hip", 2, 5, 0),
        Spec("HairTip", "Hair", 2, 6, 0),
    ], ["HairTip", "Hair", "Hip"]);
    var merged = MergeAndCheck(body, hair);
    Equal(5, merged.Bones.Length, "union bone count");
    Equal(4, merged.DeformToBone.Length, "deduplicated deform joint count");
    Equal("Hair", ParentName(merged, "HairTip"), "extra bone parent");
}

static void ReorderedBones()
{
    var source = Body();
    var first = Mesh(source, ["Hip", "Hand"]);
    var second = Mesh([source[2], source[0], source[1]], ["Hand", "Hip"]);
    var merged = MergeAndCheck(first, second);
    Equal(3, merged.Bones.Length, "reordered bone count");
    Equal(2, merged.DeformToBone.Length, "reordered deform count");
    Equal("Hip", ParentName(merged, "Hand"), "remapped parent");
}

static void CaseInsensitiveBones()
{
    var first = Mesh(Body(), ["Hip", "Hand"]);
    var second = Mesh([
        Spec("hand", "hip", 4, 3, 0),
        Spec("ROOT", null, 2, 0, 0),
        Spec("hip", "ROOT", 2, 3, 0),
    ], ["hand", "hip"]);
    var merged = MergeAndCheck(first, second);
    Equal(3, merged.Bones.Length, "case-insensitive bone count");
    Equal(2, merged.DeformToBone.Length, "case-insensitive deform count");
}

static void MissingParentChain(bool largerSubset)
{
    var specs = new List<BoneSpec> { Spec("Hip", null, 2, 3, 0), Spec("Hand", "Hip", 4, 3, 0) };
    if (largerSubset)
    {
        specs.Add(Spec("AccessoryA", "Hand", 4, 4, 0));
        specs.Add(Spec("AccessoryB", "AccessoryA", 4, 5, 0));
    }
    var subset = Mesh(specs.ToArray(), ["Hand", "Hip"]);
    var body = Mesh(Body(), ["Hip", "Hand"]);
    var merged = MergeAndCheck(subset, body);
    Equal("Root", ParentName(merged, "Hip"), "restored parent chain");
    MatrixEqual(Bone(body, "Hip").LocalBind, Bone(merged, "Hip").LocalBind,
        "restored parent must use the matching local bind");
    MatrixEqual(Bone(body, "Hip").InverseGlobalBind, Bone(merged, "Hip").InverseGlobalBind,
        "reconnecting a parent must preserve inverse global bind");
}

static void GlobalBindConflict()
{
    var body = Mesh(Body(), ["Hip"]);
    var conflicting = Mesh(Body(), ["Hip"]);
    Bone(conflicting, "Hip").InverseGlobalBind = Matrix4x4.CreateTranslation(-2, -4, 0);
    Reject(body, conflicting);
}

static void LocalBindConflict()
{
    var body = Mesh(Body(), ["Hip"]);
    var conflicting = Mesh(Body(), ["Hip"]);
    Bone(conflicting, "Hip").LocalBind = Matrix4x4.CreateTranslation(0, 4, 0);
    Reject(body, conflicting);
}

static void ParentConflict()
{
    var first = Mesh([
        Spec("Root", null, 0, 0, 0), Spec("Pelvis", "Root", 0, 0, 0), Spec("Hand", "Root", 1, 0, 0),
    ], ["Hand"]);
    var second = Mesh([
        Spec("Root", null, 0, 0, 0), Spec("Pelvis", "Root", 0, 0, 0), Spec("Hand", "Pelvis", 1, 0, 0),
    ], ["Hand"]);
    Reject(first, second);
}

static void NonReferenceBindConflict()
{
    var first = Mesh([Spec("Root", null, 0, 0, 0)], ["Root"]);
    var second = Mesh([Spec("Root", null, 0, 0, 0), Spec("Accessory", "Root", 1, 0, 0)], ["Accessory"]);
    var third = Mesh([Spec("Root", null, 0, 0, 0), Spec("Accessory", "Root", 2, 0, 0)], ["Accessory"]);
    Reject(first, second, third);
}

static void NonReferenceParentConflict()
{
    var first = Mesh([Spec("Root", null, 0, 0, 0)], ["Root"]);
    var second = Mesh([
        Spec("Root", null, 0, 0, 0), Spec("ParentA", "Root", 0, 0, 0), Spec("Accessory", "ParentA", 1, 0, 0),
    ], ["Accessory"]);
    var third = Mesh([
        Spec("Root", null, 0, 0, 0), Spec("ParentB", "Root", 0, 0, 0), Spec("Accessory", "ParentB", 1, 0, 0),
    ], ["Accessory"]);
    Reject(first, second, third);
}

static void StaticMeshes()
{
    var merged = MergeAndCheck(Mesh([], []), Mesh([], []));
    Equal(0, merged.Bones.Length, "static bone count");
    Equal(0, merged.DeformToBone.Length, "static deform joint count");
    Check(merged.Weights.All(weights => weights.Length == 0), "static weights must stay empty");
}

static void UnionHierarchyCycle()
{
    // Each source is acyclic and has identical global binds; restoring both missing
    // parents without checking the union would yield A -> B -> A.
    var first = Mesh([Spec("A", null, 0, 0, 0), Spec("B", "A", 0, 0, 0)], ["B"]);
    var second = Mesh([Spec("B", null, 0, 0, 0), Spec("A", "B", 0, 0, 0)], ["A"]);
    Reject(first, second);
}

static void MixedStaticAndSkinned(bool collideWithRigidBoneName)
{
    var specs = Body().ToList();
    if (collideWithRigidBoneName)
        specs.Add(Spec("__ReExtractor_Static", "Root", 7, 3, 0));
    var body = Mesh(specs.ToArray(), ["Hip", "Hand"]);
    var staticPart = Mesh([], []);
    var merged = MergeAndCheck(staticPart, body);
    var animatedNames = body.Bones.Select(bone => bone.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    for (var v = 0; v < staticPart.VertexCount; v++)
    {
        var skinned = Vector3.Zero;
        var weights = merged.Weights[v];
        Check(weights.Length > 0, "static part needs explicit rigid weights in a skinned export");
        foreach (var (joint, weight) in weights)
        {
            var boneIndex = merged.DeformToBone[joint];
            var pose = AnimatedGlobal(merged, boneIndex, animatedNames, []);
            var skin = merged.Bones[boneIndex].InverseGlobalBind * pose;
            skinned += Vector3.Transform(merged.Vertices[v], skin) * weight;
        }
        Check(Vector3.Distance(staticPart.Vertices[v], skinned) < 1e-5f,
            "animating existing bones must not move static geometry");
    }
}

static void SingleMesh()
{
    var mesh = Mesh(Body(), ["Hip"]);
    Check(ReferenceEquals(mesh, ViewportMesh.Merge([mesh])), "single mesh should keep its existing fast path");
}

static ViewportMesh MergeAndCheck(params ViewportMesh[] sources)
{
    var merged = ViewportMesh.Merge(sources);
    Equal(sources.Sum(mesh => mesh.VertexCount), merged.VertexCount, "merged vertex count");
    Equal(sources.Sum(mesh => mesh.FaceCount), merged.FaceCount, "merged face count");
    Equal(merged.VertexCount, merged.Weights.Length, "per-vertex weight count");
    Equal(merged.DeformToBone.Length, merged.DeformToBone.Distinct().Count(), "unique deform bones");
    var vertexOffset = 0;
    var faceOffset = 0;
    foreach (var source in sources)
    {
        for (var v = 0; v < source.VertexCount; v++)
        {
            Equal(source.Vertices[v], merged.Vertices[vertexOffset + v], "unchanged bind-space vertex");
            if (source.DeformToBone.Length == 0 && merged.DeformToBone.Length > 0)
            {
                var rigidWeights = merged.Weights[vertexOffset + v];
                Check(rigidWeights.Length > 0, "rigid vertices need an explicit fixed joint in a skinned union");
                Check(MathF.Abs(rigidWeights.Sum(weight => weight.Weight) - 1f) < 1e-5f,
                    "rigid vertex weights must sum to one");
                continue;
            }
            Equal(source.Weights[v].Length, merged.Weights[vertexOffset + v].Length, "weight influence count");
            for (var w = 0; w < source.Weights[v].Length; w++)
            {
                var originalWeight = source.Weights[v][w];
                var mergedWeight = merged.Weights[vertexOffset + v][w];
                Check(mergedWeight.Joint >= 0 && mergedWeight.Joint < merged.DeformToBone.Length,
                    "merged weight must reference an existing deform joint");
                var originalName = source.Bones[source.DeformToBone[originalWeight.Joint]].Name;
                var mergedName = merged.Bones[merged.DeformToBone[mergedWeight.Joint]].Name;
                Check(string.Equals(originalName, mergedName, StringComparison.OrdinalIgnoreCase),
                    $"weight target changed from {originalName} to {mergedName}");
                Equal(originalWeight.Weight, mergedWeight.Weight, "unchanged weight value");
            }
        }
        for (var f = 0; f < source.FaceCount; f++)
        {
            var face = source.Faces[f];
            Equal((face.A + vertexOffset, face.B + vertexOffset, face.C + vertexOffset),
                merged.Faces[faceOffset + f], "remapped triangle indices");
        }
        vertexOffset += source.VertexCount;
        faceOffset += source.FaceCount;
    }
    // A consistent union must still produce identity skinning in bind pose. This also
    // detects parent-index mistakes and mixing local binds from an incomplete source
    // with a parent restored from the full body.
    for (var b = 0; b < merged.Bones.Length; b++)
        MatrixEqual(Matrix4x4.Identity, merged.Bones[b].InverseGlobalBind * GlobalBind(merged, b, []),
            $"bind-pose skinning of {merged.Bones[b].Name}");
    return merged;
}

static Matrix4x4 GlobalBind(ViewportMesh mesh, int boneIndex, HashSet<int> chain)
{
    Check(chain.Add(boneIndex), "merged parent hierarchy contains a cycle");
    var bone = mesh.Bones[boneIndex];
    Check(bone.ParentIndex >= -1 && bone.ParentIndex < mesh.Bones.Length, "merged parent index is invalid");
    return bone.ParentIndex < 0 ? bone.LocalBind : bone.LocalBind * GlobalBind(mesh, bone.ParentIndex, chain);
}

static Matrix4x4 AnimatedGlobal(ViewportMesh mesh, int boneIndex, HashSet<string> animatedNames, HashSet<int> chain)
{
    Check(chain.Add(boneIndex), "animated parent hierarchy contains a cycle");
    var bone = mesh.Bones[boneIndex];
    var local = bone.LocalBind;
    if (animatedNames.Contains(bone.Name))
        local *= Matrix4x4.CreateRotationZ(0.7f) * Matrix4x4.CreateTranslation(11, 7, 3);
    return bone.ParentIndex < 0 ? local : local * AnimatedGlobal(mesh, bone.ParentIndex, animatedNames, chain);
}

static void Reject(params ViewportMesh[] meshes)
{
    try
    {
        ViewportMesh.Merge(meshes);
    }
    catch (InvalidOperationException exception)
    {
        Check(!string.IsNullOrWhiteSpace(exception.Message), "incompatibility must explain the failure");
        return;
    }
    throw new Exception("Merge accepted an incompatible bind pose or hierarchy");
}

static BoneSpec[] Body() =>
[
    Spec("Root", null, 2, 0, 0),
    Spec("Hip", "Root", 2, 3, 0),
    Spec("Hand", "Hip", 4, 3, 0),
];

static BoneSpec Spec(string name, string? parent, float x, float y, float z)
    => new(name, parent, Matrix4x4.CreateTranslation(x, y, z));

static ViewportMesh Mesh(BoneSpec[] specs, string[] deformNames)
{
    var indexByName = specs.Select((spec, index) => (spec.Name, index))
        .ToDictionary(item => item.Name, item => item.index, StringComparer.OrdinalIgnoreCase);
    var bones = specs.Select(spec =>
    {
        var parentIndex = spec.Parent == null ? -1 : indexByName[spec.Parent];
        Matrix4x4.Invert(spec.Global, out var inverseGlobal);
        var local = spec.Global;
        if (parentIndex >= 0)
        {
            Matrix4x4.Invert(specs[parentIndex].Global, out var inverseParent);
            local *= inverseParent;
        }
        return new ViewportBone
        {
            Name = spec.Name,
            ParentIndex = parentIndex,
            LocalBind = local,
            InverseGlobalBind = inverseGlobal,
        };
    }).ToArray();
    var weights = new (int Joint, float Weight)[3][];
    for (var vertex = 0; vertex < weights.Length; vertex++)
        weights[vertex] = deformNames.Length == 0 ? [] : deformNames.Length == 1
            ? [(0, 1f)] : [(vertex % deformNames.Length, 0.25f), ((vertex + 1) % deformNames.Length, 0.75f)];
    return new ViewportMesh
    {
        Vertices = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
        Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        Uvs = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
        Faces = [(0, 1, 2)],
        FaceTexture = [-1],
        Textures = [],
        Weights = weights,
        Bones = bones,
        DeformToBone = deformNames.Select(name => indexByName[name]).ToArray(),
    };
}

static ViewportBone Bone(ViewportMesh mesh, string name)
    => mesh.Bones.Single(bone => string.Equals(bone.Name, name, StringComparison.OrdinalIgnoreCase));

static string? ParentName(ViewportMesh mesh, string name)
{
    var bone = Bone(mesh, name);
    return bone.ParentIndex < 0 ? null : mesh.Bones[bone.ParentIndex].Name;
}

static void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void MatrixEqual(Matrix4x4 expected, Matrix4x4 actual, string message)
{
    var difference = expected - actual;
    var values = new[]
    {
        difference.M11, difference.M12, difference.M13, difference.M14,
        difference.M21, difference.M22, difference.M23, difference.M24,
        difference.M31, difference.M32, difference.M33, difference.M34,
        difference.M41, difference.M42, difference.M43, difference.M44,
    };
    Check(values.All(value => float.IsFinite(value) && MathF.Abs(value) <= 1e-5f), message);
}

internal sealed record BoneSpec(string Name, string? Parent, Matrix4x4 Global);
