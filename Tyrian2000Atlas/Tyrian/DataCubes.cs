namespace T2A.Tyrian;

/// <summary>A run of cube text. The game's fonts treat '~' as a highlight toggle
/// (font.c:145), so the text arrives already split into plain and emphasised runs.</summary>
public readonly record struct CubeSpan(string Text, bool Highlight);

/// <summary>
/// One datacube out of cubetxt%d.dat: the reading the outpost's DATA CUBES menu shows.
/// </summary>
public sealed class DataCube
{
    public int Index;              // 1-based, the number the script's ]? list refers to
    public string Marker = "";     // the raw '*NN FF (comment)' line
    public int FaceSprite = -1;    // 0-based into the FACE_SHAPES bank; -1 = no portrait
    public string Title = "";
    public string Header = "";     // the category shown under the portrait
    public readonly List<List<CubeSpan>> Lines = new();   // body, one entry per source line

    /// <summary>Body text with the highlight markers dropped, for search and tooltips.</summary>
    public string PlainText =>
        string.Join("\n", Lines.Select(l => string.Concat(l.Select(s => s.Text))));

    /// <summary>A reserved slot with nothing written in it — Episode 4's file opens with
    /// three, so its cube numbering lines up with the other episodes'.</summary>
    public bool IsEmpty => Title.Length == 0 && Lines.All(l => l.Count == 0);
}

/// <summary>
/// cubetxt%d.dat — the same XOR-encrypted Pascal strings as the episode script. A cube
/// starts at a '*' marker whose 4th character onwards is its face-sprite number, then
/// carries a title line, a header line, and body lines until the next marker.
/// See game_menu.c:load_cube (2899-2995).
/// </summary>
public static class DataCubes
{
    public static List<DataCube> Load(string path)
    {
        var cubes = new List<DataCube>();
        DataCube? cur = null;
        int field = 0;                       // 0 = title, 1 = header, 2+ = body

        foreach (var s in EpisodeScript.DecryptStrings(path))
        {
            if (s.Length > 0 && s[0] == '*')
            {
                // str_pop_int(&buf[4], &face_sprite) then --face_sprite: 1-based in the file.
                int face = EpisodeScript.AtoiAt(s, 4) - 1;
                cur = new DataCube { Index = cubes.Count + 1, Marker = s, FaceSprite = face };
                cubes.Add(cur);
                field = 0;
                continue;
            }
            if (cur == null) continue;

            if (field == 0) { cur.Title = s.Trim(); field++; }
            else if (field == 1) { cur.Header = s.Trim(); field++; }
            else cur.Lines.Add(Split(s));
        }
        return cubes;
    }

    /// <summary>Split a line on its '~' highlight toggles.</summary>
    private static List<CubeSpan> Split(string s)
    {
        var spans = new List<CubeSpan>();
        bool hi = false;
        int start = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i < s.Length && s[i] != '~') continue;
            if (i > start) spans.Add(new CubeSpan(s.Substring(start, i - start), hi));
            if (i < s.Length) hi = !hi;
            start = i + 1;
        }
        return spans;
    }

    // facepal[12] (pcxmast.c:26): the palette the outpost swaps in behind each portrait.
    private static readonly int[] FacePal = { 1, 2, 3, 4, 6, 9, 11, 12, 16, 13, 14, 15 };

    /// <summary>The palette a face is drawn in; 0 (the menu's own) for the later faces.</summary>
    public static int PaletteFor(int faceSprite) =>
        faceSprite >= 0 && faceSprite < FacePal.Length ? FacePal[faceSprite] : 0;
}
