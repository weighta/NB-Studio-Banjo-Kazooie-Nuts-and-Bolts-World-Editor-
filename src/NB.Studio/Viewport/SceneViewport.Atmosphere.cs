using NB.Core.Models;
using NB.Core.World;

namespace NB.Studio.Viewport;

/// <summary>Live atmosphere preview: the Atmosphere tab pushes the light setup it edits (its .data bytes, unsaved edits
/// included) and the skydome of the time of day; the Rendered mode shows them at once (the sun shadow map is redrawn every
/// frame from <see cref="SceneLighting.SunDirection"/>, so shadows follow the sun without extra work).</summary>
public sealed partial class SceneViewport
{
    /// <summary>Raised after the Rendered mode's light changed (light bar clicked, world loaded, preview pushed), with the
    /// light setup's short name (<see cref="LightingName"/>).</summary>
    public event Action<string>? LightApplied;

    readonly Dictionary<uint, ModelAsset?> _previewDomes = new();
    WorldScene? _previewDomesFor;

    /// <summary>Shows the light setup <paramref name="lightAsset"/> (aid_script_banjox_lightsetup_*) from the bytes
    /// <paramref name="data"/>, under the skydome model <paramref name="domeId"/> (0: keep the dome the level scripts use),
    /// and makes it the current light of the light bar. Cheap (parses ~0x100 bytes): call it on every edit.</summary>
    public bool PreviewAtmosphere(string lightAsset, byte[] data, uint domeId)
    {
        if (Scene == null) return false;
        EnsureLighting();
        var l = LevelLighting.FromData(lightAsset, data);
        if (l == null || _lights == null) return false;
        int i = _lights.FindIndex(x => x.Name == l.Name);
        if (i < 0) { _lights.Add(l); _looks.Add(null); i = _lights.Count - 1; }
        else _lights[i] = l;
        while (_looks.Count < _lights.Count) _looks.Add(null);
        var look = _looks[i];
        if (domeId != 0 && look?.DomeId != domeId)
        {
            var dome = PreviewDome(domeId, out var domeName);
            if (dome != null)
                _looks[i] = look = new WorldLook
                {
                    Script = look?.Script ?? "", Light = l, DomeId = domeId, DomeName = domeName, Dome = dome,
                    DomeFollowsCamera = look?.DomeFollowsCamera ?? true, LightBundle = look?.LightBundle ?? 0,
                };
        }
        if (look != null) look.Light = l;
        _lightIndex = i;
        ApplyLight();
        return true;
    }

    /// <summary>A skydome model by id: one the level scripts already loaded, else loaded once per scene.</summary>
    ModelAsset? PreviewDome(uint id, out string? name)
    {
        name = null;
        if (_previewDomesFor != Scene) { _previewDomes.Clear(); _previewDomesFor = Scene; }
        foreach (var lk in _looks)
            if (lk?.DomeId == id && lk.Dome != null) { name = lk.DomeName; return lk.Dome; }
        var ws = Scene!.Workspace;
        var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
        name = idx.Entries.FirstOrDefault(e => e.Id == id)?.Name;
        if (!_previewDomes.TryGetValue(id, out var m))
            _previewDomes[id] = m = WorldLooks.LoadModel(ws, idx, id, Scene, s => Scene.Log.Add(s));
        return m;
    }

    /// <summary>Draws a frame now (the preview while a slider is dragged: a plain Invalidate can wait behind the slider's
    /// own messages).</summary>
    public void RenderNow()
    {
        _gl.Invalidate();
        _gl.Update();
    }

    /// <summary>A texture was replaced in the workspace (Atmosphere > sky texture): decode it again and upload it into the
    /// same GL texture, without reloading the world. Returns the number of textures refreshed.</summary>
    public int ReloadTexture(string stem)
    {
        if (!_ready || Scene == null) return 0;
        Scene.Textures?.Forget(stem);
        _gl.MakeCurrent();
        int n = _r.ReloadTextures(name => NB.Core.Textures.TextureResolver.Stem(name).Equals(stem, StringComparison.OrdinalIgnoreCase));
        _gl.Invalidate();
        return n;
    }

    /// <summary>The current Rendered-mode light (scripts and checks).</summary>
    public SceneLighting CurrentLighting => _r.Lighting;
}
