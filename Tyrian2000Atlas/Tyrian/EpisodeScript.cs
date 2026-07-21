using System.Text;

namespace T2A.Tyrian;

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
    public bool GalagaMode;      // a ']g' preceded this ]L in the same section

    public override string ToString() => $"#{LvlFileNum} {Name.Trim()}";
}

/// <summary>
/// A whole levels%d.dat, decrypted, with the '*' section markers indexed. JE_loadMap
/// reads this file as one flat stream: it seeks past N section markers, then runs the
/// ']' commands from there, and a section that ends without loading a level or jumping
/// simply falls through into the next one. The flat line list plus per-section start
/// offsets is exactly what that walk needs.
/// </summary>
public sealed class EpisodeScriptFile
{
    public readonly List<string> Lines = new();
    /// <summary>Line index at which 1-based section N starts (index 0 unused).</summary>
    public readonly List<int> SectionStart = new() { 0 };

    public int SectionCount => SectionStart.Count - 1;

    /// <summary>Line index for the first command of a section, or -1 if out of range.</summary>
    public int StartOf(int section) =>
        section >= 1 && section < SectionStart.Count ? SectionStart[section] : -1;

    /// <summary>The '*' title line of the section a line index belongs to.</summary>
    public string TitleOf(int section) =>
        section >= 1 && section < SectionStart.Count && SectionStart[section] > 0
            ? Lines[SectionStart[section] - 1] : "";

    /// <summary>1-based section containing a line index (0 if before the first marker).</summary>
    public int SectionAt(int line)
    {
        int s = 0;
        for (int i = 1; i < SectionStart.Count && SectionStart[i] <= line; i++) s = i;
        return s;
    }

    public static EpisodeScriptFile Load(string path)
    {
        var f = new EpisodeScriptFile();
        foreach (var s in EpisodeScript.DecryptStrings(path))
        {
            f.Lines.Add(s);
            if (s.Length > 0 && s[0] == '*')
                f.SectionStart.Add(f.Lines.Count);   // commands start after the marker
        }
        return f;
    }
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
        // JE_loadMap clears galagaMode at new_game and then runs the section's commands in
        // order until a ]L loads a level, so a ]g only reaches the ]L below it in the same
        // section (tyrian2.c:4122/4241). Only ** ALE ** and SQUADRON carry one.
        bool galaga = false;
        foreach (var s in strings)
        {
            if (s.Length == 0) continue;
            if (s[0] == '*') { section++; galaga = false; continue; }
            if (s[0] != ']' || s.Length < 2) continue;
            if (s[1] == 'g') { galaga = true; continue; }
            if (s[1] != 'L') continue;

            var e = ParseLevelLine(s, section);
            e.GalagaMode = galaga;
            galaga = false;
            if (e.LvlFileNum > 0)
                levels.Add(e);
        }
        return levels;
    }

    /// <summary>Read one ']L' declaration (tyrian2.c:4370-4386). Fixed field offsets.</summary>
    public static LevelEntry ParseLevelLine(string s, int section) => new()
    {
        SectionIndex = section,
        NextLevel = AtoiAt(s, 9),
        Name = SubFixed(s, 13, 9),
        Song = AtoiAt(s, 22),
        LvlFileNum = AtoiAt(s, 25),
        NormalBonus = s.Length > 27 && s[27] == '$',
        BonusLevel = s.Length > 28 && s[28] == '$',
    };

    // C atoi(s + off): skip leading spaces, read optional sign + digits, stop at non-digit.
    public static int AtoiAt(string s, int off)
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
