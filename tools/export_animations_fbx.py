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
        if not action.fcurves:
            continue
        start, end = action.frame_range
        starts.append(float(start))
        ends.append(float(end))
    if not starts:
        return 0, 0
    start = math.floor(min(starts))
    end = math.ceil(max(ends))
    if end < start:
        end = start
    return start, end


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

for index, source in enumerate(inputs, start=1):
    clear_scene()
    scene.render.fps = export_fps
    scene.render.fps_base = 1.0
    bpy.ops.import_scene.gltf(filepath=source)

    for obj in list(bpy.data.objects):
        if obj.type == "MESH":
            bpy.data.objects.remove(obj, do_unlink=True)

    armatures = [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]
    if not armatures:
        raise RuntimeError(f"Animation has no armature: {os.path.basename(source)}")

    primary = armatures[0]
    primary.name = "Armature"
    primary.data.name = "Armature"
    for duplicate in armatures[1:]:
        bpy.data.objects.remove(duplicate, do_unlink=True)
    armatures = [primary]

    start_frame, end_frame = action_frame_range()
    scene.frame_start = start_frame
    scene.frame_end = end_frame
    scene.frame_set(start_frame)

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
        object_types={"ARMATURE", "EMPTY"},
        bake_anim=True,
        bake_anim_use_all_actions=False,
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_bones=True,
        bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0,
        bake_anim_start=start_frame,
        bake_anim_end=end_frame,
        add_leaf_bones=False,
        global_scale=1.0,
        apply_scale_options="FBX_SCALE_NONE",
        use_space_transform=True,
        armature_nodetype="NULL",
    )
    print(f"REEXTRACTOR_OK:{output_path}")
    print(f"REEXTRACTOR_PROGRESS:{index}/{len(inputs)}", flush=True)