using System.Windows;
using NB.Multiplayer.Services;

namespace NB.Multiplayer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // update helper mode: NBMultiplayer-updater.exe --apply-update <new files> <install folder> <app pid>
        if (e.Args.Length == 4 && e.Args[0] == "--apply-update")
        {
            try { Updater.ApplyUpdate(e.Args[1], e.Args[2], int.Parse(e.Args[3])); }
            catch (Exception ex) { MessageBox.Show("The update could not be installed:\n" + ex.Message + $"\n\nDownload it from {Updater.ReleasesPage}", "NB Multiplayer"); }
            Shutdown();
            return;
        }
        // the Steam overlay (loaded into this process for the Steam relay) hooks Direct3D: on Shift+Tab it drew into the
        // window and froze it. NB Multiplayer draws its window in software instead (no Direct3D for the overlay to hook),
        // and asks the overlay not to draw (it is meant for the game, not this launcher)
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Environment.SetEnvironmentVariable("SteamNoOverlayUIDrawing", "1");
        base.OnStartup(e);
        // unexpected errors: log them (crash.log in the data folder) and keep running when possible
        DispatcherUnhandledException += (_, ex) =>
        {
            LogCrash(ex.Exception);
            MessageBox.Show("Something went wrong:\n" + ex.Exception.Message + "\n\nDetails were saved to crash.log (Settings > Open data folder).", "NB Multiplayer");
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { if (ex.ExceptionObject is Exception x) LogCrash(x); };
        new MainWindow().Show();
    }

    static void LogCrash(Exception x)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Root);
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppSettings.Root, "crash.log"), $"==== {DateTime.Now} v{Updater.Current}\n{x}\n\n");
        }
        catch (Exception) { }
    }
}
