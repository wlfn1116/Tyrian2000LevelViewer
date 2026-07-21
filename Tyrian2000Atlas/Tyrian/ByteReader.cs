namespace T2LV.Tyrian;

/// <summary>
/// Little-endian binary reader over a byte[] with an explicit cursor.
/// Mirrors the fread_*_die helpers in OpenTyrian (file.h) which only
/// byte-swap on big-endian hosts, i.e. the on-disk data is little-endian.
/// </summary>
public sealed class ByteReader
{
    private readonly byte[] _data;
    public int Pos;

    public ByteReader(byte[] data, int pos = 0)
    {
        _data = data;
        Pos = pos;
    }

    public int Length => _data.Length;
    public byte[] Data => _data;

    public void Seek(int pos) => Pos = pos;

    public byte U8() => _data[Pos++];
    public sbyte S8() => (sbyte)_data[Pos++];

    public ushort U16()
    {
        ushort v = (ushort)(_data[Pos] | (_data[Pos + 1] << 8));
        Pos += 2;
        return v;
    }

    public short S16() => (short)U16();

    /// <summary>Big-endian u16 — used only for the mapSh lookup table,
    /// which OpenTyrian byte-swaps unconditionally (tyrian2.c:3141).</summary>
    public ushort U16BE()
    {
        ushort v = (ushort)((_data[Pos] << 8) | _data[Pos + 1]);
        Pos += 2;
        return v;
    }

    public int S32()
    {
        int v = _data[Pos] | (_data[Pos + 1] << 8) | (_data[Pos + 2] << 16) | (_data[Pos + 3] << 24);
        Pos += 4;
        return v;
    }

    /// <summary>A length-prefixed name in a fixed-width buffer, as the item tables store them:
    /// one length byte then <paramref name="buffer"/> bytes, space-padded (episodes.c:477-480).</summary>
    public string PascalName(int buffer = 30)
    {
        int n = U8();
        var s = System.Text.Encoding.ASCII.GetString(_data, Pos, Math.Min(buffer, Math.Max(0, Length - Pos)));
        Pos += buffer;
        return s[..Math.Min(n, s.Length)].TrimEnd();
    }

    public byte[] Bytes(int count)
    {
        var b = new byte[count];
        Array.Copy(_data, Pos, b, 0, count);
        Pos += count;
        return b;
    }

    public void Read(byte[] dest, int count)
    {
        Array.Copy(_data, Pos, dest, 0, count);
        Pos += count;
    }
}
