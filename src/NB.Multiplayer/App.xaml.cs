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
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
