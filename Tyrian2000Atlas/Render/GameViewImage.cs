using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Tyrian;
using SdlNs = Hexa.NET.SDL2;

namespace T2A.Render;

/// <summary>
/// The playback frame: a crop of the simulator's indexed buffer (the 264x184 playfield,
/// or the whole extended buffer) converted through the palette into one small SDL
/// texture, re-uploaded whenever the frame changes.
/// </summary>
public sealed unsafe class GameViewImage : IDisposable
{
    public int W { get; private set; }
    public int H { get; private set; }

    private uint[] _rgba = Array.Empty<uint>();
    private SdlNs.SDLTexturePtr _tex;
    private bool _created;

    /// <summary>Convert a crop of the sim's indexed buffer and upload.</summary>
    public void Update(SdlNs.SDLRendererPtr renderer, byte[] screen, uint[] palette,
        int x0, int y0, int w, int h)
    {
        if (w != W || h != H)
        {
            if (_created) { SdlNs.SDL.DestroyTexture(_tex); _created = false; }
            W = w; H = h;
            _rgba = new uint[w * h];
        }

        for (int y = 0; y < h; y++)
        {
            int src = (y0 + y) * GameSim.BufW + x0;
            int dst = y * w;
            for (int x = 0; x < w; x++)
                _rgba[dst + x] = palette[screen[src + x]] | 0xFF000000u;
        }

        if (!_created)
        {
            _tex = SdlNs.SDL.CreateTexture(renderer, Gfx.SDL_PIXELFORMAT_ABGR8888,
                Gfx.SDL_TEXTUREACCESS_STATIC, W, H);
            SdlNs.SDL.SetTextureBlendMode(_tex, SdlNs.SDLBlendMode.None);
            _created = true;
        }
        fixed (uint* p = _rgba)
            SdlNs.SDL.UpdateTexture(_tex, default, (nint)p, W * 4);
    }

    /// <summary>
    /// A copy of the pixels last uploaded, for saving a screenshot. Whatever crop the view is
    /// showing (Engaged / extended) is what comes out, because this is that same frame.
    /// </summary>
    public uint[] Snapshot() => (uint[])_rgba.Clone();

    public void Draw(ImDrawListPtr dl, Vector2 pos, float scale)
    {
        if (!_created) return;
        dl.AddImage(Gfx.TexRef((nint)_tex.Handle), pos, pos + new Vector2(W * scale, H * scale));
    }

    public void Dispose()
    {
        if (_created) SdlNs.SDL.DestroyTexture(_tex);
        _created = false;
    }
}
