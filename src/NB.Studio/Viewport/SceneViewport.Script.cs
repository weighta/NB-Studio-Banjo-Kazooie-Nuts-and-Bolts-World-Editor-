using System.Globalization;
using System.Numerics;

namespace NB.Studio.Viewport;

/// <summary>Scripted viewport steps for automated checks (MainForm forwards arguments it does not know).</summary>
public sealed partial class SceneViewport
{
    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>
    /// Viewport script commands:
    ///   --view-mode wire|solid|textured|rendered     --light NAME (light setup containing NAME)   --fov DEG
    ///   --view-size W,H (resizes the window so the 3D view is W×H)   --show markers|paths|terrain|scenery on|off
    ///   --key K[,K…] (key presses in the view: G, S, R, X, Y, Z, Return, Escape, D1…, OemMinus, OemPeriod, Back, ControlKey down via ^)
    ///   --mouse X,Y (mouse move to a view pixel)   --click left|right[,X,Y]   --drag X0,Y0,X1,Y1 (left drag)
    ///   --mode-cycle N (switch through all view modes N times)   --minimize-restore   --sel-info   --gizmo move|rotate|scale|select
    /// Returns false for arguments it does not handle.
    /// </summary>
    public async Task<bool> RunScriptCommand(string cmd, Func<string> next, Action<string> log)
    {
        switch (cmd)
        {
            case "--view-mode":
            {
                var v = next().ToLowerInvariant();
                ViewMode = v switch { "wire" or "wireframe" => ViewMode.Wireframe, "solid" => ViewMode.Solid, "rendered" or "render" => ViewMode.Rendered, _ => ViewMode.Textured };
                Render(); log($"script: view mode {ViewMode}" + (ViewMode == ViewMode.Rendered ? $", light {LightingName}, sky {(_sky != null ? "yes" : "none")}" : ""));
                return true;
            }
            case "--light": NextLight(next()); log($"script: light {LightingName}: {_r.Lighting.SunDirection} sun {_r.Lighting.Sun} x{_r.Lighting.Intensity} amb {_r.Lighting.Ambient} fog {_r.Lighting.Fog}"); return true;
            case "--sun":
            {
                var v = next().Split(',').Select(F).ToArray();
                EnsureLighting(); _r.Lighting.SunDirection = Vector3.Normalize(new Vector3(v[0], v[1], v[2])); _gl.Invalidate();
                log($"script: sun direction {_r.Lighting.SunDirection}"); return true;
            }
            case "--shadows": _r.Shadows = next() == "on"; if (!_r.Shadows) _r.InvalidateShadow(); _gl.Invalidate(); log($"script: shadows {_r.Shadows}"); return true;
            case "--fov": FieldOfView = F(next()); log($"script: fov {FieldOfView}"); return true;
            case "--gizmo": { var g = next().ToLowerInvariant(); Mode = g switch { "rotate" => GizmoMode.Rotate, "scale" => GizmoMode.Scale, "select" => GizmoMode.Select, _ => GizmoMode.Move }; _gl.Invalidate(); log($"script: gizmo {Mode}"); return true; }
            case "--view-size":
            {
                var v = next().Split(',').Select(int.Parse).ToArray();
                var form = FindForm()!;
                form.WindowState = FormWindowState.Normal;
                // side panels may take part of the extra width: adjust a few times
                for (int i = 0; i < 6 && (_gl.Width != v[0] || _gl.Height != v[1]); i++)
                {
                    form.Size = new Size(form.Width + v[0] - _gl.Width, form.Height + v[1] - _gl.Height);
                    Application.DoEvents(); await Task.Delay(150); Application.DoEvents();
                }
                log($"script: view size {_gl.Width}x{_gl.Height}"); return true;
            }
            case "--show":
            {
                var what = next(); bool on = next() == "on";
                switch (what) { case "markers": ShowMarkers = on; break; case "paths": ShowPaths = on; break; case "terrain": ShowTerrain = on; break; case "scenery": ShowScenery = on; break; }
                Refresh3D(); log($"script: show {what} {on}"); return true;
            }
            case "--key":
            {
                var keyList = next();
                foreach (var k0 in keyList.Split(','))
                {
                    var k = k0; bool ctrl = k.StartsWith('^'); if (ctrl) k = k[1..];
                    var key = Enum.Parse<Keys>(k, true);
                    if (ctrl) _keys.Add(Keys.ControlKey);
                    _keys.Add(key);
                    OnKey(new KeyEventArgs(key | (ctrl ? Keys.Control : Keys.None)));
                    _keys.Remove(key);
                    if (ctrl) _keys.Remove(Keys.ControlKey);
                    Render();
                }
                log($"script: keys {keyList}; transforming {Transforming}" + (Selected != null ? $"; selection at {Fmt(Selected.Transform.Translation)}" : "")); return true;
            }
            case "--mouse":
            {
                var v = next().Split(',').Select(int.Parse).ToArray();
                OnMouseMove(null, new MouseEventArgs(MouseButtons.None, 0, v[0], v[1], 0)); Render();
                log($"script: mouse {v[0]},{v[1]}" + (Selected != null ? $"; selection at {Fmt(Selected.Transform.Translation)}" : "")); return true;
            }
            case "--click":
            {
                var v = next().Split(',');
                var btn = v[0] == "right" ? MouseButtons.Right : MouseButtons.Left;
                var p = v.Length >= 3 ? new Point(int.Parse(v[1]), int.Parse(v[2])) : _mouse;
                OnMouseDown(null, new MouseEventArgs(btn, 1, p.X, p.Y, 0));
                OnMouseUp(null, new MouseEventArgs(btn, 1, p.X, p.Y, 0));
                Render();
                log($"script: click {v[0]} at {p.X},{p.Y}; selected {Selected?.Name ?? "-"}; transforming {Transforming}"); return true;
            }
            case "--drag":
            {
                var v = next().Split(',').Select(int.Parse).ToArray();
                OnMouseMove(null, new MouseEventArgs(MouseButtons.None, 0, v[0], v[1], 0));
                OnMouseDown(null, new MouseEventArgs(MouseButtons.Left, 1, v[0], v[1], 0));
                for (int i = 1; i <= 10; i++) OnMouseMove(null, new MouseEventArgs(MouseButtons.Left, 0, v[0] + (v[2] - v[0]) * i / 10, v[1] + (v[3] - v[1]) * i / 10, 0));
                Render();
                if (v.Length < 5 || v[4] != 0) OnMouseUp(null, new MouseEventArgs(MouseButtons.Left, 1, v[2], v[3], 0));
                log($"script: drag {v[0]},{v[1]} -> {v[2]},{v[3]}; selection at {(Selected != null ? Fmt(Selected.Transform.Translation) : "-")}; transforming {Transforming}"); return true;
            }
            case "--mode-cycle":
            {
                int n = int.Parse(next()); var keep = ViewMode;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < n; i++)
                    foreach (var m in Enum.GetValues<ViewMode>()) { ViewMode = m; Render(); Application.DoEvents(); }
                ViewMode = keep; Render();
                log($"script: cycled view modes {n}x in {sw.ElapsedMilliseconds} ms, back to {ViewMode}"); return true;
            }
            case "--minimize-restore":
            {
                var form = FindForm()!;
                for (int i = 0; i < 3; i++)
                {
                    form.WindowState = FormWindowState.Minimized; Application.DoEvents(); await Task.Delay(300); Render(); Application.DoEvents();
                    form.WindowState = FormWindowState.Normal; Application.DoEvents(); await Task.Delay(300); Render(); Application.DoEvents();
                }
                form.WindowState = FormWindowState.Maximized; Application.DoEvents(); await Task.Delay(300); Render();
                form.WindowState = FormWindowState.Normal; Application.DoEvents(); await Task.Delay(300); Render();
                log($"script: minimized/restored/maximized, view {_gl.Width}x{_gl.Height}"); return true;
            }
            case "--pick":
            {
                // the draw under a view pixel: object, model, draw index, material reading and bound textures
                var v = next().Split(',').Select(int.Parse).ToArray();
                var (ro, rd) = Ray(new Point(v[0], v[1]));
                (float T, NB.Core.World.SceneObject? O, NB.Core.Models.ModelAsset? M, int D) best = (float.MaxValue, null, null, -1);
                if (Scene != null)
                    foreach (var o in Scene.Objects)
                    {
                        if (!o.Visible || o.Model == null || (o.Kind == NB.Core.World.SceneObjectKind.Scenery && IsHidden(o))) continue;
                        foreach (var (m, local) in new[] { (o.Model, Matrix4x4.Identity) }.Concat(o.Children))
                        {
                            if (!Matrix4x4.Invert(local * o.Transform, out var inv)) continue;
                            var lo = Vector3.Transform(ro, inv); var ld = Vector3.TransformNormal(rd, inv);
                            for (int di = 0; di < m.Draws.Count; di++)
                            {
                                var dr = m.Draws[di]; if (m.LodOnlyNodes.Contains(dr.Node)) continue;
                                float t = RayDraw(lo, ld, dr);
                                if (t < best.T) best = (t, o, m, di);
                            }
                        }
                    }
                if (best.O == null) { log("script: pick: nothing"); return true; }
                var d = best.M!.Draws[best.D]; var mi = MaterialInfo.Of(d);
                var (lo2, ld2) = (ro, rd);
                log($"script: pick {v[0]},{v[1]}: hit point {Fmt(lo2 + ld2 * best.T)} (world space approximated by the ray distance in model space)");
                log($"script: pick {v[0]},{v[1]}: {best.O.Name} model {NB.Core.Formats.AssetIds.DisplayName(best.M.View.Name)} draw #{best.D} flags {d.SectionFlags:X} blend {mi.Blend} tint {mi.Tint} opacity {mi.Opacity} spec {mi.SpecPower} vcol {mi.VertexColour}");
                log($"    base {mi.Base} over {mi.Overlay} mask {mi.Mask} normal {mi.Normal} spec {mi.Spec} refl {mi.Reflect} ao {mi.Ao} alpha {mi.AlphaTex}");
                log($"    textures: {string.Join(" | ", d.Textures.Select(t => $"s{t.Slot}:{t.Texture}"))}");
                if (d.Passes.Length > 0) log($"    pass0 c: {string.Join(" ", d.Passes[0].Constants.OrderBy(k => k.Key).Select(k => $"c{k.Key}={k.Value}"))}");
                return true;
            }
            case "--sel-info":
            {
                if (Selected == null) { log("script: nothing selected"); return true; }
                var t = Selected.Transform;
                log($"script: {Selected.Name} at {Fmt(t.Translation)} scale ({new Vector3(t.M11, t.M12, t.M13).Length():0.###}, {new Vector3(t.M21, t.M22, t.M23).Length():0.###}, {new Vector3(t.M31, t.M32, t.M33).Length():0.###}) x-axis {Fmt(Vector3.Normalize(new Vector3(t.M11, t.M12, t.M13)))}");
                var s = ToScreen(t.Translation); if (s != null) log($"script: selection on screen at {s.Value.X:0},{s.Value.Y:0}");
                var ax = GizmoAxes(); float len = GizmoLength();
                for (int i = 0; i < 3; i++) if (ToScreen(t.Translation + ax[i] * len * 0.7f) is { } h) log($"script: {AxisName[i]} handle on screen at {h.X:0},{h.Y:0}");
                return true;
            }
        }
        return false;
    }

    static float RayDraw(Vector3 o, Vector3 d, NB.Core.Models.MeshDraw dr)
    {
        float best = float.MaxValue; var P = dr.Positions; var I = dr.Indices;
        for (int k = 0; k + 2 < I.Length; k += 3)
        {
            int a = I[k], b = I[k + 1], c = I[k + 2];
            if (a >= P.Length || b >= P.Length || c >= P.Length) continue;
            var e1 = P[b] - P[a]; var e2 = P[c] - P[a];
            var pv = Vector3.Cross(d, e2); float det = Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-9f) continue;
            float inv = 1 / det; var tv = o - P[a];
            float u = Vector3.Dot(tv, pv) * inv; if (u < 0 || u > 1) continue;
            var qv = Vector3.Cross(tv, e1); float v = Vector3.Dot(d, qv) * inv; if (v < 0 || u + v > 1) continue;
            float t = Vector3.Dot(e2, qv) * inv;
            if (t > 0 && t < best) best = t;
        }
        return best;
    }

    static string Fmt(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})");
}
