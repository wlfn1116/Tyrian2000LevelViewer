namespace T2A.Tyrian;

/// <summary>A decoded sprite: 12px-wide indexed bitmap, 0 = transparent.</summary>
public sealed class Sprite
{
    public int W;
    public int H;
    public byte[] Pixels = Array.Empty<byte>(); // W*H palette indices, 0 = transparent
}

/// <summary>
/// A "CompShapes" sprite sheet (newsh*.shp, or a sub-blob of tyrian.shp). Format:
/// a u16-LE offset table (1-based sprite index), then per-sprite nibble-RLE streams
/// terminated by 0x0F. See sprite.c:blit_sprite2.
/// </summary>
public sealed class CompShapes
{
    public const int SpriteW = 12;

    private readonly byte[] _data;
    private readonly int _base;     // offset of this sheet within _data
    private readonly int _len;
    public readonly int Count;
    private readonly Sprite?[] _cache;

    public CompShapes(byte[] data, int baseOffset, int len)
    {
        _data = data; _base = baseOffset; _len = len;
        int first = U16(0);
        Count = first / 2;
        _cache = new Sprite?[Math.Max(1, Count + 1)];
    }

    public static CompShapes LoadFile(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        return new CompShapes(d, 0, d.Length);
    }

    private ushort U16(int rel) => (ushort)(_data[_base + rel] | (_data[_base + rel + 1] << 8));

    /// <summary>Decode sprite by 1-based index. Returns null if out of range.</summary>
    public Sprite? Decode(int index)
    {
        if (index < 1 || index >= Count + 1) return null;
        if (index < _cache.Length && _cache[index] != null) return _cache[index];

        int p = _base + U16((index - 1) * 2);
        // temp buffer (sprites are small)
        const int MaxH = 64;
        var tmp = new byte[SpriteW * MaxH];
        int x = 0, y = 0, maxY = 0;

        while (p < _base + _len && _data[p] != 0x0F)
        {
            byte c = _data[p];
            int skip = c & 0x0F;
            int count = (c & 0xF0) >> 4;
            x += skip;
            if (count == 0)
            {
                y++; x = 0;
                if (y >= MaxH) break;
            }
            else
            {
                for (int k = 0; k < count; k++)
                {
                    p++;
                    if (p >= _base + _len) break;
                    if (x >= 0 && x < SpriteW && y >= 0 && y < MaxH)
                    {
                        tmp[y * SpriteW + x] = _data[p];
                        if (y > maxY) maxY = y;
                    }
                    x++;
                }
            }
            p++;
        }

        int h = maxY + 1;
        var spr = new Sprite { W = SpriteW, H = h, Pixels = new byte[SpriteW * h] };
        Array.Copy(tmp, spr.Pixels, SpriteW * h);
        if (index < _cache.Length) _cache[index] = spr;
        return spr;
    }
}

/// <summary>
/// A tyrian.shp sub-table in the plain "Sprite_array" form the fonts, planets and faces
/// use: u16 count, then per sprite a populated flag and, if set, u16 width/height/size
/// followed by size bytes of RLE. Unlike CompShapes these are full-size bitmaps of any
/// width. Decoding follows sprite.c:blit_sprite — 255 skips N pixels, 254 ends the row,
/// 253 skips one, anything else is a palette index.
/// </summary>
public sealed class SpriteBank
{
    private readonly Sprite?[] _sprites;
    public int Count => _sprites.Length;

    private SpriteBank(Sprite?[] sprites) => _sprites = sprites;

    public Sprite? Get(int index) =>
        index >= 0 && index < _sprites.Length ? _sprites[index] : null;

    public static SpriteBank Parse(byte[] d, int offset)
    {
        var r = new ByteReader(d, offset);
        int count = r.U16();
        var sprites = new Sprite?[count];
        for (int i = 0; i < count; i++)
        {
            if (r.Pos >= r.Length) break;
            if (r.U8() == 0) continue;                       // empty slot
            int w = r.U16(), h = r.U16(), size = r.U16();
            if (r.Pos + size > r.Length) break;
            var data = new byte[size];
            r.Read(data, size);
            sprites[i] = Decode(data, w, h);
        }
        return new SpriteBank(sprites);
    }

    private static Sprite Decode(byte[] data, int w, int h)
    {
        var spr = new Sprite { W = w, H = h, Pixels = new byte[Math.Max(1, w * h)] };
        int x = 0, y = 0;
        for (int p = 0; p < data.Length && y < h; p++)
        {
            switch (data[p])
            {
                case 255: x += p + 1 < data.Length ? data[++p] : 0; break;
                case 254: x = w; break;
                case 253: x++; break;
                default:
                    if (x >= 0 && x < w) spr.Pixels[y * w + x] = data[p];
                    x++;
                    break;
            }
            if (x < w) continue;
            y++;
            x = 0;
        }
        return spr;
    }
}

/// <summary>
/// tyrian.shp master file: u16 count(=13) + s32 offsets[count]. Sub-tables 0..6 are
/// Sprite_array banks (fonts, planets, faces, options, weapons); 7..12 are CompShapes.
/// See sprite.c:JE_loadMainShapeTables.
/// </summary>
public sealed class MainShapes
{
    public readonly CompShapes?[] Sheets = new CompShapes?[13];
    public readonly SpriteBank?[] Banks = new SpriteBank?[7];

    public static MainShapes Load(string path)
    {
        var ms = new MainShapes();
        byte[] d = File.ReadAllBytes(path);
        var r = new ByteReader(d);
        int count = r.U16();
        var off = new int[count + 1];
        for (int i = 0; i < count; i++) off[i] = r.S32();
        off[count] = d.Length;

        for (int i = 0; i < Math.Min(7, count); i++)
        {
            try { ms.Banks[i] = SpriteBank.Parse(d, off[i]); }
            catch { /* a bank we don't need shouldn't stop the ones we do */ }
        }
        // Sub-tables 7..12 are CompShapes blobs.
        for (int i = 7; i < count; i++)
            ms.Sheets[i] = new CompShapes(d, off[i], off[i + 1] - off[i]);
        return ms;
    }

    public CompShapes? PowerUps => Sheets[9];    // shapebank 26
    public CompShapes? CoinsGems => Sheets[10];  // shapebank 21
    public CompShapes? Ships => Sheets[8];
    public CompShapes? ShipsT2000 => Sheets[12];
    public SpriteBank? Faces => Banks[4];        // FACE_SHAPES: the datacube portraits
}
