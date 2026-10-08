namespace NB.Core.Vehicles;

/// <summary>
/// An editable vehicle: the parts with integer cells (any value while editing; written as bytes 0..255 after
/// shifting the vehicle so its lowest cell is 0 when needed), selection and undo/redo by snapshots. UI-independent.
/// </summary>
public sealed class VehicleDocument
{
    public sealed class Part
    {
        public int X, Y, Z;
        /// <summary>The record (part id, paint, settings, rotation bits …); its cell bytes are written on export.</summary>
        public BlueprintBlock B = new();
        public Part Clone() => new() { X = X, Y = Y, Z = Z, B = B.Clone() };
        public int Orientation => B.Orientation;
    }

    public Blueprint Source = Blueprint.New("NEW BLUEPRINT");
    public List<Part> Parts = new();
    public readonly HashSet<Part> Selection = new();
    public string Name { get => _name; set { _name = value ?? ""; } }
    string _name = "NEW BLUEPRINT";
    public bool Dirty;

    public event Action? Changed;

    public static VehicleDocument From(Blueprint bp)
    {
        var d = new VehicleDocument { Source = bp.Clone(), _name = bp.Name };
        foreach (var b in bp.Blocks) d.Parts.Add(new Part { X = b.X, Y = b.Y, Z = b.Z, B = b.Clone() });
        return d;
    }

    /// <summary>
    /// The blueprint: the source's header (stats, buttons, name field) with the parts, the count, the weight and the
    /// button part types updated (see <see cref="Blueprint.UpdateHeader"/>). Cells are shifted when a part lies below 0;
    /// more than 256 cells on an axis cannot be stored.
    /// </summary>
    public Blueprint ToBlueprint(PartCatalog? catalog = null, bool unchangedKeepsSource = true)
    {
        if (unchangedKeepsSource && !Dirty && Name == Source.Name) return Source.Clone();
        var bp = Source.Clone();
        bp.Blocks.Clear();
        int sx = Parts.Count == 0 ? 0 : Math.Min(0, Parts.Min(p => p.X)), sy = Parts.Count == 0 ? 0 : Math.Min(0, Parts.Min(p => p.Y)), sz = Parts.Count == 0 ? 0 : Math.Min(0, Parts.Min(p => p.Z));
        foreach (var p in Parts)
        {
            int x = p.X - sx, y = p.Y - sy, z = p.Z - sz;
            if (x > 255 || y > 255 || z > 255) throw new InvalidDataException($"the vehicle spans more than 256 cells (part at {p.X},{p.Y},{p.Z}); blueprints store cells as bytes");
            var b = p.B.Clone();
            b.X = (byte)x; b.Y = (byte)y; b.Z = (byte)z;
            if (catalog?[b.Part] is { } info) b.Category = (byte)(info.Category + 1);
            bp.Blocks.Add(b);
        }
        bp.Normalize();
        if (Name != Source.Name) bp.Name = Name;
        if (catalog != null) bp.UpdateHeader(catalog.WeightOf);
        else bp.UpdateHeader(_ => null);
        bp.Trailing = Array.Empty<byte>();
        return bp;
    }

    // ------------------------------------------------------------------ undo / redo

    sealed record Snapshot(List<Part> Parts, string Name, string Label);
    readonly List<Snapshot> _undo = new(), _redo = new();
    public int UndoLimit = 200;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    Snapshot Take(string label) => new(Parts.Select(p => p.Clone()).ToList(), Name, label);

    /// <summary>Records the current state before an edit (one undo step).</summary>
    public void Begin(string label)
    {
        _undo.Add(Take(label));
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Ends an edit: marks the document changed and notifies.</summary>
    public void Commit() { Dirty = true; Changed?.Invoke(); }

    public bool Undo() => Swap(_undo, _redo);
    public bool Redo() => Swap(_redo, _undo);

    bool Swap(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0) return false;
        var s = from[^1]; from.RemoveAt(from.Count - 1);
        to.Add(Take(s.Label));
        // keep the selection: the same list positions when the part count did not change (moves, rotations, paint),
        // else the parts still at the same cell
        var selIdx = Parts.Select((p, i) => (p, i)).Where(x => Selection.Contains(x.p)).Select(x => x.i).ToList();
        var sel = Selection.Select(p => (p.X, p.Y, p.Z, p.B.Part)).ToHashSet();
        int before = Parts.Count;
        Parts = s.Parts.Select(p => p.Clone()).ToList();
        _name = s.Name;
        Selection.Clear();
        if (Parts.Count == before) foreach (int i in selIdx) Selection.Add(Parts[i]);
        else foreach (var p in Parts) if (sel.Contains((p.X, p.Y, p.Z, p.B.Part))) Selection.Add(p);
        Dirty = true;
        Changed?.Invoke();
        return true;
    }

    public void ClearHistory() { _undo.Clear(); _redo.Clear(); }

    // ------------------------------------------------------------------ geometry

    /// <summary>Cells a part fills: the cells behind its footprint faces (attach data; an L-shaped engine fills 3 cells of its
    /// 2×2 bounds), rotated by its orientation, at its cell; the part's own cell when it has no attach data.</summary>
    public static IEnumerable<(int X, int Y, int Z)> Cells(Part p, PartInfo? info) => VehicleConnectivity.FaceCells(p, info);

    /// <summary>Box (min cell, max cell) of a part's footprint.</summary>
    public static ((int X, int Y, int Z) Min, (int X, int Y, int Z) Max) Box(Part p, PartInfo? info)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
        foreach (var (x, y, z) in Cells(p, info)) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); z0 = Math.Min(z0, z); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); z1 = Math.Max(z1, z); }
        return ((x0, y0, z0), (x1, y1, z1));
    }

    /// <summary>Which part occupies each cell (the first one when parts overlap).</summary>
    public Dictionary<(int, int, int), Part> Occupancy(PartCatalog? cat)
    {
        var d = new Dictionary<(int, int, int), Part>();
        foreach (var p in Parts) foreach (var c in Cells(p, cat?[p.B.Part])) d.TryAdd(c, p);
        return d;
    }

    // ------------------------------------------------------------------ validation

    public sealed record Issue(string Text, Part? Part, bool Error);

    /// <summary>
    /// Problems the game would have: unknown parts (Disc Read Error when the blueprint list opens), overlapping parts,
    /// more than 250 parts (stock limit; the vehicle-part-limit mod raises it), more than 19 cells (stock garage) or 31
    /// (garage-build-area mod) per axis, parts the spawner skips (group byte set), cells beyond a byte.
    /// </summary>
    public List<Issue> Validate(PartCatalog? cat, IEnumerable<uint>? loadableBundles = null)
    {
        var res = new List<Issue>();
        if (Parts.Count == 0) { res.Add(new("The vehicle has no parts.", null, true)); return res; }
        if (cat != null)
        {
            foreach (var g in Parts.Where(p => cat[p.B.Part] == null).GroupBy(p => p.B.Part))
                res.Add(new($"Unknown part 0x{g.Key:X8} ({g.Count()}×): not in this workspace — the game shows a Disc Read Error for blueprints with unknown parts.", g.First(), true));
            if (!Parts.Any(p => cat[p.B.Part]?.Class == "objDefId_vehicleBlockSeat"))
                res.Add(new("No seat: nobody can drive this vehicle.", null, false));
            else if (!Parts.Any(p => cat[p.B.Part] is { } i && i.Class == "objDefId_vehicleBlockSeat" && (i.Key.StartsWith("seats_") || i.Variant.EndsWith("ai"))))
                res.Add(new("Only passenger seats: neither the player (seats_*) nor an AI driver (secondaryseats_small / _large) can drive.", null, false));
            if (loadableBundles != null)
            {
                var set = loadableBundles.ToHashSet();
                foreach (var g in Parts.Where(p => cat[p.B.Part] is { } i && !cat.HoldersOf(i.Id).Any(set.Contains)).GroupBy(p => p.B.Part))
                    res.Add(new($"{cat[g.Key]} is not loaded in this world (its record is not in the act / world / common bundles).", g.First(), true));
            }
            // attachment like the garage: parts not joined to the driver's piece get its hazard triangle (they fall off)
            var conn = new VehicleConnectivity(Parts, cat);
            if (conn.Floating.Count > 0)
            {
                var names = conn.Floating.GroupBy(p => cat[p.B.Part]?.Name ?? $"0x{p.B.Part:X8}").Select(g => g.Count() > 1 ? $"{g.Count()}× {g.Key}" : g.Key);
                res.Add(new($"Hazard: {conn.Floating.Count} part(s) not attached to the vehicle (they fall off when it is built): {string.Join(", ", names.Take(8))}{(names.Count() > 8 ? " …" : "")}. Turn them so an attachable face meets the vehicle.", conn.Floating.First(), false));
            }
            if (conn.Overlapping.Count > 0)
                res.Add(new($"{conn.Overlapping.Count} parts fill the same cells as another part (blocked in the garage).", conn.Overlapping.First(), false));
        }
        if (Parts.Count > 250) res.Add(new($"{Parts.Count} parts: the stock game stops at 250 (Mods: \"Vehicle part limit 400\"; blueprint previews draw the first 250).", null, false));
        var minX = Parts.Min(p => p.X); var maxX = Parts.Max(p => p.X);
        var minY = Parts.Min(p => p.Y); var maxY = Parts.Max(p => p.Y);
        var minZ = Parts.Min(p => p.Z); var maxZ = Parts.Max(p => p.Z);
        int ext = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)) + 1;
        if (ext > 256) res.Add(new($"The vehicle spans {ext} cells: blueprints store cells as bytes (at most 256).", null, true));
        else if (ext > 31) res.Add(new($"The vehicle spans {ext} cells: the garage can only edit 19 (31 with the \"Larger garage build area\" mod); it still spawns from the blueprint.", null, false));
        else if (ext > 19) res.Add(new($"The vehicle spans {ext} cells: more than the stock garage's 19 (needs the \"Larger garage build area\" mod to edit it in the game).", null, false));
        foreach (var p in Parts.Where(p => p.B.Group != 0).Take(1))
            res.Add(new($"{Parts.Count(q => q.B.Group != 0)} part(s) have a group byte (+3) set: the game does not spawn them.", p, false));
        return res;
    }

    /// <summary>
    /// Checks for an AI driver's vehicle (a game vehicle with an AI driver, or one with an AI seat): the AI drives from an AI
    /// seat (secondaryseats_small / _large: every AI racer of the game has one); player parts that have an AI version
    /// (engines, jets, spring) work — a player-built car with player engines raced in Burnin' Rubber — but the game's racers
    /// use the AI versions, which are stronger (objparams: engines and jets +25 % power / thrust, the AI spring 6.4× stiffer).
    /// </summary>
    public List<Issue> ValidateAi(PartCatalog cat)
    {
        var res = new List<Issue>();
        if (Parts.Count == 0) return res;
        if (!Parts.Any(p => cat[p.B.Part]?.IsAiSeat == true))
            res.Add(new("AI driver's vehicle without an AI seat: the game's AI racers all drive from Large / Small Taxi Seat (AI driver) — add one (Seats).", null, false));
        // engines and jets: the game's racers use the AI versions (one mixes in a player Small Engine); springs: the game's
        // racers use both, so they are not listed here ("Use AI parts" still offers them)
        var player = Parts.Where(p => cat[p.B.Part] is { AiVersion: > 0 } i && i.Class != "objDefId_vehicleBlockSpring").ToList();
        if (player.Count > 0)
        {
            var names = player.GroupBy(p => cat[p.B.Part]!.Name).Select(g => $"{(g.Count() > 1 ? $"{g.Count()}× " : "")}{g.Key} → {cat[cat[g.First().B.Part]!.AiVersion]!.Name}");
            res.Add(new($"AI vehicle: {player.Count} player part(s) have the AI version the game's racers use: {string.Join(", ", names.Take(6))}{(names.Count() > 6 ? " …" : "")}. " +
                        "The AI drives player parts too, the AI versions are stronger (+25 % power). \"Use AI parts\" (note in the view) swaps them.", player[0], false));
        }
        return res;
    }
}
