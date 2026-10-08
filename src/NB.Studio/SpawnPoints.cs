using NB.Core.World;

namespace NB.Studio;

/// <summary>
/// Player start points: marker records of type 4 (Studio up to 1.9 called them "drone action").
/// <para>Verified in Xenia (2026-10-06, studio18): Showdown Town has exactly one, #84 of aid_marker_banjox_showdowntown_main
/// (-10.4, 0.5, 306.3), and a new game / Test in Xenia starts the player there (game position (-10.1, 2.5, 305.9));
/// moving it moves the start (Seattle T60, the Source map importer). Every Act's own marker asset
/// (aid_marker_banjox_&lt;world&gt;_act&lt;N&gt;_main) has exactly one, #1, and the Act starts Banjo in the challenge
/// vehicle there: Nutty Acres Act 1 marker (156.1, 7.2, 332.6) vs game (154.5, 9.0, 331.7), Act 2 (-281.8, -26.6, 128.6)
/// vs (-282.1, -24.5, 128.6). The facing direction is the record's yaw (the game camera stood behind the player along
/// -(sin yaw, cos yaw) in all three).</para>
/// <para>Not verified: the single type-4 record of a world's "global" marker asset (e.g. nuttyacres_global), and the
/// several of the multiplayer (live_*) and car park assets (one per player slot).</para>
/// </summary>
public static class SpawnPoints
{
    public const int Type = 4;

    public static bool Is(SceneObject? o) => o?.Kind == SceneObjectKind.Marker && o.Marker?.Type == Type;

    public enum Kind { None, TownStart, ActStart, MultiplayerSlot, Other }

    public static Kind KindOf(SceneObject? o)
    {
        if (!Is(o)) return Kind.None;
        var set = o!.ModelName.ToLowerInvariant();
        if (set.Contains("showdowntown_main")) return Kind.TownStart;
        if (System.Text.RegularExpressions.Regex.IsMatch(set, @"_act(\d+|ww)_main$")) return Kind.ActStart;
        if (set.Contains("_live_") || set.Contains("frontend_carpark")) return Kind.MultiplayerSlot;
        return Kind.Other;
    }

    /// <summary>Short label for the 3D view, the scene tree and the tooltip ("Player start (Banjo)"), or null.</summary>
    public static string? Label(SceneObject? o) => KindOf(o) switch
    {
        Kind.TownStart => "Player start (Banjo)",
        Kind.ActStart => "Act start (Banjo + vehicle)",
        Kind.MultiplayerSlot => $"Multiplayer start #{o!.Marker!.Index}",
        Kind.Other => "Player start point",
        _ => CameraPoints.Label(o),   // cameras (warp pad views, ...) carry their label in the same places
    };

    /// <summary>One or two sentences for the Properties panel and the tooltip.</summary>
    public static string? Detail(SceneObject? o) => KindOf(o) switch
    {
        Kind.TownStart => "Where Banjo starts in Showdown Town: a new game, and Build > Test in Xenia (F5). Move or turn this marker to move the start (World > Save). The arrow shows the way Banjo faces.",
        Kind.ActStart => "Where this Act starts Banjo, in the challenge vehicle (verified for Nutty Acres Acts 1 and 2). The arrow shows the way he faces.",
        Kind.MultiplayerSlot => "One of the player start slots of this multiplayer game (one per player; not tested).",
        Kind.Other => "A player start marker (marker type 4) of this world's shared markers. When the game uses it was not tested (Acts start at their own Act start).",
        _ => CameraPoints.Detail(o),
    };
}
