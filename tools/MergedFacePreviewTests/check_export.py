import bpy,glob,os,json
from mathutils.kdtree import KDTree
from mathutils import Vector,Matrix
root=os.path.abspath('artifacts/merged-face-proof')
def load(path):
 bpy.ops.wm.read_factory_settings(use_empty=True);s=bpy.context.scene;s.render.fps=60
 if path.endswith('.fbx'):bpy.ops.import_scene.fbx(filepath=path,anim_offset=0)
 else:bpy.ops.import_scene.gltf(filepath=path)
 arm=next(o for o in bpy.data.objects if o.type=='ARMATURE')
 def rest(n):
  key='__part1_'+n if '__part1_'+n in arm.data.bones else n
  return arm.matrix_world@arm.data.bones[key].head_local
 l,r,h=rest('LeftEye'),rest('RightEye'),rest('Head');origin=(l+r)*.5;unit=(l-r).length
 x=(l-r).normalized();y=(origin-h);y=(y-x*y.dot(x)).normalized();z=x.cross(y).normalized();basis=Matrix((x,y,z))
 print('BIND',path,'unit',unit,'origin',tuple(origin),flush=True)
 out=[]
 for t in (0,2.52,22.3,44.59):
  f=t*60;s.frame_set(int(f),subframe=f-int(f));dg=bpy.context.evaluated_depsgraph_get();pts=[]
  for o in bpy.data.objects:
   if o.type=='MESH' and any(mod.type=='ARMATURE' for mod in o.modifiers):
    e=o.evaluated_get(dg);m=e.to_mesh();pts.extend([tuple(basis@(e.matrix_world@v.co-origin)/unit) for v in m.vertices]);e.to_mesh_clear()
  if t==2.52: print('POSE',path,'bounds',[(min(p[i] for p in pts),max(p[i] for p in pts)) for i in range(3)],'arm',str(arm.matrix_world),'eye',str(arm.matrix_world@arm.pose.bones['__part1_LeftEye' if '__part1_LeftEye' in arm.pose.bones else 'LeftEye'].head),flush=True)
  out.append(pts)
 return out
solo=load(glob.glob(root+'/solo/*.glb')[0]);rows=[]
for route in ('glb','fbx'):
 other=load(glob.glob(root+'/'+route+'/*.'+route)[0]);errs=[]
 for a,b in zip(solo,other):
  kd=KDTree(len(b))
  for i,p in enumerate(b):kd.insert(p,i)
  kd.balance();errs.append(max(kd.find(p)[2] for p in a))
 rows.append({'route':route,'maxDistances':errs});print(rows[-1],flush=True)
 assert max(errs)<0.002,(route,errs)
json.dump(rows,open(root+'/export-check.json','w'),indent=2)
