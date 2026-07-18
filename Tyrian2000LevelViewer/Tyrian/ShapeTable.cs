namespace T2LV.Tyrian;

/// <summary>
/// shapes%c.dat — 600 background tiles. Each record: 1 byte "blank" flag;
/// if not blank, 24*28 = 672 raw palette-index bytes follow (row-major,
/// 0 = transparent). See tyrian2.c:3145-3219, varz.h:121 (JE_DanCShape).
/// </summary>
public sealed class ShapeTable
{
    public const int TileW = 24;
    public const int TileH = 28;
    public const int TilePixels = TileW * TileH; // 672
    public const int TileCount = 600;

    /// <summary>tiles[i] is the 672-byte index bitmap for shape id (i+1), or null if blank.</summary>
    public readonly byte[]?[] Tiles = new byte[TileCount][];
    public readonly char ShapeChar;

    private ShapeTable(char c) => ShapeChar = c;

    public static ShapeTable Load(string path, char shapeChar)
    {
        var t = new ShapeTable(shapeChar);
        byte[] raw = File.ReadAllBytes(path);
        int o = 0;
        for (int z = 0; z < TileCount && o < raw.Length; z++)
        {
            bool blank = raw[o++] != 0;
            if (blank)
                continue; // tile stays null (all transparent)
            var tile = new byte[TilePixels];
            Array.Copy(raw, o, tile, 0, TilePixels);
            o += TilePixels;
            t.Tiles[z] = tile;
        }
        return t;
    }

    /// <summary>Get tile bitmap by 1-based shape id (as stored in mapSh). Null if empty/out-of-range.</summary>
    public byte[]? GetById(int shapeId1Based)
    {
        int idx = shapeId1Based - 1;
        if (idx < 0 || idx >= TileCount) return null;
        return Tiles[idx];
    }
}
