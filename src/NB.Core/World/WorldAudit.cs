namespace NB.Core.World;

/// <summary>
/// What a <see cref="WorldScene"/> loaded, skipped or could not load: scenery placements (background model chunk 12),
/// models nested inside reference models, and objects placed by markers (props, characters, collectables). Filled while
/// the scene is built; shown in the Studio log and by <c>NB.Cli world-audit</c>.
/// </summary>
public sealed class WorldAudit
{
    /// <summary>Scenery placements; those whose model came from another bundle of the load set; those drawn as a cross
    /// (model not found anywhere or failed to parse).</summary>
    public int Scenery, SceneryOtherBundle, SceneryMissing;
    /// <summary>Placements inside reference models (composite buildings), counted once per distinct parent model.</summary>
    public int Nested, NestedMissing;
    /// <summary>Marker records; records drawn with the model of the object they place; records whose object has no model
    /// of its own (AI strategies, spawners, triggers…); vehicles placed by markers (drawn as boxes); failed model parses.</summary>
    public int Markers, MarkerModels, MarkerObjectsWithoutModel, MarkerVehicles, MarkerModelsFailed;
    /// <summary>Vehicles placed by markers that are drawn from their blueprint's parts.</summary>
    public int MarkerVehiclesDrawn;
    /// <summary>Grass layers of the background model (chunk 17), scattered by the game at run time (not drawn).</summary>
    public int GrassLayers;
    /// <summary>Distinct models parsed for the scene, and models that threw while parsing.</summary>
    public int Models, ModelsFailed;
    /// <summary>Bundles the scene read from (world, act, load-set bundles that were needed).</summary>
    public List<uint> Bundles = new();

    /// <summary>Distinct notable items per category (missing / other-bundle / failed …).</summary>
    public readonly SortedDictionary<string, List<string>> Notes = new();
    /// <summary>Counted items per category (object classes drawn at markers, classes without a model …).</summary>
    public readonly SortedDictionary<string, SortedDictionary<string, int>> Counts = new();

    public void Note(string category, string item)
    {
        if (!Notes.TryGetValue(category, out var l)) Notes[category] = l = new();
        if (!l.Contains(item)) l.Add(item);
    }

    public void Count(string category, string item)
    {
        if (!Counts.TryGetValue(category, out var d)) Counts[category] = d = new();
        d[item] = d.GetValueOrDefault(item) + 1;
    }

    /// <summary>One-line summary (Studio log).</summary>
    public string Summary() =>
        $"scenery {Scenery - SceneryMissing}/{Scenery} drawn ({SceneryOtherBundle} from other bundles, {SceneryMissing} missing), " +
        $"nested {Nested - NestedMissing}/{Nested}, markers {Markers}: {MarkerModels} objects drawn with their model, " +
        $"{MarkerVehiclesDrawn} vehicles drawn from their parts, {MarkerObjectsWithoutModel} objects without a model, {MarkerVehicles} vehicles not drawn; {Models} models, {ModelsFailed} failed to parse";

    public IEnumerable<string> Report(int maxItems = 12)
    {
        yield return Summary();
        foreach (var (cat, d) in Counts)
            yield return $"  {cat}: {d.Values.Sum()} — " + string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(maxItems).Select(kv => $"{kv.Key} ×{kv.Value}")) + (d.Count > maxItems ? ", …" : "");
        foreach (var (cat, l) in Notes)
            yield return $"  {cat}: {l.Count} — " + string.Join(", ", l.Take(maxItems)) + (l.Count > maxItems ? ", …" : "");
    }
}
