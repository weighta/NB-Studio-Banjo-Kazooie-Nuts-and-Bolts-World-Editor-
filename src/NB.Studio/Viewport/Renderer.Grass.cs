using System.Numerics;
using NB.Core.World;
using OpenTK.Graphics.OpenGL4;
using PixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace NB.Studio.Viewport;

/// <summary>
/// Grass layers (chunk 17) drawn the way the game's grass vertex shader does it (see <see cref="GrassLayer"/>): the grass
/// model's blades are one tile, drawn instanced at every tile of the layer; the vertex shader turns and scales the tile,
/// lifts each vertex by the layer's height texture, colours it with the shadow texture and drops the blades whose alpha
/// is above the shadow texture's alpha (density). The grass model's LOD levels choose the density nodes by tile distance.
/// </summary>
public sealed partial class Renderer
{
    const string GVS = """
        #version 330 core
        layout(location=0) in vec3 aPos; layout(location=1) in vec2 aUV; layout(location=2) in vec4 aCol;
        layout(location=3) in vec4 iPos; layout(location=4) in vec2 iRot;
        uniform mat4 uVP; uniform vec3 uMin; uniform vec3 uSize; uniform sampler2D tHeight; uniform sampler2D tDensity; uniform int uDebug;
        out vec2 vUV; out vec3 vCol; out vec3 vW;
        void main() {
          vec3 l = vec3(aPos.x, aPos.y * iPos.w, aPos.z);
          vec3 w = iPos.xyz + vec3(iRot.x * l.x + iRot.y * l.z, l.y, -iRot.y * l.x + iRot.x * l.z);
          vec2 tuv = (w.xz - uMin.xz) / uSize.xz;
          w.y += textureLod(tHeight, tuv, 0.0).r * uSize.y;
          vec4 sh = textureLod(tDensity, tuv, 0.0);
          vCol = aCol.rgb * sh.rgb; vUV = aUV; vW = w;
          // the game kills the vertex (and so the blade) when its alpha is above the density
          gl_Position = (aCol.a > sh.a && uDebug != 1) ? vec4(2.0, 2.0, 2.0, 1.0) : uVP * vec4(w, 1.0);
          if (uDebug == 2) vCol = vec3(sh.a, aCol.a, 0.0);
        }
        """;
    const string GFS = """
        #version 330 core
        in vec2 vUV; in vec3 vCol; in vec3 vW; out vec4 o;
        uniform sampler2D tDiff; uniform int uHasTex; uniform int uMode; uniform vec3 uLight; uniform vec3 uEye;
        uniform vec4 uFog; uniform vec3 uFogCol; uniform vec4 uFog2; uniform vec3 uGradeA; uniform vec3 uGradeB; uniform float uPost;
        void main() {
          vec4 t = uHasTex == 1 ? texture(tDiff, vUV) : vec4(0.45, 0.62, 0.30, 1.0);
          if (t.a < 0.5) discard;
          if (uMode == 0) { o = vec4(0.55, 0.85, 0.40, 1.0); return; }
          if (uMode == 1) { o = vec4(vec3(0.70, 0.74, 0.66) * mix(vec3(1.0), vCol, 0.5), 1.0); return; }
          vec3 rgb = t.rgb * vCol;
          if (uMode == 3) {
            rgb = clamp(rgb * uLight, 0.0, 1.0);
            rgb = min(rgb * uPost, vec3(1.5));
            if (uFog.w > 0.5) {
              float d = length(uEye - vW);
              float f = clamp((d - uFog.x) / max(1.0, uFog.y - uFog.x), 0.0, 1.0) * uFog.z;
              float f2 = clamp((d - uFog2.x) / max(1.0, uFog2.y - uFog2.x), 0.0, 1.0) * uFog2.z;
              rgb = mix(rgb, uFogCol, 1.0 - (1.0 - f) * (1.0 - f2));
            }
            rgb = clamp(rgb * uGradeA + uGradeB, 0.0, 1.0);
          } else rgb *= 0.92;
          o = vec4(rgb, 1.0);
        }
        """;

    int _gProg;
    readonly Dictionary<string, int> _gu = new();
    int GU(string n) { if (!_gu.TryGetValue(n, out int l)) _gu[n] = l = GL.GetUniformLocation(_gProg, n); return l; }

    sealed class GrassGpu
    {
        public int Vao, Vbo, Ebo, Inst, HeightTex, DensityTex;
        public string DensityKey = "";
        public readonly List<(int Node, string? Texture, int First, int Count)> Parts = new();
        public float[] Ground = Array.Empty<float>();
        public int TileVersion = -1;
    }
    readonly Dictionary<GrassLayer, GrassGpu> _grass = new(ReferenceEqualityComparer.Instance);
    /// <summary>Grass tiles drawn in the last frame (all layers).</summary>
    public int GrassTilesDrawn;

    /// <summary>Scale of the grass light in the Rendered mode (sun × elevation + sky ambient); the game's grass pixel shader
    /// multiplies by sun colour × sun height + c49.</summary>
    public float GrassLightScale = 1f;
    /// <summary>Debug: 1 = no density kill, 2 = colour by density (red) and blade alpha (green).</summary>
    public int GrassDebug;

    static int UploadData(byte[] rgba, int w, int h)
    {
        int t = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, t);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return t;
    }

    GrassGpu? GrassFor(GrassLayer layer, (byte[] Rgba, int W, int H) height, string densityKey, (byte[] Rgba, int W, int H) density, int tileVersion)
    {
        if (layer.Mesh == null) return null;
        if (_gProg == 0)
        {
            _gProg = Link(GVS, GFS);
            GL.UseProgram(_gProg);
            GL.Uniform1(GU("tDiff"), 0); GL.Uniform1(GU("tHeight"), 1); GL.Uniform1(GU("tDensity"), 2);
            _cur = null;
        }
        if (!_grass.TryGetValue(layer, out var g))
        {
            var m = layer.Mesh;
            g = new GrassGpu();
            var buf = new float[m.Positions.Length * 9];
            for (int i = 0; i < m.Positions.Length; i++)
            {
                var p = m.Positions[i]; var t = i < m.UVs.Length ? m.UVs[i] : Vector2.Zero; uint c = i < m.Colors.Length ? m.Colors[i] : 0xFFFFFFFF;
                int o = i * 9;
                buf[o] = p.X; buf[o + 1] = p.Y; buf[o + 2] = p.Z; buf[o + 3] = t.X; buf[o + 4] = t.Y;
                buf[o + 5] = ((c >> 16) & 0xFF) / 255f; buf[o + 6] = ((c >> 8) & 0xFF) / 255f; buf[o + 7] = (c & 0xFF) / 255f; buf[o + 8] = (c >> 24) / 255f;   // 0xAARRGGBB
            }
            var idx = new List<uint>();
            foreach (var (node, tex, tris) in m.Parts) { g.Parts.Add((node, tex, idx.Count, tris.Length)); idx.AddRange(tris.Select(x => (uint)x)); }
            g.Vao = GL.GenVertexArray(); g.Vbo = GL.GenBuffer(); g.Ebo = GL.GenBuffer(); g.Inst = GL.GenBuffer();
            GL.BindVertexArray(g.Vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, g.Vbo); GL.BufferData(BufferTarget.ArrayBuffer, buf.Length * 4, buf, BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 36, 0);
            GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 36, 12);
            GL.EnableVertexAttribArray(2); GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, 36, 20);
            GL.BindBuffer(BufferTarget.ArrayBuffer, g.Inst);
            GL.EnableVertexAttribArray(3); GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, 24, 0); GL.VertexAttribDivisor(3, 1);
            GL.EnableVertexAttribArray(4); GL.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, 24, 16); GL.VertexAttribDivisor(4, 1);
            var ia = idx.ToArray();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, g.Ebo); GL.BufferData(BufferTarget.ElementArrayBuffer, ia.Length * 4, ia, BufferUsageHint.StaticDraw);
            GL.BindVertexArray(0);
            g.HeightTex = UploadData(height.Rgba, height.W, height.H);
            _grass[layer] = g;
        }
        if (g.DensityKey != densityKey)
        {
            if (g.DensityTex != 0) GL.DeleteTexture(g.DensityTex);
            g.DensityTex = UploadData(density.Rgba, density.W, density.H);
            g.DensityKey = densityKey;
        }
        if (g.TileVersion != tileVersion || g.Ground.Length != layer.Tiles.Count)
        {
            // ground height under each tile (distance and culling use the lifted tile, not the layer's floor)
            g.Ground = new float[layer.Tiles.Count];
            var size = layer.Size;
            for (int i = 0; i < layer.Tiles.Count; i++)
            {
                var p = layer.Tiles[i].Position;
                int x = Math.Clamp((int)((p.X - layer.Min.X) / size.X * height.W), 0, height.W - 1), y = Math.Clamp((int)((p.Z - layer.Min.Z) / size.Z * height.H), 0, height.H - 1);
                g.Ground[i] = layer.Min.Y + height.Rgba[(y * height.W + x) * 4] / 255f * size.Y;
            }
            g.TileVersion = tileVersion;
        }
        return g;
    }

    /// <summary>Draws one grass layer (call between <see cref="Begin"/> and <see cref="FlushTransparent"/>).</summary>
    public void DrawGrass(GrassLayer layer, (byte[] Rgba, int W, int H) height, string densityKey, (byte[] Rgba, int W, int H) density, int tileVersion, Frustum? frustum)
    {
        if (layer.Mesh == null || layer.Tiles.Count == 0) return;
        float reach = layer.MaxDistance + layer.Spacing;
        // whole layer: in reach and in view
        var c = (layer.Min + layer.Max) / 2; float r = (layer.Max - layer.Min).Length() / 2;
        var q = Vector3.Clamp(_eye, layer.Min, layer.Max + new Vector3(0, 4, 0));
        if (Vector3.Distance(q, _eye) > reach) return;
        if (frustum is { } f0 && !f0.Visible(c, r + 4)) return;
        var g = GrassFor(layer, height, densityKey, density, tileVersion);
        if (g == null) return;
        var mesh = layer.Mesh;
        int levels = mesh.Levels.Count;
        var buckets = new List<float>[Math.Max(1, levels)];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<float>();
        float tr = layer.Spacing * 0.75f + 2f * layer.ScaleMax;
        for (int i = 0; i < layer.Tiles.Count; i++)
        {
            var t = layer.Tiles[i];
            var centre = new Vector3(t.Position.X, g.Ground[i] + 0.5f, t.Position.Z);
            float d = Vector3.Distance(centre, _eye);
            if (d > reach) continue;
            if (frustum is { } f && !f.Visible(centre, tr)) continue;
            int lv = 0;
            for (int k = 0; k < levels; k++) if (mesh.Levels[k].Distance <= d) lv = k;
            var b = buckets[lv];
            float cs = t.Rotation switch { 0 => 1, 2 => -1, _ => 0 }, sn = t.Rotation switch { 1 => 1, 3 => -1, _ => 0 };
            b.Add(t.Position.X); b.Add(t.Position.Y); b.Add(t.Position.Z); b.Add(t.ScaleY); b.Add(cs); b.Add(sn);
        }
        GL.UseProgram(_gProg); _cur = null;
        UniformMat(GU("uVP"), _viewProj);
        GL.Uniform3(GU("uMin"), layer.Min.X, layer.Min.Y, layer.Min.Z);
        var sz = layer.Size; GL.Uniform3(GU("uSize"), sz.X, sz.Y, sz.Z);
        GL.Uniform1(GU("uMode"), (int)Mode);
        GL.Uniform1(GU("uDebug"), GrassDebug);
        var l = Lighting;
        var light = (l.Sun * l.Intensity * MathF.Max(0, Vector3.Normalize(l.SunDirection).Y) + l.Ambient * l.AmbientBase) * GrassLightScale;   // c49 taken as the SH constant term
        GL.Uniform3(GU("uLight"), light.X, light.Y, light.Z);
        GL.Uniform3(GU("uEye"), _eye.X, _eye.Y, _eye.Z);
        GL.Uniform4(GU("uFog"), l.FogStart, l.FogEnd, l.FogMax, l.Fog ? 1f : 0f);
        GL.Uniform3(GU("uFogCol"), l.FogColour.X, l.FogColour.Y, l.FogColour.Z);
        GL.Uniform4(GU("uFog2"), l.Fog2Start, l.Fog2End, l.Fog && Fog2 ? Math.Clamp(l.Fog2Max, 0, 1) : 0f, 0f);
        GL.Uniform3(GU("uGradeA"), GradeScale.X, GradeScale.Y, GradeScale.Z);
        GL.Uniform3(GU("uGradeB"), GradeOffset.X, GradeOffset.Y, GradeOffset.Z);
        GL.Uniform1(GU("uPost"), Exposure > 0 ? 1f : l.PostScale);
        GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, g.HeightTex);
        GL.ActiveTexture(TextureUnit.Texture2); GL.BindTexture(TextureTarget.Texture2D, g.DensityTex);
        GL.ActiveTexture(TextureUnit.Texture0);
        Array.Fill(_bound, -1);
        GL.BindVertexArray(g.Vao);
        string? boundTex = "\0";
        for (int k = 0; k < buckets.Length; k++)
        {
            var b = buckets[k];
            int n = b.Count / 6;
            if (n == 0) continue;
            GrassTilesDrawn += n;
            var nodes = levels > 0 ? mesh.Levels[k].Nodes : null;
            GL.BindBuffer(BufferTarget.ArrayBuffer, g.Inst);
            GL.BufferData(BufferTarget.ArrayBuffer, b.Count * 4, b.ToArray(), BufferUsageHint.StreamDraw);
            foreach (var (node, tex, first, count) in g.Parts)
            {
                if (nodes != null && !nodes.Contains(node)) continue;
                if (tex != boundTex)
                {
                    int tx = Texture(tex);
                    GL.BindTexture(TextureTarget.Texture2D, tx);
                    GL.Uniform1(GU("uHasTex"), tx != 0 ? 1 : 0);
                    boundTex = tex;
                }
                GL.DrawElementsInstanced(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt, (IntPtr)(first * 4), n);
                DrawsIssued++;
            }
        }
        GL.BindVertexArray(0);
    }

    void ClearGrass()
    {
        foreach (var g in _grass.Values)
        {
            GL.DeleteVertexArray(g.Vao); GL.DeleteBuffer(g.Vbo); GL.DeleteBuffer(g.Ebo); GL.DeleteBuffer(g.Inst);
            if (g.HeightTex != 0) GL.DeleteTexture(g.HeightTex);
            if (g.DensityTex != 0) GL.DeleteTexture(g.DensityTex);
        }
        _grass.Clear();
    }
}
