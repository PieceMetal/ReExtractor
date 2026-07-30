import bpy
import os
import re
import sys


def arguments():
    values = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if len(values) != 2:
        raise RuntimeError("需要参数：临时 GLB 目录、输出 FBX 路径")
    return values[0], values[1]


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.armatures, bpy.data.materials,
                  bpy.data.images, bpy.data.actions):
        for item in list(block):
            block.remove(item)


def safe_texture_name(name, used):
    stem = re.sub(r"[^0-9A-Za-z_.-]+", "_", name).strip("._")
    stem = stem.replace(".tex", "_tex")
    if not stem:
        stem = "texture"
    if stem.lower().endswith(".png"):
        stem = stem[:-4]
    candidate = stem + ".png"
    index = 1
    while candidate.lower() in used:
        candidate = f"{stem}_{index:03d}.png"
        index += 1
    used.add(candidate.lower())
    return candidate


def save_external_textures(output_path):
    texture_dir = os.path.splitext(output_path)[0] + ".fbm"
    os.makedirs(texture_dir, exist_ok=True)
    used = set()
    saved = 0
    for image in bpy.data.images:
        if image.name in {"Render Result", "Viewer Node"}:
            continue
        if image.type != "IMAGE" or image.size[0] <= 0 or image.size[1] <= 0:
            continue
        texture_path = os.path.join(texture_dir, safe_texture_name(image.name, used))
        try:
            image.filepath_raw = texture_path
            image.file_format = "PNG"
            image.save()
            verify_saved_texture(texture_path)
        except Exception as exc:
            try:
                image.save_render(texture_path, scene=bpy.context.scene)
                verify_saved_texture(texture_path)
                print(f"REEXTRACTOR_TEXTURE_RECOVERED:{image.name}:{exc}", flush=True)
            except Exception as fallback_exc:
                print(f"REEXTRACTOR_TEXTURE_WARN:{image.name}:{exc}; fallback={fallback_exc}", flush=True)
                continue
        image.filepath = texture_path
        image.filepath_raw = texture_path
        saved += 1
    print(f"REEXTRACTOR_TEXTURES:{saved}:{texture_dir}", flush=True)


def verify_saved_texture(path):
    loaded = bpy.data.images.load(path, check_existing=False)
    try:
        if loaded.size[0] <= 0 or loaded.size[1] <= 0:
            raise RuntimeError("saved PNG has invalid size")
    finally:
        bpy.data.images.remove(loaded)


def is_bound_to_armature(obj, armature):
    return obj.parent == armature or any(
        modifier.type == "ARMATURE" and modifier.object == armature
        for modifier in obj.modifiers
    )


def remove_unbound_meshes(armature, keep=None):
    for obj in list(bpy.data.objects):
        if obj.type != "MESH" or obj == keep:
            continue
        if is_bound_to_armature(obj, armature):
            continue
        mesh_data = obj.data
        bpy.data.objects.remove(obj, do_unlink=True)
        if mesh_data.users == 0:
            bpy.data.meshes.remove(mesh_data)


def remove_meshes_except(keep):
    for obj in list(bpy.data.objects):
        if obj.type != "MESH" or obj == keep:
            continue
        mesh_data = obj.data
        bpy.data.objects.remove(obj, do_unlink=True)
        if mesh_data.users == 0:
            bpy.data.meshes.remove(mesh_data)


source_dir, output_path = arguments()
inputs = sorted(
    os.path.join(source_dir, name)
    for name in os.listdir(source_dir)
    if name.lower().endswith(".glb")
)
if not inputs:
    raise RuntimeError("没有可转换的 GLB 模型")

clear_scene()
bpy.context.scene.render.fps = 60
bpy.context.scene.render.fps_base = 1.0

for source in inputs:
    bpy.ops.import_scene.gltf(filepath=source, import_pack_images=True)

# Character parts exported from separate .mesh files usually carry identical
# armatures. Rebind them to one shared armature so DCC applications receive one
# coherent character instead of a stack of duplicate skeletons.
armatures = [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]
if armatures:
    primary = armatures[0]
    # UE has a Blender compatibility rule that removes an armature object named exactly
    # "Armature" instead of importing it as an extra root bone. Keep this name identical
    # for model and animation exports.
    primary.name = "Armature"
    primary.data.name = "Armature"
    primary_bones = {bone.name for bone in primary.data.bones}
    for duplicate in armatures[1:]:
        if {bone.name for bone in duplicate.data.bones} != primary_bones:
            continue
        for obj in bpy.data.objects:
            if obj.parent == duplicate:
                world = obj.matrix_world.copy()
                obj.parent = primary
                obj.matrix_world = world
            for modifier in obj.modifiers:
                if modifier.type == "ARMATURE" and modifier.object == duplicate:
                    modifier.object = primary
        bpy.data.objects.remove(duplicate, do_unlink=True)

# Drop glTF helper/bone-shape meshes before joining. They are not skinned to the
# character armature and can otherwise reappear in FBX/UE as a stray Cube mesh.
if armatures:
    primary = armatures[0]
    remove_unbound_meshes(primary)

# UE treats every FBX mesh object as a separate skeletal/static mesh candidate. Keep all
# material slots and vertex groups, but physically join every imported part into one object.
mesh_objects = [obj for obj in bpy.data.objects if obj.type == "MESH"]
if mesh_objects:
    bpy.ops.object.select_all(action="DESELECT")
    for obj in mesh_objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = mesh_objects[0]
    if len(mesh_objects) > 1:
        bpy.ops.object.join()
    merged_mesh = bpy.context.view_layer.objects.active
    merged_mesh.name = "合并模型"
    merged_mesh.data.name = "合并模型"
    if armatures:
        merged_mesh.parent = armatures[0]

# Export only the unified skeleton and unified mesh. Imported helper empties or bone-shape
# geometry must not become extra UE assets.
if armatures:
    primary = armatures[0]
    remove_unbound_meshes(primary)
    mesh_objects = [obj for obj in bpy.data.objects if obj.type == "MESH"]
    if len(mesh_objects) != 1:
        raise RuntimeError(f"模型 FBX 导出前应只剩 1 个蒙皮网格，当前 {len(mesh_objects)} 个")

if mesh_objects:
    merged_mesh = mesh_objects[0]
    remove_meshes_except(merged_mesh)

bpy.ops.object.select_all(action="DESELECT")
if armatures:
    armatures[0].select_set(True)
if mesh_objects:
    merged_mesh.select_set(True)
    bpy.context.view_layer.objects.active = merged_mesh

save_external_textures(output_path)
os.makedirs(os.path.dirname(output_path), exist_ok=True)
bpy.ops.export_scene.fbx(
    filepath=output_path,
    use_selection=True,
    object_types={"ARMATURE", "MESH"},
    bake_anim=False,
    add_leaf_bones=False,
    global_scale=1.0,
    apply_scale_options="FBX_SCALE_NONE",
    use_space_transform=True,
    use_armature_deform_only=True,
    armature_nodetype="NULL",
    path_mode="RELATIVE",
    embed_textures=False,
)
print(f"REEXTRACTOR_OK:{output_path}")
