using System.Drawing.Drawing2D;
using System.Numerics;
using NB.Core.Models;
using NB.Core.World;

namespace NB.Studio.Viewport;

/// <summary>What the 3D view can show or hide, in one place: the "Show ▾" button left of the Collision toggle opens a
/// menu of these groups (the View menu keeps its own items; both stay in step through <see cref="VisibilityChanged"/>).</summary>
public enum Visgroup { Terrain, Scenery, Objects, Grass, Water, Sky, Markers, Paths, Collision }

public sealed partial class SceneViewport
{
    /// <summary>Draw the world's water (chunk-38 surfaces and the open sea) in the Textured / Rendered modes.</summary>
    public bool ShowWater { get => _showWater; set { if (_showWater != value) { _showWater = value; _gl.Invalidate(); } } }
    bool _showWater = true;
    /// <summary>Draw the sky dome in the Textured / Rendered modes.</summary>
    public bool ShowSky { get => _showSky; set { if (_showSky != value) { _showSky = value; _gl.Invalidate(); } } }
    bool _showSky = true;

    /// <summary>A group was shown or hidden through <see cref="SetVisible"/> (the "Show" menu): keeps menus in step.</summary>
    public event Action<Visgroup, bool>? VisibilityChanged;

    public static readonly (Visgroup Group, string Label)[] VisgroupLabels =
    {
        (Visgroup.Terrain, "Terrain"), (Visgroup.Scenery, "Scenery"), (Visgroup.Objects, "Objects at markers"), (Visgroup.Grass, "Grass"),
        (Visgroup.Water, "Water"), (Visgroup.Sky, "Sky"), (Visgroup.Markers, "Markers"), (Visgroup.Paths, "Paths"), (Visgroup.Collision, "Collision"),
    };

    public bool IsVisible(Visgroup g) => g switch
    {
        Visgroup.Terrain => ShowTerrain, Visgroup.Scenery => ShowScenery, Visgroup.Objects => ShowObjects, Visgroup.Grass => ShowGrass,
        Visgroup.Water => ShowWater, Visgroup.Sky => ShowSky, Visgroup.Markers => ShowMarkers, Visgroup.Paths => ShowPaths,
        Visgroup.Collision => ShowCollision, _ => true,
    };

    public void SetVisible(Visgroup g, bool on)
    {
        if (IsVisible(g) == on) return;
        switch (g)
        {
            case Visgroup.Terrain: ShowTerrain = on; break;
            case Visgroup.Scenery: ShowScenery = on; break;
            case Visgroup.Objects: ShowObjects = on; break;
            case Visgroup.Grass: ShowGrass = on; break;
            case Visgroup.Water: ShowWater = on; break;
            case Visgroup.Sky: ShowSky = on; break;
            case Visgroup.Markers: ShowMarkers = on; break;
            case Visgroup.Paths: ShowPaths = on; break;
            case Visgroup.Collision: ShowCollision = on; break;
        }
        Refresh3D();
        VisibilityChanged?.Invoke(g, on);
    }

    // ------------------------------------------------------------------ "Show ▾" button and menu

    const int VisW = 70;
    /// <summary>The visibility-groups button, left of the Collision toggle.</summary>
    Rectangle VisRect => new(CollRect.X - CollGap - VisW, BarMargin, VisW, BarH);
    const int VisHit = 5;

    readonly ContextMenuStrip _visMenu = new() { ShowCheckMargin = true, ShowImageMargin = false };

    /// <summary>Opens the visibility-groups menu under the "Show" button (or at <paramref name="at"/>).</summary>
    public void ShowVisgroupsMenu(Point? at = null)
    {
        _visMenu.Items.Clear();
        _visMenu.Items.Add(new ToolStripLabel("Show in the 3D view") { ForeColor = Color.FromArgb(105, 108, 118) });
        foreach (var (g, label) in VisgroupLabels)
        {
            var mi = new ToolStripMenuItem(label) { Checked = IsVisible(g), CheckOnClick = true };
            if (g is Visgroup.Water or Visgroup.Sky) mi.ToolTipText = "Textured and Rendered modes";
            mi.CheckedChanged += (_, _) => SetVisible(g, mi.Checked);
            _visMenu.Items.Add(mi);
        }
        _visMenu.Items.Add(new ToolStripSeparator());
        _visMenu.Items.Add("Show all", null, (_, _) => { foreach (var (g, _) in VisgroupLabels) if (g != Visgroup.Collision) SetVisible(g, true); });
        _visMenu.AutoClose = true;
        var p = at ?? new Point(VisRect.X, VisRect.Bottom + 2);
        _visMenu.Show(_gl, p);
    }

    /// <summary>The "Show ▾" button, drawn at the left of the bar bitmap (see DrawBar).</summary>
    void DrawVisButton(Graphics g, Font font, Brush bg, Pen edge)
    {
        using var cp = Rounded(new Rectangle(0, 0, VisW - 1, BarH - 1), 4);
        g.FillPath(bg, cp); g.DrawPath(edge, cp);
        var r = new Rectangle(3, 3, VisW - 6, BarH - 7);
        bool hover = _hoverBar == VisHit || _visMenu.Visible;
        if (hover) { using var p2 = Rounded(r, 3); using var b2 = new SolidBrush(BarHover); g.FillPath(b2, p2); }
        // an eye
        using (var pen = new Pen(IconFg, 1.3f))
        {
            var e = new RectangleF(r.X + 5, r.Y + 5.5f, 15, 9);
            using var eye = new GraphicsPath();
            eye.AddBezier(e.Left, e.Top + e.Height / 2, e.Left + 4, e.Top - 1, e.Right - 4, e.Top - 1, e.Right, e.Top + e.Height / 2);
            eye.AddBezier(e.Right, e.Top + e.Height / 2, e.Right - 4, e.Bottom + 1, e.Left + 4, e.Bottom + 1, e.Left, e.Top + e.Height / 2);
            g.DrawPath(pen, eye);
            using var pupil = new SolidBrush(IconFg);
            g.FillEllipse(pupil, e.X + e.Width / 2 - 2.5f, e.Y + e.Height / 2 - 2.5f, 5, 5);
        }
        using var tb = new SolidBrush(BarText);
        g.DrawString("Show ▾", font, tb, r.X + 23, r.Y + 2);
    }

    // ------------------------------------------------------------------ water

    /// <summary>Water objects are drawn (and picked) in the Textured and Rendered modes when Water is shown.</summary>
    bool WaterShown => _showWater && _viewMode is ViewMode.Textured or ViewMode.Rendered;

    /// <summary>The look of the water objects: translucent blue-green with the sky reflected (the game's dynamic water
    /// shader is not translated; matched by eye to Showdown Town's harbour), the open sea a clearer turquoise.</summary>
    void SetWaterMaterials(WorldScene scene)
    {
        string? skyTex = (_sky != null ? new[] { _sky } : _skies.Select(sk => sk.Model)).Select(m => m?.Draws.SelectMany(d => d.Textures).Select(t => t.Texture).FirstOrDefault(t => !t.StartsWith('#'))).FirstOrDefault(t => t != null);
        foreach (var o in scene.Objects.Where(o => o.Kind == SceneObjectKind.Water && o.Model != null))
            foreach (var d in o.Model!.Draws)
                _r.MaterialOverrides[d] = o.WaterPlane.Length == 4
                    ? new MaterialInfo { Blend = BlendKind.Blend, Tint = new Vector3(0.12f, 0.33f, 0.34f), Opacity = 0.72f, SpecPower = 140, SpecColour = new Vector3(0.9f), Reflect = skyTex, ReflectStrength = 0.38f }
                    : new MaterialInfo { Blend = BlendKind.Blend, Tint = new Vector3(0.10f, 0.21f, 0.24f), Opacity = 0.85f, SpecPower = 140, SpecColour = new Vector3(0.9f), Reflect = skyTex, ReflectStrength = 0.38f };
    }

}
