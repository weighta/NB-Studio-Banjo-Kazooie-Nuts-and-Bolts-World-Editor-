#!/bin/sh
# Snowy Showdown Town: rebuilds the mod from the untouched game and exports snow/dist/SnowyShowdownTown.nbpatch.
#   sh snow/build.sh            (about 10 minutes; needs Python with numpy + Pillow, and NB.Cli built in Release)
# It is built the way a player would mod by hand, to exercise "Create Patch from a Modified Game Folder":
#   1. a workspace (Workspaces/snowy) gets the snow textures, falling snow and winter light/fog/sky;
#   2. its changed files are copied into a plain game folder (hard links for the rest) and default.xex is hand-patched
#      with the snow-follows-camera words;
#   3. NB.Cli patch-from-folder compares that folder with the retail game and builds the mod.
# Research: snow/research/fx/REPORT.md (falling snow), snow/research/light/REPORT.md (light, fog, sky).
set -e
cd "$(dirname "$0")/.."
# NB_CLI / NB_GAME override the command-line tool and the untouched game folder (the GitHub repositories keep src/ at the top)
CLI=${NB_CLI:-NBModTool/src/NB.Cli/bin/Release/net9.0-windows/NB.Cli.exe}; [ -f "$CLI" ] || CLI=src/NB.Cli/bin/Release/net9.0-windows/NB.Cli.exe
FRESH=${NB_GAME:-"Banjo Kazooie Nuts & Bolts (FRESH)"}
WS=${SNOW_WS:-Workspaces/snowy}
DIR="work/Snowy Showdown Town"

echo "== workspace $WS"
rm -rf "${WS:?}"
$CLI ws-create "$FRESH" "$WS"

echo "== textures: snow on ground/paving/roofs, frost, holly, Christmas bunting (town bundle 234cec only)"
mkdir -p snow/tex/orig snow/tex/top
[ -n "$(ls snow/tex/orig 2>/dev/null)" ] || $CLI tex-extract "$WS/game/Bundle/4f/234cec" snow/tex/orig
[ -n "$(ls snow/tex/top 2>/dev/null)" ] || $CLI tex-extract "$WS/game/Bundle/50/234cec" snow/tex/top
rm -rf snow/tex/out && python snow/make_snow.py
$CLI tex-batch "$WS" 234cec snow/tex/out | tail -1

echo "== winter light, fog and skies for the four times of day"
[ -f snow/research/light/sky_winter/sky_day.png ] || python snow/research/light/winter_sky.py
python snow/research/light/apply_winter.py "$WS" | tail -1
$CLI obj-set "$WS" aid_script_banjox_lightsetup_showdowntown_morning 58 f:0.45      # morning a little less white
$CLI obj-set "$WS" aid_script_banjox_lightsetup_showdowntown_morning 68 h:B4BECE00

echo "== falling snow: camera-following emitters (exe mod snow-follows-camera)"
python snow/research/fx/snowtown.py "$WS" --size1 0.55 --size2 1.0 --emit1 1500 --emit2 80 | grep emit

echo "== the hand-modded game folder"
rm -rf "${DIR:?}"
$CLI link-copy "$FRESH" "$DIR" Bundle/4f/234cec Bundle/50/234cec Bundle/4f/685374 default.xex
for f in Bundle/4f/234cec Bundle/50/234cec Bundle/4f/685374; do chmod u+w "$DIR/$f"; cp "$WS/game/$f" "$DIR/$f"; done
chmod u+w "$DIR/default.xex"
$CLI xex-poke "$FRESH/default.xex" "$DIR/default.xex" --mod snow-follows-camera

echo "== mod"
mkdir -p snow/dist
$CLI patch-from-folder "$DIR" snow/dist/SnowyShowdownTown.nbpatch --ref "$FRESH" --name "Snowy Showdown Town" --id snowy-showdown-town \
     --version 1.0 --author weighta --category map --tags "Showdown Town,Winter,Christmas,Weather,Textures" \
     --desc "Showdown Town under a winter sky: deep snow on the streets and roofs, frosted trees, holly garlands and red-and-green bunting, falling snow all over town, and cold winter light, fog and skies for morning, midday, dusk and a starry blue night." \
  | grep -E "^(delta|new|xexmods|exe mods|wrote)"
