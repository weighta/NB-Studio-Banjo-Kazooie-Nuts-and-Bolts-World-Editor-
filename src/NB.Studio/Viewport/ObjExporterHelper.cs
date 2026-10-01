namespace NB.Studio.Models;

internal static class ObjExporterHelper
{
    public static string? Diffuse(NB.Core.Models.MeshDraw d) => NB.Core.Models.ObjExporter.DiffuseTexture(d);
}
