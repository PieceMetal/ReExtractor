"""Read the production GLB bridge back through Blender and compare actual skinned vertices."""
import bpy
import json
import os
import sys
from mathutils import Vector
from mathutils.kdtree import KDTree

root = os.path.abspath(sys.argv[sys.argv.index("--") + 1])
reports = []
for gender in ("m", "f"):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    source = os.path.join(root, gender + "-animated.glb")
    bpy.ops.import_scene.gltf(filepath=source)
    scene = bpy.context.scene
    position = 92 * scene.render.fps / 60
    scene.frame_set(int(position), subframe=position % 1)
    bpy.context.view_layer.update()
    graph = bpy.context.evaluated_depsgraph_get()
    actual = []
    for obj in scene.objects:
        if obj.type != "MESH" or not any(m.type == "ARMATURE" for m in obj.modifiers):
            continue
        evaluated = obj.evaluated_get(graph)
        mesh = evaluated.to_mesh()
        actual.extend(evaluated.matrix_world @ v.co for v in mesh.vertices)
        evaluated.to_mesh_clear()
    with open(os.path.join(root, gender + "-frame92.json"), encoding="utf-8") as stream:
        parts = json.load(stream)
    expected = []
    for part in parts:
        visible = {index for face in part["faces"] for index in face}
        expected.extend(Vector((v[0], -v[2], v[1])) for i, v in enumerate(part["vertices"]) if i in visible)
    def distance(a, b):
        tree = KDTree(len(b))
        for index, point in enumerate(b):
            tree.insert(point, index)
        tree.balance()
        return max(tree.find(point)[2] for point in a)
    error = max(distance(actual, expected), distance(expected, actual))
    reports.append(dict(gender=gender, actual=len(actual), expected=len(expected), max_error=error, passed=error < 0.0001))
    print("EXPORT_READBACK", reports[-1], flush=True)
with open(os.path.join(root, "export-readback.json"), "w", encoding="utf-8") as stream:
    json.dump(reports, stream, indent=2)
if not all(row["passed"] for row in reports):
    raise RuntimeError("Merged animated GLB diverged from actual viewport skinning")
