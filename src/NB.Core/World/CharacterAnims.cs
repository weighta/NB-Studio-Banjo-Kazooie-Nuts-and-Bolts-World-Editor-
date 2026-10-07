using System.Numerics;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Models;
using NB.Core.Project;

namespace NB.Core.World;

/// <summary>One action of an animtable: the name the game's code asks for, the animation it plays.</summary>
public sealed record AnimAction(string Action, uint AnimId, string AnimName, uint EventsId, float BlendIn, float BlendOut, uint Flags);

/// <summary>
/// The animations a character placed in a world can play: its objparams (marker record, class 0x1F…) name the model at
/// +0xC0 and the animtable at +0xD0 (coop/research/charsel: the player's objDefId_banjoactor uses the same fields).
/// Animtable .data: u32 count, u32 0, count × 92-byte records {char[64] action, u32 anim id, u32 animevents id, 0, 0,
/// f32 blend in, f32 blend out, u32 flags}. Animations bind to the model's skeleton by joint index (one track per joint).
/// Assets are looked up the way the world loads them: the scene's own bundles first, then any resident copy, then the
/// Bundle/50 stream archives.
/// </summary>
public sealed class CharacterAnims
{
    public string ObjParams = "", AnimTable = "", ModelName = "";
    public uint ModelId, AnimTableId;
    public List<Joint>? Skeleton;
    public List<AnimAction> Actions = new();
    readonly Workspace _ws; readonly AssetIndex _idx; readonly List<uint> _prefer;
    readonly Dictionary<uint, AnimAsset?> _cache = new();

    readonly Dictionary<uint, List<AssetEntry>> _byId;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AssetIndex, Dictionary<uint, List<AssetEntry>>> ById = new();

    CharacterAnims(Workspace ws, AssetIndex idx, List<uint> prefer)
    {
        _ws = ws; _idx = idx; _prefer = prefer;
        lock (ById)
            if (!ById.TryGetValue(idx, out _byId!))
            {
                _byId = new Dictionary<uint, List<AssetEntry>>();
                foreach (var e in idx.Entries) { if (e.Id == 0) continue; if (!_byId.TryGetValue(e.Id, out var l)) _byId[e.Id] = l = new(); l.Add(e); }
                ById.Add(idx, _byId);
            }
    }

    IEnumerable<AssetEntry> Entries(uint id) => _byId.TryGetValue(id, out var l) ? l : Enumerable.Empty<AssetEntry>();

    /// <summary>The character at a marker, or null when the object is not a character with an animtable.</summary>
    public static CharacterAnims? For(WorldScene scene, SceneObject o, AssetIndex idx)
    {
        if (o.Marker == null) return null;
        var prefer = new List<uint> { scene.Bundle & 0xFFFFFF };
        prefer.AddRange(scene.MarkerBundles.Select(b => b & 0xFFFFFF));
        prefer.AddRange(scene.LoadSet.Select(b => b & 0xFFFFFF));
        var c = new CharacterAnims(scene.Workspace, idx, prefer);
        foreach (var id in o.Marker.AssetIds.Where(a => a >> 24 == 0x1F))
        {
            var d = c.Data(id, out var name);
            if (d == null || d.Length < 0xD4) continue;
            uint model = BE.U32(d, 0xC0), table = BE.U32(d, 0xD0);
            if (table >> 24 != 0x20) continue;
            var td = c.Data(table, out var tname);
            if (td == null || td.Length < 8) continue;
            c.ObjParams = name; c.AnimTable = tname; c.AnimTableId = table; c.ModelId = model;
            int n = (int)BE.U32(td, 0);
            for (int k = 0; k < n && 8 + 92 * (k + 1) <= td.Length; k++)
            {
                int r = 8 + 92 * k;
                string action = BE.CStr(td, r, 64);
                uint anim = BE.U32(td, r + 64);
                c.Actions.Add(new AnimAction(action, anim, c.NameOf(anim), BE.U32(td, r + 68), BE.F32(td, r + 80), BE.F32(td, r + 84), BE.U32(td, r + 88)));
            }
            var md = c.Data(model, out var mname);
            if (md != null) { c.Skeleton = NB.Core.Models.Skeleton.Parse(md); c.ModelName = mname; }
            return c;
        }
        return null;
    }

    public string NameOf(uint id) => Entries(id).FirstOrDefault()?.Name ?? $"0x{id:X8}";

    /// <summary>The animation of an action (parsed once).</summary>
    public AnimAsset? Load(AnimAction a)
    {
        if (_cache.TryGetValue(a.AnimId, out var have)) return have;
        AnimAsset? anim = null;
        try { var d = Data(a.AnimId, out _); if (d != null && AnimAsset.IsAnim(d)) anim = AnimAsset.Parse(d); } catch (Exception) { }
        return _cache[a.AnimId] = anim;
    }

    /// <summary>The CAFF and symbol holding an asset (resident: the preferred bundles first; else a stream archive).</summary>
    public (CaffFile Caff, int Symbol, uint Bundle, bool Streamed)? Locate(uint id)
    {
        var entries = Entries(id).OrderBy(e => e.Streamed ? 1 : 0)
            .ThenBy(e => { int i = _prefer.IndexOf(e.Bundle & 0xFFFFFF); return i < 0 ? int.MaxValue : i; }).ToList();
        foreach (var e in entries)
        {
            try
            {
                if (!e.Streamed && e.Symbol > 0) return (_ws.LoadResident(e.Bundle), e.Symbol, e.Bundle, false);
                var arch = _ws.LoadStream(e.Bundle);
                var be = arch.Entries.FirstOrDefault(x => x.Id == id && x.Data != null);
                if (be == null) continue;
                var c = CaffFile.Read(be.Data!);
                int sym = c.Symbols.FindIndex(s => AssetIds.IdOf(s) == id) + 1;
                if (sym > 0) return (c, sym, e.Bundle, true);
            }
            catch (Exception) { }
        }
        return null;
    }

    byte[]? Data(uint id, out string name)
    {
        name = NameOf(id);
        var l = Locate(id);
        if (l == null) return null;
        var v = new AssetView(l.Value.Caff, l.Value.Symbol);
        return v.Has(".data") ? v.Data(".data") : null;
    }

    // ------------------------------------------------------------------ posing

    /// <summary>Model-space bind matrices of the joints (rotation, then the translation relative to the parent).</summary>
    public static Matrix4x4[] BindMatrices(IReadOnlyList<Joint> joints) => Globals(joints, k => (joints[k].Rotation, joints[k].LocalTranslation, Vector3.One));

    /// <summary>Joint matrices of a frame of an animation (time in seconds; keys slerped / lerped like the game): rotation
    /// = the key's, translation = bind translation + key offset (the FBX exporter's convention, checked in Blender).</summary>
    public static Matrix4x4[] PoseMatrices(IReadOnlyList<Joint> joints, AnimAsset anim, float seconds)
    {
        float frame = Math.Clamp(seconds * AnimAsset.Fps, 0, Math.Max(0, anim.Frames - 1));
        return Globals(joints, k =>
        {
            if (k >= anim.Tracks || anim.Keys.Length <= k || anim.Keys[k].Length == 0) return (joints[k].Rotation, joints[k].LocalTranslation, Vector3.One);
            var (q, t, s) = Sample(anim, k, frame);
            return (q, joints[k].LocalTranslation + t, s);
        });
    }

    /// <summary>Model-space joint matrices. Scale keys are not inherited by child joints (segment scale compensation):
    /// Mr. Fit's animations scale every joint by 1.42, which inherited down the 8-deep chains blew him up to ~16×; a joint's
    /// scale only acts on what it skins, the children follow its rotation and translation.</summary>
    static Matrix4x4[] Globals(IReadOnlyList<Joint> joints, Func<int, (Quaternion R, Vector3 T, Vector3 S)> local)
    {
        var u = new Matrix4x4[joints.Count]; var g = new Matrix4x4[joints.Count]; var done = new bool[joints.Count];
        Matrix4x4 Get(int k, int depth)
        {
            if (done[k]) return u[k];
            var (r, t, s) = local(k);
            var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(r)) * Matrix4x4.CreateTranslation(t);
            int p = joints[k].Parent;
            u[k] = p >= 0 && p < joints.Count && depth < 256 ? m * Get(p, depth + 1) : m;
            g[k] = Matrix4x4.CreateScale(s) * u[k];
            done[k] = true; return u[k];
        }
        for (int k = 0; k < joints.Count; k++) Get(k, 0);
        return g;
    }

    static (Quaternion Q, Vector3 T, Vector3 S) Sample(AnimAsset a, int track, float frame)
    {
        var keys = a.Keys[track]; var kf = a.KeyFrames;
        for (int k = 0; k + 1 < kf.Length && k + 1 < keys.Length; k++)
            if (frame >= kf[k] && frame <= kf[k + 1])
            {
                float u = kf[k + 1] == kf[k] ? 0f : (frame - kf[k]) / (kf[k + 1] - kf[k]);
                return (Quaternion.Slerp(keys[k].Rotation, keys[k + 1].Rotation, u), Vector3.Lerp(keys[k].Translation, keys[k + 1].Translation, u),
                        Vector3.Lerp(keys[k].Scale, keys[k + 1].Scale, u));
            }
        var last = keys[Math.Min(keys.Length, kf.Length) - 1];
        return (last.Rotation, last.Translation, last.Scale);
    }

    /// <summary>Skins a draw's bind-pose vertices: v' = Σ w · v · inverse(bind) · pose, per joint. Draws without joint
    /// weights are left as they are.</summary>
    public static void Skin(MeshDraw bind, Vector3[] pos, Vector3[]? nrm, Matrix4x4[] skin)
    {
        var bi = bind.BlendIndices; var bw = bind.BlendWeights;
        if (bi == null || bw == null) return;
        for (int v = 0; v < bind.Positions.Length && 4 * v + 3 < bi.Length; v++)
        {
            var p = bind.Positions[v]; var n = bind.Normals != null ? bind.Normals[v] : Vector3.Zero;
            Vector3 sp = Vector3.Zero, sn = Vector3.Zero; float tw = 0;
            for (int k = 0; k < 4; k++)
            {
                float w = bw[4 * v + k]; int j = bi[4 * v + k];
                if (w <= 0 || j < 0 || j >= skin.Length) continue;
                sp += w * Vector3.Transform(p, skin[j]);
                sn += w * Vector3.TransformNormal(n, skin[j]);
                tw += w;
            }
            if (tw <= 0) { pos[v] = p; if (nrm != null) nrm[v] = n; continue; }
            pos[v] = sp / tw;
            if (nrm != null && bind.Normals != null) nrm[v] = sn.LengthSquared() > 1e-12f ? Vector3.Normalize(sn) : n;
        }
    }
}
