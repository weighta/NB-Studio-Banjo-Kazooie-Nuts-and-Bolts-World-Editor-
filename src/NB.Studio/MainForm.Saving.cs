namespace NB.Studio;

/// <summary>
/// What is saved when (Help > What saves what): everything you change waits in NB Studio until you save it, and
/// Ctrl+S (File / World > Save All) saves all of it at once — the open world's object, marker and path edits, its
/// collision edits, the Atmosphere tab's sky / light / fog / weather changes, Tag Editor edits, Dialogue lines and the
/// Text tab's table — as one Ctrl+Z step for the files it writes. Opening another world or Act, closing the workspace or
/// quitting with anything unsaved asks Save / Don't save / Cancel. The title's * and the status bar's "Unsaved: …" show
/// exactly what is waiting. (The Atmosphere tab's own Save to Workspace button saves only its changes, as before.)
/// </summary>
public sealed partial class MainForm
{
    readonly ToolStripStatusLabel _unsaved = new() { ForeColor = Color.FromArgb(190, 90, 0), Alignment = ToolStripItemAlignment.Right };
    readonly System.Windows.Forms.Timer _pendingTimer = new() { Interval = 1000 };
    /// <summary>The world or Act the Atmosphere tab shows (its unsaved changes are named after it).</summary>
    string? _atmosShownFor;
    /// <summary>Scripted runs show the Save / Don't save / Cancel question too (--prompts on): tests answer it with real keys.</summary>
    bool _scriptPrompts;

    void InitSaving(StatusStrip strip)
    {
        strip.Items.Add(_unsaved);
        _pendingTimer.Tick += (_, _) => UpdatePending();   // Dialogue / Text edits have no change event
        _pendingTimer.Start();
    }

    bool WorldPending => _scene != null && (_scene.Objects.Any(o => o.Dirty) || CollisionDirty);

    /// <summary>Everything waiting to be saved, in words ("world edits (3 objects)", "atmosphere (Nutty Acres — Act 6)" …).</summary>
    List<string> PendingEdits()
    {
        var p = new List<string>();
        if (_scene != null)
        {
            int n = _scene.Objects.Count(o => o.Dirty);
            if (n > 0) p.Add($"world edits ({n} object{(n == 1 ? "" : "s")})");
            if (CollisionDirty) p.Add("collision edits");
        }
        if (_atmos.HasUnsaved) p.Add($"atmosphere ({_atmosShownFor ?? "this world"})");
        if (_tags.HasUnsaved) p.Add("Tag Editor edits");
        if (_dialogue.UnsavedLines > 0) p.Add($"dialogue lines ({_dialogue.UnsavedLines})");
        if (_text.HasUnsaved) p.Add("text table");
        return p;
    }

    /// <summary>The status bar's "Unsaved: …" and the title's *.</summary>
    void UpdatePending()
    {
        if (IsDisposed) return;
        if (_unsaved.Text != PendingText()) UpdateTitle();
    }

    string PendingText()
    {
        var p = _ws == null ? new List<string>() : PendingEdits();
        return p.Count == 0 ? "" : "Unsaved: " + string.Join(", ", p) + "  (Ctrl+S saves all)";
    }

    const string SavingHelp =
        "Everything you change waits in NB Studio until you save it. The status bar says what is unsaved (\"Unsaved: world edits (2 objects), atmosphere (Act 6)\") and the title shows a *.\n\n" +
        "Ctrl+S (World > Save All Changes, or the Save All button) saves all of it into the workspace at once:\n" +
        "  • the open world: moved, added or deleted objects, markers and paths, and its collision edits\n" +
        "  • the Atmosphere tab: sky, light, fog and weather (its own Save to Workspace button saves just these)\n" +
        "  • Tag Editor edits, Dialogue lines and the Text tab's table\n\n" +
        "Ctrl+Z after a save takes back what that save wrote to the files of the Atmosphere, Tag Editor, Dialogue and Text (one step for one Ctrl+S); world edits stay undoable one by one as before.\n\n" +
        "Opening another world or Act, closing the workspace or quitting with unsaved changes asks: Yes saves them all, No drops them, Cancel goes back.\n\n" +
        "Imports, duplicates, deletes, collision repairs and the Parts, Texture and Audio tools write the workspace straight away (no Ctrl+S needed; Ctrl+Z undoes them).";

    /// <summary>
    /// Ctrl+S: saves everything pending in the workspace. The world first (objects, markers, collision: refused while its
    /// collision is damaged), then the Atmosphere tab, the Tag Editor, Dialogue lines and the Text tab; the files written
    /// by one Save All are one Ctrl+Z step.
    /// </summary>
    void SaveAll()
    {
        if (_ws == null) return;
        var before = PendingEdits();
        if (before.Count == 0) { Log("Nothing to save: every change is in the workspace."); UpdatePending(); return; }
        if (WorldPending) SaveWorld();
        try { if (_atmos.HasUnsaved) _atmos.Save(); } catch (Exception e) { Log("Atmosphere: saving failed: " + e.Message); }
        try { _tags.SaveNow(); } catch (Exception e) { Log("Tag Editor: saving failed: " + e.Message); }
        try { if (_dialogue.UnsavedLines > 0) _dialogue.SaveNow(); } catch (Exception e) { Log("Dialogue: saving failed: " + e.Message); }
        try { _text.SaveNow(); } catch (Exception e) { Log("Text: saving failed: " + e.Message); }
        var left = PendingEdits();
        Log(left.Count == 0 ? $"Saved all: {string.Join(", ", before)}." : $"Saved what could be saved; still unsaved: {string.Join(", ", left)} (see above).");
        UpdatePending();
    }

    /// <summary>
    /// Before something that would lose unsaved changes (another world or Act, closing the workspace, quitting): Save /
    /// Don't save / Cancel. Returns false when the action should not go on (Cancel, or saving failed).
    /// </summary>
    bool AskSavePending(string what)
    {
        var p = _ws == null ? new List<string>() : PendingEdits();
        if (p.Count == 0 || (_scripted && !_scriptPrompts)) return true;
        var ans = MessageBox.Show(this, $"Save your changes before {what}?\n\nUnsaved: {string.Join(", ", p)}.\n\nYes: save them all\nNo: don't save (they are dropped)\nCancel: go back",
            "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (ans == DialogResult.Cancel) return false;
        if (ans == DialogResult.Yes)
        {
            SaveAll();
            if (PendingEdits().Count > 0) return false;   // something could not be saved (the log says why)
            return true;
        }
        DiscardPending();
        return true;
    }

    /// <summary>"Don't save": unsaved edits that would otherwise linger (the Atmosphere tab and the Tag Editor keep theirs in
    /// the shared bundle objects; Dialogue and Text edits are dropped with their lists). World edits go with the world.</summary>
    void DiscardPending()
    {
        try { if (_atmos.HasUnsaved) _atmos.Discard(); } catch (Exception) { }
        _tags.DiscardNow();
        Log("Unsaved changes dropped.");
        UpdatePending();
    }
}
