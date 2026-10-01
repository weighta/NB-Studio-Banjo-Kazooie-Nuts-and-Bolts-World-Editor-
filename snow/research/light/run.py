"""run.py N WS PHASE OUT.png : boot co-op test instance N (mp/cN, muted, offline) on workspace WS with Showdown Town's
time of day forced to PHASE (1 morning, 2 midday, 3 afternoon, 4 night; 0 = game's choice) through the coop-shared-time
exe mod (mailbox 0x82FBCB30), drive to town, save a screenshot. The instance keeps running (pid in mp/cN/pid.txt)."""
import os, sys, shutil, subprocess, threading, time
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'mp')); sys.path.insert(0, os.path.join(ROOT, 'tools', 'xenia'))
CLI = os.path.join(ROOT, 'work', 'cli_snapshot', 'NB.Cli.exe')
n, ws, phase, out = int(sys.argv[1]), sys.argv[2], int(sys.argv[3]), sys.argv[4]
root = os.path.join(ROOT, 'mp', f'c{n}')
for _ in range(30):
    try:
        if os.path.isdir(root): shutil.rmtree(root)
        break
    except PermissionError: time.sleep(1)
os.makedirs(root)
print(subprocess.run([CLI, 'exe-mods', os.path.join(ROOT, 'Workspaces', ws), root, 'coop-shared-time', 'pause-opens-photos', 'photo-camera-unlimited'], capture_output=True, text=True).stdout.strip().splitlines()[-1])
import coop
p = coop.launch(n, ws)
print('pid', p.pid, flush=True)
os.environ['XENIA_PID'] = str(p.pid)
stop = False
def mailbox():
    import xmem
    m = None
    while not stop:
        try:
            if m is None: m = xmem.XMem()
            m.w32(0x82FBCB30, phase)
        except SystemExit: m = None
        except Exception: m = None
        time.sleep(0.5)
threading.Thread(target=mailbox, daemon=True).start()
time.sleep(12)
print('town:', coop.to_town(n, p.pid), flush=True)
coop.capture(p.pid, out)
stop = True
time.sleep(1)
from nav_mp import pad
pad(20 + n, p.pid, 'RX=1:900, wait:1500')
coop.capture(p.pid, out.replace('.png', '_b.png'))
pad(20 + n, p.pid, 'RY=-1:500, wait:1500')
coop.capture(p.pid, out.replace('.png', '_c.png'))
# photo mode (pause opens on Photos & Videos): look up at the sky in two directions
import struct
from xmem import XMem
pad(20 + n, p.pid, 'START:150, wait:2000, A:150, wait:2500')
m = XMem(); cam = m.u32(m.u32(0x82FAC7AC) + 0x15B0) + 0x40
print('photo state', m.u32(cam + 0x124), 'pos', struct.unpack('>3f', m.read(cam + 0x60, 12)), flush=True)
pos0 = struct.unpack('>3f', m.read(cam + 0x60, 12))
for k, (dy, pitch, yaw) in enumerate(((45.0, -0.12, 0.0), (45.0, -0.05, 3.14159), (5.0, 0.35, 1.5708))):
    t0 = time.time(); pos = struct.pack('>3f', pos0[0], pos0[1] + dy, pos0[2])
    while time.time() - t0 < 2.0:
        m.write(cam + 0x60, pos); m.write(cam + 0x130, pos)
        m.write(cam + 0xA0, struct.pack('>2f', pitch, yaw)); time.sleep(0.02)
    coop.capture(p.pid, out.replace('.png', f'_sky{k}.png'))
print('shot', out)
