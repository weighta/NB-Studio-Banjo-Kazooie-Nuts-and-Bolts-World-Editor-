"""coopB: duration-aware hybrid animtables for Character Select (fixes "frozen hanging from a ledge" and "the wrench is
buggy with movement" for every character but Banjo).

Why: a hybrid animtable keeps Banjo's records (flags, blends) and only swaps the animation (+ its animevents) for one of
the character's own. Many of Banjo's body states play a ONE-SHOT animation and move on when it ends (WrenchAroundInto is
an entityStateAnim2Stand: Banjo's 0.23 s wrench start; LedgeGrab plays 1.67 s before the hang). The research mapping
(coop/research/charsel/hybrid.py) picked by name only: Mumbo's wrench start became 'hit' (2.83 s) - measured in game:
WrenchAroundInto lasted 5.5 s and the spin 4 s, the player could not move meanwhile; his ledge grab became 'stand'
(13.33 s): hanging, frozen. Here each record whose substitute lasts far longer than Banjo's animation gets a shorter
animation of the character (same preference lists first, then a family list of quick motions).

python hybrid2.py <char> [--all]   prints the changed records (or every record)"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, os.path.join(HERE, '..', 'tools'))
import cb_anim, animtab, hybrid
from cb_dur import dur

_orig_mapping = hybrid.mapping

QUICK_HITS = ['passenger_hitright', 'passenger_hitleft', 'passenger_hitrear', 'passenger_hitfront', 'vehicle_hitright',
              'vehicle_hitleft', 'vehicle_hitrear', 'vehicle_hitfront', 'gunner_hitright', 'gunner_hitleft', 'hit', 'leapoutofway']
# family of a Banjo action -> what to try when the research choice is too long (first that fits wins)
FAMILIES = [
    (('wrench', 'flip'), QUICK_HITS + ['knockdown_hitfloor', 'land', 'run', 'walk']),
    (('ledgegrab',), ['drop', 'fall', 'jump', 'knockdown_loop', 'land', 'hit', 'leapoutofway']),
    (('kazooie', 'summonvehicle', 'saveblueprint'), ['crate_magic', 'leapoutofway', 'passenger_jumpout', 'vehicle_jumpout',
                                                     'passenger_jumpin', 'vehicle_jumpin', 'hit'] + QUICK_HITS),
    (('',), ['knockdown_hitfloor', 'land', 'leapoutofway', 'passenger_jumpout', 'vehicle_jumpout', 'hit'] + QUICK_HITS + ['run', 'walk']),
]
# one-shot actions left alone: the jump family follows the physics (in the air as long as it takes), moods / gunner /
# vehicle poses only play while seated (no movement to block)
KEEP = ('jump_', 'drop_hold', 'challenge_', 'vehicle_', 'gunner', 'standidle', 'upgrade_', 'edgelook')
# looping actions whose state still waits for the loop (the wrench spin ends at the end of a loop)
TIMED_LOOPS = ('wrencharound', 'wrenchwhirl')

def limit(bd): return max(bd * 2.0, bd + 0.6)

_dur_cache = {}
def D(aid):
    if aid not in _dur_cache: _dur_cache[aid] = dur(aid)
    return _dur_cache[aid]

def mapping(char, bundles=('234cec', '685374'), verbose=False, report=None):
    base = _orig_mapping(char, bundles, verbose)
    _, banjo = animtab.records('685374', 'aid_animtable_banjox_actor_banjo')
    b, t = hybrid.TABLE[char]
    _, own = animtab.records(b, 'aid_animtable_banjox_actor_' + t)
    N = animtab.names()
    avail = {}
    for nm, w in own:
        if w[0] in N and any(x.rstrip('s') in bundles for x in N[w[0]][1]): avail.setdefault(nm, w)
    out = []
    for (act, w), (act2, a, e, nm, p) in zip(banjo, base):
        assert act == act2
        oneshot = bool(w[6] & 0x01000000)
        bd, d = D(w[0]), D(a) if a else None
        timed = oneshot or act.startswith(TIMED_LOOPS)
        if bd is None or d is None or not timed or act.startswith(KEEP) or d <= limit(bd):
            out.append((act, a, e, nm, p)); continue
        fam = next(f for keys, f in FAMILIES if any(act.startswith(k) or k in act for k in keys))
        pick = None
        for cand in fam:
            if cand in avail and D(avail[cand][0]) is not None and D(avail[cand][0]) <= limit(bd): pick = cand; break
        if pick is None:   # nothing short enough: the shortest of the family
            fits = [(D(avail[c][0]), c) for c in fam if c in avail and D(avail[c][0]) is not None]
            if fits and min(fits)[0] < d: pick = min(fits)[1]
        if pick is None: out.append((act, a, e, nm, p)); continue
        ww = avail[pick]
        if report is not None: report.append((act, bd, p, d, pick, D(ww[0])))
        out.append((act, ww[0], ww[1], N[ww[0]][0], pick))
    return out

def install():
    """genplan / make plans use the duration-aware mapping from now on."""
    hybrid.mapping = mapping

if __name__ == '__main__':
    c = sys.argv[1]
    rep = []
    allb = tuple(sorted({b for x in cb_anim.names().values() for b in x[1]}))
    m = mapping(c, bundles=allb, report=rep)
    if '--all' in sys.argv:
        for act, a, e, nm, p in m: print(f'{act:34} -> {p:20} {D(a)}')
    for act, bd, p, d, pick, nd in rep: print(f'{act:34} banjo {bd:5.2f}s: {p:18} {d:5.2f}s -> {pick:20} {nd:5.2f}s')
