#!/bin/sh
# Character Select: builds charsel/dist/CharacterSelect.nbpatch from the untouched game.
#   sh charsel/build.sh
# The mod = world edits (charsel/ops.json, made by make_ops.py from the research plans): 17 playable characters as clones
# of Banjo's player actor with the character's model and a hybrid animation table (Kazooie, Mumbo, Grunty, L.O.G. in the
# common bundle = every level; Thomas, Klungo, Mr. Fit, Humba, Bottles, Boggy, King Jingaling, Jolly, Blubber, Piddles,
# Jinjo in Showdown Town), plus the exe mod "charsel" (NB Multiplayer writes the chosen character's actor id into its
# mailbox; it applies at the next player spawn). Tuxedo and Robot Banjo are the game's own actors.
set -e
cd "$(dirname "$0")/.."
# NB_CLI / NB_GAME override the command-line tool and the untouched game folder (the GitHub repositories keep src/ at the top)
CLI=${NB_CLI:-NBModTool/src/NB.Cli/bin/Release/net9.0-windows/NB.Cli.exe}; [ -f "$CLI" ] || CLI=src/NB.Cli/bin/Release/net9.0-windows/NB.Cli.exe
WS=${CHARSEL_WS:-Workspaces/charselbuild}
FRESH=${NB_GAME:-"Banjo Kazooie Nuts & Bolts (FRESH)"}
python charsel/make_ops.py
if [ ! -f "$WS/workspace.json" ]; then echo "== workspace $WS (fresh copy of the game)"; $CLI ws-create "$FRESH" "$WS"; fi
python - "$WS/workspace.json" <<'EOF'
import json, sys
p = sys.argv[1]; m = json.load(open(p)); m['ExeMods'] = ['charsel']; json.dump(m, open(p, 'w'), indent=2)
EOF
mkdir -p charsel/dist
$CLI patch-build $WS ${CHARSEL_OUT:-charsel/dist/CharacterSelect.nbpatch} --name "Character Select" --author weighta \
     --version 1.0 --category gameplay --ops charsel/ops.json --multiplayer world --tags "Characters,Showdown Town" \
     --desc "Play as someone else: Tuxedo or Robot Banjo, Kazooie, Mumbo, Grunty or L.O.G. everywhere, and in Showdown Town also Trophy Thomas, Klungo, Mr. Fit, Humba Wumba, Bottles, Boggy, King Jingaling, Jolly, Captain Blubber, Piddles or a Jinjo. Choose your character in NB Multiplayer (Play page); in co-op the other players see you as that character." \
     --extra mode=charsel
