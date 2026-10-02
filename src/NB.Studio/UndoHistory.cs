using System.Numerics;
using NB.Core.Project;
using NB.Core.World;

namespace NB.Studio;

/// <summary>One undoable change.</summary>
public abstract class UndoStep
{
    public string Label = "";
}

/// <summary>An object moved, rotated or scaled (in memory until World > Save).</summary>
public sealed class TransformStep : UndoStep
{
    public SceneObject? Obj;
    public string Key = "";
    public Matrix4x4 Before, After;
}

/// <summary>A path node's "next node" changed (in memory until World > Save).</summary>
public sealed class LinkStep : UndoStep
{
    public SceneObject? Obj;
    public string Key = "";
    public int Before, After;
}

/// <summary>Workspace files written by one action (duplicate, delete, model import, tag or atmosphere save, …): the
/// version before the action, and (once undone) the version after it, are kept as copies in the undo folder.</summary>
public sealed class FileStep : UndoStep
{
    public sealed class Entry { public string Path = ""; public string? Before; public string? After; public bool Existed; }
    public readonly List<Entry> Files = new();
    public long Bytes;
    public readonly List<string> Descriptions = new();
}

/// <summary>
/// The editor's undo / redo history (Ctrl+Z / Ctrl+Y): transforms and path links of the open world (in memory) and every
/// workspace file an action writes (captured automatically through <see cref="Workspace.BeforeWrite"/>, so model
/// imports, duplicates, deletes, tag edits, atmosphere and texture saves are undoable too). The number of steps is a
/// setting; file copies live in &lt;workspace&gt;\history\undo and are removed when they leave the history.
/// </summary>
public sealed class UndoHistory
{
    readonly List<UndoStep> _undo = new(), _redo = new();
    SynchronizationContext? _ui;
    readonly object _gate = new();
    Workspace? _ws;
    string? _dir;
    FileStep? _pending;
    bool _restoring;
    int _seq;
    /// <summary>Most steps kept (older ones fall off the bottom).</summary>
    public int Limit { get; set; } = 100;
    /// <summary>A single action that writes more than this many bytes of files is not kept (it would fill the disk).</summary>
    public long MaxStepBytes = 3L << 30;
    public event Action? Changed;
    public Action<string>? Log;

    public bool CanUndo { get { lock (_gate) return _undo.Count > 0 || _pending != null; } }
    public bool CanRedo { get { lock (_gate) return _redo.Count > 0; } }
    public string? UndoLabel { get { lock (_gate) return (_pending as UndoStep ?? _undo.LastOrDefault())?.Label; } }
    public string? RedoLabel { get { lock (_gate) return _redo.LastOrDefault()?.Label; } }
    public int Count { get { lock (_gate) return _undo.Count; } }

    /// <summary>Starts recording file writes of a (newly opened) workspace; the old history is dropped.</summary>
    public void Attach(Workspace? ws)
    {
        _ui = SynchronizationContext.Current ?? _ui;   // the UI thread: a step ends when it is idle again
        if (_ws != null) { _ws.BeforeWrite -= OnBeforeWrite; _ws.Changed -= OnChanged; }
        Clear();
        DeleteStore();
        _ws = ws;
        _dir = ws != null ? Path.Combine(ws.HistoryDir, "undo") : null;
        DeleteStore();
        if (ws != null) { ws.BeforeWrite += OnBeforeWrite; ws.Changed += OnChanged; }
    }

    /// <summary>Removes the file copies (when the program closes).</summary>
    public void Detach() => Attach(null);

    void DeleteStore()
    {
        if (_dir == null || !Directory.Exists(_dir)) return;
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var s in _undo.Concat(_redo)) Release(s);
            _undo.Clear(); _redo.Clear(); _pending = null;
        }
        Changed?.Invoke();
    }

    /// <summary>Drops the in-memory steps of the open world (when another world opens); file steps stay.</summary>
    public void DropSceneSteps()
    {
        lock (_gate) { _undo.RemoveAll(s => s is not FileStep); _redo.RemoveAll(s => s is not FileStep); }
        Changed?.Invoke();
    }

    public static string KeyOf(SceneObject o) => o.Kind switch
    {
        SceneObjectKind.Scenery => "s" + o.Instance?.Index,
        SceneObjectKind.Marker => $"m{o.MarkerSet?.Bundle:x6}:{o.MarkerSet?.Name}#{o.Marker?.Index}",
        _ => "t" + o.Name,
    };

    /// <summary>After the open world was reloaded (an undone file step, a duplicate …): points the in-memory steps at the
    /// new scene's objects.</summary>
    public void Rebind(WorldScene scene)
    {
        var map = new Dictionary<string, SceneObject>();
        foreach (var o in scene.Objects) map.TryAdd(KeyOf(o), o);
        lock (_gate)
            foreach (var s in _undo.Concat(_redo))
            {
                if (s is TransformStep t) t.Obj = map.GetValueOrDefault(t.Key);
                else if (s is LinkStep l) l.Obj = map.GetValueOrDefault(l.Key);
            }
    }

    public void PushTransform(SceneObject o, Matrix4x4 before, Matrix4x4 after, string? label = null)
    {
        if (before == after) return;
        string what = label ?? (before.Translation != after.Translation && Scale(before) == Scale(after) ? "Move" : Scale(before) != Scale(after) ? "Scale" : "Rotate");
        Push(new TransformStep { Obj = o, Key = KeyOf(o), Before = before, After = after, Label = $"{what} {o.Name}" });
    }

    public void PushLink(SceneObject o, int before, int after)
    {
        if (before == after) return;
        Push(new LinkStep { Obj = o, Key = KeyOf(o), Before = before, After = after, Label = $"Path link of {o.Name}" });
    }

    static Vector3 Scale(Matrix4x4 m) => new(new Vector3(m.M11, m.M12, m.M13).Length(), new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length());

    void Push(UndoStep s)
    {
        Commit();
        lock (_gate)
        {
            _undo.Add(s);
            foreach (var r in _redo) Release(r);
            _redo.Clear();
            Trim();
        }
        Changed?.Invoke();
    }

    void Trim()
    {
        int limit = Math.Max(1, Limit);
        while (_undo.Count > limit) { Release(_undo[0]); _undo.RemoveAt(0); }
    }

    /// <summary>Applies a lowered <see cref="Limit"/> now.</summary>
    public void ApplyLimit() { lock (_gate) Trim(); Changed?.Invoke(); }

    static void Release(UndoStep s)
    {
        if (s is not FileStep f) return;
        foreach (var e in f.Files)
            foreach (var p in new[] { e.Before, e.After })
                if (p != null) try { File.Delete(p); } catch (Exception) { }
    }

    // ------------------------------------------------------------------ file capture

    int _suppress;

    /// <summary>Writes inside this scope are not undo steps (World > Save: saving keeps the in-memory steps as they are).</summary>
    public IDisposable Suppress() { _suppress++; return new Scope(() => _suppress--); }

    sealed class Scope(Action end) : IDisposable { Action? _end = end; public void Dispose() { _end?.Invoke(); _end = null; } }

    void OnBeforeWrite(string path)
    {
        if (_restoring || _suppress > 0 || _dir == null || Limit <= 0) return;
        lock (_gate)
        {
            bool fresh = _pending == null;
            _pending ??= new FileStep();
            if (_pending.Files.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            var e = new FileStep.Entry { Path = path, Existed = File.Exists(path) };
            if (e.Existed && _pending.Bytes >= 0)
            {
                long len = new FileInfo(path).Length;
                if (_pending.Bytes + len > MaxStepBytes)
                {
                    // too big to keep: this action can't be undone here (Edit > Undo Last Bundle Save still can)
                    foreach (var x in _pending.Files) if (x.Before != null) try { File.Delete(x.Before); } catch (Exception) { }
                    _pending.Bytes = -1;
                }
                else
                {
                    Directory.CreateDirectory(_dir);
                    e.Before = Path.Combine(_dir, $"{++_seq:D5}-before-{Path.GetFileName(path)}");
                    File.Copy(path, e.Before, true);
                    File.SetAttributes(e.Before, FileAttributes.Normal);
                    _pending.Bytes += len;
                }
            }
            _pending.Files.Add(e);
            // everything one action writes in one go is one step: it ends when the program is idle again
            if (fresh) Post(() => Commit());
        }
    }

    void OnChanged(string file, string description)
    {
        if (_restoring) return;
        lock (_gate) _pending?.Descriptions.Add(description);
    }

    void Post(Action a)
    {
        if (_ui != null) _ui.Post(_ => a(), null);
        else a();
    }

    /// <summary>Ends the file step being recorded (called automatically when the action's writes are done).</summary>
    public void Commit()
    {
        FileStep? p;
        lock (_gate) { p = _pending; _pending = null; }
        if (p == null) return;
        if (p.Bytes < 0)
        {
            Log?.Invoke("This change wrote more than " + (MaxStepBytes >> 30) + " GB of game files, so it can't be undone with Ctrl+Z (Edit > Undo Last Bundle Save can restore a world file).");
            return;
        }
        var d = p.Descriptions.Distinct().ToList();
        p.Label = d.Count == 0 ? "File change" : Cap(d[0]) + (d.Count > 1 ? $" (+{d.Count - 1} more)" : "");
        if (p.Label.Length > 70) p.Label = p.Label[..67] + "…";
        lock (_gate)
        {
            _undo.Add(p);
            foreach (var r in _redo) Release(r);
            _redo.Clear();
            Trim();
        }
        Changed?.Invoke();
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ------------------------------------------------------------------ undo / redo

    /// <summary>Undoes the newest step. Returns it (so the caller can refresh what it touched), or null.</summary>
    public UndoStep? Undo()
    {
        Commit();
        UndoStep s;
        lock (_gate) { if (_undo.Count == 0) return null; s = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); }
        Apply(s, undo: true);
        lock (_gate) _redo.Add(s);
        Changed?.Invoke();
        return s;
    }

    public UndoStep? Redo()
    {
        Commit();
        UndoStep s;
        lock (_gate) { if (_redo.Count == 0) return null; s = _redo[^1]; _redo.RemoveAt(_redo.Count - 1); }
        Apply(s, undo: false);
        lock (_gate) { _undo.Add(s); Trim(); }
        Changed?.Invoke();
        return s;
    }

    void Apply(UndoStep s, bool undo)
    {
        switch (s)
        {
            case TransformStep t when t.Obj != null: t.Obj.Transform = undo ? t.Before : t.After; break;
            case LinkStep l when l.Obj?.Marker != null: l.Obj.Marker.Link = undo ? l.Before : l.After; break;
            case FileStep f: SwapFiles(f, undo); break;
        }
    }

    /// <summary>Undo: keeps the current files as the "after" copies and puts the "before" copies back. Redo: the reverse.</summary>
    void SwapFiles(FileStep f, bool undo)
    {
        _restoring = true;
        try
        {
            Directory.CreateDirectory(_dir!);
            foreach (var e in f.Files)
            {
                string? keep = undo ? e.Before : e.After;
                bool keepExists = undo ? e.Existed : File.Exists(e.After ?? "");
                // keep what's there now for the other direction
                string? now = null;
                if (File.Exists(e.Path))
                {
                    now = Path.Combine(_dir!, $"{++_seq:D5}-{(undo ? "after" : "before")}-{Path.GetFileName(e.Path)}");
                    File.Copy(e.Path, now, true);
                    File.SetAttributes(now, FileAttributes.Normal);
                }
                if (keep != null && keepExists && File.Exists(keep)) Restore(keep, e.Path);
                else if (File.Exists(e.Path)) NB.Core.IO.FileLinks.DeleteIgnoringReadOnly(e.Path);
                if (undo) { if (e.After != null && e.After != now) TryDelete(e.After); e.After = now; }
                else { if (e.Before != null && e.Before != now) TryDelete(e.Before); e.Before = now; e.Existed = now != null; }
            }
            _ws?.ForgetCaches();
            if (_ws != null)
                foreach (var e in f.Files)
                    try { _ws.Log(Path.GetRelativePath(_ws.Game.Root, e.Path), (undo ? "undo: " : "redo: ") + f.Label); } catch (Exception) { }
        }
        finally { _restoring = false; }
    }

    /// <summary>Puts a kept copy in place without writing into the existing file (it may be a hard link to the
    /// original game: a new file replaces the name instead).</summary>
    static void Restore(string copy, string path)
    {
        var tmp = path + ".undo.tmp";
        File.Copy(copy, tmp, true);
        File.SetAttributes(tmp, FileAttributes.Normal);
        if (File.Exists(path)) NB.Core.IO.FileLinks.DeleteIgnoringReadOnly(path);
        File.Move(tmp, path);
    }

    static void TryDelete(string p) { try { File.Delete(p); } catch (Exception) { } }
}
