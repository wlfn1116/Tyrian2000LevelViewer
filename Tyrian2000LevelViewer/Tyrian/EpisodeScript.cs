using System.Text;

namespace T2LV.Tyrian;

/// <summary>One playable level as declared by a ]L command in levels%d.dat.</summary>
public sealed class LevelEntry
{
    public int SectionIndex;     // which '*' section this ]L appeared in
    public string Name = "";     // levelName (offset 13, 9 chars)
    public int NextLevel;        // offset 9
    public int Song;             // offset 22 (1-based)
    public int LvlFileNum;       // offset 25 (1-based section in tyrian%d.lvl)
    public bool BonusLevel;      // offset 28 == '$'
    public bool NormalBonus;     // offset 27 == '$'

    public override string ToString() => $"#{LvlFileNum} {Name.Trim()}";
}

/// <summary>
/// levels%d.dat — a list of XOR-encrypted, length-prefixed Pascal strings that
/// script an episode. We decrypt them all and extract the ]L level definitions.
/// Decryption: helptext.c:83-117. ]L parsing: tyrian2.c:2686-2699.
/// </summary>
public static class EpisodeScript
{
    private static readonly byte[] CryptKey = { 204, 129, 63, 255, 71, 19, 25, 62, 1, 99 };

    public static List<string> DecryptStrings(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var result = new List<string>();
        int pos = 0;
        while (pos < data.Length)
        {
            int len = data[pos++];
            if (pos + len > data.Length) break;
            var buf = new byte[len];
            Array.Copy(data, pos, buf, 0, len);
            pos += len;
            for (int i = len - 1; i >= 0; i--)
            {
                buf[i] ^= CryptKey[i % 10];
                if (i > 0) buf[i] ^= buf[i - 1];
            }
            result.Add(Encoding.Latin1.GetString(buf));
        }
        return result;
    }

    /// <summary>Parse the script and return every ]L level definition in order.</summary>
    public static List<LevelEntry> ParseLevels(string path)
    {
        var strings = DecryptStrings(path);
        var levels = new List<LevelEntry>();
        int section = 0;
        foreach (var s in strings)
        {
            if (s.Length == 0) continue;
            if (s[0] == '*') { section++; continue; }
            if (s[0] != ']' || s.Length < 2) continue;
            if (s[1] != 'L') continue;

            var e = new LevelEntry { SectionIndex = section };
            e.NextLevel = AtoiAt(s, 9);
            e.Name = SubFixed(s, 13, 9);
            e.Song = AtoiAt(s, 22);
            e.LvlFileNum = AtoiAt(s, 25);
            e.NormalBonus = s.Length > 27 && s[27] == '$';
            e.BonusLevel = s.Length > 28 && s[28] == '$';
            if (e.LvlFileNum > 0)
                levels.Add(e);
        }
        return levels;
    }

    // C atoi(s + off): skip leading spaces, read optional sign + digits, stop at non-digit.
    private static int AtoiAt(string s, int off)
    {
        if (off >= s.Length) return 0;
        int i = off;
        while (i < s.Length && s[i] == ' ') i++;
        int sign = 1;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) { if (s[i] == '-') sign = -1; i++; }
        long v = 0; bool any = false;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9') { v = v * 10 + (s[i] - '0'); i++; any = true; }
        return any ? (int)(sign * v) : 0;
    }

    private static string SubFixed(string s, int off, int len)
    {
        if (off >= s.Length) return "";
        int end = Math.Min(off + len, s.Length);
        return s.Substring(off, end - off);
    }
}
