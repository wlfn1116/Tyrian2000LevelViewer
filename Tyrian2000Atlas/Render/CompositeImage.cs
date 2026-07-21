using System.Numerics;
using Hexa.NET.ImGui;
using SdlNs = Hexa.NET.SDL2;

namespace T2A.Render;

/// <summary>
/// A large RGBA image (the full composited level) backed by a vertical stack of
/// SDL textures, because a single texture can exceed the GPU max dimension.
/// </summary>
public sealed unsafe class CompositeImage : IDisposable
{
    public const int ChunkH = 2048;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] Pixels = Array.Empty<uint>();
    // The game renders into an indexed 8-bit framebuffer. Keep the exact index beside
    // the display RGBA so palette-index effects (notably BG2 blending) remain exact
    // even when two palette entries have the same RGB value. Partial-opacity UI layers
    // leave the underlying game index in place; the default opaque stack is exact.
    public byte[] PaletteIndices = Array.Empty<byte>();

    private readonly List<(SdlNs.SDLTexturePtr tex, int y0, int h)> _chunks = new();
    private bool _dirty;

    public void Resize(int w, int h)
    {
        if (w == Width && h == Height) return;
        Width = w; Height = h;
        Pixels = new uint[w * h];
        PaletteIndices = new byte[w * h];
        _dirty = true;
    }

    public void Clear(uint color = 0)
    {
        Array.Fill(Pixels, color);
        Array.Fill(PaletteIndices, (byte)0);
        _dirty = true;
    }

    public void MarkDirty() => _dirty = true;

    public void Upload(SdlNs.SDLRendererPtr renderer)
    {
        if (!_dirty) return;
        _dirty = false;
        DestroyChunks();
        if (Width <= 0 || Height <= 0) return;

        for (int y0 = 0; y0 < Height; y0 += ChunkH)
        {
            int h = Math.Min(ChunkH, Height - y0);
            var tex = SdlNs.SDL.CreateTexture(renderer, Gfx.SDL_PIXELFORMAT_ABGR8888,
                Gfx.SDL_TEXTUREACCESS_STATIC, Width, h);
            SdlNs.SDL.SetTextureBlendMode(tex, SdlNs.SDLBlendMode.Blend);
            fixed (uint* p = &Pixels[y0 * Width])
                SdlNs.SDL.UpdateTexture(tex, default, (nint)p, Width * 4);
            _chunks.Add((tex, y0, h));
        }
    }

    /// <summary>Draw the image into a draw list with top-left at origin, scaled by 'scale'.</summary>
    public void Draw(ImDrawListPtr dl, Vector2 origin, float scale, uint tint = 0xFFFFFFFF)
    {
        foreach (var (tex, y0, h) in _chunks)
        {
            var pmin = new Vector2(origin.X, origin.Y + y0 * scale);
            var pmax = new Vector2(origin.X + Width * scale, origin.Y + (y0 + h) * scale);
            dl.AddImage(Gfx.TexRef((nint)tex.Handle), pmin, pmax,
                new Vector2(0, 0), new Vector2(1, 1), tint);
        }
    }

    /// <summary>Draw the whole image squashed into an arbitrary rect (minimap thumbnails).</summary>
    public void DrawInRect(ImDrawListPtr dl, Vector2 rectMin, Vector2 rectMax, uint tint = 0xFFFFFFFF)
    {
        if (Height <= 0) return;
        float h = rectMax.Y - rectMin.Y;
        foreach (var (tex, y0, ch) in _chunks)
        {
            var pmin = new Vector2(rectMin.X, rectMin.Y + (float)y0 / Height * h);
            var pmax = new Vector2(rectMax.X, rectMin.Y + (float)(y0 + ch) / Height * h);
            dl.AddImage(Gfx.TexRef((nint)tex.Handle), pmin, pmax,
                new Vector2(0, 0), new Vector2(1, 1), tint);
        }
    }

    private void DestroyChunks()
    {
        foreach (var (tex, _, _) in _chunks)
            SdlNs.SDL.DestroyTexture(tex);
        _chunks.Clear();
    }

    public void Dispose() => DestroyChunks();
}
