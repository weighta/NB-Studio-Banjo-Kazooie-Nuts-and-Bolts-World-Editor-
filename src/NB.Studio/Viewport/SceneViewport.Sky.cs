using System.Numerics;
using NB.Core.World;

namespace NB.Studio.Viewport;

/// <summary>
/// The sky dome as an object of the scene: selectable (a click on the sky when nothing else is under the mouse), listed
/// under Terrain in the Scene tree, its textures editable through the object menu (Textures…) or the Atmosphere tab.
/// It is not drawn by the object loop (the sky pass draws it) and has no transform the game reads: the level script
/// either centres it on the camera (Showdown Town, Terrarium) or places it at the world origin (Nutty Acres' bluesky,
/// Spiral Mountain), so it cannot be moved.
/// </summary>
public sealed partial class SceneViewport
{
    SceneObject? _skyObj;

    /// <summary>Puts the dome drawn now into the scene as an object (called when the light / sky changes).</summary>
    void UpdateSkyObject()
    {
        if (Scene == null) { _skyObj = null; return; }
        if (_skyObj != null && !Scene.Objects.Contains(_skyObj)) _skyObj = null;   // a new scene
        if (_sky == null)
        {
            if (_skyObj != null) { Scene.Objects.Remove(_skyObj); if (Selected == _skyObj) Select(null); _skyObj = null; }
            return;
        }
        string name = SkyName ?? "sky dome";
        string shortName = NB.Core.Formats.AssetIds.DisplayName(name);
        int i = shortName.LastIndexOf("skydome", StringComparison.Ordinal);
        if (i >= 0) shortName = shortName[i..];
        if (_skyObj == null)
        {
            _skyObj = new SceneObject { Id = Scene.Objects.Count == 0 ? 0 : Scene.Objects.Max(o => o.Id) + 1, Kind = SceneObjectKind.Terrain, IsSkyDome = true };
            Scene.Objects.Add(_skyObj);
        }
        _skyObj.Model = _sky;
        _skyObj.ModelName = name;
        _skyObj.ModelBundle = Scene.Bundle & 0xFFFFFF;
        _skyObj.Name = $"Sky dome ({shortName})";
        _skyObj.SkyFollowsCamera = _skyFollows;
        _skyObj.Transform = _skyObj.OriginalTransform = Matrix4x4.Identity;
        var pts = _sky.Draws.SelectMany(d => d.Indices.Where(k => k < d.Positions.Length).Select(k => d.Positions[k])).ToList();
        if (pts.Count > 0) { _skyObj.BoundsMin = pts.Aggregate(Vector3.Min); _skyObj.BoundsMax = pts.Aggregate(Vector3.Max); }
    }

    /// <summary>The sky dome object when the sky is shown (picked when nothing else is under the mouse).</summary>
    SceneObject? PickableSky => _skyObj != null && ShowSky && _viewMode is ViewMode.Textured or ViewMode.Rendered && Scene?.Objects.Contains(_skyObj) == true ? _skyObj : null;
}
