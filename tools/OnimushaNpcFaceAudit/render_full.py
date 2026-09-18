import bpy,sys,os,glob,json
from mathutils import Vector, Matrix
source=open('tools/render_glb_frame.py',encoding='utf-8-sig').read();source=source[:source.index('render("front"')]
source=source.replace('scene.render.filepath =', "camera.rotation_euler.rotate_axis('Z', math.pi)\n    camera.location = center + follow @ (camera.location-center)\n    camera.rotation_euler = (follow @ camera.rotation_euler.to_matrix()).to_euler()\n    scene.render.filepath =")
root=os.path.abspath('artifacts/onimusha-all-faces')
requested=sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else []
paths=sorted(glob.glob(root+'/ch*-*.glb'))
valid=set()
for r in json.load(open(root+'/results.json',encoding='utf-8')):
 for c in r['checks']:
  kind='cinematic' if '/cutscene/' in c['path'] else 'talk' if '/talk/' in c['path'] else 'common'
  valid.add('ch'+r['id']+'-'+kind+'.glb')
paths=[p for p in paths if os.path.basename(p) in valid]
if requested:paths=[p for p in paths if any(os.path.basename(p).startswith(n+'-') for n in requested)]
if not paths: paths=glob.glob(os.path.abspath('artifacts/onimusha-other-faces/ch024-face.glb'))
for path in paths:
 name=os.path.basename(path).replace('.glb','')
 for t in (0,1,2):
  sys.argv=['blender','--',path,str(t),root+'/render/'+name+'-'+str(t)];env={};exec(compile(source,'setup','exec'),env)
  end=max((a.frame_range[1] for a in bpy.data.actions),default=0);env['scene'].frame_set(round(end*(t/2)));bpy.context.view_layer.update()
  arm=next(o for o in bpy.context.scene.objects if o.type=='ARMATURE');head=arm.pose.bones['Head']
  env['center']=arm.matrix_world@head.head;env['radius']=.3;env['camera'].data.ortho_scale=.4
  env['follow']=(arm.matrix_world@head.matrix@head.bone.matrix_local.inverted()@arm.matrix_world.inverted()).to_3x3().normalized()
  # Face landmarks provide a stable inspection camera even when source root
  # motion rotates the character or the bind head has a different orientation.
  names=head.id_data.pose.bones
  def point(*choices):
   for n in choices:
    if n in names:return arm.matrix_world@names[n].head
   return None
  left=point('LeftEye','Left_Eye','eye_Lt','L_Eye');right=point('RightEye','Right_Eye','eye_Rt','R_Eye')
  chin=point('LOD1_Chin_2_C','C_LipUnder','C_Llp','C_NoseBridge','C_Jaw','jaw','Jaw','Jaw_Jnt');nose=point('LOD1_NoseBase_C','C_Nose','C_NoseBridge','C_Nt')
  if left is not None and right is not None and chin is not None:
   eyes=(left+right)*.5;horizontal=(left-right).normalized();brow=point('C_EyeBrow','C_Ebs');up=brow-eyes if brow is not None else eyes-chin
   up=(up-horizontal*up.dot(horizontal)).normalized();normal=horizontal.cross(up).normalized()
   if nose is not None and (nose-eyes).dot(normal)<0:normal=-normal;horizontal=-horizontal
   if name[:5] in ('ch001','ch012','ch038','ch039','ch040'):
    normal=(normal*.5+up*.8660254).normalized();up=normal.cross(horizontal).normalized()
   camera=env['camera'];center=eyes-up*(left-right).length*.4
   camera.data.ortho_scale=max(.4,(left-right).length*4.5)
   camera.location=center+normal;camera.rotation_euler=Matrix((horizontal,up,normal)).transposed().to_euler()
   env['scene'].render.filepath=os.path.join(env['output_dir'],'face.png');bpy.ops.render.render(write_still=True)
  else:env['render']('face',(0,0,-1),'Y')
