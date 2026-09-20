import bpy,glob,os
from mathutils import Vector,Matrix
root=os.path.abspath('artifacts/merged-face-proof');bpy.ops.wm.read_factory_settings(use_empty=True);s=bpy.context.scene;s.render.fps=60
bpy.ops.import_scene.fbx(filepath=glob.glob(root+'/fbx/*.fbx')[0],anim_offset=0);s.frame_set(151,subframe=.2);bpy.context.view_layer.update();a=next(o for o in bpy.data.objects if o.type=='ARMATURE')
def pt(n):return a.matrix_world@a.pose.bones['__part1_'+n if '__part1_'+n in a.pose.bones else n].head
l,r=pt('LeftEye'),pt('RightEye');eyes=(l+r)*.5;chin=pt('Jaw_Jnt');nose=pt('LOD1_NoseBase_C');x=(l-r).normalized();up=eyes-chin;up=(up-x*up.dot(x)).normalized();normal=x.cross(up).normalized()
if normal.dot(nose-eyes)<0:normal=-normal;x=-x
center=eyes-up*.055;bpy.ops.object.camera_add(location=center+normal);cam=bpy.context.object;cam.rotation_euler=Matrix((x,up,normal)).transposed().to_euler();cam.data.type='ORTHO';cam.data.ortho_scale=.38;s.camera=cam
s.render.engine='BLENDER_WORKBENCH';s.display.shading.light='STUDIO';s.display.shading.color_type='SINGLE';s.display.shading.single_color=(.65,.68,.72);s.render.resolution_x=800;s.render.resolution_y=800;s.render.resolution_percentage=100;s.render.filepath=root+'/fbx-face.png';bpy.ops.render.render(write_still=True)
