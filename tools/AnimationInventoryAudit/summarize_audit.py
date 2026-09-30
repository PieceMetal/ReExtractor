import json, pathlib, collections, re
root=pathlib.Path('artifacts/preview-fix/full-audit')
registry=json.load(open('src/ReExtractor.Core/AnimationEvidence/re4-player-usage.json',encoding='utf-8'))
reg={(x['Path'].lower(),x['Id'],x['Name'].lower()):x for x in registry}
summary=[]
for name in ('RE4','SF6','MHWs','OniWS'):
 p=root/name
 inv=json.load(open(p/'inventory.json',encoding='utf-8'))
 records={}
 for line in open(p/'motions.jsonl',encoding='utf-8'):
  try:d=json.loads(line)
  except json.JSONDecodeError:continue
  records[d['Path']]=d
 rows=list(records.values());stats=collections.Counter(x['Status'] for x in rows)
 motions=[m for r in rows for m in r.get('Motions',[]) if m.get('Type')=='MotFile']
 errors=[{'Path':r['Path'],'Error':r.get('Error')} for r in rows if r['Status']=='ERROR']
 unknown=0;usage=collections.Counter();current=[]
 for r in rows:
  for m in r.get('Motions',[]):
   if m.get('Type')!='MotFile':continue
   hit=reg.get((r['Path'].lower(),m['Id'],(m.get('Name') or '').lower()))
   kind=hit['Kind'] if hit else ('AdditiveHint' if re.search(r'_add(?:_|$)',m.get('Name') or '',re.I) else 'Unknown')
   if r['Path'].endswith('/cha0_wp4000h.motlist.663') and m['Id'] in (500,501,502):kind='Additive'
   usage[kind]+=1
   if r['Path'].endswith('/cha0_wp4000h.motlist.663'):
    current.append({'Id':m['Id'],'Name':m['Name'],'SourceIndex':m['Index'],'Frames':m['Frames'],'Tracks':len(m['Tracks'] or []),'Usage':kind,'Evidence':hit['Evidence'] if hit else []})
 status={'Game':name,'ResolvedLists':len(inv['Lists']),'VisitedLists':len(rows),'States':dict(stats),'EmbeddedMotions':len(motions),'ExternalOrOtherEntries':sum(len(r.get('Motions',[])) for r in rows)-len(motions),'ListsWithBaseReference':sum(bool(r.get('BasePath')) for r in rows),'Usage':dict(usage),'BankFiles':len(inv['Banks']),'FsmFiles':len(inv['Fsms']),'Errors':errors,'UnresolvedPakHashesNotCovered':True}
 summary.append(status)
 if current:json.dump(current,open(p/'wp4000h-all-76-review.json','w',encoding='utf-8'),ensure_ascii=False,indent=2)
 print(name,len(rows),'/',len(inv['Lists']),dict(stats),'motions',len(motions),'usage',dict(usage),'refs',status['ListsWithBaseReference'])
 if len(rows)!=len(inv['Lists']):print('INCOMPLETE')
fsms=[json.loads(l) for l in open(root/'RE4/fsms.jsonl',encoding='utf-8')]
json.dump({'Games':summary,'RE4FSM':dict(collections.Counter(x['Status'] for x in fsms)),'RE4FSMErrors':[x for x in fsms if x['Status']=='ERROR'],'RegistryEntries':len(registry),'RegistryLists':len({r['Path'] for r in registry}),'RegistryCounts':dict(collections.Counter(r['Kind'] for r in registry)),'NoSuffixAdditiveCount':sum(r['Kind']=='Additive' and '_add' not in r['Name'].lower() for r in registry),'SF6FullDecodeResourceLimit':'natives/stm/product/animation/bakeasset/2mesh/npc8091_01_om15000_009_2m.motlist.653'},open(root/'summary.json','w',encoding='utf-8'),ensure_ascii=False,indent=2)
print('CURRENT',len(json.load(open(root/'RE4/wp4000h-all-76-review.json',encoding='utf-8'))),'FSM',collections.Counter(x['Status'] for x in fsms))
