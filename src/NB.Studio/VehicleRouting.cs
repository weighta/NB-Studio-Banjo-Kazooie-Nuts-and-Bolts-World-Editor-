using NB.Core.Formats;
using NB.Core.Vehicles;

namespace NB.Studio;

/// <summary>
/// Sends Nuts &amp; Bolts vehicle saves (packages "VEHICLE: …", their content files, blueprints) to the Vehicle Editor:
/// from NBModStudio.exe's command line (files dropped on the exe) and from the Xbox 360 Photo Viewer.
/// </summary>
static class VehicleRouting
{
    /// <summary>Is this file a vehicle: a N&amp;B vehicle package, a vehicle content file, or a bare blueprint (.bin)?</summary>
    public static bool IsVehicleFile(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32 << 20) return false;
            var d = File.ReadAllBytes(path);
            return VehicleFile.Detect(d) switch
            {
                VehicleFileKind.Package => VehicleFile.IsVehiclePackage(new StfsPackage(d)),
                VehicleFileKind.Content => true,
                VehicleFileKind.Blueprint => path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Opens the files in this process's main window, or starts NB Studio with them.</summary>
    public static void Open(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        if (Application.OpenForms.OfType<MainForm>().FirstOrDefault() is { } main) { main.OpenVehicles(files); return; }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = false };
            foreach (var f in files) psi.ArgumentList.Add(f);
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Vehicle Editor"); }
    }
}
