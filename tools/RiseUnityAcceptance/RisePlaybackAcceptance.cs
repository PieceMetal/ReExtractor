using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

[Serializable] public class RisePoseInput { public int frame; public GameObject reference; }
[Serializable] public class RiseCaseInput
{
    public string label;
    public GameObject model;
    public GameObject animationModel;
    public AnimationClip clip;
    public RisePoseInput[] poses;
}
[Serializable] public class RiseSampleResult
{
    public string label;
    public int frame, vertices, boneCount;
    public float referenceHeight, sourcePoseError, separateModelError, normalizedSourceError;
    public bool passed;
    public bool animationOnlyFbx;
}
[Serializable] public class RiseUnityResult
{
    public string unityVersion, graphicsDevice, testedAtUtc;
    public string rig = "Generic; Preserve Hierarchy; no compression; Custom 8 weights; runtime Unlimited";
    public bool playMode, passed;
    public List<RiseSampleResult> samples = new List<RiseSampleResult>();
    public List<string> errors = new List<string>();
}

public class RisePlaybackAcceptance : MonoBehaviour
{
    public RiseCaseInput[] cases;
    public string outputDirectory;
    private RiseUnityResult report;
    private readonly List<Playback> demonstration = new List<Playback>();
    private string status = "Preparing runtime samples";
    private bool ready, paused;
    private float clock;
    private int lastView = -1;
    private readonly HashSet<int> capturedViews = new HashSet<int>();

    private sealed class Playback : IDisposable
    {
        public GameObject root;
        public PlayableGraph graph;
        public AnimationClipPlayable playable;
        public float duration;
        public Playback(GameObject prefab, AnimationClip clip)
        {
            root = Instantiate(prefab);
            var animator = root.GetComponent<Animator>();
            if (animator == null) animator = root.AddComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            graph = PlayableGraph.Create("Rise actual playback");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            playable = AnimationClipPlayable.Create(graph, clip);
            playable.SetApplyFootIK(false);
            var output = AnimationPlayableOutput.Create(graph, "Animator", animator);
            output.SetSourcePlayable(playable);
            graph.Play();
            duration = clip.length;
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            { renderer.updateWhenOffscreen = true; renderer.quality = SkinQuality.Auto; }
        }
        public void Sample(float time) { playable.SetTime(time); graph.Evaluate(0); }
        public void Dispose() { if (graph.IsValid()) graph.Destroy(); if (root != null) Destroy(root); }
    }

    private IEnumerator Start()
    {
        QualitySettings.skinWeights = SkinWeights.Unlimited;
        Application.targetFrameRate = 60;
        Application.runInBackground = true;
        report = new RiseUnityResult { unityVersion = Application.unityVersion,
            graphicsDevice = SystemInfo.graphicsDeviceName, testedAtUtc = DateTime.UtcNow.ToString("O"), playMode = Application.isPlaying };
        Directory.CreateDirectory(outputDirectory);
        yield return null;
        foreach (var item in cases)
        {
            Playback target = null, embedded = null;
            try
            {
                target = new Playback(item.model, item.clip);
                bool animationOnly = item.animationModel.GetComponentsInChildren<SkinnedMeshRenderer>().Length == 0;
                if (!animationOnly) embedded = new Playback(item.animationModel, item.clip);
                // Mesh object names differ by design; verify every bone path instead.
                var modelBones = target.root.GetComponentsInChildren<SkinnedMeshRenderer>().SelectMany(r => r.bones)
                    .Select(t => RelativePath(t, target.root.transform)).Distinct().OrderBy(x => x).ToArray();
                var animationBones = item.animationModel.GetComponentsInChildren<Transform>()
                    .Select(t => RelativePath(t, item.animationModel.transform)).ToHashSet();
                if (modelBones.Any(path => !animationBones.Contains(path))) throw new Exception(item.label + ": model/animation bone paths differ");
                foreach (var pose in item.poses)
                {
                    status = item.label + " / frame " + pose.frame;
                    target.Sample(pose.frame / 60f);
                    if (embedded != null) embedded.Sample(pose.frame / 60f);
                    var reference = Instantiate(pose.reference);
                    var expected = StaticVertices(reference);
                    var actual = SkinnedVertices(target.root);
                    var sameAnimationMesh = embedded == null ? null : SkinnedVertices(embedded.root);
                    Destroy(reference);
                    if (actual.Length == 0 || expected.Length == 0) throw new Exception("No vertices available");
                    var bounds = new Bounds(expected[0], Vector3.zero);
                    foreach (var p in expected) bounds.Encapsulate(p);
                    float extent = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                    float sourceError = SymmetricError(actual, expected);
                    float separateError = sameAnimationMesh == null ? 0 : SymmetricError(actual, sameAnimationMesh);
                    var result = new RiseSampleResult { label = item.label, frame = pose.frame,
                        vertices = actual.Length, boneCount = modelBones.Length, referenceHeight = extent, animationOnlyFbx = animationOnly,
                        sourcePoseError = sourceError, separateModelError = separateError,
                        normalizedSourceError = sourceError / Mathf.Max(extent, .000001f),
                        passed = sourceError < Mathf.Max(.000001f, extent * .0005f) && separateError < Mathf.Max(.000001f, extent * .0001f) };
                    report.samples.Add(result);
                    Debug.Log("RISE_UNITY_SAMPLE " + JsonUtility.ToJson(result));
                    Save();
                }
            }
            catch (Exception ex) { report.errors.Add(ex.ToString()); Debug.LogError("RISE_UNITY_FAILURE " + ex); }
            finally { if (target != null) target.Dispose(); if (embedded != null) embedded.Dispose(); }
            yield return null;
        }
        report.passed = report.playMode && report.samples.Count == cases.Sum(c => c.poses.Length) &&
            report.samples.Count > 0 && report.samples.All(s => s.passed) && report.errors.Count == 0;
        Save();
        status = report.passed ? "Runtime sampling PASS - visual playback inspection pending" : "Runtime sampling FAILED - inspect report";
        Debug.Log("RISE_UNITY_RUNTIME " + (report.passed ? "PASS" : "FAIL"));
        SetupDemonstration();
        ready = true;
    }

    private void SetupDemonstration()
    {
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(.22f, .22f, .22f);
        var fill = Camera.main.gameObject.AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.intensity = .35f;
        for (int i = 0; i < cases.Length; i++)
        {
            var player = new Playback(cases[i].model, cases[i].clip);
            player.Sample(0);
            var vertices = SkinnedVertices(player.root);
            var bounds = new Bounds(vertices[0], Vector3.zero);
            foreach (var p in vertices) bounds.Encapsulate(p);
            float extent = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            player.root.transform.localScale *= 2f / extent;
            player.root.transform.position = new Vector3((i % 3 - 1) * 3.2f, 0, (i / 3) * 3.3f);
            foreach (var renderer in player.root.GetComponentsInChildren<Renderer>())
            {
                var materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    var material = new Material(Shader.Find("Standard"));
                    material.color = Color.HSVToRGB((m * .19f + i * .025f) % 1f, .32f, .6f);
                    material.SetFloat("_Glossiness", .25f);
                    materials[m] = material;
                }
                renderer.sharedMaterials = materials;
            }
            demonstration.Add(player);
        }
    }

    private void Update()
    {
        if (!ready || paused) return;
        clock += Time.deltaTime * .6f;
        foreach (var player in demonstration) player.Sample(clock % Mathf.Max(.01f, player.duration));
        // Rotate independent test views; no model/bone offsets are changed.
        int view = (int)(Time.realtimeSinceStartup / 8f) % (demonstration.Count * 3);
        if (view != lastView)
        {
            lastView = view;
            int subject = view / 3;
            int angle = view % 3;
            for (int i = 0; i < demonstration.Count; i++)
                foreach (var r in demonstration[i].root.GetComponentsInChildren<Renderer>()) r.enabled = i == subject;
            var points = SkinnedVertices(demonstration[subject].root);
            var bounds = new Bounds(points[0], Vector3.zero);
            foreach (var point in points) bounds.Encapsulate(point);
            Vector3 direction = angle == 0 ? new Vector3(0, .1f, -1) : angle == 1 ? new Vector3(1, .1f, 0) : new Vector3(0, .1f, 1);
            Camera.main.transform.position = bounds.center + direction.normalized * 4.2f;
            Camera.main.transform.LookAt(bounds.center + Vector3.up * .4f);
            Camera.main.fieldOfView = 42;
            status = cases[subject].label + " | view " + angle + " | runtime " + (report.passed ? "PASS" : "FAIL");
            File.WriteAllText(Path.Combine(outputDirectory, "current-view.json"), JsonUtility.ToJson(new VisualView { label=cases[subject].label, view=angle, time=Time.realtimeSinceStartup }, true));
        }
        int captureSlot = (int)(Time.realtimeSinceStartup / 2f) % (demonstration.Count * 12);
        if (capturedViews.Add(captureSlot))
        {
            string folder = Path.Combine(outputDirectory, "playback-frames");
            Directory.CreateDirectory(folder);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "view-" + lastView.ToString("00") + "-phase-" + (captureSlot % 4) + ".png"));
        }
    }
    private void OnGUI()
    {
        GUI.Box(new Rect(10,10,Mathf.Min(Screen.width-20,1000),96), "Rise pl004 / Unity actual Animator playback");
        GUI.Label(new Rect(24,34,960,24), status);
        GUI.Label(new Rect(24,57,960,24), "Automatic close-up: each case, three angles, 8 seconds per view");
        if (GUI.Button(new Rect(24,82,120,22), paused ? "Resume" : "Pause")) paused = !paused;
        if (GUI.Button(new Rect(154,82,140,22), "Inspect frame 92"))
        { paused = true; foreach (var player in demonstration) player.Sample(Mathf.Min(92 / 60f, player.duration)); }
    }
    private void OnDestroy() { foreach (var player in demonstration) player.Dispose(); }
    [Serializable] private class VisualView { public string label; public int view; public float time; }
    private void Save() { File.WriteAllText(Path.Combine(outputDirectory,"unity-runtime-report.json"), JsonUtility.ToJson(report, true)); }
    private static string RelativePath(Transform t, Transform root)
    {
        var names = new List<string>();
        while (t != root && t != null) { names.Add(t.name); t = t.parent; }
        names.Reverse(); return string.Join("/", names.ToArray());
    }
    private static Vector3[] StaticVertices(GameObject root)
    { return root.GetComponentsInChildren<MeshFilter>().SelectMany(m => m.sharedMesh.vertices.Select(v => m.transform.TransformPoint(v))).ToArray(); }
    private static Vector3[] SkinnedVertices(GameObject root)
    {
        var values = new List<Vector3>();
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var mesh = new Mesh(); renderer.BakeMesh(mesh);
            values.AddRange(mesh.vertices.Select(v => renderer.transform.TransformPoint(v)));
            Destroy(mesh);
        }
        return values.ToArray();
    }
    private static float SymmetricError(Vector3[] a, Vector3[] b)
    { return Mathf.Sqrt(Mathf.Max(new PointTree(a).MaxNearestSquared(b), new PointTree(b).MaxNearestSquared(a))); }
    private sealed class PointTree
    {
        private sealed class Node { public Vector3 point; public int axis; public Node left, right; }
        private readonly Node root;
        public PointTree(Vector3[] points) { root = Build((Vector3[])points.Clone(), 0, points.Length, 0); }
        private static Node Build(Vector3[] p, int start, int length, int depth)
        {
            if (length == 0) return null;
            int axis = depth % 3;
            Array.Sort(p, start, length, Comparer<Vector3>.Create((a,b) => a[axis].CompareTo(b[axis])));
            int middle = start + length / 2;
            return new Node { point = p[middle], axis = axis, left = Build(p,start,middle-start,depth+1),
                right = Build(p,middle+1,start+length-middle-1,depth+1) };
        }
        private static void Nearest(Node n, Vector3 p, ref float best)
        {
            if (n == null) return;
            best = Mathf.Min(best,(p-n.point).sqrMagnitude);
            float delta = p[n.axis] - n.point[n.axis];
            Nearest(delta < 0 ? n.left : n.right,p,ref best);
            if (delta*delta < best) Nearest(delta < 0 ? n.right : n.left,p,ref best);
        }
        public float MaxNearestSquared(Vector3[] points)
        { float max=0; foreach(var p in points) { float best=float.PositiveInfinity; Nearest(root,p,ref best); max=Mathf.Max(max,best); } return max; }
    }
}
