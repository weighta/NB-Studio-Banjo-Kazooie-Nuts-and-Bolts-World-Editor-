using NB.Core.Project;
using NB.Core.Vehicles;

namespace NB.Studio.Panels;

/// <summary>
/// The Vehicle Editor's save questions: the vehicle's name (what the game shows: Your Blueprints, the package's
/// "VEHICLE: …" title, the vehicle saves list) and a choice between a few answers (Replace / Save as a copy / Cancel).
/// </summary>
/// <summary>
/// The Vehicle Editor's question windows. In background test runs (NB_STUDIO_BACKGROUND=1) they open without becoming the
/// active window (as NB Studio's main window does there); tests answer them with window messages. Normal use is unchanged.
/// </summary>
public class QuietForm : Form
{
    protected override bool ShowWithoutActivation => NB.Core.IO.QuietLaunch.Enabled || base.ShowWithoutActivation;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (NB.Core.IO.QuietLaunch.Enabled) cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
            return cp;
        }
    }
}

public static class VehicleSaveDialogs
{
    /// <summary>
    /// The longest name: the blueprint's name field holds 32 UTF-16 characters. The game writes all 32 without a closing
    /// zero when the keyboard entry is longer, and lists and loads such names; the longest name among 75 vehicles saved on a
    /// console is 25. In Garage › Vehicle Database the list shows about 13 characters and the blueprint's title about 22
    /// before "…" (seen in Xenia, 2026-10-08).
    /// </summary>
    public const int MaxChars = Blueprint.MaxNameChars;

    /// <summary>Characters a name may hold: the game font's (<see cref="Blueprint.InGameFont"/>: ASCII, Latin-1, Latin
    /// Extended-A, Greek, Cyrillic, € ™ ‘ ’ “ ” …); no control characters.</summary>
    public static bool Allowed(char c) => Blueprint.InGameFont(c);

    /// <summary>The name cleaned the way the dialog does: characters outside the game font dropped (except those of
    /// <paramref name="keep"/>, the vehicle's present name: the game wrote them), spaces trimmed, cut to <see cref="MaxChars"/>.</summary>
    public static string Clean(string s, string? keep = null)
    {
        var t = new string((s ?? "").Where(c => Allowed(c) || (keep != null && !char.IsControl(c) && keep.Contains(c))).ToArray()).Trim();
        return t.Length > MaxChars ? t[..MaxChars].TrimEnd() : t;
    }

    /// <summary>Scripted runs answer here instead of showing the window (null: show it).</summary>
    public static Func<string, string?>? ScriptName;
    public static Func<string, int?>? ScriptChoice;

    /// <summary>Asks for the vehicle's name; null on Cancel.</summary>
    public static string? AskName(IWin32Window? owner, string title, string intro, string prefill)
    {
        if (ScriptName?.Invoke(prefill) is { } scripted) return Clean(scripted, prefill);
        bool Ok(char c) => Allowed(c) || (!char.IsControl(c) && prefill.Contains(c));   // the present name's own characters stay
        using var f = new QuietForm
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(440, 184), Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont,
        };
        var lab = new Label { Text = intro, Location = new Point(12, 10), Size = new Size(416, 48) };
        var box = new TextBox { Location = new Point(12, 62), Width = 416, MaxLength = MaxChars, Text = Clean(prefill, prefill) };
        var hint = new Label { Location = new Point(12, 90), Size = new Size(416, 50), ForeColor = SystemColors.GrayText };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(262, 148), Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(348, 148), Width = 80 };
        f.Controls.AddRange(new Control[] { lab, box, hint, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        void Update()
        {
            int n = box.Text.Trim().Length;
            ok.Enabled = n > 0;
            hint.Text = n == 0 ? "The game needs a name." : $"{n} / {MaxChars} characters{(n > 13 ? " (the game's list cuts long names after about 13 with …)" : "")}. Shown in Garage › Vehicle Database › Your Blueprints and as \"VEHICLE: {box.Text.Trim()}\".";
        }
        box.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !Ok(e.KeyChar)) { e.Handled = true; System.Media.SystemSounds.Beep.Play(); hint.Text = $"'{e.KeyChar}' is not in the game's font: letters, digits, spaces and punctuation only."; } };
        box.TextChanged += (_, _) =>
        {
            var c = new string(box.Text.Where(Ok).ToArray());   // pasted text
            if (c != box.Text) { int at = box.SelectionStart; box.Text = c; box.SelectionStart = Math.Min(at, c.Length); }
            Update();
        };
        f.Shown += (_, _) => { box.SelectAll(); box.Focus(); };
        Update();
        return f.ShowDialog(owner) == DialogResult.OK ? Clean(box.Text, prefill) : null;
    }

    /// <summary>A question with several answers (the last is Cancel); the index of the answer, -1 for Cancel / closed.
    /// Enter picks the second answer when there are three or more (the first is Replace: never by accident).</summary>
    public static int Choose(IWin32Window? owner, string title, string message, params string[] answers)
    {
        if (ScriptChoice?.Invoke(message) is { } scripted) return scripted;
        using var f = new QuietForm
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont,
        };
        var icon = new PictureBox { Image = SystemIcons.Warning.ToBitmap(), Location = new Point(14, 14), Size = new Size(32, 32) };
        var lab = new Label { Text = message, Location = new Point(56, 14), MaximumSize = new Size(420, 0), AutoSize = true };
        f.Controls.Add(icon); f.Controls.Add(lab);
        int result = -1;
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(8) };
        for (int i = answers.Length - 1; i >= 0; i--)
        {
            int k = i;
            bool isCancel = i == answers.Length - 1;
            var b = new Button { Text = answers[i], AutoSize = true, MinimumSize = new Size(88, 0), Padding = new Padding(6, 0, 6, 0) };
            b.Click += (_, _) => { result = isCancel ? -1 : k; f.Close(); };
            row.Controls.Add(b);
            if (isCancel) f.CancelButton = b;
            if (i == (answers.Length > 2 ? 1 : 0)) { f.AcceptButton = b; var accept = b; f.Shown += (_, _) => accept.Focus(); }
        }
        f.Controls.Add(row);
        f.Load += (_, _) => f.ClientSize = new Size(Math.Max(row.PreferredSize.Width + 16, Math.Min(500, lab.PreferredSize.Width + 72)), Math.Max(lab.Bottom, icon.Bottom) + 16 + row.PreferredSize.Height);
        f.ShowDialog(owner);
        return result;
    }
}
