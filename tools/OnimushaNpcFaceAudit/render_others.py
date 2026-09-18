import bpy,sys,os,glob
from mathutils import Vector
source=open('tools/render_glb_frame.py',encoding='utf-8-sig').read();source=source[:source.index('render("front"')]
source=source.replace('scene.render.filepath =', "camera.rotation_euler.rotate_axis('Z', math.pi)\n    scene.render.filepath =")
root=os.path.abspath('artifacts/onimusha-other-faces')
for path in sorted(glob.glob(root+'/*-face.glb')):
 name=os.path.basename(path).replace('.glb','')
 for t in (0,1):
  sys.argv=['blender','--',path,str(t),root+'/'+name+'-'+str(t)];env={};exec(compile(source,'setup','exec'),env)
  arm=next(o for o in bpy.context.scene.objects if o.type=='ARMATURE')
  env['center']=arm.matrix_world@arm.pose.bones['Head'].head;env['radius']=.3;env['camera'].data.ortho_scale=.4
  env['render']('face',(0,0,-1),'Y')
