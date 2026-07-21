using Hexa.NET.ImGui;
using SdlNs = Hexa.NET.SDL2;

namespace T2A.Render;

/// <summary>Small interop helpers shared by the rendering code.</summary>
public static unsafe class Gfx
{
    // SDL_DEFINE_PIXELFORMAT(PACKED32, ABGR, 8888, 32, 4) -> bytes R,G,B,A in memory on LE.
    public const uint SDL_PIXELFORMAT_ABGR8888 = 0x16762004u;
    public const int SDL_TEXTUREACCESS_STATIC = 0;

    /// <summary>Wrap a raw SDL_Texture* as an ImTextureRef for the SDLRenderer2 ImGui backend.</summary>
    public static ImTextureRef TexRef(nint sdlTexture)
    {
        ImTextureID id = default;
        *(ulong*)&id = (ulong)sdlTexture;
        return new ImTextureRef(null, id);
    }

    public static uint Rgba(byte r, byte g, byte b, byte a = 255)
        => (uint)(r | (g << 8) | (b << 16) | (a << 24));
}
