using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Tyrian;
using SdlNs = Hexa.NET.SDL2;

namespace T2LV.Render;

/// <summary>
/// One indexed sprite pushed through a palette into an SDL texture — the datacube
/// portraits. Index 0 is the sprite format's transparent pixel, so it uploads as
/// fully transparent rather than as palette colour 0.
/// </summary>
public sealed unsafe class SpriteImage : IDisposable
{
    public int W { get; private set; }
    public int H { get; private set; }

    private SdlNs.SDLTexturePtr _tex;
    private bool _created;

    public void Update(SdlNs.SDLRendererPtr renderer, Sprite sprite, uint[] palette)
    {
        if (sprite.W != W || sprite.H != H)
        {
            Dispose();
            W = sprite.W; H = sprite.H;
        }
        if (W <= 0 || H <= 0) return;

        var rgba = new uint[W * H];
        for (int i = 0; i < rgba.Length; i++)
        {
            byte c = sprite.Pixels[i];
            rgba[i] = c == 0 ? 0u : palette[c] | 0xFF000000u;
        }

        if (!_created)
        {
            _tex = SdlNs.SDL.CreateTexture(renderer, Gfx.SDL_PIXELFORMAT_ABGR8888,
                Gfx.SDL_TEXTUREACCESS_STATIC, W, H);
            SdlNs.SDL.SetTextureBlendMode(_tex, SdlNs.SDLBlendMode.Blend);
            _created = true;
        }
        fixed (uint* p = rgba)
            SdlNs.SDL.UpdateTexture(_tex, default, (nint)p, W * 4);
    }

    public void Draw(ImDrawListPtr dl, Vector2 pos, float scale)
    {
        if (!_created) return;
        dl.AddImage(Gfx.TexRef((nint)_tex.Handle), pos, pos + new Vector2(W * scale, H * scale));
    }

    public void Dispose()
    {
        if (_created) SdlNs.SDL.DestroyTexture(_tex);
        _created = false;
        W = H = 0;
    }
}
