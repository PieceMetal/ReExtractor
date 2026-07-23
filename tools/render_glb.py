import bpy, sys, math

argv = sys.argv[sys.argv.index("--") + 1:]
glb_path, out_path = argv[0], argv[1]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb_path)

# stats
mesh_objs = [o for o in bpy.data.objects if o.type == 'MESH']
total_verts = sum(len(o.data.vertices) for o in mesh_objs)
total_faces = sum(len(o.data.polygons) for o in mesh_objs)
print(f"[stats] objects={len(mesh_objs)} verts={total_verts} faces={total_faces}")

# frame all
for o in bpy.context.scene.objects:
    o.select_set(True)

# camera
import mathutils
mins = [1e9]*3; maxs = [-1e9]*3
for o in mesh_objs:
    for c in o.bound_box:
        w = o.matrix_world @ mathutils.Vector(c)
        for i in range(3):
            mins[i] = min(mins[i], w[i]); maxs[i] = max(maxs[i], w[i])
center = mathutils.Vector([(mins[i]+maxs[i])/2 for i in range(3)])
size = max(maxs[i]-mins[i] for i in range(3))
print(f"[bbox] size={size:.3f} center={tuple(round(c,3) for c in center)}")

cam_data = bpy.data.cameras.new("cam")
cam = bpy.data.objects.new("cam", cam_data)
bpy.context.scene.collection.objects.link(cam)
direction = mathutils.Vector((1, -1, 0.6)).normalized()
cam.location = center + direction * size * 1.8
look = center - cam.location
cam.rotation_euler = look.to_track_quat('-Z', 'Y').to_euler()
bpy.context.scene.camera = cam

light_data = bpy.data.lights.new("sun", 'SUN')
light = bpy.data.objects.new("sun", light_data)
bpy.context.scene.collection.objects.link(light)
light.rotation_euler = (math.radians(50), 0, math.radians(30))

scene = bpy.context.scene
scene.render.engine = 'BLENDER_EEVEE'
scene.render.resolution_x = 640
scene.render.resolution_y = 640
scene.render.filepath = out_path
bpy.ops.render.render(write_still=True)
print("[done]")
