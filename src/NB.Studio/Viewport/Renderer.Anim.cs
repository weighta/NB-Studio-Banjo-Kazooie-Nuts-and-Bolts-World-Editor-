using NB.Core.Models;
using OpenTK.Graphics.OpenGL4;

namespace NB.Studio.Viewport;

public sealed partial class Renderer
{
    /// <summary>Re-uploads the vertices of a model whose vertex arrays changed in place (an animated character): the
    /// batches are rebuilt with the same grouping (<paramref name="mats"/> = the materials the batches were made with)
    /// and written into the existing buffers. A model drawn for the first time gets its buffers when it is drawn.</summary>
    public void UpdateVertices(ModelAsset model, Dictionary<MeshDraw, MaterialInfo> mats)
    {
        if (!_batches.TryGetValue(model, out var bs)) return;
        var prep = PrepareModel(model, mats);
        // Batches() orders the groups opaque first (stable sort by blend kind)
        var order = Enumerable.Range(0, prep.Groups.Count).OrderBy(i => prep.Groups[i].Mat.Blend).ToArray();
        if (order.Length != bs.Length) { ForgetModel(model); return; }
        for (int k = 0; k < bs.Length; k++)
        {
            var verts = prep.Groups[order[k]].Verts;
            GL.BindBuffer(BufferTarget.ArrayBuffer, bs[k].Vbo);
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, verts.Length * 4, verts);
        }
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    /// <summary>Frees the GPU buffers of a model that will not be drawn again (a posed copy).</summary>
    public void ForgetModel(ModelAsset model)
    {
        if (!_batches.Remove(model, out var bs)) return;
        foreach (var b in bs) { GL.DeleteVertexArray(b.Vao); GL.DeleteBuffer(b.Vbo); GL.DeleteBuffer(b.Ebo); }
        _batchGen.Remove(model);
        Array.Fill(_bound, -1);
    }
}
