namespace NB.Studio;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (NB.Core.IO.QuietLaunch.Enabled)
        {
            // background test runs: the 3D views' GLFW windows must not take the foreground when they are shown
            NB.Core.IO.QuietLaunch.LockForeground();
            OpenTK.Windowing.Desktop.GLFWProvider.EnsureInitialized();
            OpenTK.Windowing.GraphicsLibraryFramework.GLFW.WindowHint(OpenTK.Windowing.GraphicsLibraryFramework.WindowHintBool.FocusOnShow, false);
            OpenTK.Windowing.GraphicsLibraryFramework.GLFW.WindowHint(OpenTK.Windowing.GraphicsLibraryFramework.WindowHintBool.Focused, false);
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.ToString(), "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        // "--photos [files]", or console photo packages dropped on NBStudio.exe: only the Xbox 360 Photo Viewer
        var files = args.Where(a => a != "--photos").ToList();
        // vehicle saves (packages "VEHICLE: …", their content files, blueprints): the Vehicle Editor
        if (!args.Contains("--photos") && files.Count > 0 && files.All(VehicleRouting.IsVehicleFile))
        {
            MainForm.VehicleFilesAtStart = files;
            Application.Run(new MainForm());
            return;
        }
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
            var head = new byte[0x28];
            int n = f.Read(head, 0, head.Length);
            // a 360 package, or a photo's content extracted from one (game header + JPEG): both open in the photo viewer
            return (n >= 4 && NB.Core.Formats.StfsPackage.IsStfs(head.AsSpan(0, 4))) || (n == head.Length && NB.Core.Formats.NbPhoto.IsPhotoContent(head));
        }
        catch (IOException) { return false; }
    }
}
