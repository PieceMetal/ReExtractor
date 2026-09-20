import bpy,json,os
from mathutils import Vector
root=os.path.abspath('artifacts/merged-face-proof')
bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
v=[tuple(map(float,l.split())) for l in open(root+'/after.xyz')]
v=[(x,-z,y) for x,y,z in v]
m=bpy.data.meshes.new('head');m.from_pydata(v,[],json.load(open(root+'/faces.json')));m.update();o=bpy.data.objects.new('head',m);bpy.context.collection.objects.link(o)
lo=Vector(tuple(min(p[i] for p in v) for i in range(3)));hi=Vector(tuple(max(p[i] for p in v) for i in range(3)));center=(lo+hi)/2
bpy.ops.object.camera_add(location=center+Vector((0,-1,0)));cam=bpy.context.object;cam.rotation_euler=(center-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.type='ORTHO';cam.data.ortho_scale=max(hi-lo)*1.25
s=bpy.context.scene;s.camera=cam;s.render.engine='BLENDER_WORKBENCH';s.display.shading.light='STUDIO';s.display.shading.color_type='SINGLE';s.display.shading.single_color=(.65,.68,.72);s.render.resolution_x=700;s.render.resolution_y=700;s.render.resolution_percentage=100;s.render.filepath=root+'/after.png';bpy.ops.render.render(write_still=True)
