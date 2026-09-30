import json,collections,pathlib
root=pathlib.Path('artifacts/preview-fix/full-audit/RE4')
records={}
for line in open(root/'motions.jsonl',encoding='utf-8'):
 d=json.loads(line)
 if d['Status']=='PARSED':records[d['Path']]=d
usage=json.load(open(root/'player-usages.json',encoding='utf-8'))
allowed={'chainsaw.BehaviorTreeAction_MFSM_AppPlayMotion','chainsaw.BehaviorTreeAction_MFSM_AppPlayMotionOptionalLayer','chainsaw.PlayerBehaviorTreeAction_MFSM_WeaponPutOutVariationMotion','chainsaw.PlayerBehaviorTreeAction_MFSM_HandOverWrite'}
byid=collections.defaultdict(list)
for u in usage:
 if u['action'] in allowed and u['motion']<2147483647:byid[(u['bank'],u['motion'])].append(u)
found=collections.defaultdict(list)
for line in open(root/'banks.jsonl',encoding='utf-8'):
 b=json.loads(line)
 if b['Status']!='PARSED' or '/animation/ch/cha0/motbank/' not in b['Path']:continue
 for e in b['Entries']:
  path='natives/stm/'+e['Path'].lower()+'.663'
  if path not in records:continue
  for m in records[path]['Motions']:
   if m['Type']!='MotFile':continue
   for u in byid[(e['BankID'],m['Id'])]:
    found[(path,m['Id'],m['Name'])].append(dict(u,bankFile=b['Path'],bankType=e['BankType']))
result=[]
for (path,id,name),uu in sorted(found.items()):
 blends={u['blend'] for u in uu};layers={u['layer'] for u in uu};masks={u['mask'] for u in uu}
 kind='Additive' if blends=={1} else ('Override' if blends=={0} and len(layers)==1 else 'ContextDependent')
 evidence=sorted(set(u['bankFile']+' -> '+u['fsm']+' | '+u['node']+' | layer='+str(u['layer'])+' blend='+str(u['blend']) for u in uu))
 result.append(dict(Path=path,Id=id,Name=name,Kind=kind,Evidence=evidence))
path=pathlib.Path('src/ReExtractor.Core/AnimationEvidence/re4-player-usage.json')
json.dump(result,open(path,'w',encoding='utf-8'),ensure_ascii=False,indent=2)
print('REGISTRY',len(result),'LISTS',len({r['Path'] for r in result}),collections.Counter(r['Kind'] for r in result))
print('NO_SUFFIX_ADDITIVE',[(r['Path'].split('/')[-1],r['Id'],r['Name']) for r in result if r['Kind']=='Additive' and '_add' not in r['Name'].lower()][:50])
print('CURRENT',collections.Counter(r['Kind'] for r in result if r['Path'].endswith('/cha0_wp4000h.motlist.663')))
