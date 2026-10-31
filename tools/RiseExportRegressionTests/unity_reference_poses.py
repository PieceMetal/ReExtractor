"""Bake independent static reference poses from the pre-FBX animated GLBs for Unity QA."""
import bpy
import hashlib
import json
import os
import sys
import math
from mathutils import Matrix

unity = os.environ.get("REEXTRACTOR_FBX_TARGET", "ue") == "unity"

source, destination = [os.path.abspath(p) for p in sys.argv[sys.argv.index("--") + 1:]]
os.makedirs(destination, exist_ok=True)
entries = []
for gender in ("m", "f"):
    folder = os.path.join(source, gender, "animation")
    for filename in sorted(os.listdir(folder)):
        if not filename.endswith(".glb"):
            continue
        path = os.path.join(folder, filename)
        bpy.ops.wm.read_factory_settings(use_empty=True)
        scene = bpy.context.scene
        scene.render.fps = 60
        scene.unit_settings.system = "METRIC"
        scene.unit_settings.scale_length = 0.01
        bpy.ops.import_scene.gltf(filepath=path)
        end = max(a.frame_range[1] for a in bpy.data.actions)
        frames = [0, 50, 92, 150, int(end)] if end >= 150 else [0, round(end * .25), round(end * .46), round(end * .75), int(end)]
        frames = sorted(set(frames))
        meshes = [o for o in scene.objects if o.type == "MESH" and any(m.type == "ARMATURE" for m in o.modifiers)]
        references = []
        for frame in frames:
            scene.frame_set(frame)
            graph = bpy.context.evaluated_depsgraph_get()
            bpy.ops.object.select_all(action="DESELECT")
            baked = []
            for obj in meshes:
                evaluated = obj.evaluated_get(graph)
                data = bpy.data.meshes.new_from_object(evaluated, depsgraph=graph)
                data.transform(evaluated.matrix_world)
                if unity:
                    data.transform(Matrix.Rotation(-math.pi / 2, 4, 'X'))
                item = bpy.data.objects.new("SourcePose", data)
                scene.collection.objects.link(item)
                item.select_set(True)
                baked.append(item)
            output_name = gender + "_" + filename[:-4] + "_frame" + str(frame) + ".fbx"
            bpy.ops.export_scene.fbx(filepath=os.path.join(destination, output_name), use_selection=True,
                object_types={"MESH"}, bake_anim=False, add_leaf_bones=False, global_scale=1.0,
                apply_unit_scale=True, apply_scale_options="FBX_SCALE_NONE", axis_forward="-Z" if unity else "Y", axis_up="Y" if unity else "Z",
                use_space_transform=False)
            references.append(dict(frame=frame, asset="Assets/RiseAcceptance/References/" + output_name))
            for item in baked:
                bpy.data.objects.remove(item, do_unlink=True)
        entries.append(dict(gender=gender, model="Assets/RiseAcceptance/Inputs/" + gender + "/model.fbx",
            animation="Assets/RiseAcceptance/Inputs/" + gender + "/" + filename[:-4] + ".fbx",
            sourceSha256=hashlib.sha256(open(path,"rb").read()).hexdigest(), poses=references))
with open(os.path.join(destination, "source-poses.json"), "w", encoding="utf-8") as f:
    json.dump(dict(cases=entries), f, indent=2)
print("UNITY_REFERENCE_POSES_READY cases=" + str(len(entries)), flush=True)
