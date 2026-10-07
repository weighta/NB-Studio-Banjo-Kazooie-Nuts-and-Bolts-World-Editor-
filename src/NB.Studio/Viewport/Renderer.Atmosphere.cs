using OpenTK.Graphics.OpenGL4;

namespace NB.Studio.Viewport;

public sealed partial class Renderer
{
    /// <summary>Decodes the textures whose name matches again (through <see cref="TextureSource"/>) and uploads them into
    /// their existing GL textures, so batches that already resolved them show the new image. Returns how many were
    /// refreshed. The GL context must be current.</summary>
    public int ReloadTextures(Func<string, bool> match)
    {
        int n = 0;
        foreach (var (name, t) in _textures.ToList())
        {
            if (t == 0 || !match(name)) continue;
            var img = TextureSource?.Invoke(name);
            if (img is not { } im || im.W <= 0 || im.H <= 0) continue;
            GL.BindTexture(TextureTarget.Texture2D, t);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, im.W, im.H, 0, PixelFormat.Rgba, PixelType.UnsignedByte, im.Rgba);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            n++;
        }
        Array.Fill(_bound, -1);
        return n;
    }
}
