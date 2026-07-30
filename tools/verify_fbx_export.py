import bpy
import math
import sys
from io_scene_fbx import parse_fbx


values = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if len(values) != 2:
    raise RuntimeError("need arguments: model FBX, animation FBX")


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


def rounded_vec(values):
    return [round(float(v), 6) for v in values]


def fbx_mesh_geometry_names(path):
    root, _version = parse_fbx.parse(path)
    names = []

    def walk(elem):
        if elem.id == b"Objects":
            for child in elem.elems:
                if child.id == b"Geometry" and len(child.props) >= 3 and child.props[2] == b"Mesh":
                    raw = child.props[1].split(b"\x00\x01", 1)[0]
                    names.append(raw.decode("utf-8", errors="replace"))
        for child in elem.elems:
            walk(child)

    walk(root)
    return names


def is_real_imported_mesh(obj, fbx_geometry_names):
    return obj.name in fbx_geometry_names or obj.data.name in fbx_geometry_names


def mesh_bounds(meshes):
    if not meshes:
        return {"min": None, "max": None, "size": None}
    mins = [math.inf, math.inf, math.inf]
    maxs = [-math.inf, -math.inf, -math.inf]
    for obj in meshes:
        for corner in obj.bound_box:
            world = obj.matrix_world @ mathutils.Vector(corner)
            for axis in range(3):
                mins[axis] = min(mins[axis], float(world[axis]))
                maxs[axis] = max(maxs[axis], float(world[axis]))
    size = [maxs[i] - mins[i] for i in range(3)]
    return {"min": rounded_vec(mins), "max": rounded_vec(maxs), "size": rounded_vec(size)}


def inspect(path):
    clear_scene()
    # Start from a deliberately different rate so the result proves the FBX import
    # establishes the requested time base instead of inheriting Blender defaults.
    bpy.context.scene.render.fps = 24
    bpy.context.scene.render.fps_base = 1.0
    fbx_geometries = fbx_mesh_geometry_names(path)
    bpy.ops.import_scene.fbx(filepath=path)

    imported_meshes = [obj for obj in bpy.data.objects if obj.type == "MESH"]
    meshes = [obj for obj in imported_meshes if is_real_imported_mesh(obj, fbx_geometries)]
    armatures = [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]
    armature = armatures[0] if armatures else None
    bones = list(armature.data.bones) if armature else []
    root_names = [bone.name for bone in bones if bone.parent is None]
    parents = {bone.name: (bone.parent.name if bone.parent else None) for bone in bones}

    return {
        "fbx_geometries": len(fbx_geometries),
        "fbx_geometry_names": fbx_geometries,
        "imported_meshes": len(imported_meshes),
        "imported_mesh_names": [obj.name for obj in imported_meshes],
        "meshes": len(meshes),
        "mesh_names": [obj.name for obj in meshes],
        "armatures": len(armatures),
        "armature_name": armature.name if armature else None,
        "armature_data_name": armature.data.name if armature else None,
        "armature_scale": rounded_vec(armature.scale) if armature else None,
        "armature_rotation": rounded_vec(armature.rotation_euler) if armature else None,
        "root_names": root_names,
        "bone_names": [bone.name for bone in bones],
        "parents": parents,
        "actions": len(bpy.data.actions),
        "action_slots": sum(len(action.slots) for action in bpy.data.actions),
        "fps": bpy.context.scene.render.fps / bpy.context.scene.render.fps_base,
        "frame_start": bpy.context.scene.frame_start,
        "frame_end": bpy.context.scene.frame_end,
        "mesh_bounds": mesh_bounds(meshes),
    }


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def summarize(label, data):
    bounds = data["mesh_bounds"]["size"]
    return (
        f"{label}: meshes={data['meshes']} armatures={data['armatures']} "
        f"bones={len(data['bone_names'])} roots={data['root_names']} "
        f"scale={data['armature_scale']} fps={data['fps']} actions={data['actions']} "
        f"bounds={bounds}"
    )


def first_list_diff(left, right):
    limit = min(len(left), len(right))
    for index in range(limit):
        if left[index] != right[index]:
            return f"first diff at {index}: model={left[index]!r}, animation={right[index]!r}"
    if len(left) != len(right):
        return f"length differs: model={len(left)}, animation={len(right)}"
    return "no diff"


def first_parent_diff(left, right):
    names = sorted(set(left) | set(right))
    for name in names:
        if left.get(name) != right.get(name):
            return f"{name}: model parent={left.get(name)!r}, animation parent={right.get(name)!r}"
    return "no diff"


def first_set_diff(left, right):
    left_set = set(left)
    right_set = set(right)
    missing = sorted(left_set - right_set)
    extra = sorted(right_set - left_set)
    if missing:
        return f"missing in animation: {missing[:8]}"
    if extra:
        return f"extra in animation: {extra[:8]}"
    return "no diff"


import mathutils

model = inspect(values[0])
animation = inspect(values[1])
print("REEXTRACTOR_VERIFY_MODEL " + summarize("model", model))
print("REEXTRACTOR_VERIFY_ANIM  " + summarize("animation", animation))

require(model["fbx_geometries"] == 1,
        f"model FBX should contain exactly 1 real mesh Geometry, got {model['fbx_geometries']}: {model['fbx_geometry_names']}")
require(model["meshes"] == 1, f"model FBX should import exactly 1 real mesh, got {model['meshes']}: {model['mesh_names']}")
require(model["armatures"] == 1, f"model FBX should contain exactly 1 armature, got {model['armatures']}")
require(animation["fbx_geometries"] == 0,
        f"animation FBX should contain no mesh Geometry, got {animation['fbx_geometries']}: {animation['fbx_geometry_names']}")
require(animation["meshes"] == 0, f"animation FBX should import no real mesh, got {animation['meshes']}: {animation['mesh_names']}")
require(animation["armatures"] == 1, f"animation FBX should contain exactly 1 armature, got {animation['armatures']}")
require(animation["actions"] >= 1, "animation FBX is missing an action")
require(animation["fps"] == 60, f"animation FBX should import at 60 FPS, got {animation['fps']}")

require(model["armature_name"] == "Armature", f"model armature object is {model['armature_name']!r}, expected 'Armature'")
require(animation["armature_name"] == "Armature", f"animation armature object is {animation['armature_name']!r}, expected 'Armature'")
require(model["root_names"] == animation["root_names"], "model and animation root bones differ")
require(model["root_names"] == ["root"], f"expected one real root bone named 'root', got {model['root_names']}")
require(set(model["bone_names"]) == set(animation["bone_names"]),
        "model and animation bone name sets differ: " +
        first_set_diff(model["bone_names"], animation["bone_names"]))
if model["bone_names"] != animation["bone_names"]:
    print("REEXTRACTOR_VERIFY_WARN bone order differs: " +
          first_list_diff(model["bone_names"], animation["bone_names"]))
require(model["parents"] == animation["parents"],
        "model and animation bone parent maps differ: " +
        first_parent_diff(model["parents"], animation["parents"]))
require(model["armature_scale"] == animation["armature_scale"], "model and animation armature scales differ")
require(model["armature_rotation"] == animation["armature_rotation"], "model and animation armature rotations differ")

size = model["mesh_bounds"]["size"]
require(size is not None and max(size) > 1.0, f"model bounds look too small, possible 100x scale loss: {size}")
print("REEXTRACTOR_VERIFY_OK")
