using System.Numerics;
using System.Reflection;
using NB.Core.Models;
using NB.Core.World;

namespace NB.Studio.Viewport;

/// <summary>Characters posed by an animation (Animations tab): a copy of the object's model whose skinned vertex arrays
/// are rewritten for every frame and uploaded into the copy's GPU buffers; drawn instead of the model.</summary>
public sealed partial class SceneViewport
{
    sealed class Posed
    {
        public ModelAsset Source = null!, Copy = null!;
        public readonly List<(MeshDraw Bind, Vector3[] Pos, Vector3[]? Nrm)> Buffers = new();
        public readonly Dictionary<MeshDraw, MaterialInfo> Mats = new();
    }
    readonly Dictionary<SceneObject, Posed> _posed = new();
    static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>The model drawn for an object: its posed copy while an animation plays on it.</summary>
    ModelAsset? ModelFor(SceneObject o) => o.Model != null && _posed.TryGetValue(o, out var p) && p.Source == o.Model ? p.Copy : o.Model;

    /// <summary>Poses a character: <paramref name="skin"/>[joint] = inverse bind × joint pose (see
    /// <see cref="CharacterAnims.Skin"/>); null puts the model back.</summary>
    public void SetPose(SceneObject o, Matrix4x4[]? skin)
    {
        if (skin == null || o.Model == null)
        {
            if (_posed.Remove(o, out var old) && _ready) { _gl.MakeCurrent(); _r.ForgetModel(old.Copy); }
            _gl.Invalidate();
            return;
        }
        if (!_posed.TryGetValue(o, out var p) || p.Source != o.Model)
        {
            p = new Posed { Source = o.Model, Copy = o.Model.ShallowCopy() };
            var arrays = new Dictionary<Vector3[], (Vector3[] Pos, Vector3[]? Nrm)>(ReferenceEqualityComparer.Instance as IEqualityComparer<Vector3[]>);
            var draws = new List<MeshDraw>();
            foreach (var d in o.Model.Draws)
            {
                if (d.BlendIndices == null || d.BlendWeights == null) { draws.Add(d); continue; }
                var c = (MeshDraw)CloneMethod.Invoke(d, null)!;
                if (!arrays.TryGetValue(d.Positions, out var a))
                {
                    arrays[d.Positions] = a = ((Vector3[])d.Positions.Clone(), d.Normals != null ? (Vector3[])d.Normals.Clone() : null);
                    p.Buffers.Add((d, a.Pos, a.Nrm));
                }
                c.Positions = a.Pos; c.Normals = a.Nrm ?? c.Normals;
                draws.Add(c);
            }
            p.Copy.Draws = draws;
            foreach (var d in draws) p.Mats[d] = MaterialInfo.Of(d);
            _posed[o] = p;
        }
        foreach (var (bind, pos, nrm) in p.Buffers) CharacterAnims.Skin(bind, pos, nrm, skin);
        if (_ready) { _gl.MakeCurrent(); _r.UpdateVertices(p.Copy, p.Mats); }
        _gl.Invalidate();
    }

    /// <summary>Forgets every posed copy (a new scene).</summary>
    void ClearPoses() { foreach (var o in _posed.Keys.ToList()) SetPose(o, null); _idleSkin.Clear(); _idleGen++; }

    /// <summary>Characters stand in the first frame of their idle animation (as in the game) instead of the stored bind
    /// pose (arms out). Off: bind pose.</summary>
    public bool IdlePoses
    {
        get => _idlePoses;
        set { if (_idlePoses == value) return; _idlePoses = value; if (value) StartIdlePoses(); else { foreach (var o in _idleSkin.Keys.ToList()) SetPose(o, null); _idleSkin.Clear(); } }
    }
    bool _idlePoses = true;
    readonly Dictionary<SceneObject, Matrix4x4[]> _idleSkin = new();
    int _idleGen;
    /// <summary>What the idle posing did (log / scripts).</summary>
    public string IdlePoseInfo { get; private set; } = "";

    /// <summary>Works out the idle pose of every character of the scene in the background (animtable action "idle1",
    /// else another idle / stand action, frame 0) and applies them.</summary>
    void StartIdlePoses()
    {
        _idleSkin.Clear();
        var scene = Scene; int gen = ++_idleGen;
        if (scene == null || !_idlePoses) return;
        var chars = scene.Objects.Where(o => o.Kind == SceneObjectKind.Marker && o.Model != null && o.Model.Draws.Any(d => d.BlendIndices != null)).ToList();
        if (chars.Count == 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IdlePoseInfo = $"working ({chars.Count} candidates)";
        Task.Run(() =>
        {
            var idx = NB.Core.Project.AssetIndex.LoadOrBuild(scene.Workspace);
            var res = new List<(SceneObject, Matrix4x4[])>();
            var bySet = new Dictionary<string, Matrix4x4[]?>();
            foreach (var o in chars)
            {
                try
                {
                    // markers placing the same objparams share the result (Showdown Town: 478 skinned markers, ~45 kinds)
                    string key = string.Join(",", o.Marker!.AssetIds.Where(x => x >> 24 == 0x1F).Select(x => x.ToString("X8"))) + "|" + o.Model!.GetHashCode();
                    if (!bySet.TryGetValue(key, out var skin))
                    {
                        var set = CharacterAnims.For(scene, o, idx);
                        if (set?.Skeleton == null) { bySet[key] = null; continue; }
                        skin = null;
                        var a = set.Actions.FirstOrDefault(x => x.Action == "idle1") ?? set.Actions.FirstOrDefault(x => x.Action == "stand")
                             ?? set.Actions.FirstOrDefault(x => x.Action.StartsWith("idle")) ?? set.Actions.FirstOrDefault(x => x.Action.Contains("stand"));
                        var anim = a != null ? set.Load(a) : null;
                        if (anim != null)
                        {
                            var bind = CharacterAnims.BindMatrices(set.Skeleton);
                            var pose = CharacterAnims.PoseMatrices(set.Skeleton, anim, 0);
                            skin = new Matrix4x4[pose.Length];
                            for (int j = 0; j < pose.Length; j++) skin[j] = (Matrix4x4.Invert(bind[j], out var inv) ? inv : Matrix4x4.Identity) * pose[j];
                        }
                        bySet[key] = skin;
                    }
                    if (skin != null) res.Add((o, skin));
                }
                catch (Exception) { }
            }
            return res;
        }).ContinueWith(t =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (t.IsFaulted) { IdlePoseInfo = "failed: " + t.Exception?.GetBaseException().Message; return; }
            BeginInvoke(() =>
            {
                if (gen != _idleGen || Scene != scene || !_idlePoses) return;
                foreach (var (o, skin) in t.Result) { _idleSkin[o] = skin; if (!_posed.ContainsKey(o)) SetPose(o, skin); }
                IdlePoseInfo = $"{t.Result.Count} of {chars.Count} candidates in their idle pose ({sw.ElapsedMilliseconds} ms)";
            });
        });
    }

    /// <summary>The pose an object shows when no animation plays on it (its idle pose, or the bind pose).</summary>
    public void RestorePose(SceneObject o) => SetPose(o, _idlePoses && _idleSkin.TryGetValue(o, out var s) ? s : null);

    /// <summary>Draws a frame now (animation playback: the timer of the Animations tab).</summary>
    public void RenderFrame() { _gl.Invalidate(); _gl.Update(); }
}
