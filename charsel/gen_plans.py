"""coopB: Character Select plans with the duration-aware hybrid animtables (hybrid2.py) -> plan_common.json / plan_town.json
here (same characters and order as coop/research/charsel's shipped plans; plan_kazooie_common.json copied unchanged)."""
import os, sys, json, shutil
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, os.path.join(HERE, '..', 'tools')); sys.path.insert(0, HERE)
import cb_anim, hybrid2, genplan
hybrid2.install()
R = os.path.join(cb_anim.ROOT, 'coop', 'research', 'charsel')
shutil.copy(os.path.join(R, 'plan_kazooie_common.json'), os.path.join(HERE, 'plan_kazooie_common.json'))
for out, bundle, chars in (('plan_common.json', '685374', ['mumbo', 'grunty', 'log']),
                           ('plan_town.json', '234cec', ['thomas', 'klungo', 'mrfit', 'humba', 'bottles', 'boggy', 'jinjoking', 'jolly', 'blubber', 'piddles', 'jinjo'])):
    steps = genplan.plan(bundle, chars)
    json.dump({'steps': steps}, open(os.path.join(HERE, out), 'w'), indent=0)
    old = json.load(open(os.path.join(R, out)))['steps']
    print(out, len(steps), 'steps (shipped', len(old), ')')
