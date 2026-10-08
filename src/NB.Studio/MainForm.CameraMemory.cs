using System.Numerics;
using System.Text.Json;
using NB.Core.Project;

namespace NB.Studio;

/// <summary>
/// The 3D view's camera is remembered per world (and Act) of a workspace: leaving a world (opening another, closing the
/// workspace, quitting) stores where the camera was, and opening that world again puts it back there. Kept in
/// studio-cameras.json in the workspace. Scripted test runs neither read nor write it (their views stay predictable).
/// </summary>
public partial class MainForm
{
    sealed record CameraSpot(float X, float Y, float Z, float Yaw, float Pitch);

    Dictionary<string, CameraSpot>? _camSpots;
    // scripted test runs keep predictable views unless a test of this feature asks for it
    bool CameraMemoryOff => _scripted && Environment.GetEnvironmentVariable("NB_STUDIO_CAMERA_MEMORY") != "1";
    string? _camSpotsRoot;

    string? CameraFile => _ws == null ? null : Path.Combine(_ws.Root, "studio-cameras.json");

    Dictionary<string, CameraSpot> CameraSpots()
    {
        if (_camSpots != null && _camSpotsRoot == _ws?.Root) return _camSpots;
        _camSpotsRoot = _ws?.Root;
        _camSpots = new();
        try
        {
            if (CameraFile is { } f && File.Exists(f))
                _camSpots = JsonSerializer.Deserialize<Dictionary<string, CameraSpot>>(File.ReadAllText(f)) ?? new();
        }
        catch (Exception) { _camSpots = new(); }
        return _camSpots;
    }

    /// <summary>Stores the camera of the open world (call before leaving it).</summary>
    void RememberCamera()
    {
        if (CameraMemoryOff || _ws == null || _scene == null || _sceneEntry == null) return;
        var p = _view.CameraPosition; var (yaw, pitch) = _view.LookAngles;
        if (!float.IsFinite(p.X + p.Y + p.Z + yaw + pitch)) return;
        var spots = CameraSpots();
        spots[WorldKey(_sceneEntry, _sceneAct)] = new CameraSpot(p.X, p.Y, p.Z, yaw, pitch);
        try { if (CameraFile is { } f) File.WriteAllText(f, JsonSerializer.Serialize(spots, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception) { }   // a read-only or busy workspace: the camera simply isn't remembered
    }

    /// <summary>Puts the camera where it was when this world was last left; false when there is no remembered spot.</summary>
    bool RestoreCamera(WorldEntry w, ActEntry? act)
    {
        if (CameraMemoryOff || _ws == null) return false;
        if (!CameraSpots().TryGetValue(WorldKey(w, act), out var s)) return false;
        _view.SetCamera(new Vector3(s.X, s.Y, s.Z), s.Yaw * 180 / MathF.PI, s.Pitch * 180 / MathF.PI);
        return true;
    }
}
