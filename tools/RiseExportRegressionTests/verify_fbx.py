"""Blender background readback: compare model/animation binds and animated vertices.

Run after the production model and animation FBX conversion scripts:
blender -b --python verify_fbx.py -- <regression-output-directory>
"""
import bpy
import json
import os
import sys
from mathutils.kdtree import KDTree

root = os.path.abspath(sys.argv[sys.argv.index("--") + 1])
# FBX Euler baking/reimport introduces float roundoff beyond the GLB comparison.
# Keep the positional tolerance below 0.2 mm for these meter-scale source meshes.
vertex_tolerance = 0.0002


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.render.fps = 60
    bpy.context.scene.unit_settings.system = "METRIC"
    bpy.context.scene.unit_settings.scale_length = 0.01


def load(path):
    clear()
    if path.endswith(".fbx"):
        bpy.ops.import_scene.fbx(filepath=path, anim_offset=0.0)
    else:
        bpy.ops.import_scene.gltf(filepath=path)
    arms = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]
    assert len(arms) == 1, (path, "armature count", len(arms))
    return arms[0]


def skeleton(arm):
    return {b.name: (b.parent.name if b.parent else None, b.matrix_local.copy()) for b in arm.data.bones}


def vertices(frame):
    bpy.context.scene.frame_set(frame)
    graph = bpy.context.evaluated_depsgraph_get()
    result = []
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH" or not any(m.type == "ARMATURE" for m in obj.modifiers):
            continue
        evaluated = obj.evaluated_get(graph)
        mesh = evaluated.to_mesh()
        result.extend(evaluated.matrix_world @ v.co for v in mesh.vertices)
        evaluated.to_mesh_clear()
    assert result, "No skinned vertices"
    return result


def distance(a, b):
    tree = KDTree(len(b))
    for i, point in enumerate(b):
        tree.insert(point, i)
    tree.balance()
    return max(tree.find(point)[2] for point in a)


results = []
for gender in ("m", "f"):
    folder = os.path.join(root, gender)
    arm = load(os.path.join(folder, "fbx", "model.fbx"))
    model_bones = skeleton(arm)
    for name in sorted(os.listdir(os.path.join(folder, "animation"))):
        if not name.endswith(".glb"):
            continue
        arm = load(os.path.join(folder, "animation", name))
        expected = {frame: vertices(frame) for frame in (0, 50, 92, 150)}
        # Blender FBX import restores the declared axis system to Blender coordinates.
        arm = load(os.path.join(folder, "fbx", "animation", name[:-4] + ".fbx"))
        actual_bones = skeleton(arm)
        assert model_bones.keys() == actual_bones.keys(), "Model and animation bone names differ"
        max_bind = 0.0
        for bone, (parent, matrix) in model_bones.items():
            other_parent, other_matrix = actual_bones[bone]
            assert parent == other_parent, (bone, "parent mismatch")
            max_bind = max(max_bind, max(abs(matrix[r][c] - other_matrix[r][c]) for r in range(4) for c in range(4)))
        assert max_bind < 0.0001, (gender, name, "FBX bind mismatch", max_bind)
        # Production animation FBXs contain bones/tracks only; test on the model below.
        max_vertex = None
        if any(o.type == "MESH" for o in bpy.context.scene.objects):
            max_vertex = 0.0
            for frame, points in expected.items():
                actual = vertices(frame)
                max_vertex = max(max_vertex, distance(actual, points), distance(points, actual))
            assert max_vertex < vertex_tolerance, (gender, name, "FBX vertex mismatch", max_vertex)
        # Apply the separately exported animation to the separately exported model.
        action = arm.animation_data.action
        action_slot = arm.animation_data.action_slot
        action.use_fake_user = True
        bpy.data.objects.remove(arm, do_unlink=True)
        for obj in list(bpy.context.scene.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.ops.import_scene.fbx(filepath=os.path.join(folder, "fbx", "model.fbx"), anim_offset=0.0)
        target = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
        target.animation_data_create()
        target.animation_data.action = action
        target.animation_data.action_slot = action_slot
        max_retarget = 0.0
        for frame, points in expected.items():
            actual = vertices(frame)
            max_retarget = max(max_retarget, distance(actual, points), distance(points, actual))
        assert max_retarget < vertex_tolerance, (gender, name, "separate-model playback mismatch", max_retarget)
        item = dict(gender=gender, animation=name, bones=len(model_bones), max_bind_error=max_bind,
                    max_vertex_error=max_vertex, max_separate_model_error=max_retarget)
        print("RISE_FBX_SAMPLE " + json.dumps(item), flush=True)
        results.append(item)

with open(os.path.join(root, "fbx-readback.json"), "w", encoding="utf-8") as f:
    json.dump(results, f, indent=2)
print("RISE_FBX_READBACK PASS", flush=True)
