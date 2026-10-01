"""apply_winter.py <workspace dir> [--light] [--sky] [--dry]
Makes Showdown Town wintery in a workspace with NB.Cli, every change tracked in
the workspace history:
  --light  winter values in the 4 light-setup scripts (aid_script_banjox_lightsetup_showdowntown_{main,morning,
           afternoon,night}, bundle 234cec): ambient colour, sun colour, sun intensity, fog colour/start/end/max.
  --sky    midday uses the morning skydome (so the Spiral Mountain / title sky blue03 stays untouched), then the
           morning, evening, night and stars sky textures are replaced with the PNGs from winter_sky.py; the night and
           stars textures are shared with other worlds, so those other bundles are reverted to the original afterwards
           (town-only). Run --sky BEFORE other edits to those other bundles (the revert would undo them).
Without --light/--sky both are applied. --dry prints the commands only.
Field offsets (in the script asset's .data, big-endian): see REPORT.md."""
import os, subprocess, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..'))
CLI = os.path.join(ROOT, 'NBModTool', 'src', 'NB.Cli', 'bin', 'Release', 'net9.0-windows', 'NB.Cli.exe')
HERE = os.path.dirname(os.path.abspath(__file__))

# .data offsets inside aid_script_banjox_lightsetup_showdowntown_* (228 bytes each)
AMBIENT, SUN, INTENSITY = 0x08, 0x0C, 0x1C            # RGB0 bytes, RGB0 bytes, f32
FOG_START, FOG_END, FOG_MAX, FOG_COLOUR = 0x50, 0x54, 0x58, 0x68   # f32, f32, f32 (0..1 opacity), RGB0 bytes

WINTER = {   # phase light-setup: ambient, sun colour, intensity, fog colour, fog start, fog end, fog max
    'main':      dict(amb='8A94A8', sun='CCDBFA', inten=0.65, fog='CED8E6', start=25.0, end=400.0, fmax=0.60),   # midday
    'morning':   dict(amb='6E7387', sun='F5DBCC', inten=0.85, fog='BEC8D8', start=20.0, end=380.0, fmax=0.55),
    'afternoon': dict(amb='6B6B85', sun='FACC9E', inten=0.90, fog='D1C4C7', start=25.0, end=380.0, fmax=0.60),
    'night':     dict(amb='333D5C', sun='7387B8', inten=1.30, fog='3D4A6B', start=10.0, end=320.0, fmax=0.75),
}
ORIGINAL = {
    'main':      dict(amb='585858', sun='FFFFFF', inten=1.233, fog='CECFEC', start=80.156, end=901.87, fmax=0.33),
    'morning':   dict(amb='515151', sun='DCDBC0', inten=1.183, fog='C4C9DF', start=64.0, end=1540.7, fmax=0.394),
    'afternoon': dict(amb='585858', sun='F1C169', inten=1.033, fog='E0AD61', start=58.0, end=901.87, fmax=0.404),
    'night':     dict(amb='2E2E4B', sun='5E607B', inten=1.431, fog='282D3E', start=17.156, end=1240.1, fmax=0.615),
}
SKY = [   # (texture stem, png, bundles to revert afterwards = other worlds sharing the texture)
    ('aid_texture_banjox_shared_skydomes_showdown_morning_0x0f8c0fa5', 'sky_day.png', []),
    ('aid_texture_banjox_shared_skydomes_showdown_evening_0x065c0aa5', 'sky_dusk.png', []),
    ('aid_texture_banjox_shared_skydomes_showdown_night_0x001489a5', 'sky_night.png',
     ['181575', '455e80', '45a42d', '67b0d3', '68f1b8', '9cedca', 'c016b4', 'cc7c54', 'cd43ee', 'e37086']),
    ('aid_texture_banjox_shared_skydomes_showdown_stars_0x04b61f75', 'sky_stars.png', ['45a42d', '67b0d3', 'cc7c54']),
]

def run(args, dry):
    print('>', 'NB.Cli', ' '.join(args), flush=True)
    if not dry:
        r = subprocess.run([CLI, *args], capture_output=True, text=True)
        print((r.stdout + r.stderr).strip())
        if r.returncode: raise SystemExit(f'failed: {args}')

def light_cmds(ws, table):
    for phase, v in table.items():
        a = f'aid_script_banjox_lightsetup_showdowntown_{phase}'
        yield ['obj-set', ws, a, f'{AMBIENT:x}', f'h:{v["amb"]}00']
        yield ['obj-set', ws, a, f'{SUN:x}', f'h:{v["sun"]}00']
        yield ['obj-set', ws, a, f'{INTENSITY:x}', f'f:{v["inten"]}']
        yield ['obj-set', ws, a, f'{FOG_START:x}', f'f:{v["start"]}']
        yield ['obj-set', ws, a, f'{FOG_END:x}', f'f:{v["end"]}']
        yield ['obj-set', ws, a, f'{FOG_MAX:x}', f'f:{v["fmax"]}']
        yield ['obj-set', ws, a, f'{FOG_COLOUR:x}', f'h:{v["fog"]}00']

def sky_cmds(ws, sky_dir):
    # midday phase script op 0x2C (skydome model) at .data+0x1C: skydomes_afternoon (blue03 sky) -> skydomes_morning
    yield ['obj-set', ws, 'aid_script_banjox_showdowntown_midday', '1c', 'h:043AE90C']
    for stem, png, others in SKY:
        yield ['tex-replace', ws, stem, os.path.join(sky_dir, png)]
        for b in others:
            yield ['ws-revert', ws, f'Bundle\\4f\\{b}']
            yield ['ws-revert', ws, f'Bundle\\50\\{b}']

if __name__ == '__main__':
    ws = sys.argv[1]; dry = '--dry' in sys.argv
    both = '--light' not in sys.argv and '--sky' not in sys.argv
    if '--original-light' in sys.argv:
        for c in light_cmds(ws, ORIGINAL): run(c, dry)
        raise SystemExit
    if both or '--sky' in sys.argv:
        for c in sky_cmds(ws, os.path.join(HERE, 'sky_winter')): run(c, dry)
    if both or '--light' in sys.argv:
        for c in light_cmds(ws, WINTER): run(c, dry)
