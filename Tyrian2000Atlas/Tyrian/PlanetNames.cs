namespace T2LV.Tyrian;

/// <summary>
/// pName[21] from tyrian.hdt — the galaxy-map planet names the outpost's "next level"
/// menu lists. JE_loadHelpText (helptext.c:188-208) reads the file as a plain int32
/// followed by encrypted Pascal strings, so reaching the planet block is just a matter
/// of skipping the 39 help texts ahead of it.
/// </summary>
public static class PlanetNames
{
    private const int HelpTxtCount = 39;   // helpTxt[39][231]
    private const int PNameCount = 21;     // pName[21][16]

    /// <summary>Planet names indexed 1..21, or an empty list if tyrian.hdt is unreadable.</summary>
    public static List<string> Load(string dataDir)
    {
        var names = new List<string> { "" };   // 1-based, like the script's planet ids
        try
        {
            byte[] raw = File.ReadAllBytes(Path.Combine(dataDir, "tyrian.hdt"));
            int pos = 4;                                   // episode1DataLoc
            Skip(raw, ref pos);                            // section marker string
            for (int i = 0; i < HelpTxtCount; i++) Skip(raw, ref pos);
            Skip(raw, ref pos); Skip(raw, ref pos);        // end marker + planet-block marker
            for (int i = 0; i < PNameCount; i++)
                names.Add(ReadEncrypted(raw, ref pos).Trim());
        }
        catch { return new List<string> { "" }; }
        return names;
    }

    public static string Get(List<string> names, int planet) =>
        planet >= 1 && planet < names.Count ? names[planet] : "";

    private static void Skip(byte[] raw, ref int pos)
    {
        if (pos >= raw.Length) return;
        pos += 1 + raw[pos];
    }

    private static string ReadEncrypted(byte[] raw, ref int pos)
    {
        if (pos >= raw.Length) return "";
        int len = raw[pos++];
        if (pos + len > raw.Length) { pos = raw.Length; return ""; }
        var buf = new byte[len];
        Array.Copy(raw, pos, buf, 0, len);
        pos += len;
        for (int i = len - 1; i >= 0; i--)
        {
            buf[i] ^= CryptKey[i % 10];
            if (i > 0) buf[i] ^= buf[i - 1];
        }
        return System.Text.Encoding.Latin1.GetString(buf);
    }

    private static readonly byte[] CryptKey = { 204, 129, 63, 255, 71, 19, 25, 62, 1, 99 };
}
