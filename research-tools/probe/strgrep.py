"""List printable strings in work/default.exe matching a regex. python strgrep.py <regex>"""
import re, sys, os
img = open(os.path.join(os.path.dirname(__file__), '..', '..', 'work', 'default.exe'), 'rb').read()
pat = re.compile(sys.argv[1].encode(), re.I)
for m in re.finditer(rb'[\x20-\x7e]{5,}', img):
    if pat.search(m.group()): print(f'{0x82000000 + m.start():#010x} {m.group().decode()}')
