using System.IO.Compression;

namespace T2A.Util;

/// <summary>Minimal PNG writer for 32-bit RGBA images (used by export + screenshots).</summary>
public static class Png
{
    /// <summary>pixels are packed 0xAABBGGRR (memory order R,G,B,A), row-major, top-to-bottom.</summary>
    public static void WriteRgba(string path, int w, int h, uint[] pixels)
    {
        using var fs = File.Create(path);
        Span<byte> sig = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        fs.Write(sig);

        // IHDR
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w);
        WriteBE(ihdr, 4, h);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type RGBA
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(fs, "IHDR", ihdr);

        // Raw image data with per-row filter byte 0.
        var raw = new byte[h * (1 + w * 4)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0; // filter: none
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                uint p = pixels[row + x];
                raw[o++] = (byte)(p & 0xFF);          // R
                raw[o++] = (byte)((p >> 8) & 0xFF);   // G
                raw[o++] = (byte)((p >> 16) & 0xFF);  // B
                raw[o++] = (byte)((p >> 24) & 0xFF);  // A
            }
        }

        byte[] idat = ZlibCompress(raw);
        Chunk(fs, "IDAT", idat);
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x9C); // zlib header (default compression)
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(data, 0, data.Length);
        uint adler = Adler32(data);
        Span<byte> a = stackalloc byte[4];
        a[0] = (byte)(adler >> 24); a[1] = (byte)(adler >> 16); a[2] = (byte)(adler >> 8); a[3] = (byte)adler;
        ms.Write(a);
        return ms.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        const uint MOD = 65521;
        uint a = 1, b = 0;
        foreach (byte d in data) { a = (a + d) % MOD; b = (b + a) % MOD; }
        return (b << 16) | a;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        WriteBE(len, 0, data.Length);
        s.Write(len);
        var t = new byte[4] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        s.Write(t);
        s.Write(data);
        uint crc = Crc32(t, data);
        Span<byte> c = stackalloc byte[4];
        WriteBE(c, 0, (int)crc);
        s.Write(c);
    }

    private static void WriteBE(Span<byte> dst, int off, int v)
    {
        dst[off] = (byte)(v >> 24); dst[off + 1] = (byte)(v >> 16);
        dst[off + 2] = (byte)(v >> 8); dst[off + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = BuildCrc();
    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
