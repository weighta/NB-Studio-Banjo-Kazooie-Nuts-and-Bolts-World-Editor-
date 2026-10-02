using System.Numerics;
using NB.Core.IO;
using NB.Core.Mods;

namespace NB.Core.Live;

/// <summary>
/// Character select in co-op (coop/research/charsel/REPORT.txt, sections 1 and 6).
/// LOCAL player: NB Multiplayer's character service writes the charsel mailbox (Characters.MailboxTown / MailboxAny);
/// co-op only sends the index (CoopState.Character).
/// REMOTE players: each puppet route k (1..3) has its own AI driver objparams in the town bundle
/// (aid_objparams_banjox_actor_coop_driverK, a copy of actor_banjoai told apart by u32 +0x104 = 0x3F000000 + k). Its
/// +0xC0 model / +0xD0 animtable are rewritten with the remote player's character, then the co-op puppets' marker set
/// is respawned (coop-onfoot command 4 RESPAWN), so the new drivers are that character - seated and on foot. A driver
/// actor holds no pointer to its objparams: a puppet's route is found from where its vehicle appears (the route's spawn
/// point) and remembered per driver avatar (drivers survive REBUILD, eject and seat).
/// </summary>
public sealed partial class CoopSync
{
    /// <summary>coop-onfoot with command 4 RESPAWN (word "cmplwi cr6,r0,4" of the regenerated cave).</summary>
    const uint RespawnWordAt = 0x82D62D40, RespawnWord = 0x2B000004;
    /// <summary>Spawn points of the three puppet routes (coop/town_route.txt nodes 0, 17, 34; tags 20, 21, 22).</summary>
    static readonly Vector3[] RouteSpawn = { new(134.1f, 16.6f, -56.1f), new(-18.9f, 14.1f, 452.4f), new(-223.9f, 18.3f, 159.9f) };

    bool? _charselMod, _respawnMod;
    /// <summary>actor_banjoai's own model / animtable (what the co-op driver copies start with).</summary>
    const uint BanjoAiModel = 0x04041D5E, BanjoAiAnim = 0x20041D5E;
    bool HasRespawn => _respawnMod ??= HasFootMod && _x.U32(RespawnWordAt) == RespawnWord;

    /// <summary>This game has the Character Select exe mod (charsel): its player can be another character.</summary>
    public bool HasCharselMod => _charselMod ??= _x.U32(Characters.Hook) == Characters.HookWord;

    // ---- remote players' puppets
    uint _charLevel;                                              // the town load the state below belongs to
    readonly uint[] _driverParams = new uint[3];                  // objparams of route k (0 = not found)
    readonly uint[] _origModel = new uint[3], _origAnim = new uint[3];
    readonly int[] _written = new int[3], _spawned = new int[3];  // character written into / shown by route k's driver
    readonly Dictionary<uint, int> _routeOfDriver = new();        // driver avatar -> route 0..2
    readonly HashSet<uint> _vehSeen = new();                      // puppet vehicles already looked at
    readonly Dictionary<int, bool> _charAvailable = new();       // character -> its objparams are loaded in this town
    Task<(uint[] Params, Dictionary<int, bool> Avail)>? _charScan;
    readonly HashSet<int> _charWanted = new();
    DateTime _lastRespawn = DateTime.MinValue, _charScanAt = DateTime.MinValue;
    bool _respawnPending;
    /// <summary>Diagnostics: respawns run, characters per route (written / shown), puppets whose route is known.</summary>
    public int Respawns { get; private set; }
    public int RespawnsForRoutes { get; private set; }
    public string CharacterStatus => $"respawns {Respawns} (routes {RespawnsForRoutes}) routes {string.Join("/", Enumerable.Range(0, 3).Select(k => _driverParams[k] == 0 ? "-" : $"{_written[k]}:{_spawned[k]}"))} mapped {_routeOfDriver.Count}{(_respawnPending ? " RESPAWNING" : "")}";

    /// <summary>Route 0..2 of a puppet (by its driver, or the Banjo who got out of it), -1 = unknown driver, -2 = no driver
    /// to tell (being ejected / seated / changed: wait).</summary>
    int RouteOf(uint veh, long id)
    {
        uint d = DriverOf(veh);
        if (d == 0 && _walkers.TryGetValue(id, out var w)) d = w.R;
        if (d == 0) return -2;
        return _routeOfDriver.TryGetValue(d, out var k) ? k : -1;
    }

    /// <summary>New puppet vehicles standing on a route's spawn point: their driver belongs to that route (after a town
    /// load, a RESPAWN or a local Change Vehicle). A rebuilt puppet appears where the old one was: its driver keeps its route.</summary>
    void MapRoutes()
    {
        foreach (var v in _vehicles)
        {
            if (_vehSeen.Contains(v) || !Alive(v)) continue;
            uint d = DriverOf(v);
            if (d == 0) continue;                                            // not seated yet: next tick
            _vehSeen.Add(v);
            var p = _x.V3(BodyOf(_x, v));
            int best = -1; float bd = 8f;
            for (int k = 0; k < 3; k++) { float dd = Vector3.Distance(p, RouteSpawn[k]); if (dd < bd) { bd = dd; best = k; } }
            // a new driver spawned from the route's objparams as they are now (our RESPAWN or a local Change Vehicle)
            if (best >= 0) { _routeOfDriver[d] = best; _spawned[best] = _written[best]; }
        }
        if (_vehSeen.Count > 64) _vehSeen.IntersectWith(_vehicles);
        if (_routeOfDriver.Count > 32) foreach (var d in _routeOfDriver.Keys.Where(d => !AvatarAlive(d)).ToList()) _routeOfDriver.Remove(d);
    }

    /// <summary>A town (re)load reloads the bundle: driver objparams are found again and are Banjo again.</summary>
    void CharactersLevel()
    {
        uint level = _x.Player;
        if (level == _charLevel) return;
        _charLevel = level;
        Array.Clear(_driverParams); Array.Clear(_written); Array.Clear(_spawned);
        _routeOfDriver.Clear(); _vehSeen.Clear(); _charAvailable.Clear(); _charScan = null; _charScanAt = DateTime.MinValue;
    }

    /// <summary>
    /// Background scan (it reads the whole heap): the three driver objparams (u32 0x3F000001..3 at +0x104, a model id at
    /// +0xC0 and an animtable id at +0xD0) and, for each wanted character, whether its actor objparams (the mod's
    /// actor_charsel_*: the same model + animtable pair) is loaded - a puppet is never given a model or animation table
    /// this game does not have (the level would wait for it forever).
    /// </summary>
    void StartCharScan()
    {
        if (_charScan != null || DateTime.UtcNow - _charScanAt < TimeSpan.FromSeconds(2)) return;
        _charScanAt = DateTime.UtcNow;
        var x = _x; var want = _charWanted.Where(c => c != 0 && !_charAvailable.ContainsKey(c)).ToList();
        var known = _driverParams.ToArray();
        _charScan = Task.Run(() =>
        {
            var found = known.ToArray();
            for (int k = 0; k < 3; k++)
            {
                if (found[k] != 0) continue;
                foreach (var a in x.FindU32(0x3F000001u + (uint)k))
                {
                    uint h = a - 0x104;
                    if (x.U32(h + 0xC0) >> 24 == 0x04 && x.U32(h + 0xD0) >> 24 == 0x20) { found[k] = h; break; }
                }
            }
            var avail = new Dictionary<int, bool>();
            foreach (var ci in want)
            {
                var c = Characters.All[ci];
                bool ok = false;
                foreach (var a in x.FindU32(c.AnimTable))
                {
                    uint h = a - 0xD0;
                    if (found.Contains(h)) continue;                     // our own driver copies do not count
                    if (x.U32(h + 0xC0) == c.Model) { ok = true; break; }
                }
                avail[ci] = ok;
            }
            return (found, avail);
        });
    }

    void TakeCharScan()
    {
        if (_charScan is not { IsCompleted: true } t) return;
        _charScan = null;
        if (t.Status != TaskStatus.RanToCompletion) return;
        for (int k = 0; k < 3; k++)
            if (_driverParams[k] == 0 && t.Result.Params[k] != 0)
            {
                _driverParams[k] = t.Result.Params[k];
                uint m = _x.U32(_driverParams[k] + 0xC0), a = _x.U32(_driverParams[k] + 0xD0);
                // already a character (written by an earlier run of the app in this town load)? Banjo = the AI Banjo's own ids
                var c = Characters.All.Skip(1).FirstOrDefault(cc => cc.Model == m && cc.AnimTable == a);
                if (c != null) { _written[k] = _spawned[k] = c.Index; _origModel[k] = BanjoAiModel; _origAnim[k] = BanjoAiAnim; }
                else { _origModel[k] = m; _origAnim[k] = a; }
            }
        foreach (var (c, ok) in t.Result.Avail) _charAvailable[c] = ok;
    }

    /// <summary>The character a remote player's puppet should show here: theirs if this game has it, else Banjo;
    /// -1 while that is not known yet (the check is a background scan: meanwhile the puppet keeps its look, instead of
    /// a respawn as Banjo followed by another one as the character).</summary>
    int ShownCharacter(CoopRemote r)
    {
        int c = r.State.Character;
        if (c <= 0 || c >= Characters.All.Count) return 0;
        _charWanted.Add(c);
        return _charAvailable.TryGetValue(c, out var ok) ? (ok ? c : 0) : -1;
    }

    /// <summary>Which free puppet a remote player gets: one whose driver already is their character, if any.</summary>
    uint PickPuppet(CoopRemote r)
    {
        if (_free.Count == 0) return 0;
        int want = ShownCharacter(r);
        foreach (var v in _free)
        {
            uint d = DriverOf(v);
            if (d != 0 && _routeOfDriver.TryGetValue(d, out var k) && _spawned[k] == want) return v;
        }
        return _free[0];
    }

    /// <summary>
    /// Per tick (in town): every shown remote's puppet driver must be the remote's character. A route whose driver shows
    /// another one gets its objparams rewritten; then one RESPAWN for all changes (at most every 2 s, never while a
    /// vehicle request is under way, and only after every walking Banjo is seated again - a respawn leaves ejected
    /// drivers behind). A puppet whose route is unknown (the app attached after the puppets had moved) is found by a
    /// respawn too.
    /// </summary>
    void CharactersTick(IReadOnlyDictionary<long, CoopRemote> remotes)
    {
        if (!HasRespawn) return;
        CharactersLevel();
        TakeCharScan();
        MapRoutes();
        bool need = false, mapping = false;
        foreach (var (id, veh) in _assigned)
        {
            if (!remotes.TryGetValue(id, out var r)) continue;
            int want = ShownCharacter(r);
            if (want < 0) { StartCharScan(); continue; }
            int k = RouteOf(veh, id);
            // route unknown (the app attached after the puppets had moved): a respawn finds it - not right after one
            // (the new puppets are mapped when the scan finds them on their spawn points)
            if (k == -2) continue;
            if (k < 0) { if (want != 0 && DateTime.UtcNow - _lastRespawn > TimeSpan.FromSeconds(6)) { need = true; mapping = true; } continue; }
            if (_driverParams[k] == 0) { if (want != 0) StartCharScan(); continue; }
            if (_spawned[k] == want) continue;
            if (_written[k] != want) WriteDriver(k, want);
            need = true;
        }
        if (_driverParams.Any(p => p == 0) && _charWanted.Count > 0) StartCharScan();
        if (!need || DateTime.UtcNow - _lastRespawn < TimeSpan.FromSeconds(2)) return;
        if (_op != null || _rebuilding.Count > 0 || FootBusy()) return;
        if (_walkers.Count > 0)
        {
            foreach (var (id, w) in _walkers.ToList()) FootEnd(id, _assigned.GetValueOrDefault(id));
            return;
        }
        // the new drivers are spawned from the objparams as written now
        PostFoot(FOp.Respawn, 0, 0, 0);
        if (mapping) RespawnsForRoutes++;
        _respawnPending = true;
        _lastRespawn = DateTime.UtcNow;
    }

    void WriteDriver(int k, int c)
    {
        uint h = _driverParams[k];
        if (h == 0) return;
        uint model = c == 0 ? _origModel[k] : Characters.All[c].Model, anim = c == 0 ? _origAnim[k] : Characters.All[c].AnimTable;
        if (model == 0 || anim == 0) return;
        var b = new byte[4];
        BE.W32(b, 0, model); _x.Write(h + 0xC0, b);
        BE.W32(b, 0, anim); _x.Write(h + 0xD0, b);
        _written[k] = c;
    }

    /// <summary>The puppets were respawned: every puppet (and driver) is new - like a local Change Vehicle.</summary>
    void OnRespawned(bool ok)
    {
        _respawnPending = false;
        if (!ok) return;
        Respawns++;
        for (int k = 0; k < 3; k++) _spawned[k] = _written[k];
        _assigned.Clear(); _free.Clear(); _vehicles.Clear(); _walkers.Clear();
        for (int i = 0; i < 4; i++) _x.Write(FSlots + 0x40 * (uint)i, new byte[4]);
        _routeOfDriver.Clear(); _vehSeen.Clear();
        _lastScan = DateTime.MinValue;
        LastVehicleEvent = "puppets respawned (characters)";
    }
}
