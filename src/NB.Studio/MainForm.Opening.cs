namespace NB.Studio;

/// <summary>
/// While File > Open Recent (or the start page) opens a workspace and its last world, the window keeps its normal layout
/// (menus, tabs, panels) and the status bar shows "Opening NAME…" with a progress bar. Before 1.23 switching workspaces
/// showed the start page for a moment; 1.23.0 covered the work area with a dark "Opening …" screen instead.
/// </summary>
public sealed partial class MainForm
{
    bool _scriptLastWorld;   // script --open-recent: open the workspace's last world as File > Open Recent does
    string? _openingName;    // the workspace being opened (null: not opening)

    /// <summary>"Opening NAME…" in the status bar with a moving progress bar, until the new world is shown.</summary>
    void ShowOpening(string name)
    {
        _openingName = name;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Visible = true;
        _status.Text = $"Opening {name}…";
        _status.Owner?.Update();
    }

    /// <summary>The current step (asset index, world) after the name, while opening.</summary>
    void OpeningStep(string? text)
    {
        if (_openingName == null) return;
        if (text == null)
        {
            // a step finished: keep showing that the workspace is still opening
            _progress.Style = ProgressBarStyle.Marquee; _progress.Visible = true;
            _status.Text = $"Opening {_openingName}…";
        }
        else _progress.Style = ProgressBarStyle.Blocks;   // a step with real progress (loading textures …)
        _status.Owner?.Update();
    }

    void HideOpening()
    {
        if (_openingName == null) return;
        _openingName = null;
        _progress.Style = ProgressBarStyle.Blocks;
        if (!_busy) { _progress.Visible = false; if (_status.Text.StartsWith("Opening ")) _status.Text = ""; }
    }
}
