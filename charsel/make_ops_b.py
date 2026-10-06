"""coopB: charsel/make_ops.py reading the plans of this folder -> ops.json here."""
import json, os
HERE = os.path.dirname(os.path.abspath(__file__)); R = HERE
ops = [['model-keep-joints', '685374', 'aid_model_banjox_actor_banjo', 'aid_model_banjox_actor_charsel_kazooie', 'PACK']]
for plan in ('plan_kazooie_common.json', 'plan_common.json', 'plan_town.json'):
    sets = {}; order = []
    for st in json.load(open(os.path.join(R, plan)))['steps']:
        if st['op'] == 'copy':
            if st.get('mode'): raise SystemExit('unsupported copy mode ' + st['mode'])
            order.append(['asset-copy', st['srcBundle'], st['src'], st['dstBundle'], st['name']])
        elif st['op'] == 'u32':
            k = (st['bundle'], st['asset'])
            if k not in sets: sets[k] = []; order.append(k)
            sets[k].append('%X=%s' % (st['off'], st['val']))
        else: raise SystemExit('unsupported op ' + st['op'])
    for o in order:
        ops.append(['asset-set', o[0], o[1]] + sets[o] if isinstance(o, tuple) else o)
json.dump(ops, open(os.path.join(HERE, 'ops.json'), 'w'), indent=0)
from collections import Counter
print(len(ops), 'ops', dict(Counter(o[0] for o in ops)))
