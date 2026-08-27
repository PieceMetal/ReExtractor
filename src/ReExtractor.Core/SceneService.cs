using System.Numerics;
using ReeLib;

namespace ReExtractor.Core;

public sealed record SceneLoadResult(
    ViewportMesh? Mesh,
    int ObjectCount,
    int MeshReferenceCount,
    int LoadedMeshCount,
    int MissingMeshCount,
    int PrefabReferenceCount,
    IReadOnlyList<string> MissingResources,
    string Game);

/// <summary>Loads the static, renderable subset of RE Engine SCN/PFB files.</summary>
public static class SceneService
{
    public static SceneLoadResult Load(Stream input, string nativePath,
        Func<string, Stream?> openResource, string? gameHint = null, bool loadTextures = false)
    {
        using var workspace = new Workspace(new GameConfig(new GameIdentifier(InferGame(nativePath, gameHint))));
        var instances = new List<(string Path, Matrix4x4 Transform)>();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objectCount = 0;
        var prefabCount = 0;

        if (nativePath.Contains(".scn.", StringComparison.OrdinalIgnoreCase))
        {
            var scene = new ScnFile(workspace.RszFileOption, new FileHandler(input, nativePath));
            if (!scene.Read()) throw new InvalidDataException("SCN 读取失败");
            scene.SetupGameObjects();
            prefabCount = scene.PrefabInfoList.Count;
            foreach (var root in scene.GameObjects ?? [])
                Collect(root, Matrix4x4.Identity, instances, ref objectCount);
        }
        else
        {
            var prefab = new PfbFile(workspace.RszFileOption, new FileHandler(input, nativePath));
            if (!prefab.Read()) throw new InvalidDataException("PFB 读取失败");
            prefab.SetupGameObjects();
            foreach (var root in prefab.GameObjects)
                Collect(root, Matrix4x4.Identity, instances, ref objectCount);
        }

        var meshes = new List<ViewportMesh>();
        foreach (var instance in instances)
        {
            using var stream = OpenVersioned(openResource, instance.Path);
            if (stream == null) { missing.Add(instance.Path); continue; }
            try
            {
                var mesh = ViewportDataLoader.LoadMesh(stream, instance.Path, 1, openResource, loadTextures);
                // Animated/skinned characters are resources, not reliable static scene geometry.
                if (mesh.Bones.Length > 0) continue;
                meshes.Add(mesh.WithTransform(instance.Transform));
            }
            catch { missing.Add(instance.Path); }
        }

        return new SceneLoadResult(meshes.Count == 0 ? null : ViewportMesh.Merge(meshes), objectCount,
            instances.Count, meshes.Count, missing.Count, prefabCount, missing.ToArray(), workspace.Config.BuiltInGame.ToString());
    }

    private static void Collect(IGameObject gameObject, Matrix4x4 parent,
        List<(string Path, Matrix4x4 Transform)> output, ref int objectCount)
    {
        objectCount++;
        var local = Matrix4x4.Identity;
        foreach (var component in gameObject.Components)
        {
            if (component.RszClass.name.Equals("via.Transform", StringComparison.OrdinalIgnoreCase))
            {
                var position = component.GetFieldValue("Position") is Vector3 p ? p : Vector3.Zero;
                var rotation = component.GetFieldValue("Rotation") is Quaternion q ? q : Quaternion.Identity;
                var scale = component.GetFieldValue("Scale") is Vector3 s ? s : Vector3.One;
                local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
            }
        }
        var world = local * parent;
        foreach (var component in gameObject.Components)
        {
            foreach (var fieldName in new[] { "Mesh", "mesh", "MeshPath", "_Mesh" })
            {
                if (component.GetFieldValue(fieldName) is not string value || !value.Contains(".mesh", StringComparison.OrdinalIgnoreCase)) continue;
                output.Add((value.Replace('\\', '/'), world));
                break;
            }
        }
        foreach (var child in gameObject.GetChildren()) Collect(child, world, output, ref objectCount);
    }

    private static Stream? OpenVersioned(Func<string, Stream?> opener, string path)
    {
        var direct = opener(path);
        if (direct != null) return direct;
        foreach (var suffix in new[] { ".241111606", ".240423143", ".230110883", ".221108797", ".2109148288", ".2101050001", ".1902042334", ".1808312334", ".1808282334" })
        {
            var stream = opener(path + suffix);
            if (stream != null) return stream;
        }
        return null;
    }

    private static string InferGame(string path, string? hint)
    {
        var value = (hint ?? "").ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        if (value.Contains("wild")) return "mhwilds";
        if (value.Contains("rise") || value.Contains("mhr")) return "mhrise";
        if (value.Contains("dragon") || value.Contains("dd2")) return "dd2";
        if (value.Contains("streetfighter") || value.Contains("sf6")) return "sf6";
        if (value.Contains("resident4") || value.Contains("re4")) return "re4";
        if (value.Contains("village") || value.Contains("re8")) return "re8";
        if (path.EndsWith(".scn.21", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pfb.18", StringComparison.OrdinalIgnoreCase)) return "mhwilds";
        if (path.EndsWith(".scn.20", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pfb.17", StringComparison.OrdinalIgnoreCase)) return "dd2";
        return "mhrise";
    }
}
