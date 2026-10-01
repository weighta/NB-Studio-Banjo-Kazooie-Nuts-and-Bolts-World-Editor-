"""shoot.py WS PHASE PREFIX [poses.json] : screenshots of Snowy Showdown Town in one time-of-day phase.
Boots test instance 9 (mp/c9, muted, offline) on Workspaces/WS with the workspace's exe mods plus coop-shared-time,
pause-opens-photos and photo-camera-unlimited; the new game's "go to Showdown Town" (test mode, startofgame .data+0x534)
is pointed at PHASE (1 morning, 2 midday, 3 afternoon, 4 night). Saves snow/shots/PREFIX_play*.png (gameplay) and
PREFIX_cam<k>.png (photo mode, camera at start + offset), then closes the instance."""
import json, os, struct, subprocess, sys, time, shutil
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
sys.path.insert(0, os.path.join(ROOT, 'mp')); sys.path.insert(0, os.path.join(ROOT, 'tools', 'xenia'))
CLI = os.path.join(ROOT, 'NBModTool', 'src', 'NB.Cli', 'bin', 'Release', 'net9.0-windows', 'NB.Cli.exe')
ws, phase, prefix = sys.argv[1], int(sys.argv[2]), sys.argv[3]
N = 9
OUT = os.path.join(ROOT, 'snow', 'shots')
PHASE_SCRIPT = {1: '1901D1B6', 2: '19E00470', 3: '196BD4A7', 4: '193F0052'}
# (dx, dy, dz, pitch, yaw, hold seconds) relative to the player's start; yaw 0 = the game's +z
POSES = json.load(open(sys.argv[4])) if len(sys.argv) > 4 else [
    [0, 8, 0, -0.18, 0.0, 3], [0, 8, 0, -0.15, 1.57, 3], [0, 8, 0, -0.15, 3.14, 3], [0, 8, 0, -0.15, -1.57, 3],
    [0, 45, 0, -0.30, 0.6, 3], [0, 45, 0, -0.25, 3.6, 3], [0, 2.5, 0, 0.25, 2.2, 3],
]

subprocess.run([CLI, 'obj-set', os.path.join(ROOT, 'Workspaces', ws), 'aid_script_banjox_spiralmountain_startofgame', '534', 'h:' + PHASE_SCRIPT[phase]],
               capture_output=True, text=True, check=True)
root = os.path.join(ROOT, 'mp', f'c{N}')
for _ in range(30):
    try:
        if os.path.isdir(root): shutil.rmtree(root)
        break
    except PermissionError: time.sleep(1)
os.makedirs(root)
mods = json.load(open(os.path.join(ROOT, 'Workspaces', ws, 'workspace.json'))).get('ExeMods', [])
print(subprocess.run([CLI, 'exe-mods', os.path.join(ROOT, 'Workspaces', ws), root, *mods, 'coop-shared-time', 'pause-opens-photos', 'photo-camera-unlimited'],
                     capture_output=True, text=True).stdout.strip().splitlines()[-1])
import coop
from nav_mp import pad
from xmem import XMem
p = coop.launch(N, ws)
print('pid', p.pid, flush=True)
os.environ['XENIA_PID'] = str(p.pid)
try:
    time.sleep(12)
    print('town:', coop.to_town(N, p.pid), flush=True)
    time.sleep(4)
    coop.capture(p.pid, os.path.join(OUT, f'{prefix}_play.png'))
    pad(20 + N, p.pid, 'RX=1:900, wait:1500')
    coop.capture(p.pid, os.path.join(OUT, f'{prefix}_play_b.png'))
    pad(20 + N, p.pid, 'START:150, wait:2000, A:150, wait:2500, BACK:150, wait:800')   # BACK: hide the photo-mode panel
    m = XMem(); cam = m.u32(m.u32(0x82FAC7AC) + 0x15B0) + 0x40
    pos0 = struct.unpack('>3f', m.read(cam + 0x60, 12))
    print('photo camera at', pos0, flush=True)
    for k, (dx, dy, dz, pitch, yaw, hold) in enumerate(POSES):
        t0 = time.time(); pos = struct.pack('>3f', pos0[0] + dx, pos0[1] + dy, pos0[2] + dz)
        while time.time() - t0 < hold:
            m.write(cam + 0x60, pos); m.write(cam + 0x130, pos)
            m.write(cam + 0xA0, struct.pack('>2f', pitch, yaw)); time.sleep(0.02)
        coop.capture(p.pid, os.path.join(OUT, f'{prefix}_cam{k}.png'))
    print('shots', prefix, flush=True)
finally:
    subprocess.run(['powershell', '-NoProfile', '-Command', f'Stop-Process -Id {p.pid} -Force'], capture_output=True)
