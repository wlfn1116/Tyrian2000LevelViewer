namespace T2A.Tyrian;

/// <summary>
/// palette.dat — 24 palettes of 256 RGB colors. Each channel is a 6-bit VGA
/// value (0..63) and must be expanded to 8-bit with (c&lt;&lt;2)|(c&gt;&gt;6-ish).
/// See palette.c:JE_loadPals.
/// </summary>
public sealed class PaletteSet
{
    public const int ColorsPerPalette = 256;

    /// <summary>palettes[p] = 256 packed RGBA (0xAABBGGRR little-endian byte order R,G,B,A).</summary>
    public readonly uint[][] Palettes;
    public int Count => Palettes.Length;

    private PaletteSet(uint[][] pals) => Palettes = pals;

    public static PaletteSet Load(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        int count = raw.Length / (ColorsPerPalette * 3);
        var pals = new uint[count][];
        int o = 0;
        for (int p = 0; p < count; p++)
        {
            var pal = new uint[ColorsPerPalette];
            for (int i = 0; i < ColorsPerPalette; i++)
            {
                byte r = raw[o++], g = raw[o++], b = raw[o++];
                byte r8 = (byte)((r << 2) | (r >> 4));
                byte g8 = (byte)((g << 2) | (g >> 4));
                byte b8 = (byte)((b << 2) | (b >> 4));
                // Packed as R,G,B,A in memory (matches SDL_PIXELFORMAT_ABGR8888 on LE).
                pal[i] = (uint)(r8 | (g8 << 8) | (b8 << 16) | (0xFFu << 24));
            }
            pals[p] = pal;
        }
        return new PaletteSet(pals);
    }

    public uint[] Get(int index)
    {
        if (Palettes.Length == 0) return new uint[ColorsPerPalette];
        index = Math.Clamp(index, 0, Palettes.Length - 1);
        return Palettes[index];
    }
}
