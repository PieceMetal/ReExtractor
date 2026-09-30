import json,re,collections,pathlib
root=pathlib.Path('artifacts/preview-fix/full-audit/RE4')
prefab=json.load(open(root/'player-layer-context.json',encoding='utf-8'))
mo=next(x['Fields'] for x in prefab['Items'] if x['Type']=='via.motion.Motion')
fsm=next(x['Fields'] for x in prefab['Items'] if x['Type']=='via.motion.MotionFsm2')
contexts={}
for i,l in enumerate(fsm['v11_Layer']):
 f=l['Fields'];name=f.get('MotionFsm2Resource','')
 if not name:continue
 target=f['TargetMotionLayerNo'];target=i if target<0 else target
 base=mo['Layer'][target]['Fields']
 contexts[name.lower()]=(target,base['BlendMode'],base['JointMaskID'])
print('CONTEXTS',contexts)
rows=[]
for line in open(root/'fsms.jsonl',encoding='utf-8'):
 doc=json.loads(line)
 if doc['Status']!='PARSED':continue
 key=doc['Path'].removeprefix('natives/stm/').rsplit('.',1)[0].lower()
 if key not in contexts:continue
 layer,blend,mask=contexts[key]
 for node in doc['Nodes']:
  for action in node['Actions']:
   if not isinstance(action,dict):continue
   fields=action.get('Fields',{})
   if fields.get('v0_Enabled') is False:continue
   def walk(obj,prefix=''):
    if not isinstance(obj,dict):return
    ff=obj.get('Fields',{})
    if '_BankID' in ff and '_MotionID' in ff:
     target=ff.get('_LayerIndex',layer)
     b=mo['Layer'][target]['Fields'] if isinstance(target,int) and 0<=target<len(mo['Layer']) else {}
     rows.append(dict(fsm=doc['Path'],node=node['Name'],action=action['Type'],field=prefix,bank=ff['_BankID'],motion=ff['_MotionID'],layer=target,blend=b.get('BlendMode'),mask=b.get('JointMaskID')))
    if '_BankID_Optional' in ff and '_MotionID_Optional' in ff and ff.get('_IsOtherLayerSet') is True:
     target=ff.get('_LayerIndex_Optional');b=mo['Layer'][target]['Fields'] if isinstance(target,int) and 0<=target<len(mo['Layer']) else {}
     rows.append(dict(fsm=doc['Path'],node=node['Name'],action=action['Type'],field=prefix+'.optional',bank=ff['_BankID_Optional'],motion=ff['_MotionID_Optional'],layer=target,blend=b.get('BlendMode'),mask=b.get('JointMaskID')))
    for k,v in ff.items():
     if isinstance(v,dict):walk(v,prefix+'.'+k)
   walk(action)
json.dump(rows,open(root/'player-usages.json','w',encoding='utf-8'),ensure_ascii=False,indent=2)
print('BANK 2000')
for id,rr in __import__('itertools').groupby(sorted([r for r in rows if r['bank']==2000],key=lambda r:r['motion']),key=lambda r:r['motion']):
 rr=list(rr);print(id,sorted(set((r['layer'],r['blend'],r['mask']) for r in rr)),sorted(set(r['action'].split('.')[-1] for r in rr)))
print('ERRORS',sum(json.loads(l)['Status']=='ERROR' for l in open(root/'fsms.jsonl',encoding='utf-8')))
