using System.Numerics;
using NB.Core.Formats;
using NB.Core.World;

namespace NB.Studio.Panels;

/// <summary>Numeric transform editor: position, rotation (degrees, applied X then Y then Z) and scale.</summary>
public sealed class TransformPanel : UserControl
{
    readonly Label _title = new() { Dock = DockStyle.Top, Height = 44, AutoEllipsis = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
    readonly NumericUpDown[] _pos = new NumericUpDown[3], _rot = new NumericUpDown[3], _scl = new NumericUpDown[3];
    readonly Button _apply = new() { Text = "Apply", Width = 90 }, _reset = new() { Text = "Revert fields", Width = 90 };
    readonly Label _info = new() { Dock = DockStyle.Bottom, Height = 175, ForeColor = SystemColors.GrayText };
    readonly NumericUpDown _link = new() { Minimum = 0, Maximum = 65535, Width = 90 };
    readonly FlowLayoutPanel _linkRow = new() { Dock = DockStyle.Top, Height = 32, Visible = false };
    SceneObject? _obj;
    bool _filling;
    public event Action<SceneObject, Matrix4x4>? TransformChanged;
    /// <summary>Info text for special selections (the collision selection's stand-in); null: the usual text.</summary>
    public Func<SceneObject, string?>? InfoFor;
    /// <summary>A path node's next-node index was edited (object, previous index).</summary>
    public event Action<SceneObject, int>? LinkChanged;

    public TransformPanel()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 136, ColumnCount = 4, RowCount = 4, Padding = new Padding(4) };
        for (int i = 0; i < 4; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        for (int i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        grid.Controls.Add(new Label { Text = "" }, 0, 0);
        grid.Controls.Add(new Label { Text = "X", TextAlign = ContentAlignment.MiddleCenter }, 1, 0);
        grid.Controls.Add(new Label { Text = "Y", TextAlign = ContentAlignment.MiddleCenter }, 2, 0);
        grid.Controls.Add(new Label { Text = "Z", TextAlign = ContentAlignment.MiddleCenter }, 3, 0);
        void Row(string name, NumericUpDown[] arr, int row, decimal min, decimal max, int dec, decimal inc)
        {
            grid.Controls.Add(new Label { Text = name, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, row);
            for (int i = 0; i < 3; i++)
            {
                arr[i] = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = dec, Increment = inc, Dock = DockStyle.Fill };
                arr[i].ValueChanged += (_, _) => { if (!_filling) ApplyFields(); };
                grid.Controls.Add(arr[i], i + 1, row);
            }
        }
        Row("Position", _pos, 1, -100000, 100000, 3, 0.5m);
        Row("Rotation°", _rot, 2, -360, 360, 2, 5);
        Row("Scale", _scl, 3, 0.001m, 1000, 3, 0.1m);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34 };
        buttons.Controls.Add(_apply); buttons.Controls.Add(_reset);
        _apply.Click += (_, _) => ApplyFields();
        _reset.Click += (_, _) => Fill();
        _linkRow.Controls.Add(new Label { Text = "Next path node", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        _linkRow.Controls.Add(_link);
        _link.ValueChanged += (_, _) =>
        {
            if (_filling || _obj?.Marker is not { Type: 22 } m || m.Link == (int)_link.Value) return;
            int before = m.Link; m.Link = (int)_link.Value;
            LinkChanged?.Invoke(_obj, before); Fill();
        };
        Controls.Add(_info); Controls.Add(_linkRow); Controls.Add(buttons); Controls.Add(grid); Controls.Add(_title);
        SetObject(null);
    }

    public void SetObject(SceneObject? o)
    {
        _obj = o;
        bool editable = o != null && o.Kind != SceneObjectKind.Terrain;
        foreach (var n in _pos.Concat(_rot)) n.Enabled = editable;
        // markers keep their scale (the record's scale field is not a size; path nodes use it as a node value)
        foreach (var n in _scl) n.Enabled = editable && !Viewport.SceneViewport.ScaleLocked(o);
        _apply.Enabled = _reset.Enabled = editable;
        _linkRow.Visible = o?.Marker is { Type: 22 };
        _title.Text = o == null ? "No selection" : $"{(SpawnPoints.Label(o) is { } sl ? sl + " — " : "")}{o.Name}\n{AssetIds.DisplayName(o.ModelName)}";
        _title.ForeColor = SpawnPoints.Is(o) ? Color.FromArgb(20, 130, 50) : SystemColors.ControlText;
        Fill();
    }

    void Fill()
    {
        _filling = true;
        try
        {
            if (_obj == null) { _info.Text = ""; return; }
            Matrix4x4.Decompose(_obj.Transform, out var s, out var q, out var t);
            var e = ToEuler(q);
            Set(_pos, t); Set(_rot, e); Set(_scl, s);
            if (InfoFor?.Invoke(_obj) is string special) { _info.Text = special; return; }
            if (_obj.Marker is { Type: 22 } pm) _link.Value = Math.Clamp(pm.Link, 0, 65535);
            _info.Text = _obj.IsSkyDome
                ? $"Sky dome {AssetIds.DisplayName(_obj.ModelName)}: the level script draws it " + (_obj.SkyFollowsCamera
                    ? "centred on the camera, so it always surrounds the view: the game stores no position, size or rotation for it and it cannot be moved. "
                    : "at the world's origin at its own size: the game stores no transform for it (the model itself would have to change), so it cannot be moved here. ") +
                  "Its textures can be replaced: right-click > Textures…, or the Atmosphere tab (Sky textures, and which dome each time of day / Act uses)."
                : _obj.Kind == SceneObjectKind.Terrain
                ? "Terrain is the background model's own geometry; it is not movable here. Use the Tag Editor for its data."
                : _obj.Kind == SceneObjectKind.Water
                ? $"Water region {_obj.WaterRegion + 1} of the background model (chunk 38): its surface triangles{(_obj.WaterPlane.Length == 4 ? " and the open-sea plane around the world" : "")}. " +
                  "Move, scale and turn it about the vertical axis (Y); World > Save writes the region again. The game's water is flat: X/Z rotations are dropped on saving, " +
                  "and each triangle keeps one height.\n" + (_obj.Dirty ? "Modified (not yet saved)" : "Unmodified")
                : _obj.Kind == SceneObjectKind.Marker
                ? (SpawnPoints.Label(_obj) is { } spl ? $"{spl.ToUpperInvariant()}: {SpawnPoints.Detail(_obj)}\n" : "") +
                  $"Marker type {_obj.Marker!.Type} ({NB.Core.World.MarkerRecord.TypeName(_obj.Marker.Type)}) #{_obj.Marker.Index} in {_obj.ModelName} at 0x{_obj.Marker.Offset:X}\n" +
                  $"References: {string.Join(", ", _obj.Marker.AssetIds.Zip(_obj.Marker.AssetNames).Take(6).Select(p => p.Second == "?" ? p.First.ToString("X8") : p.Second))}\n" +
                  $"Strings: {string.Join(", ", _obj.Marker.Strings.Take(4))}\n" +
                  (_obj.ModelSource != "" ? $"Drawn with: {(_obj.Model?.View != null ? AssetIds.DisplayName(_obj.Model.View.Name) + " from " : "")}{_obj.ModelSource}\n" : "") +
                  (_obj.GruntySign is bool gs ? $"Grunty challenge sign: {(gs ? "yes" : "no")} ({_obj.GruntySignWhy}). In the game the purple Grunty card on a world door marks an Act in which Grunty challenges Banjo " +
                      "(a Grunty battle). It is not a door setting, and the game does not read it from data Studio can edit (tested in Xenia), so it is shown for information.\n" : "") +
                  (_obj.Marker.Type == 22 ? $"Path: next node #{_obj.Marker.Link}" + (_obj.Marker.Link == _obj.Marker.Index ? " (end of path)" : "") + "; a node linking to itself ends the path\n" : "") +
                  (_obj.Dirty ? "Modified (not yet saved)" : "Unmodified") + "\nRotation is stored as X/Y/Z angles; the engine's order is assumed X-Y-Z." +
                  $"\nScale is locked for markers (stored value {_obj.Marker.Scale:0.###} is kept)."
                : $"Instance #{_obj.Instance?.Index}, reference model #{_obj.Instance?.RefModel}\n" +
                  $"Record 0x{_obj.Instance?.RecordOffset:X}, matrix 0x{_obj.Instance?.MatrixOffset:X}, node {_obj.Instance?.PlacementNode}\n" +
                  (_obj.Dirty ? "Modified (not yet saved to the workspace)" : "Unmodified") +
                  "\nIts collision (its model's Havok asset) moves and turns with it in the game (verified). Scaling does not scale the collision " +
                  "(Havok shapes keep their size): fit it with Edit Collision (toolbar) or the collision right-click menu.";
        }
        finally { _filling = false; }
    }

    static void Set(NumericUpDown[] a, Vector3 v)
    {
        decimal C(NumericUpDown n, float f) => Math.Clamp((decimal)(float.IsFinite(f) ? f : 0), n.Minimum, n.Maximum);
        a[0].Value = C(a[0], v.X); a[1].Value = C(a[1], v.Y); a[2].Value = C(a[2], v.Z);
    }

    void ApplyFields()
    {
        if (_obj == null || _obj.Kind == SceneObjectKind.Terrain) return;
        var before = _obj.Transform;
        var t = new Vector3((float)_pos[0].Value, (float)_pos[1].Value, (float)_pos[2].Value);
        var r = new Vector3((float)_rot[0].Value, (float)_rot[1].Value, (float)_rot[2].Value) * (MathF.PI / 180);
        var s = new Vector3((float)_scl[0].Value, (float)_scl[1].Value, (float)_scl[2].Value);
        if (Viewport.SceneViewport.ScaleLocked(_obj)) { Matrix4x4.Decompose(before, out var keep, out _, out _); s = keep; }
        var m = Matrix4x4.CreateScale(s) * Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateRotationZ(r.Z);
        m.Translation = t;
        if (m == before) return;
        _obj.Transform = m;
        TransformChanged?.Invoke(_obj, before);
        Fill();
    }

    /// <summary>Quaternion → Euler (X, Y, Z order matching CreateRotationX * Y * Z in row-vector convention), degrees.</summary>
    static Vector3 ToEuler(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        float y = MathF.Asin(Math.Clamp(-m.M13, -1, 1));
        float x, z;
        if (MathF.Abs(m.M13) < 0.9999f) { x = MathF.Atan2(m.M23, m.M33); z = MathF.Atan2(m.M12, m.M11); }
        else { x = MathF.Atan2(-m.M32, m.M22); z = 0; }
        return new Vector3(x, y, z) * (180 / MathF.PI);
    }
}
