"""coopB: regenerate the research plans with the unmodified rules (asset names from Workspaces/mp) and compare with the
shipped ones - proves the generator reproduces them before any rule change."""
import os, sys, json
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, os.path.join(HERE, '..', 'tools'))
import cb_anim, genplan
R = os.path.join(cb_anim.ROOT, 'coop', 'research', 'charsel')
for out, bundle, chars in (('plan_common.json', '685374', ['mumbo', 'grunty', 'log']),
                           ('plan_town.json', '234cec', ['thomas', 'klungo', 'mrfit', 'humba', 'bottles', 'boggy', 'jinjoking', 'jolly', 'blubber', 'piddles', 'jinjo'])):
    new = genplan.plan(bundle, chars)
    old = json.load(open(os.path.join(R, out)))['steps']
    print(out, 'same' if new == old else f'DIFFERENT ({len(new)} vs {len(old)} steps)')
    if new != old:
        for i, (a, b) in enumerate(zip(new, old)):
            if a != b: print('  first diff at', i, a, b); break
