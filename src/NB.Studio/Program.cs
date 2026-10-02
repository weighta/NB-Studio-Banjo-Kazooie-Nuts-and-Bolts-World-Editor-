namespace NB.Studio;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.ToString(), "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        // "--photos [files]", or console photo packages dropped on NBStudio.exe: only the Xbox 360 Photo Viewer
        var files = args.Where(a => a != "--photos").ToList();
        if (args.Contains("--photos") || (files.Count > 0 && files.All(IsPackage)))
        {
            Application.Run(new Panels.PhotoViewerForm(files));
            return;
        }
        Application.Run(new MainForm());
    }

    static bool IsPackage(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var f = File.OpenRead(path);
            var head = new byte[4];
            return f.Read(head, 0, 4) == 4 && NB.Core.Formats.StfsPackage.IsStfs(head);
        }
        catch (IOException) { return false; }
    }
}
