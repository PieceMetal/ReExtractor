import bpy,sys,os
from mathutils import Vector
source=open('tools/render_glb_frame.py',encoding='utf-8-sig').read()
source=source[:source.index('render("front"')]
source=source.replace('scene.render.filepath =', "camera.rotation_euler.rotate_axis('Z', math.pi)\n    scene.render.filepath =")
root=os.path.abspath('artifacts/onimusha-npc-face')
for name in ('ch004-static','ch004-before','elc1203_50_npc004_00_000_00_face_c006'):
 for t in (0,1):
  sys.argv=['blender','--',root+'/'+name+'.glb',str(t),root+'/'+name+'-'+str(t)]
  env={};exec(compile(source,'setup','exec'),env)
  print('OBJECTS',[(o.name,o.type,tuple(o.dimensions),tuple(o.location)) for o in bpy.context.scene.objects]);dg=bpy.context.evaluated_depsgraph_get()
  pts=[o.matrix_world@v.co for o in bpy.context.scene.objects if o.type=='MESH' for v in o.evaluated_get(dg).data.vertices]
  lo=Vector(tuple(min(p[i] for p in pts) for i in range(3)));hi=Vector(tuple(max(p[i] for p in pts) for i in range(3)))
  arm=next(o for o in bpy.context.scene.objects if o.type=='ARMATURE') if name!='ch004-static' else None
  if arm: print('HEAD',arm.matrix_world@arm.pose.bones['Head'].head)
  env['center']=(arm.matrix_world@arm.pose.bones['Head'].head) if arm else (lo+hi)*.5;env['radius']=max(hi-lo)*.55;env['camera'].data.ortho_scale=max(hi-lo)*1.2
  print('BOUNDS',name,lo,hi);env['camera'].data.ortho_scale=.35;env['render']('front',(0,-1,0));env['render']('opposite',(0,1,0));env['render']('face',(0,0,-1),'Y')
