using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Tyrian;
using SdlNs = Hexa.NET.SDL2;

namespace T2LV.Render;

/// <summary>
/// The playback frame: the simulator's 264x184 playfield crop converted through the
/// palette into a single small SDL texture, re-uploaded whenever the frame changes.
/// </summary>
public sealed unsafe class GameViewImage : IDisposable
{
    public const int W = GameSim.ViewW, H = GameSim.ViewH;

    private readonly uint[] _rgba = new uint[W * H];
    private SdlNs.SDLTexturePtr _tex;
    private bool _created;

    /// <summary>Convert the sim's indexed screen (playfield crop) and upload.</summary>
    public void Update(SdlNs.SDLRendererPtr renderer, byte[] screen, uint[] palette)
    {
        for (int y = 0; y < H; y++)
        {
            int src = y * GameSim.Pitch + GameSim.ViewX;
            int dst = y * W;
            for (int x = 0; x < W; x++)
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
