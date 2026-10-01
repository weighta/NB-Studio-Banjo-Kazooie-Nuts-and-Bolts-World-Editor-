"""Scenery instances of a world bundle near a point. python nearby.py <bundle hex> x y z [radius] [count]"""
import sys, subprocess, math, os
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
NB = os.path.join(ROOT, 'NBModTool', 'src', 'NB.Cli', 'bin', 'Release', 'net9.0-windows', 'NB.Cli.exe')
b = sys.argv[1]; p = [float(v) for v in sys.argv[2:5]]; r = float(sys.argv[5]) if len(sys.argv) > 5 else 60; n = int(sys.argv[6]) if len(sys.argv) > 6 else 25
out = subprocess.run([NB, 'world-objects', os.path.join(ROOT, 'Workspaces', 'dev'), b], capture_output=True, text=True).stdout
rows = []
for l in out.splitlines():
    f = l.split()
    if len(f) >= 5 and f[0] == 'Scenery':
        try: q = [float(f[1]), float(f[2]), float(f[3])]
        except ValueError: continue
        d = math.dist(p, q)
        if d <= r: rows.append((d, f[4], q))
for d, name, q in sorted(rows)[:n]: print(f'{d:6.1f} {name:40s} {q}')
