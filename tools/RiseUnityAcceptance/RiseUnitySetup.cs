using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class RiseUnitySetup
{
    [Serializable] private class Pose { public int frame; public string asset; }
    [Serializable] private class Input { public string gender, model, animation, sourceSha256; public Pose[] poses; }
    [Serializable] private class Manifest { public Input[] cases; }
    [Serializable] private class ImportEntry
    {
        public string asset, sha256, animationType, skinWeights;
        public int maxBonesPerVertex, animationClips, skinnedRenderers;
        public bool preserveHierarchy, optimizeGameObjects;
    }
    [Serializable] private class ImportReport { public string unityVersion; public ImportEntry[] assets; }

    [MenuItem("Rise Acceptance/Import and Play")]
    public static void Run()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        foreach (var path in Directory.GetFiles("Assets/RiseAcceptance", "*.fbx", SearchOption.AllDirectories))
        {
            var asset = path.Replace('\\','/');
            var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
            importer.isReadable = true;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.optimizeMeshVertices = false;
            importer.optimizeMeshPolygons = false;
            importer.preserveHierarchy = true;
            if (asset.Contains("/Inputs/"))
            {
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.optimizeGameObjects = false;
                importer.importAnimation = !asset.EndsWith("/model.fbx");
                importer.animationCompression = ModelImporterAnimationCompression.Off;
                importer.skinWeights = ModelImporterSkinWeights.Custom;
                importer.maxBonesPerVertex = 8;
                importer.minBoneWeight = 0;
            }
            else { importer.animationType = ModelImporterAnimationType.None; importer.importAnimation = false; }
            importer.SaveAndReimport();
        }
        var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText("Assets/RiseAcceptance/References/source-poses.json"));
        var results = Path.GetFullPath(Path.Combine(Application.dataPath,"../../Results"));
        Directory.CreateDirectory(results);
        var importReport = new ImportReport { unityVersion = Application.unityVersion,
            assets = Directory.GetFiles("Assets/RiseAcceptance/Inputs", "*.fbx", SearchOption.AllDirectories).Select(path => {
                var asset = path.Replace('\\','/');
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var file = File.OpenRead(path)) hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
                return new ImportEntry { asset=asset, sha256=hash, animationType=importer.animationType.ToString(),
                    skinWeights=importer.skinWeights.ToString(), maxBonesPerVertex=importer.maxBonesPerVertex,
                    preserveHierarchy=importer.preserveHierarchy, optimizeGameObjects=importer.optimizeGameObjects,
                    animationClips=AssetDatabase.LoadAllAssetsAtPath(asset).OfType<AnimationClip>().Count(c=>!c.name.StartsWith("__preview__")),
                    skinnedRenderers=AssetDatabase.LoadAssetAtPath<GameObject>(asset).GetComponentsInChildren<SkinnedMeshRenderer>().Length };
            }).ToArray() };
        File.WriteAllText(Path.Combine(results,"unity-import-report.json"),JsonUtility.ToJson(importReport,true));
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var runner = new GameObject("Rise actual import and playback acceptance").AddComponent<RisePlaybackAcceptance>();
        runner.outputDirectory = results;
        runner.cases = manifest.cases.Select(input => new RiseCaseInput {
            label = input.gender + "/" + Path.GetFileNameWithoutExtension(input.animation),
            model = AssetDatabase.LoadAssetAtPath<GameObject>(input.model),
            animationModel = AssetDatabase.LoadAssetAtPath<GameObject>(input.animation),
            clip = AssetDatabase.LoadAllAssetsAtPath(input.animation).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__")),
            poses = input.poses.Select(p => new RisePoseInput { frame = p.frame, reference = AssetDatabase.LoadAssetAtPath<GameObject>(p.asset) }).ToArray()
        }).ToArray();
        var camera = new GameObject("Main Camera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.transform.position = new Vector3(6.5f,7,-12);
        camera.transform.LookAt(new Vector3(0,.7f,1.5f));
        camera.backgroundColor = new Color(.09f,.11f,.15f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.nearClipPlane = .01f;
        var light = new GameObject("Key Light").AddComponent<Light>();
        light.type = LightType.Directional; light.intensity = .6f;
        light.transform.rotation = Quaternion.Euler(45,-30,0);
        RenderSettings.ambientLight = new Color(.45f,.45f,.45f);
        Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(scene,"Assets/Scenes/RiseAcceptance.unity");
        AssetDatabase.SaveAssets();
        EditorApplication.ExecuteMenuItem("Window/General/Game");
        var gameView = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
        gameView.maximized = true;
        Debug.Log("RISE_UNITY_IMPORT_READY cases=" + runner.cases.Length);
        EditorApplication.isPlaying = true;
    }
}
