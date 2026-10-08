using System.Diagnostics;
using System.Runtime.InteropServices;
using NB.Core.IO;

namespace NB.Studio;

/// <summary>
/// Background test runs (env NB_STUDIO_BACKGROUND=1, set by test harnesses): NB Studio and the games it starts open
/// behind the other windows and never take the keyboard focus, so someone using the same desktop is not interrupted.
/// Normal use is unchanged.
/// </summary>
public partial class MainForm
{
    protected override bool ShowWithoutActivation => QuietLaunch.Enabled || base.ShowWithoutActivation;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (QuietLaunch.Enabled) cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE: the window never becomes the active one on its own
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        if (QuietLaunch.Enabled) QuietLaunch.PushBehind(Handle);
        base.OnShown(e);
    }

    /// <summary>Starts a game (Xenia); in background test runs without activating it, behind the other windows.</summary>
    static Process? StartGame(ProcessStartInfo psi) => QuietLaunch.Enabled ? QuietLaunch.Start(psi) : Process.Start(psi);

    internal static class Background
    {
        [DllImport("user32")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

        /// <summary>The window's pixels even when other windows cover it (PrintWindow, full content).</summary>
        public static Bitmap Capture(Form f)
        {
            var b = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height));
            using var g = Graphics.FromImage(b);
            var hdc = g.GetHdc();
            try { PrintWindow(f.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */); } finally { g.ReleaseHdc(hdc); }
            return b;
        }
    }
}
