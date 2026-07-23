import bpy, sys, math, mathutils

argv = sys.argv[sys.argv.index("--") + 1:]
glb_path, out_path, frame = argv[0], argv[1], int(argv[2]) if len(argv) > 2 else 15

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb_path)

mesh_objs = [o for o in bpy.data.objects if o.type == 'MESH']
actions = list(bpy.data.actions)
print(f"[anim] actions={len(actions)} frames={[ (a.frame_range[0], a.frame_range[1]) for a in actions ]}")
bpy.context.scene.frame_set(frame)

depsgraph = bpy.context.evaluated_depsgraph_get()
mins = [1e9]*3; maxs = [-1e9]*3
for o in mesh_objs:
    eo = o.evaluated_get(depsgraph)
    m = eo.to_mesh()
    for v in m.vertices:
        w = eo.matrix_world @ v.co
        for i in range(3):
            mins[i] = min(mins[i], w[i]); maxs[i] = max(maxs[i], w[i])
    eo.to_mesh_clear()
center = mathutils.Vector([(mins[i]+maxs[i])/2 for i in range(3)])
size = max(maxs[i]-mins[i] for i in range(3))
print(f"[bbox] size={size:.3f} center={tuple(round(c,3) for c in center)}")

cam_data = bpy.data.cameras.new("cam"); cam = bpy.data.objects.new("cam", cam_data)
bpy.context.scene.collection.objects.link(cam)
direction = mathutils.Vector((1, -1, 0.5)).normalized()
cam.location = center + direction * max(size, 0.5) * 1.8
cam.rotation_euler = (center - cam.location).to_track_quat('-Z', 'Y').to_euler()
bpy.context.scene.camera = cam
light_data = bpy.data.lights.new("sun", 'SUN'); light = bpy.data.objects.new("sun", light_data)
bpy.context.scene.collection.objects.link(light)
light.rotation_euler = (math.radians(50), 0, math.radians(30))

scene = bpy.context.scene
scene.render.engine = 'BLENDER_EEVEE'
scene.render.resolution_x = 640; scene.render.resolution_y = 640
scene.render.filepath = out_path
bpy.ops.render.render(write_still=True)
print("[done]")
