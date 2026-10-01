#!/bin/sh
# Showdown Town co-op edition: rebuilds Workspaces/coop from the untouched game and exports coop/dist/ShowdownTownCoop.nbpatch.
#   sh coop/build.sh
# The edition is the normal single-player game plus:
#   * three "puppet" trolleys (AI vehicles with an AI Banjo at the wheel, parked on the town's road loop); NB Multiplayer takes one
#     over for every other player in the room and steers it to where that player is (docs: Showdown Town co-op)
#   * executable mods: destructible town vehicles, Change/Build Vehicle in town, AI vehicles restart after Change Vehicle,
#     and the Change Vehicle NPC pathfinding crash fix
set -e
cd "$(dirname "$0")/.."
CLI=NBModTool/src/NB.Cli/bin/Release/net9.0-windows/NB.Cli.exe
WS=${COOP_WS:-Workspaces/coop}
FRESH="Banjo Kazooie Nuts & Bolts (FRESH)"
PUPPET=aid_vehicle_banjox_coop_puppet

echo "== workspace $WS (fresh copy of the game)"
rm -rf "$WS"
$CLI ws-create "$FRESH" "$WS"

echo "== executable mods"
python - "$WS/workspace.json" <<'EOF'
import json, sys
p = sys.argv[1]; m = json.load(open(p))
m['ExeMods'] = ['town-vehicles-normal-rules', 'change-vehicle-town', 'town-ai-restart-on-change-vehicle', 'town-npc-path-guard', 'coop-remote-damage']
json.dump(m, open(p, 'w'), indent=2)
print('  ' + ', '.join(m['ExeMods']))
EOF

echo "== puppet driver and strategy (the title screen's AI Banjo and Jogger strategy, copied into the town)"
$CLI objparams-copy $WS 757c4b actorstrategy_locococo 234cec actorstrategy_showdowntown_mrfit actorstrategy_coop_puppet
$CLI objparams-set $WS aid_objparams_banjox_actorstrategy_coop_puppet 2AC 00000000            # cruising speed 0: puppets only move when NB Multiplayer steers them (an AI throttle fought the sync: ~10 u overshoot)
$CLI objparams-copy $WS 757c4b actor_banjoai 234cec actor_npc_thomas

echo "== puppet vehicle blueprint, kept in the town's own bundle (234cec) so the edition never touches the shared bundle"
echo "   685374 that vehicle-part mods change: co-op then combines with them in NB Multiplayer"
$CLI asset-copy $WS 685374 aid_vehicle_banjox_general_golfcartcomplete 234cec $PUPPET

echo "== three puppet trolleys on the town's road loop (101-node AI path of the town, every 2nd node)"
ROUTE="$(cat coop/town_route.txt)"
AI="--driver actor_banjoai --keep-strategy --mask 00010000 --vehicle-template aid_vehicle_banjox_general_golfcartcomplete"
$CLI ai-route $WS 234cec aid_marker_banjox_showdowntown_main --points "$ROUTE" --width 12 --tag 20 --spawn-node 0 \
     --vehicle $PUPPET --strategy actorstrategy_coop_puppet $AI
$CLI ai-route $WS 234cec aid_marker_banjox_showdowntown_main --points "$ROUTE" --width 12 --tag 21 --spawn-node 17 \
     --vehicle $PUPPET --strategy actorstrategy_coop_puppet $AI
$CLI ai-route $WS 234cec aid_marker_banjox_showdowntown_main --points "$ROUTE" --width 12 --tag 22 --spawn-node 34 \
     --vehicle $PUPPET --strategy actorstrategy_coop_puppet $AI
# spawned by a command after the town's trolley command (op 0x8D at 0x250): spawned at level load, an AI car would be taken
# as the player's vehicle (ULTRA U29/U31)
$CLI script-insert $WS 234cec aid_script_banjox_showdowntown_general 250 0000000C 00000065 00010000

if [ -n "$COOP_TESTMODE" ]; then   # test builds only: new games start in Showdown Town (never in the published patch)
  echo "== TEST MODE: skip intro"
  $CLI skip-intro $WS --no-town-intro --preset-town
fi

BP=$(python -c "import zlib; print('%08x' % ((zlib.crc32(b'banjox_coop_puppet') ^ 0xFFFFFFFF) & 0xFFFFFF))")
echo "== patch (puppet blueprint id $BP)"
mkdir -p coop/dist
$CLI patch-build $WS ${COOP_OUT:-coop/dist/ShowdownTownCoop.nbpatch} --name "Showdown Town Co-op" --author weighta \
     --version 1.1 --category coop --multiplayer coop --tags "Showdown Town" \
     --desc "Play the single-player game together in NB Multiplayer: other players drive through your Showdown Town. Town vehicles are destructible and can be changed in town." \
     --extra mode=coop --extra puppetBlueprint=$BP --extra parkSpot=0,0,0
