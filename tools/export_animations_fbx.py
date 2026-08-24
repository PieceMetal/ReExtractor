import bpy
import math
import os
import sys


def arguments():
    values = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if len(values) not in {2, 3}:
        raise RuntimeError("Need args: temp GLB directory, animation FBX output directory, optional FPS")
    fps = int(values[2]) if len(values) == 3 else 60
    if fps <= 0:
        raise RuntimeError(f"Invalid animation FPS: {fps}")
    return values[0], values[1], fps


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.armatures, bpy.data.materials,
                  bpy.data.images, bpy.data.actions):
        for item in list(block):
            block.remove(item)


def action_frame_range():
    starts = []
    ends = []
    for action in bpy.data.actions:
        # Blender 5.x moved animation data internals; Action.fcurves is not
        # guaranteed to exist. frame_range remains the stable export boundary.
        start, end = action.frame_range
        if end < start:
            continue
        starts.append(float(start))
        ends.append(float(end))
    if not starts:
        return 0, 0
    start = math.floor(min(starts))
    end = math.ceil(max(ends))
    if end < start:
        end = start
    return start, end


def ensure_export_root(armature):
    """Add the same identity Root bone used by model export."""
    if any(bone.name.casefold() == "root" for bone in armature.data.bones):
        return False

    bpy.ops.object.select_all(action="DESELECT")
    armature.select_set(True)
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.mode_set(mode="EDIT")
    try:
        source_roots = [bone for bone in armature.data.edit_bones if bone.parent is None]
        source_matrices = {bone.name: bone.matrix.copy() for bone in source_roots}
        root = armature.data.edit_bones.new("root")
        root.head = (0.0, 0.0, 0.0)
        root.tail = (0.0, 0.01, 0.0)
        root.use_deform = False
        for bone in source_roots:
            bone.parent = root
            bone.use_connect = False
            bone.matrix = source_matrices[bone.name]
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")

    print(f"REEXTRACTOR_ROOT_MODE:ADDED_BONE:{len(source_roots)}", flush=True)
    return True


source_dir, output_dir, export_fps = arguments()
inputs = sorted(
    os.path.join(source_dir, name)
    for name in os.listdir(source_dir)
    if name.lower().endswith(".glb")
)
if not inputs:
    raise RuntimeError("No exportable animations found in MotionList")

os.makedirs(output_dir, exist_ok=True)
scene = bpy.context.scene
scene.render.fps = export_fps
scene.render.fps_base = 1.0
# Must match model FBX export. Do not alter rest bones, animation keys or axes;
# only write the FBX in centimeter units so UE does not add a 100x root scale.
scene.unit_settings.system = "METRIC"
scene.unit_settings.scale_length = 0.01
scene.unit_settings.length_unit = "CENTIMETERS"

for index, source in enumerate(inputs, start=1):
    clear_scene()
    scene.render.fps = export_fps
    scene.render.fps_base = 1.0
    bpy.ops.import_scene.gltf(filepath=source)

    armatures = [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]
    if not armatures:
        raise RuntimeError(f"Animation has no armature: {os.path.basename(source)}")

    primary = armatures[0]
    primary.name = "Armature"
    primary.data.name = "Armature"
    # Keep the skinned reference mesh in the animation FBX. Without a skin/cluster
    # bind pose, animation-only FBX files make importers infer the skeleton rest
    # transforms from the first animated frame. UE then applies those tracks to a
    # different reference pose and character parts (especially head/face) separate.
    # Users can leave UE's "Import Mesh" disabled when importing these as animations.
    for mesh in (obj for obj in bpy.data.objects if obj.type == "MESH"):
        mesh.name = "REEXTRACTOR_BIND_POSE_REFERENCE"
        mesh.data.name = "REEXTRACTOR_BIND_POSE_REFERENCE"
    for duplicate in armatures[1:]:
        bpy.data.objects.remove(duplicate, do_unlink=True)
    armatures = [primary]
    if not ensure_export_root(primary):
        print("REEXTRACTOR_ROOT_MODE:EXPLICIT", flush=True)

    start_frame, end_frame = action_frame_range()
    scene.frame_start = int(start_frame)
    scene.frame_end = int(end_frame)
    scene.frame_set(int(start_frame))

    bpy.ops.object.select_all(action="DESELECT")
    for armature in armatures:
        armature.select_set(True)
    bpy.context.view_layer.objects.active = armatures[0]

    output_path = os.path.join(
        output_dir, os.path.splitext(os.path.basename(source))[0] + ".fbx"
    )
    bpy.ops.export_scene.fbx(
        filepath=output_path,
        use_selection=True,
        object_types={"ARMATURE", "MESH", "EMPTY"},
        bake_anim=True,
        bake_anim_use_all_actions=False,
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_bones=True,
        bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0,
        add_leaf_bones=False,
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_NONE",
        axis_forward="Y",
        axis_up="Z",
        use_space_transform=False,
        primary_bone_axis="Z",
        secondary_bone_axis="X",
        armature_nodetype="NULL",
    )
    print(f"REEXTRACTOR_OK:{output_path}")
    print(f"REEXTRACTOR_PROGRESS:{index}/{len(inputs)}", flush=True)
