import bpy
import json
import mathutils
import os
import sys
from io_scene_fbx import parse_fbx


values = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if len(values) != 2:
    raise RuntimeError("need arguments: model FBX, animation FBX directory")

model_path, animation_dir = values


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (
        bpy.data.meshes,
        bpy.data.armatures,
        bpy.data.materials,
        bpy.data.images,
        bpy.data.actions,
    ):
        for item in list(block):
            block.remove(item)


def fbx_mesh_geometry_count(path):
    root, _version = parse_fbx.parse(path)
    count = 0

    def walk(elem):
        nonlocal count
        if elem.id == b"Objects":
            for child in elem.elems:
                if child.id == b"Geometry" and len(child.props) >= 3 and child.props[2] == b"Mesh":
                    count += 1
        for child in elem.elems:
            walk(child)

    walk(root)
    return count


def rounded_vec(values):
    return [round(float(v), 6) for v in values]


def inspect(path):
    clear_scene()
    geometry_count = fbx_mesh_geometry_count(path)
    bpy.context.scene.render.fps = 24
    bpy.context.scene.render.fps_base = 1.0
    bpy.ops.import_scene.fbx(filepath=path)
    armatures = [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]
    armature = armatures[0] if armatures else None
    bones = list(armature.data.bones) if armature else []
    parents = {bone.name: (bone.parent.name if bone.parent else None) for bone in bones}
    return {
        "path": path,
        "geometry_count": geometry_count,
        "armatures": len(armatures),
        "armature_name": armature.name if armature else None,
        "armature_scale": rounded_vec(armature.scale) if armature else None,
        "armature_rotation": rounded_vec(armature.rotation_euler) if armature else None,
        "roots": [bone.name for bone in bones if bone.parent is None],
        "bones": [bone.name for bone in bones],
        "parents": parents,
        "actions": len(bpy.data.actions),
        "fps": bpy.context.scene.render.fps / bpy.context.scene.render.fps_base,
    }


def fail(message, payload=None):
    if payload is not None:
        print("REEXTRACTOR_BATCH_FAIL:" + json.dumps(payload, ensure_ascii=False))
    raise RuntimeError(message)


model = inspect(model_path)
if model["geometry_count"] != 1:
    fail(f"model should contain 1 mesh Geometry, got {model['geometry_count']}", model)
if model["armatures"] != 1:
    fail(f"model should contain 1 armature, got {model['armatures']}", model)
if model["armature_scale"] != [1.0, 1.0, 1.0]:
    fail(f"model armature scale should be [1,1,1], got {model['armature_scale']}", model)
if model["roots"] != ["root"]:
    fail(f"model should have one root bone named root, got {model['roots']}", model)

animation_paths = [
    os.path.join(animation_dir, name)
    for name in sorted(os.listdir(animation_dir))
    if name.lower().endswith(".fbx")
]
if not animation_paths:
    fail("animation directory contains no FBX files", {"animation_dir": animation_dir})

checked = 0
for animation_path in animation_paths:
    animation = inspect(animation_path)
    checked += 1
    if animation["geometry_count"] != 0:
        fail(f"animation should contain no mesh Geometry: {animation_path}", animation)
    if animation["armatures"] != 1:
        fail(f"animation should contain 1 armature: {animation_path}", animation)
    if animation["actions"] < 1:
        fail(f"animation is missing action: {animation_path}", animation)
    if animation["fps"] != 60:
        fail(f"animation should import at 60 FPS: {animation_path}", animation)
    if animation["armature_name"] != model["armature_name"]:
        fail(f"armature object name differs: {animation_path}", animation)
    if animation["armature_scale"] != model["armature_scale"]:
        fail(f"armature scale differs: {animation_path}", animation)
    if animation["armature_rotation"] != model["armature_rotation"]:
        fail(f"armature rotation differs: {animation_path}", animation)
    if animation["roots"] != model["roots"]:
        fail(f"root bones differ: {animation_path}", animation)
    if set(animation["bones"]) != set(model["bones"]):
        fail(f"bone name set differs: {animation_path}", {
            "path": animation_path,
            "model_bones": len(model["bones"]),
            "animation_bones": len(animation["bones"]),
        })
    if animation["bones"] != model["bones"]:
        print("REEXTRACTOR_BATCH_WARN:bone order differs:" + animation_path)
    if animation["parents"] != model["parents"]:
        fail(f"bone parent map differs: {animation_path}", {"path": animation_path})

print("REEXTRACTOR_BATCH_VERIFY_OK:" + json.dumps({
    "model": model_path,
    "animation_dir": animation_dir,
    "animations": checked,
    "bones": len(model["bones"]),
    "fps": 60,
    "armature_scale": model["armature_scale"],
}, ensure_ascii=False))
