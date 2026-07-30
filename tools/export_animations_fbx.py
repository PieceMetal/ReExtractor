import bpy
import os
import sys


def arguments():
    values = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if len(values) not in {2, 3}:
        raise RuntimeError("需要参数：临时 GLB 目录、动画 FBX 输出目录、可选 FPS(30/60)")
    fps = int(values[2]) if len(values) == 3 else 60
    if fps not in {30, 60}:
        raise RuntimeError(f"动画导出 FPS 只支持 30 或 60，当前 {fps}")
    return values[0], values[1], fps


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.armatures, bpy.data.materials,
                  bpy.data.images, bpy.data.actions):
        for item in list(block):
            block.remove(item)


source_dir, output_dir, export_fps = arguments()
inputs = sorted(
    os.path.join(source_dir, name)
    for name in os.listdir(source_dir)
    if name.lower().endswith(".glb")
)
if not inputs:
    raise RuntimeError("MotionList 中没有可导出的动作")

os.makedirs(output_dir, exist_ok=True)
scene = bpy.context.scene
scene.render.fps = export_fps
scene.render.fps_base = 1.0

for source in inputs:
    clear_scene()
    # glTF 时间单位是秒；导入前设置目标 FPS，可保持动作时长并落到对应时间轴。
    scene.render.fps = export_fps
    scene.render.fps_base = 1.0
    bpy.ops.import_scene.gltf(filepath=source)

    for obj in list(bpy.data.objects):
        if obj.type == "MESH":
            bpy.data.objects.remove(obj, do_unlink=True)

    armatures = [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]
    if not armatures:
        raise RuntimeError(f"动作缺少骨架：{os.path.basename(source)}")

    primary = armatures[0]
    primary.name = "Armature"
    primary.data.name = "Armature"
    # One animation FBX must contain the same single armature object as the model FBX.
    for duplicate in armatures[1:]:
        bpy.data.objects.remove(duplicate, do_unlink=True)
    armatures = [primary]

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
        add_leaf_bones=False,
        global_scale=1.0,
        apply_scale_options="FBX_SCALE_NONE",
        use_space_transform=True,
        armature_nodetype="NULL",
    )
    print(f"REEXTRACTOR_OK:{output_path}")
    print(f"REEXTRACTOR_PROGRESS:{inputs.index(source) + 1}/{len(inputs)}", flush=True)
