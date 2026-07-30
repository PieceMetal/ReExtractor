import bpy
import json
import math
import mathutils
import sys
from io_scene_fbx import parse_fbx


path = sys.argv[sys.argv.index("--") + 1]


def fbx_mesh_geometry_names(fbx_path):
    root, _version = parse_fbx.parse(fbx_path)
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


def rounded_vec(values):
    return [round(float(v), 6) for v in values]


def is_real_imported_mesh(obj, fbx_geometry_names):
    # Blender's FBX importer may create an 8-vertex Cube display object for a
    # Null/LimbNode even when the FBX contains only one real Geometry node.
    # Use the FBX Geometry table as the source of truth.
    return obj.name in fbx_geometry_names or obj.data.name in fbx_geometry_names


def bounds_size(objects):
    if not objects:
        return None
    mins = [math.inf, math.inf, math.inf]
    maxs = [-math.inf, -math.inf, -math.inf]
    for obj in objects:
        for corner in obj.bound_box:
            world = obj.matrix_world @ mathutils.Vector(corner)
            for axis in range(3):
                mins[axis] = min(mins[axis], float(world[axis]))
                maxs[axis] = max(maxs[axis], float(world[axis]))
    return rounded_vec(maxs[i] - mins[i] for i in range(3))


fbx_geometries = fbx_mesh_geometry_names(path)
bpy.ops.import_scene.fbx(filepath=path)

imported_meshes = [obj for obj in bpy.data.objects if obj.type == "MESH"]
real_meshes = [obj for obj in imported_meshes if is_real_imported_mesh(obj, fbx_geometries)]
armatures = [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]
materials = list(bpy.data.materials)
image_nodes = sum(
    1 for material in materials if material.use_nodes
    for node in material.node_tree.nodes
    if node.type == "TEX_IMAGE" and node.image
)

armature = armatures[0] if armatures else None
bones = list(armature.data.bones) if armature else []
roots = [bone.name for bone in bones if bone.parent is None]
result = {
    "fbx_geometries": len(fbx_geometries),
    "fbx_geometry_names": fbx_geometries,
    "imported_meshes": len(imported_meshes),
    "imported_mesh_names": [obj.name for obj in imported_meshes],
    "real_meshes": len(real_meshes),
    "real_mesh_names": [obj.name for obj in real_meshes],
    "armatures": len(armatures),
    "armature_name": armature.name if armature else None,
    "armature_scale": rounded_vec(armature.scale) if armature else None,
    "roots": roots,
    "bones": len(bones),
    "materials": len(materials),
    "image_textures": image_nodes,
    "vertices": sum(len(obj.data.vertices) for obj in real_meshes),
    "polygons": sum(len(obj.data.polygons) for obj in real_meshes),
    "bounds": bounds_size(real_meshes),
}
print("REEXTRACTOR_MODEL_VERIFY:" + json.dumps(result, ensure_ascii=False))

if result["fbx_geometries"] != 1:
    raise RuntimeError(f"模型 FBX 内部应只有 1 个真实网格 Geometry，当前 {result['fbx_geometries']} 个：{result['fbx_geometry_names']}")
if result["real_meshes"] != 1:
    raise RuntimeError(f"模型 FBX 导入后应只有 1 个真实网格，当前 {result['real_meshes']} 个：{result['real_mesh_names']}")
if result["armatures"] != 1:
    raise RuntimeError(f"模型 FBX 应只有 1 套骨架，当前 {result['armatures']} 套")
if result["armature_name"] != "Armature":
    raise RuntimeError(f"骨架对象名应为 Armature，当前 {result['armature_name']!r}")
if result["armature_scale"] != [1.0, 1.0, 1.0]:
    raise RuntimeError(f"骨架缩放应为 [1,1,1]，当前 {result['armature_scale']}")
if roots != ["root"]:
    raise RuntimeError(f"应只有一个真实根骨 root，当前 {roots}")
if not materials:
    raise RuntimeError("模型 FBX 缺少材质")
if result["bounds"] is None or max(result["bounds"]) <= 1.0:
    raise RuntimeError(f"模型尺寸过小，疑似仍有 100 倍缩放问题：{result['bounds']}")
