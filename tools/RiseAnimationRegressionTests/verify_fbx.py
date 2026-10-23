"""Import separate production model/animation FBXs and check the shared bone paths and skin pose."""
import bpy
import json
import os
import sys
from mathutils import Vector
from mathutils.kdtree import KDTree

values = sys.argv[sys.argv.index("--") + 1:]
if len(values) != 3:
    raise SystemExit("usage: blender -b --python verify_fbx.py -- <Unity audit output> <model.fbx> <animation.fbx>")
root, model_path, animation_path = [os.path.abspath(value) for value in values]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=model_path)
model = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
before = set(bpy.context.scene.objects)
bpy.ops.import_scene.fbx(filepath=animation_path)
added = set(bpy.context.scene.objects) - before
animation = next(o for o in added if o.type == "ARMATURE")
if any(o.type == "MESH" for o in added):
    raise RuntimeError("Animation FBX unexpectedly contains geometry")
model_bones = {b.name: b.parent.name if b.parent else None for b in model.data.bones}
animation_bones = {b.name: b.parent.name if b.parent else None for b in animation.data.bones}
if model_bones != animation_bones:
    raise RuntimeError("Separate model and animation FBX hierarchies differ")
model.animation_data_create()
model.animation_data.action = animation.animation_data.action
model.animation_data.action_slot = animation.animation_data.action_slot
scene = bpy.context.scene
start = int(animation.animation_data.action.frame_range[0])
print("FBX_ACTION_RANGE", tuple(animation.animation_data.action.frame_range), flush=True)
scene.frame_set(start + 92)
bpy.context.view_layer.update()
graph = bpy.context.evaluated_depsgraph_get()
actual = []
for obj in meshes:
    evaluated = obj.evaluated_get(graph)
    mesh = evaluated.to_mesh()
    actual.extend(evaluated.matrix_world @ v.co for v in mesh.vertices)
    evaluated.to_mesh_clear()
with open(os.path.join(root, "m-frame92.json"), encoding="utf-8") as stream:
    parts = json.load(stream)
expected = []
for part in parts:
    visible = {i for face in part["faces"] for i in face}
    expected.extend(Vector((v[0], -v[2], v[1])) for i, v in enumerate(part["vertices"]) if i in visible)
def distance(a, b):
    tree = KDTree(len(b))
    for i, point in enumerate(b):
        tree.insert(point, i)
    tree.balance()
    return max(tree.find(point)[2] for point in a)
error = max(distance(actual, expected), distance(expected, actual))
result = dict(bones=len(model_bones), mesh_objects=len(meshes), animation_mesh_objects=0, max_vertex_error=error, passed=error < 0.0001)
print("FBX_ROUNDTRIP", result, flush=True)
with open(os.path.join(root, "fbx-readback.json"), "w", encoding="utf-8") as stream:
    json.dump(result, stream, indent=2)
if not result["passed"]:
    raise RuntimeError("Separate FBX animation differs from the viewport")
