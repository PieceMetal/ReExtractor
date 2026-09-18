import bpy,os,sys,json,math
root=os.path.abspath('artifacts/onimusha-all-faces');rows=[]
def sample(path):
 bpy.ops.wm.read_factory_settings(use_empty=True);scene=bpy.context.scene;scene.render.fps=60
 if path.endswith('.fbx'):bpy.ops.import_scene.fbx(filepath=path,anim_offset=0)
 else:bpy.ops.import_scene.gltf(filepath=path)
 arm=next(o for o in bpy.data.objects if o.type=='ARMATURE');bones=arm.pose.bones
 left=next(n for n in ('LeftEye','Left_Eye','eye_Lt','L_Eye') if n in bones)
 right=next(n for n in ('RightEye','Right_Eye','eye_Rt','R_Eye') if n in bones)
 unit=(arm.matrix_world@bones[left].bone.head_local-arm.matrix_world@bones[right].bone.head_local).length
 assert unit>1e-5,(path,unit)
 end=max(a.frame_range[1] for a in bpy.data.actions);out={};print('SAMPLE',path,'end',end,'unit',unit,flush=True)
 for ratio in (0,.25,.5,.75,1):
  frame=end*ratio;scene.frame_set(int(frame),subframe=frame-int(frame));bpy.context.view_layer.update()
  positions={b.name:arm.matrix_world@b.head for b in bones}
  out[ratio]={n:[(p-positions[a]).length/unit for a in (left,right,'Head')] for n,p in positions.items() if not n.lower().startswith('bscontrol')}
 return out
for fn in sorted(os.listdir(root+'/route-main')):
 if not fn.endswith('.glb'):continue
 base=sample(root+'/route-main/'+fn);row={'file':fn,'frames':5,'routes':[]}
 for route,path in [('preview',root+'/'+fn),('legacy',root+'/route-legacy/'+fn),('fbx',root+'/route-fbx/'+fn[:-4]+'.fbx')]:
  other=sample(path);common=set(base[0])&set(other[0]);error=max(abs(x-y) for ratio in base for n in common for x,y in zip(base[ratio][n],other[ratio][n]))
  row['routes'].append({'route':route,'bones':len(common),'maxNormalizedDistanceError':error})
  if error>=.002:print('DIFFERENCES',fn,route,sorted(((max(abs(x-y) for ratio in base for x,y in zip(base[ratio][n],other[ratio][n])),n) for n in common),reverse=True)[:12],flush=True)
  assert math.isfinite(error) and error<.002,(fn,route,error)
 rows.append(row);print('ROUTES_PASS',fn,flush=True)
json.dump(rows,open(root+'/export-route-results.json','w'),indent=2)
