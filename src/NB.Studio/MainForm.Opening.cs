namespace NB.Studio;

/// <summary>
/// A plain "Opening …" screen over the work area while File > Open Recent (or the start page) opens a workspace and its
/// last world. Before 1.23 switching workspaces showed the start page for a moment (closing the old workspace brought it
/// up), then an empty view, then the new world.
/// </summary>
public sealed partial class MainForm
{
    Panel? _opening;
    bool _scriptLastWorld;   // script --open-recent: open the workspace's last world as File > Open Recent does
    Label? _openingTitle, _openingStep;

    /// <summary>Covers the work area (not the menu, toolbar or status bar) with "Opening NAME…".</summary>
    void ShowOpening(string name)
    {
        if (_opening == null)
        {
            _opening = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 17, 22), Visible = false };
            _openingStep = new Label { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.TopCenter, ForeColor = Color.FromArgb(140, 146, 160), Font = new Font(Font.FontFamily, 9.5f) };
            _openingTitle = new Label { Dock = DockStyle.Top, Height = 40, TextAlign = ContentAlignment.BottomCenter, ForeColor = Color.FromArgb(244, 244, 248), Font = new Font(Font.FontFamily, 14f) };
            var spacer = new Panel { Dock = DockStyle.Top, Height = 0 };
            _opening.Controls.Add(_openingStep); _opening.Controls.Add(_openingTitle); _opening.Controls.Add(spacer);
            _opening.Resize += (_, _) => spacer.Height = Math.Max(0, _opening.Height / 2 - 50);
            Controls.Add(_opening);
        }
        _openingTitle!.Text = $"Opening {name}…";
        _openingStep!.Text = "";
        Controls.SetChildIndex(_opening, 0);   // in front of the start page and the work area
        _opening.Visible = true;
        _opening.BringToFront();
        _opening.Update();
    }

    /// <summary>The current step (asset index, world) under the title, while the cover is up.</summary>
    void OpeningStep(string? text)
    {
        if (_opening is { Visible: true } && text != null && _openingStep != null) { _openingStep.Text = text; _openingStep.Update(); }
    }

    void HideOpening()
    {
        if (_opening is { Visible: true }) _opening.Visible = false;
    }
}
