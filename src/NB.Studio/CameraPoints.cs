using System.Numerics;
using System.Runtime.CompilerServices;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.World;

namespace NB.Studio;

/// <summary>
/// Fixed cameras of a world, as the game stores them: marker records of type 1 ("point") used as a camera. A camera point
/// is the record's position, looking along its +Z turned by pitch (rotation X, positive = down) and yaw (rotation Y); the
/// game builds the view from these two (verified: the WARP TO menu camera of Showdown Town's Town Square pad is point
/// #1083, the game's camera matrix equals the point and its pitch / yaw). No field of view or roll is stored.
/// The object using a camera points at it with the u16 at record +8 (the same field path nodes use for their next node):
/// <list type="bullet">
/// <item>warp pads (type 37, aid_objparams_banjox_props_showdowntown_warppad; pad number at +0x38): the WARP TO menu shows
/// the pad from its camera (moving the point moved the menu view in Xenia);</item>
/// <item>bolt heads (type 33, props_boltheads_*), info points (type 14, Banjoland / Spiral Mountain info points), the
/// Jiggy Bank (type 14) and type 40 records: the camera of their close-up.</item>
/// </list>
/// Points with a pitch and no owner are listed as camera points too (a camera looks down; plain points stand level).
/// </summary>
public static class CameraPoints
{
    public const int PointType = 1;
    public sealed record Info(string Label, string Detail, SceneObject? Owner);

    static readonly ConditionalWeakTable<SceneObject, Info> Cameras = new();
    static readonly ConditionalWeakTable<SceneObject, List<SceneObject>> Owned = new();

    /// <summary>Showdown Town's warp pads by their number (record +0x38), as the WARP TO menu names them (pads 1..5 checked
    /// in Xenia by warping: the menu lists them in number order; pad 0 is the one in the town square).</summary>
    static readonly string[] WarpPads = { "Town Square (Mumbo's Motors)", "Theater District", "Seaside (King Jingaling's Bingo Palace)", "Lakeshore (Trophy Thomas' House)", "Docks (Boggy's Gym)", "Uptown" };

    public static bool Is(SceneObject? o) => Of(o) != null;
    public static Info? Of(SceneObject? o) =>
        o == null ? null
        : o.Kind == SceneObjectKind.CutsceneKey && o.Cutscene is { } c
            ? new Info("Cut-scene camera", $"Key of the camera of {c.Asset} ({c.CameraName}, {c.Frames} frames at 30 fps, field of view {c.FovAt(o.CutsceneFrame):0.#}°{(c.Streamed ? ", streamed" : "")}). " +
                "Move or turn it: the path changes around it and fades out towards the neighbouring keys. World > Save All writes the cut-scene.", null)
        : Cameras.TryGetValue(o, out var i) ? i : null;
    public static string? Label(SceneObject? o) => Of(o)?.Label;
    public static string? Detail(SceneObject? o) => Of(o)?.Detail;
    /// <summary>The cameras an object uses (a warp pad's WARP TO view, ...).</summary>
    public static IReadOnlyList<SceneObject> CamerasOf(SceneObject? owner) => owner != null && Owned.TryGetValue(owner, out var l) ? l : Array.Empty<SceneObject>();

    /// <summary>u16 at a record's +8: the index of the record it uses (camera point, next path node, ...).</summary>
    static int Ref(SceneObject o)
    {
        var d = Data(o);
        return d == null || o.Marker!.Offset + 10 > d.Length ? -1 : BE.U16(d, o.Marker.Offset + 8);
    }

    static byte[]? Data(SceneObject o) => o.MarkerSet?.Caff?.PartsOf(o.MarkerSet.Symbol).FirstOrDefault(p => o.MarkerSet.Caff.SectionOf(p).Name == ".data")?.Data;

    static string OwnerName(SceneObject owner)
    {
        var r = owner.Marker!;
        string? op = r.AssetNames.FirstOrDefault(n => n.StartsWith("aid_objparams_"))?.Replace("aid_objparams_banjox_", "");
        if (r.Type == 37 && op?.Contains("warppad") == true)
        {
            var d = Data(owner);
            int n = d != null && r.Size >= 0x3C ? (int)BE.U32(d, r.Offset + 0x38) : -1;
            return n >= 0 && n < WarpPads.Length ? $"Warp camera: {WarpPads[n]}" : $"Warp camera: pad {n}";
        }
        if (op == null) return $"Camera of type {r.Type} #{r.Index}";
        if (op.Contains("boltheads")) return $"Bolt-head camera #{owner.Marker.Index}";
        if (op.Contains("jiggybank")) return "Jiggy Bank camera";
        if (op.Contains("infopoint")) return "Info point camera: " + op.Replace("props_", "").Replace("infopoint_", "").Replace("_", " ");
        return "Camera of " + op.Replace("props_", "");
    }

    /// <summary>Finds the camera points of a scene (call when the scene is shown).</summary>
    public static void Build(WorldScene? scene)
    {
        if (scene == null) return;
        var markers = scene.Objects.Where(o => o.Kind == SceneObjectKind.Marker && o.Marker != null && o.MarkerSet != null).ToList();
        var byKey = new Dictionary<(MarkerAsset, int), SceneObject>();
        foreach (var o in markers) byKey.TryAdd((o.MarkerSet!, o.Marker!.Index), o);
        var found = new Dictionary<SceneObject, Info>();
        foreach (var o in markers)
        {
            if (o.Marker!.Type is PointType or 22) continue;   // path nodes link to path nodes; points to points (pairs)
            int t = Ref(o);
            if (t <= 0 || !byKey.TryGetValue((o.MarkerSet!, t), out var cam) || cam.Marker!.Type != PointType || cam == o) continue;
            bool pitched = Pitched(cam);
            if (o.Marker.Type != 37 && !pitched) continue;   // world doors etc. point at level points: where Banjo is put, not cameras
            var name = OwnerName(o);
            found[cam] = new Info(name,
                (o.Marker.Type == 37
                    ? "The WARP TO menu shows this pad through this camera (verified in Xenia: moving it moved the menu view). Arriving at a pad uses the normal camera behind Banjo, which follows the pad. "
                    : "The camera this object's close-up uses (its record +8 points here). ")
                + $"Used by {o.Name}. Move it with the pad (NB Studio offers this when you move the pad), or right-click: Look Through Camera / Set Camera from 3D View. Pitch = rotation X (down), yaw = rotation Y; the game stores no field of view or roll.",
                o);
            if (!Owned.TryGetValue(o, out var l)) Owned.Add(o, l = new List<SceneObject>());
            if (!l.Contains(cam)) l.Add(cam);
        }
        foreach (var o in markers)
            if (o.Marker!.Type == PointType && !found.ContainsKey(o) && Pitched(o))
                found[o] = new Info($"Camera point #{o.Marker.Index}", "A point that looks down (pitch): marker points used as cameras have a pitch, level ones are places. Not used by any marker of this set (scripts or code may use it).", null);
        foreach (var (o, i) in found) Cameras.AddOrUpdate(o, i);
    }

    /// <summary>Looks up or down (a camera), from the direction, not the raw angles (X = Z = 180° is level too).</summary>
    static bool Pitched(SceneObject o) => MathF.Abs(View(o.Transform).Pitch) > 0.01f;

    /// <summary>A camera's view direction (local +Z) and its pitch (up positive, the 3D view's convention) and yaw.</summary>
    public static (Vector3 Forward, float Yaw, float Pitch) View(Matrix4x4 m)
    {
        var f = new Vector3(m.M31, m.M32, m.M33);
        f = f.LengthSquared() > 1e-10f ? Vector3.Normalize(f) : Vector3.UnitZ;
        return (f, MathF.Atan2(f.X, f.Z), MathF.Asin(Math.Clamp(f.Y, -1f, 1f)));
    }

    /// <summary>The marker transform of a camera at <paramref name="pos"/> looking with the 3D view's yaw / pitch (the
    /// record keeps rotation X = -pitch, Y = yaw, no roll).</summary>
    public static Matrix4x4 FromView(Vector3 pos, float yaw, float pitch) =>
        Matrix4x4.CreateRotationX(-pitch) * Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(pos);
}
