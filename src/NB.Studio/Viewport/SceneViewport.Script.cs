using System.Globalization;
using System.Numerics;
using NB.Core.Models;

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
                Render(); log($"script: view mode {ViewMode}" + (ViewMode is ViewMode.Rendered or ViewMode.Textured ? $", light {LightingName}, sky {SkyName ?? "none"}, exposure {(_r.Exposure > 0 ? _r.Exposure : _r.Lighting.AutoExposure):0.00}, fill {_r.Lighting.FillColour}" : ""));
                return true;
            }
            case "--light": NextLight(next()); log($"script: light {LightingName}: {_r.Lighting.SunDirection} sun {_r.Lighting.Sun} x{_r.Lighting.Intensity} amb {_r.Lighting.Ambient} fog {_r.Lighting.Fog}"); return true;
            case "--sun":
            {
                var v = next().Split(',').Select(F).ToArray();
                EnsureLighting(); _r.Lighting.SunDirection = Vector3.Normalize(new Vector3(v[0], v[1], v[2])); _gl.Invalidate();
                log($"script: sun direction {_r.Lighting.SunDirection}"); return true;
            }
            case "--xlate-stats":
            {
                Render();
                log($"script: translated programs {_r.ProgramsCompiled}, failed {_r.ProgramsFailed}" + (_r.LastProgramError != null ? Environment.NewLine + _r.LastProgramError[..Math.Min(6000, _r.LastProgramError.Length)] : ""));
                return true;
            }
            case "--shadow-cull": _r.ShadowCull = int.Parse(next()); _r.InvalidateShadow(); _gl.Invalidate(); log($"script: shadow cull {_r.ShadowCull}"); return true;
            case "--grade":
            {
                var v = next().Split(',').Select(F).ToArray();
                _r.GradeScale = new Vector3(v[0], v[1], v[2]); _r.GradeOffset = new Vector3(v[3], v[4], v[5]); _gl.Invalidate();
                log($"script: grade {_r.GradeScale} {_r.GradeOffset}"); return true;
            }
            case "--shadow-casters": ShadowCasters = int.Parse(next()); _gl.Invalidate(); log($"script: shadow casters {ShadowCasters}"); return true;
            case "--shadow-reach": ShadowReach = F(next()); _gl.Invalidate(); log($"script: shadow reach {ShadowReach}"); return true;
            case "--amb-scale": { float k = F(next()); EnsureLighting(); _r.Lighting.Ambient *= k; _r.Lighting.AmbientUp = F(next()); _gl.Invalidate(); log($"script: ambient {_r.Lighting.Ambient} up {_r.Lighting.AmbientUp}"); return true; }
            case "--fog2": _r.Fog2 = next() == "on"; _gl.Invalidate(); log($"script: fog2 {_r.Fog2}"); return true;
            case "--exposure": _r.Exposure = F(next()); _gl.Invalidate(); log($"script: exposure {_r.Exposure}"); return true;
            case "--shadows": _r.Shadows = next() == "on"; if (!_r.Shadows) _r.InvalidateShadow(); _gl.Invalidate(); log($"script: shadows {_r.Shadows}"); return true;
            case "--hide-objects":
            {
                // hide objects drawn at markers whose model source or name contains the text (performance / audit checks)
                var q = next(); int n = 0;
                if (Scene != null)
                    foreach (var o in Scene.Objects)
                        if (o.Kind == NB.Core.World.SceneObjectKind.Marker && o.Model != null && (o.ModelSource.Contains(q, StringComparison.OrdinalIgnoreCase) || o.Name.Contains(q, StringComparison.OrdinalIgnoreCase))) { o.Visible = false; n++; }
                _linesVersion++; _gl.Invalidate(); log($"script: hid {n} objects matching '{q}'"); return true;
            }
            case "--lod-cull": LodCulling = next() == "on"; _gl.Invalidate(); log($"script: LOD distance culling {(LodCulling ? "on" : "off")}"); return true;
            case "--fov": FieldOfView = F(next()); log($"script: fov {FieldOfView}"); return true;
            case "--visgroup":
            {
                // --visgroup water off : the same path as the "Show" menu
                var name = next(); bool on = next() is "on" or "1";
                var g = Enum.Parse<Visgroup>(name, ignoreCase: true); SetVisible(g, on); Render();
                log($"script: visgroup {g} {(on ? "shown" : "hidden")}; {string.Join(", ", VisgroupLabels.Select(v => $"{v.Group}={(IsVisible(v.Group) ? "on" : "off")}"))}"); return true;
            }
            case "--visgroups-shot":
            {
                // the 3D view with the "Show" menu open, drawn into one picture
                var file = next();
                ShowVisgroupsMenu(); Application.DoEvents();
                using var v = Capture();
                using (var g = Graphics.FromImage(v))
                using (var m = new Bitmap(_visMenu.Width, _visMenu.Height))
                {
                    _visMenu.DrawToBitmap(m, new Rectangle(0, 0, m.Width, m.Height));
                    var at = _gl.PointToClient(_visMenu.Bounds.Location);
                    g.DrawImage(m, at.X, at.Y);
                }
                _visMenu.Close();
                v.Save(file); log("script: visgroups menu captured"); return true;
            }
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
                switch (what) { case "markers": ShowMarkers = on; break; case "paths": ShowPaths = on; break; case "terrain": ShowTerrain = on; break; case "scenery": ShowScenery = on; break; case "objects": ShowObjects = on; break; case "grass": ShowGrass = on; break; }
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
            case "--keyseq":
            {
                // held keys like a person: "+W" press (repeats count as key repeat), "-W" release, "tick" one fly
                // step, "rmb+" / "rmb-" right button down / up (looking), "sleep700" wait 700 ms
                var seq = next(); var log2 = new List<string>();
                foreach (var t in seq.Split(','))
                {
                    if (t == "tick") { TickCore(); continue; }
                    if (t == "rmb+") { OnMouseDown(null, new MouseEventArgs(MouseButtons.Right, 1, 300, 300, 0)); OnMouseMove(null, new MouseEventArgs(MouseButtons.Right, 0, 320, 300, 0)); continue; }
                    if (t == "rmb-") { OnMouseUp(null, new MouseEventArgs(MouseButtons.Right, 1, 320, 300, 0)); continue; }
                    if (t.StartsWith("sleep")) { Thread.Sleep(int.Parse(t[5..])); continue; }
                    var key = Enum.Parse<Keys>(t[1..], true);
                    if (t[0] == '+') { bool fresh = _keys.Add(key); OnKey(new KeyEventArgs(key), fresh); }
                    else { _keys.Remove(key); if (key == Keys.S) _sFlies = false; }
                    log2.Add($"{t}:{Transforming}");
                }
                var cam = _camPos;
                log($"script: keyseq {seq} -> {string.Join(" ", log2)}; transforming {Transforming}; camera {Fmt(cam)}");
                if (Transforming) CancelTransform();
                return true;
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
            case "--mouse-up":
            {
                var v = next().Split(',').Select(int.Parse).ToArray();
                OnMouseUp(null, new MouseEventArgs(MouseButtons.Left, 1, v[0], v[1], 0)); Render();
                log($"script: mouse up at {v[0]},{v[1]}; transforming {Transforming}" + (Selected != null ? $"; selection at {Fmt(Selected.Transform.Translation)}" : "")); return true;
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
                        if (!DrawsModel(o, keepSelected: false)) continue;
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
                if (mi.Shader is { } tr)
                {
                    log($"    shader: {tr}; normal tf{tr.NormalSampler}; uv2 samplers [{string.Join(",", tr.SamplerUv2)}]; vcol {tr.UsesVertexColour}; textures {string.Join(" ", mi.SamplerTextures.Select(k => $"tf{k.Key}:{k.Value}"))}");
                    log(tr.Body);
                }
                else if (d.ColourShader is { } sh2) log("    shader not translated: " + XenosTranslator.Translate(sh2, d.Passes.Length > 0 ? d.Passes[0].Constants : d.PixelConstants, d.Colors != null, d.UVs2 != null).Fail);
                if (Environment.GetEnvironmentVariable("NB_PICK_DISASM") == "1" && d.ColourShader is { } sh3) log(sh3.Disassemble());
                return true;
            }
            case "--sunray":
            {
                // the first surface between a view pixel's surface point and the sun (shadow debugging)
                var v = next().Split(',').Select(int.Parse).ToArray();
                var (ro, rd) = Ray(new Point(v[0], v[1]));
                var hit = RayScene(ro, rd);
                if (hit.O == null) { log("script: sunray: nothing under the pixel"); return true; }
                var p0 = ro + rd * hit.T;
                var sd = Vector3.Normalize(_r.Lighting.SunDirection);
                var h2 = RayScene(p0 + sd * 0.05f, sd);
                log($"script: sunray from {Fmt(p0)} ({hit.O.Name}) towards {Fmt(sd)}: " + (h2.O == null ? "lit" : $"blocked by {h2.O.Name} ({NB.Core.Formats.AssetIds.DisplayName(h2.M!.View.Name)}) draw #{h2.D} flags {h2.M.Draws[h2.D].SectionFlags:X} at {h2.T:0.0} units, point {Fmt(p0 + sd * h2.T)}"));
                return true;
            }
            case "--grass-wait":
            {
                // waits until the grass layers are laid out (background task), then renders a frame and reports
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!GrassReady && Scene != null && Scene.Grass.Count > 0 && sw.ElapsedMilliseconds < 120000) { Application.DoEvents(); await Task.Delay(100); }
                Render();
                log($"script: {GrassInfo} (after {sw.ElapsedMilliseconds} ms); last frame drew {_r.GrassTilesDrawn} grass tiles");
                return true;
            }
            case "--collision-all":
            {
                // --collision-all on|off: the collision toggle next to the view-mode bar; waits for the decoding
                ShowCollision = next() == "on";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (ShowCollision && !CollisionReady && sw.ElapsedMilliseconds < 180000) { Application.DoEvents(); await Task.Delay(100); }
                for (int i = 0; i < 400 && ShowCollision; i++) { Render(); Application.DoEvents(); }   // line batches are built a few per frame
                var bsw = System.Diagnostics.Stopwatch.StartNew(); Render(); _gl.MakeCurrent(); OpenTK.Graphics.OpenGL4.GL.Finish();
                log($"script: collision of all objects {(ShowCollision ? "on" : "off")}: {CollisionSummary}; last frame drew {_collObjectsDrawn} objects in {bsw.Elapsed.TotalMilliseconds:F0} ms (after {sw.ElapsedMilliseconds} ms)"); return true;
            }
            case "--hover":
            {
                // --hover X,Y: mouse rests over a view pixel (the start-point tooltip)
                var v = next().Split(',').Select(int.Parse).ToArray();
                OnMouseMove(null, new MouseEventArgs(MouseButtons.None, 0, v[0], v[1], 0)); HoverTick();
                log($"script: hover {v[0]},{v[1]}: {(_tipFor != null ? "tooltip for " + _tipFor.Name : "no tooltip")} (picked {Pick(new Point(v[0], v[1])).Obj?.Name ?? "-"})"); return true;
            }
            case "--sel-collision":
            {
                ShowSelectionCollision = next() == "on"; Render();
                log($"script: selection collision {(ShowSelectionCollision ? "on" : "off")}: {SelectionCollisionInfo}"); return true;
            }
            case "--grass-debug": _r.GrassDebug = int.Parse(next()); _gl.Invalidate(); log($"script: grass debug {_r.GrassDebug}"); return true;
            case "--grass-light": _r.GrassLightScale = F(next()); _gl.Invalidate(); log($"script: grass light scale {_r.GrassLightScale}"); return true;
            case "--sel-mat":
            {
                // materials of the selected object's model (debugging: which textures, blend, translated shader)
                if (Selected?.Model == null) { log("script: no model selected"); return true; }
                var mdl = Selected.Model;
                for (int di = 0; di < mdl.Draws.Count; di++)
                {
                    var d = mdl.Draws[di]; var mi = MaterialInfo.Of(d);
                    log($"script: draw #{di} node {d.Node} flags {d.SectionFlags:X} blend {mi.Blend} base {mi.Base} alpha {mi.AlphaTex} uv2 {(d.UVs2 != null)} layout [{string.Join(" ", d.Layout)}]");
                    if (d.ColourVertexShader is { } vsx && d.Passes.Length > 0)
                        log("    interpolators: " + string.Join(" ", XenosShader.InterpolatorUvs(vsx, d.Passes[0].VsConstants, d.Layout).OrderBy(k => k.Key).Select(k => $"{(k.Key & 0xFF)}{((k.Key & XenosShader.ZwKey) != 0 ? ".zw" : ".xy")}=set{k.Value.Set}{(k.Value.Swap ? " swapped" : "")}{(k.Value.RowX != null ? $" rows {k.Value.RowX} {k.Value.RowY}" : "")}")) + $"; uv sets decoded: {(d.UVs != null ? 1 : 0) + (d.UVs2 != null ? 1 : 0) + (d.UVs3 != null ? 1 : 0)}");
                    if (mi.Shader is { } tr) { log($"    shader: {tr}; uv2 samplers [{string.Join(",", tr.SamplerUv2)}]"); if (di == 0) log(tr.Body); }
                    else if (d.ColourShader is { } sh2) log("    not translated: " + XenosTranslator.Translate(sh2, d.Passes.Length > 0 ? d.Passes[0].Constants : d.PixelConstants, d.Colors != null, d.UVs2 != null).Fail);
                    if (di == 0 && d.ColourShader is { } sh3) log(sh3.Disassemble());
                    if (di == 0 && d.ColourVertexShader is { } vs3) log(vs3.Disassemble());
                }
                return true;
            }
            case "--list-objects": { var f = next(); foreach (var o in Scene?.Objects.Where(o => o.Name.Contains(f, StringComparison.OrdinalIgnoreCase)) ?? Enumerable.Empty<NB.Core.World.SceneObject>()) log($"script: object {o.Name} at {Fmt(o.Transform.Translation)} model {(o.Model != null ? "yes" : "no")}"); return true; }
            case "--idle-poses": IdlePoses = next() == "on"; log($"script: idle poses {(IdlePoses ? "on" : "off")}"); return true;
            case "--idle-info": log("script: idle poses: " + IdlePoseInfo + "; posed " + string.Join(", ", _posed.Keys.Select(o => o.Name))); return true;

            case "--deselect": Select(null); log("script: selection cleared"); return true;
            case "--xlate-audit":
            {
                // every model of the open world (objects and marker objects): draws whose pixel shader is not translated,
                // grouped by reason, with the models (names containing the filter argument, "all" for every model)
                var filter = next();
                var fails = new Dictionary<string, HashSet<string>>(); int draws = 0, failed = 0;
                if (Scene != null)
                    foreach (var o in Scene.Objects)
                        foreach (var m in o.Children.Select(c => c.Model).Prepend(o.Model))
                        {
                            if (m == null || (filter != "all" && !o.ModelName.Contains(filter, StringComparison.OrdinalIgnoreCase) && !o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))) continue;
                            foreach (var d in m.Draws)
                            {
                                if (d.ColourShader == null) continue;
                                draws++;
                                if (MaterialInfo.Of(d).Shader != null) continue;
                                failed++;
                                var why = XenosTranslator.Translate(d.ColourShader, d.Passes.Length > 0 ? d.Passes[0].Constants : d.PixelConstants, d.Colors != null, d.UVs2 != null).Fail ?? "?";
                                why = System.Text.RegularExpressions.Regex.Replace(why, @"r\d+\.[xyzw]|pc \d+", "#");
                                if (!fails.TryGetValue(why, out var set)) fails[why] = set = new();
                                set.Add(o.Kind == NB.Core.World.SceneObjectKind.Marker ? System.Text.RegularExpressions.Regex.Replace(o.Name, @"#\d+ ", "") : NB.Core.Formats.AssetIds.DisplayName(o.ModelName).Replace("aid_model_banjox_", ""));
                            }
                        }
                log($"script: shader audit ({filter}): {draws} draws with a pixel shader, {failed} not translated");
                foreach (var (why, set) in fails.OrderByDescending(kv => kv.Value.Count)) log($"    {why}: {set.Count} models: {string.Join(", ", set.Take(12))}{(set.Count > 12 ? " …" : "")}");
                return true;
            }
            case "--sel-mat-draw":
            {
                // one draw of the selected model: textures, pixel constants, pixel and vertex shader disassembly
                int di = int.Parse(next());
                if (Selected?.Model == null || di >= Selected.Model.Draws.Count) { log("script: no such draw"); return true; }
                var d = Selected.Model.Draws[di];
                log($"script: draw #{di} textures {string.Join(" | ", d.Textures.Select(t => $"s{t.Slot}:{t.Texture}"))}");
                log("    constants " + string.Join(" ", d.PixelConstants.Select(kv => $"c{kv.Key}={kv.Value}")));
                if (d.ColourShader is { } ps) log(ps.Disassemble());
                if (d.ColourVertexShader is { } vs) log(vs.Disassemble());
                return true;
            }
            case "--sel-info":
            {
                if (Selected == null) { log("script: nothing selected"); return true; }
                var t = Selected.Transform;
                log($"script: {Selected.Name} at {Fmt(t.Translation)} scale ({new Vector3(t.M11, t.M12, t.M13).Length():0.###}, {new Vector3(t.M21, t.M22, t.M23).Length():0.###}, {new Vector3(t.M31, t.M32, t.M33).Length():0.###}) x-axis {Fmt(Vector3.Normalize(new Vector3(t.M11, t.M12, t.M13)))}");
                var s = ToScreen(t.Translation); if (s != null) log($"script: selection on screen at {s.Value.X:0},{s.Value.Y:0}");
                var gp = GizmoPoint();
                if (_extra.Count > 0 && SelectionBox() is { } sb)
                    log($"script: {_extra.Count + 1} objects, box {Fmt(sb.Min)}..{Fmt(sb.Max)}, gizmo at {Fmt(gp)}" + (ToScreen(gp) is { } gs ? $", on screen at {gs.X:0},{gs.Y:0}" : ""));
                var ax = GizmoAxes(); float len = GizmoLength();
                for (int i = 0; i < 3; i++) if (ToScreen(gp + ax[i] * len * 0.7f) is { } h) log($"script: {AxisName[i]} handle on screen at {h.X:0},{h.Y:0}");
                return true;
            }
        }
        return false;
    }

    (float T, NB.Core.World.SceneObject? O, NB.Core.Models.ModelAsset? M, int D) RayScene(Vector3 ro, Vector3 rd)
    {
        (float T, NB.Core.World.SceneObject? O, NB.Core.Models.ModelAsset? M, int D) best = (float.MaxValue, null, null, -1);
        if (Scene == null) return best;
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
        return best;
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
