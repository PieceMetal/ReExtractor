"""Extract scoped synchronous upper-body pairs, including ancestor mask overrides.

This does not infer pairs from adjacent IDs, names, or cross-state motion uses.
Run from the repository root after the RE4 FSM/resource audit.
"""
import json
from pathlib import Path

root = Path('artifacts/preview-fix/full-audit/RE4')
source = 'natives/stm/_chainsaw/appsystem/character/ch0common/motion/fsm/ch0commonupperbody.motfsm2.43'
path = 'natives/stm/_chainsaw/animation/ch/cha0/motlist/cha0_wp4000h.motlist.663'
fsm = next(d for line in open(root/'fsms.jsonl', encoding='utf-8')
           if (d := json.loads(line))['Path'] == source)
inventory = next(d for line in open(root/'motions.jsonl', encoding='utf-8')
                 if (d := json.loads(line))['Path'] == path)
motions = {m['Id']: m for m in inventory['Motions'] if m['Type'] == 'MotFile'}
bank_path = 'natives/stm/_chainsaw/animation/ch/cha0/motbank/cha0_wp4000.motbank.3'
bank = next(d for line in open(root/'banks.jsonl', encoding='utf-8')
            if (d := json.loads(line))['Path'] == bank_path)
assert any(e['BankID'] == 2000 and 'natives/stm/'+e['Path'].lower()+'.663' == path for e in bank['Entries'])
prefab = json.load(open(root/'player-layer-context.json', encoding='utf-8'))
motion = next(i['Fields'] for i in prefab['Items'] if i['Type'] == 'via.motion.Motion')
layers = motion['Layer']
assert not motion['EnableLayerUpdateOrder']
assert layers[3]['Fields']['BlendMode'] == 1 and layers[4]['Fields']['BlendMode'] == 0
by_id = {n['Id']: n for n in fsm['Nodes']}
records = {}

def enabled(node):
    return [a for a in node['Actions'] if isinstance(a, dict) and a['Fields'].get('v0_Enabled') is not False]

def chain(node):
    result = []
    while node:
        assert node not in result, 'Cyclic FSM parent chain'
        result.append(node)
        node = by_id.get(node['Parent'])
    return result[::-1]

def add_pair(node, base, add):
    if base not in motions or add not in motions:
        return
    masks = {i: layers[i]['Fields']['JointMaskID'] for i in (3, 4)}
    ancestors = chain(node)
    mask_evidence = []
    for parent in ancestors:
        for action in enabled(parent):
            typ, fields = action['Type'].split('_')[-1], action['Fields']
            if typ == 'SetJointMask':
                masks[4] = fields['_JointMaskID']
                mask_evidence.append(dict(Node=parent['Name'], Layer=4, Mask=masks[4]))
            elif typ == 'SetJointMaskOptionalLayer' and fields['_LayerID'] in masks:
                masks[fields['_LayerID']] = fields['_JointMaskID']
                mask_evidence.append(dict(Node=parent['Name'], Layer=fields['_LayerID'], Mask=fields['_JointMaskID']))
    key = (path, base, add)
    item = dict(Path=path, BaseId=base, AdditiveId=add, BaseName=motions[base]['Name'],
                AdditiveName=motions[add]['Name'], BaseMaskId=masks[4], AdditiveMaskId=masks[3],
                Evidence=[])
    if key in records:
        assert (records[key]['BaseMaskId'], records[key]['AdditiveMaskId']) == (masks[4], masks[3]), 'Ambiguous masks'
    else:
        records[key] = item
    records[key]['Evidence'].append(dict(BankFile=bank_path, BankId=2000, Fsm=source, Node=node['Name'], NodeId=node['Id'],
        Parents=[p['Name'] for p in ancestors], MaskOverrides=mask_evidence))

def synchronized(fields):
    return (fields.get('_BankID') == 2000 and fields.get('_Speed') == 1
            and fields.get('_StartFrame') == 0 and not fields.get('_Mirror')
            and not fields.get('_RandamizeStartFrame'))

for node in fsm['Nodes']:
    actions = enabled(node)
    bases = [a['Fields'] for a in actions if a['Type'] == 'chainsaw.BehaviorTreeAction_MFSM_AppPlayMotion']
    adds = [a['Fields'] for a in actions if a['Type'] == 'chainsaw.BehaviorTreeAction_MFSM_AppPlayMotionOptionalLayer'
            and a['Fields'].get('_LayerIndex') == 3]
    if len(bases) == len(adds) == 1 and synchronized(bases[0]) and synchronized(adds[0]):
        add_pair(node, bases[0]['_MotionID'], adds[0]['_MotionID'])
    for action in actions:
        if action['Type'] != 'chainsaw.PlayerBehaviorTreeAction_MFSM_WeaponPutOutVariationMotion':
            continue
        for value in action['Fields'].values():
            if not isinstance(value, dict):
                continue
            f = value.get('Fields', {})
            if f.get('_IsOtherLayerSet') and f.get('_LayerIndex_Optional') == 3 and f.get('_BankID') == f.get('_BankID_Optional') == 2000:
                add_pair(node, f['_MotionID'], f['_MotionID_Optional'])

result = sorted(records.values(), key=lambda r: (r['Path'], r['BaseId'], r['AdditiveId']))
dest = Path('src/ReExtractor.Core/AnimationEvidence/re4-preview-pairs.json')
dest.write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
review = []
for id, motion in motions.items():
    review.append(dict(Id=id, Name=motion['Name'], SourceIndex=motion['Index'],
        BasePairs=[p['AdditiveId'] for p in result if p['BaseId'] == id],
        PairedWith=[p['BaseId'] for p in result if p['AdditiveId'] == id]))
(root/'wp4000h-pair-review.json').write_text(json.dumps(review, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
for row in result:
    print(row['BaseId'], '+', row['AdditiveId'], 'masks', row['BaseMaskId'], row['AdditiveMaskId'])
print('PAIRS', len(result), 'MOTIONS_REVIEWED', len(review))
