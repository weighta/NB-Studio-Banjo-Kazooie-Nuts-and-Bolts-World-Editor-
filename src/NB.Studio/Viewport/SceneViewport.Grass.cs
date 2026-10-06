using NB.Core.Formats;
using NB.Core.Textures;
using NB.Core.World;

namespace NB.Studio.Viewport;

/// <summary>Grass layers (chunk 17): textures chosen and tiles laid out in the background, drawn by
/// <see cref="Renderer.DrawGrass"/>.</summary>
public sealed partial class SceneViewport
{
    /// <summary>Draw the grass layers (View > Grass).</summary>
    public bool ShowGrass { get => _showGrass; set { if (_showGrass != value) { _showGrass = value; _gl.Invalidate(); } } }
    bool _showGrass = true;

    sealed class GrassState
    {
        public GrassLayer Layer = null!;
        public (byte[] Rgba, int W, int H) Height;
        public string DensityName = "";
        public (byte[] Rgba, int W, int H) Density;
        public List<GrassTile> Tiles = new();
        public int Version;
    }
    List<GrassState>? _grassStates;
    string _grassKey = "";
    int _grassGen;
    /// <summary>What the grass loader did (log / scripted checks).</summary>
    public string GrassInfo { get; private set; } = "";
    public bool GrassReady => _grassStates != null;

    /// <summary>Picks each layer's shadow (density + colour) texture for the current light and lays its tiles out, in the
    /// background. The shadow textures come in lighting variants (_0, _1, …) stored in the time-of-day / act bundles
    /// (Showdown Town: _0 midday, _1 morning, _2 afternoon, _4 night): the variant held by the light's own bundle wins.</summary>
    void StartGrass()
    {
        var scene = Scene;
        if (scene == null || scene.Grass.Count == 0) { _grassStates = null; GrassInfo = scene == null ? "" : "no grass layers"; return; }
        var prefer = new List<uint>();
        void P(string? n)
        {
            if (string.IsNullOrEmpty(n)) return;
            n = n.Replace("aid_script_", "").Replace("aid_lightsetup_", "");
            if (!n.StartsWith("banjox_")) n = "banjox_" + n;
            prefer.Add(AssetIds.BundleId(n) & 0xFFFFFF);
        }
        var look = _lights is { Count: > 0 } && _lightIndex < _looks.Count ? _looks[_lightIndex] : null;
        P(look?.Script);
        if (_lights is { Count: > 0 }) P(_lights[_lightIndex].Name);
        prefer.AddRange(scene.MarkerBundles.Select(b => b & 0xFFFFFF));
        prefer.Add(scene.Bundle & 0xFFFFFF);
        prefer.AddRange(scene.LoadSet);
        string key = string.Join(",", prefer.Take(3).Select(b => b.ToString("x6")));
        if (key == _grassKey && _grassStates != null) return;
        _grassKey = key;
        int gen = ++_grassGen;
        var old = _grassStates;
        Task.Run(() =>
        {
            var ws = scene.Workspace;
            var res = new TextureResolver(ws, NB.Core.Project.AssetIndex.LoadOrBuild(ws), scene.Caff, scene.Bundle);
            var states = new List<GrassState>();
            var notes = new List<string>();
            foreach (var layer in scene.Grass)
            {
                if (layer.Mesh == null) { notes.Add($"layer {layer.Index}: grass model {layer.ModelId:X8} not found"); continue; }
                var prev = old?.FirstOrDefault(s => s.Layer == layer);
                var h = prev?.Height ?? res.LoadFull(layer.HeightName);
                if (h == null) { notes.Add($"layer {layer.Index}: height texture {layer.HeightName} missing"); continue; }
                // the lighting variants of the shadow texture and the bundles that hold them
                string stem = layer.ShadowName.Length > 0 ? layer.ShadowName : "";
                var variants = new List<(string Name, List<uint> Bundles)>();
                for (int k = 0; k < 10 && stem.Length > 0; k++)
                {
                    var loc = res.Locate(stem + "_" + k);
                    if (loc.Count > 0) variants.Add((stem + "_" + k, loc.Select(x => x.Bundle ?? (scene.Bundle & 0xFFFFFF)).Select(b => b & 0xFFFFFF).ToList()));
                }
                if (variants.Count == 0 && stem.Length > 0 && res.Locate(stem).Count > 0) variants.Add((stem, new List<uint>()));
                if (variants.Count == 0) { notes.Add($"layer {layer.Index}: shadow texture {stem}_* missing"); continue; }
                var pick = prefer.Select(b => variants.FirstOrDefault(v => v.Bundles.Contains(b))).FirstOrDefault(v => v.Name != null);
                if (pick.Name == null) pick = variants[0];
                (byte[] Rgba, int W, int H)? d = prev != null && prev.DensityName == pick.Name ? prev.Density : res.LoadFull(pick.Name);
                if (d == null) { notes.Add($"layer {layer.Index}: {pick.Name} could not be decoded"); continue; }
                states.Add(new GrassState { Layer = layer, Height = h.Value, DensityName = pick.Name, Density = d.Value, Tiles = layer.LayOut(d.Value.Rgba, d.Value.W, d.Value.H), Version = gen });
            }
            return (states, notes);
        }).ContinueWith(t =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() =>
            {
                if (gen != _grassGen || Scene != scene) return;
                if (t.IsFaulted) { GrassInfo = "grass: " + t.Exception?.GetBaseException().Message; scene.Log.Add(GrassInfo); return; }
                var (states, notes) = t.Result;
                foreach (var s in states) s.Layer.Tiles = s.Tiles;
                _grassStates = states;
                GrassInfo = $"grass: {states.Count}/{scene.Grass.Count} layers, {states.Sum(s => s.Tiles.Count)} tiles, shadow variants {string.Join(" ", states.Select(s => s.DensityName[(s.DensityName.LastIndexOf('_') + 1)..]).Distinct())}"
                    + (notes.Count > 0 ? "; " + string.Join("; ", notes.Take(4)) : "");
                GrassChanged?.Invoke(GrassInfo);
                _gl.Invalidate();
            });
        });
    }

    /// <summary>Raised on the UI thread when the grass layers were (re)laid out (log line).</summary>
    public event Action<string>? GrassChanged;

    void DrawGrassLayers(Renderer.Frustum? fr)
    {
        if (!_showGrass || !ShowTerrain || _grassStates == null) return;
        foreach (var s in _grassStates) _r.DrawGrass(s.Layer, s.Height, s.DensityName, s.Density, s.Version, fr);
    }

    void ResetGrass() { _grassStates = null; _grassKey = ""; _grassGen++; GrassInfo = ""; }
}
