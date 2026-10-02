using System.Drawing.Drawing2D;

namespace NB.Studio.Panels;

/// <summary>One stop of the beginner's tour: what to point at (screen rectangle, or null for a centred message),
/// a title, plain-language text, and an optional action when the stop is shown (e.g. switch to the tab it explains).</summary>
public sealed record TourStep(string Title, string Text, Func<Rectangle?>? Target = null, Action? OnShow = null);

/// <summary>
/// The guided tour for people who have never modded before: the owner window is dimmed except for a spotlight on the
/// part being explained, and a card next to it says what it is and what it's for, with Back / Next / Skip. Two
/// windows: a dimming layer (semi-transparent, with the spotlight cut out of its region) and the card. Both follow the
/// owner when it moves or resizes; Esc or Skip ends the tour.
/// </summary>
public sealed class TourOverlay : IDisposable
{
    readonly Form _owner;
    readonly IReadOnlyList<TourStep> _steps;
    readonly Form _dim, _card;
    readonly Label _title = new() { UseMnemonic = false, AutoSize = false, Font = new Font("Segoe UI Semibold", 13f), ForeColor = Color.FromArgb(255, 170, 70), Dock = DockStyle.Top, Height = 34 };
    readonly Label _text = new() { UseMnemonic = false, AutoSize = false, Font = new Font("Segoe UI", 10.5f), ForeColor = Color.FromArgb(236, 236, 242), Dock = DockStyle.Fill };
    readonly Label _count = new() { AutoSize = true, Font = new Font("Segoe UI", 9f), ForeColor = Color.FromArgb(150, 154, 168) };
    readonly Button _back = Btn("Back"), _next = Btn("Next", true), _skip = Btn("Skip tour");
    int _i;
    public event Action? Finished;

    public TourOverlay(Form owner, IReadOnlyList<TourStep> steps)
    {
        _owner = owner; _steps = steps;
        _dim = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            BackColor = Color.Black, Opacity = 0.55, Owner = owner,
        };
        _dim.Paint += (_, e) =>
        {
            // a glowing frame around the spotlight
            if (Spot() is not { } s) return;
            var local = _dim.RectangleToClient(s); local.Inflate(3, 3);
            using var pen = new Pen(Color.FromArgb(255, 170, 70), 3f);
            e.Graphics.DrawRectangle(pen, local);
        };
        _dim.MouseClick += (_, _) => { };   // clicks on the dimmed area are swallowed (the tour is in charge)
        _card = new CardForm
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            BackColor = Color.FromArgb(28, 30, 38), Owner = owner, Size = new Size(430, 250), Padding = new Padding(18, 14, 18, 12), KeyPreview = true,
        };
        _card.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(255, 150, 50), 2f); e.Graphics.DrawRectangle(pen, 1, 1, _card.Width - 3, _card.Height - 3); };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.Transparent, Padding = new Padding(0, 6, 0, 0) };
        bar.Controls.AddRange(new Control[] { _next, _back, _skip });
        _count.Margin = new Padding(0, 10, 40, 0);
        bar.Controls.Add(_count);
        _card.Controls.Add(_text); _card.Controls.Add(_title); _card.Controls.Add(bar);
        _next.Click += (_, _) => Go(_i + 1);
        _back.Click += (_, _) => Go(_i - 1);
        _skip.Click += (_, _) => End();
        ((CardForm)_card).Key = k =>
        {
            if (k == Keys.Escape) { End(); return true; }
            if (k is Keys.Right or Keys.Enter) { Go(_i + 1); return true; }
            if (k == Keys.Left) { Go(_i - 1); return true; }
            return false;
        };
        _owner.Move += Follow; _owner.Resize += Follow;
    }

    static Button Btn(string t, bool primary = false)
    {
        var b = new Button
        {
            Text = t, AutoSize = true, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10f, primary ? FontStyle.Bold : FontStyle.Regular),
            BackColor = primary ? Color.FromArgb(242, 140, 40) : Color.FromArgb(48, 50, 62), ForeColor = primary ? Color.FromArgb(22, 18, 14) : Color.FromArgb(230, 230, 236),
            Margin = new Padding(6, 0, 0, 0), Padding = new Padding(10, 2, 10, 2), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? Color.FromArgb(255, 200, 140) : Color.FromArgb(80, 84, 98);
        return b;
    }

    /// <summary>The card window: arrow keys, Enter and Esc drive the tour (buttons would otherwise take the arrows for focus).</summary>
    sealed class CardForm : Form
    {
        public Func<Keys, bool>? Key;
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => (Key?.Invoke(keyData) ?? false) || base.ProcessCmdKey(ref msg, keyData);
    }

    public void Start() { _dim.Show(_owner); _card.Show(_owner); Go(0); }

    /// <summary>The first-start question: "Would you like a quick tour?" Returns true for yes.</summary>
    public static bool AskWelcome(IWin32Window owner)
    {
        using var f = new Form
        {
            Text = "Welcome to NB Studio", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(520, 230), BackColor = Color.FromArgb(28, 30, 38), ShowInTaskbar = false,
        };
        var title = new Label { UseMnemonic = false, Text = "Welcome to NB Studio!", Font = new Font("Segoe UI Semibold", 16f), ForeColor = Color.FromArgb(255, 170, 70), AutoSize = true, Location = new Point(24, 20) };
        var text = new Label
        {
            UseMnemonic = false,
            Text = "It looks like this is your first time here. Would you like a quick tour? It takes about two minutes and explains " +
                   "what every part of NB Studio does, in plain words. No modding experience needed.\n\nYou can take it later from Help > Take the Tour.",
            Font = new Font("Segoe UI", 10.5f), ForeColor = Color.FromArgb(232, 232, 240), Location = new Point(26, 64), Size = new Size(470, 110),
        };
        var yes = Btn("Yes, show me around", true); var no = Btn("No thanks");
        yes.DialogResult = DialogResult.Yes; no.DialogResult = DialogResult.No;
        var bar = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Location = new Point(20, 178), Size = new Size(480, 40), BackColor = Color.Transparent };
        bar.Controls.Add(yes); bar.Controls.Add(no);
        f.Controls.AddRange(new Control[] { title, text, bar });
        f.AcceptButton = yes; f.CancelButton = no;
        return f.ShowDialog(owner) == DialogResult.Yes;
    }

    Rectangle? Spot()
    {
        try { return _steps[_i].Target?.Invoke() is { Width: > 0, Height: > 0 } r ? r : null; } catch (Exception) { return null; }
    }

    void Go(int i)
    {
        if (i < 0) return;
        if (i >= _steps.Count) { End(); return; }
        _i = i;
        var s = _steps[i];
        try { s.OnShow?.Invoke(); } catch (Exception) { }
        _title.Text = s.Title;
        _text.Text = s.Text;
        _count.Text = $"{i + 1} / {_steps.Count}";
        _back.Enabled = i > 0;
        _next.Text = i == _steps.Count - 1 ? "Finish" : "Next";
        // card height grows with the text
        using (var g = _card.CreateGraphics())
        {
            var sz = g.MeasureString(s.Text, _text.Font, 394);
            _card.Height = Math.Clamp((int)sz.Height + 34 + 40 + 40, 170, 520);
        }
        _owner.BeginInvoke(Follow);
    }

    void Follow() => Follow(null, EventArgs.Empty);

    void Follow(object? sender, EventArgs e)
    {
        if (_dim.IsDisposed) return;
        if (_owner.WindowState == FormWindowState.Minimized) { _dim.Hide(); _card.Hide(); return; }
        if (!_dim.Visible) { _dim.Show(_owner); _card.Show(_owner); }
        var area = _owner.RectangleToScreen(_owner.ClientRectangle);
        _dim.Bounds = area;
        var spot = Spot();
        var region = new Region(new Rectangle(0, 0, area.Width, area.Height));
        if (spot is { } s) { var local = _dim.RectangleToClient(s); local.Inflate(6, 6); region.Exclude(local); }
        var old = _dim.Region; _dim.Region = region; old?.Dispose();
        _dim.Invalidate();
        // the card: beside the spotlight (right, else left, else below), or centred
        var c = _card.Size;
        Point p;
        if (spot is { } t)
        {
            if (t.Right + 24 + c.Width <= area.Right) p = new Point(t.Right + 24, t.Top);
            else if (t.Left - 24 - c.Width >= area.Left) p = new Point(t.Left - 24 - c.Width, t.Top);
            else if (t.Bottom + 24 + c.Height <= area.Bottom) p = new Point(t.Left, t.Bottom + 24);
            else p = new Point(area.Left + (area.Width - c.Width) / 2, area.Top + (area.Height - c.Height) / 2);
            p.Y = Math.Clamp(p.Y, area.Top + 10, Math.Max(area.Top + 10, area.Bottom - c.Height - 10));
            p.X = Math.Clamp(p.X, area.Left + 10, Math.Max(area.Left + 10, area.Right - c.Width - 10));
        }
        else p = new Point(area.Left + (area.Width - c.Width) / 2, area.Top + (area.Height - c.Height) / 2);
        _card.Location = p;
        _card.BringToFront();
        _next.Focus();
    }

    void End()
    {
        _owner.Move -= Follow; _owner.Resize -= Follow;
        _dim.Close(); _card.Close();
        Finished?.Invoke();
    }

    public void Dispose() { if (!_dim.IsDisposed) End(); _dim.Dispose(); _card.Dispose(); }
}
