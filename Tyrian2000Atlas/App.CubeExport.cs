using System.Text;
using T2LV.Tyrian;

namespace T2LV;

/// <summary>
/// Saving datacube readings out of the reader as Markdown: the one on show, or every reading
/// the list is holding.
///
/// Same shape as <see cref="StartSpritePngExport"/> -- the Save-As box pumps its own message
/// loop and has to run on an STA thread, so the picker and the file write share one background
/// thread and the frame keeps running while the box is open. The document itself is built
/// HERE, on the UI thread, before that thread starts: laying a cube out reads
/// <see cref="GameData"/>'s cube and graph caches, and filling those from a second thread
/// while the frame is drawing out of them is the kind of race that shows up once a month and
/// never in a repro.
///
/// What the game's own formatting becomes:
///   ~x~ highlight    ->  **x**, which is what that font toggle is for
///   one source line  ->  one line, held there by Markdown's two-space hard break
///
/// The readings keep the lines they were written on rather than being reflowed into prose,
/// because a good few of them are laid out on purpose -- the jukebox list, the letter home
/// with its indent and its signature, a transmission jammed down to dots and dashes -- and a
/// paragraph made out of one of those reads as nonsense.
/// </summary>
public sealed unsafe partial class App
{
    /// <summary>What one leading space is written as; see <see cref="MdBlockSafe"/>.</summary>
    private const string MdIndent = "&nbsp;";

    private volatile bool _cubeExpActive;

    /// <summary>True while a datacube Save-As box is open or its file is being written.
    /// Shares <see cref="_exportActive"/> with the other saves on purpose: one write at a
    /// time, and one status line to report it.</summary>
    private bool CubeExportBusy => _cubeExpActive || _exportActive || _pickActive;

    /// <summary>Ask where to put a finished document and write it there.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void StartCubeMdExport(string suggested, string text, string what)
    {
        if (CubeExportBusy || text.Length == 0) return;

        _cubeExpActive = true;
        _status = "Choose where to save " + suggested + " ...";
        IntPtr owner = NativeFileDialog.ForegroundWindow();
        string startDir = DefaultExportDir();

        var t = new Thread(() =>
        {
            try
            {
                string? path = NativeFileDialog.SaveFileBlocking(startDir, suggested, owner, "Export Markdown");
                if (path == null) { _exportDone = "Export cancelled."; return; }
                if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) path += ".md";
                _exportDir = Path.GetDirectoryName(path) ?? _exportDir;
                // No BOM: these are meant to be read by anything, including tools that take a
                // leading BOM for the first character of the first heading.
                File.WriteAllText(path, text, new UTF8Encoding(false));
                _exportDone = $"Saved {path} ({what}).";
            }
            catch (Exception e) { _exportDone = "Markdown save failed: " + e.Message; }
            finally { _cubeExpActive = false; }
        })
        { IsBackground = true, Name = "CubeMdExport" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>The reading on the reader, on its own.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ExportOneCube(EpisodeInfo ep, DataCube cube)
    {
        if (_gd == null || CubeExportBusy) return;

        var sb = new StringBuilder();
        WriteCubeMd(sb, ep, cube, 1, numbered: false, withEpisode: true);
        StartCubeMdExport($"ep{ep.Number} cube{cube.Index:00} {Safe(StripMarks(cube.Title))}.md",
            sb.ToString(), "1 reading");
    }

    /// <summary>
    /// Every reading the list is showing, in file order. What that is follows the window: the
    /// episode picker decides which files are in it, the filter box decides which readings are.
    /// The by-level grouping is deliberately not followed -- it shows a cube once per outpost
    /// that stocks it, which is right for a shelf and wrong for a document; which outposts
    /// those are is written under each reading instead.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ExportListedCubes()
    {
        var gd = _gd;
        if (gd == null || CubeExportBusy) return;

        var shown = ShownEpisodes()
            .Select(e => gd.Episodes[e])
            .Select(ep => (Ep: ep, Cubes: gd.GetCubes(ep).Where(c => !c.IsEmpty && CubeMatches(c)).ToList()))
            .Where(x => x.Cubes.Count > 0)
            .ToList();
        int total = shown.Sum(x => x.Cubes.Count);
        if (total == 0) { _status = "Nothing matches that filter -- nothing to write."; return; }

        string filter = BufText(_cubeFilter).Trim();
        string what = total == 1 ? "1 reading" : $"{total} readings";
        bool one = shown.Count == 1;

        var sb = new StringBuilder();
        sb.Append(one ? $"# Tyrian 2000 — episode {shown[0].Ep.Number} datacubes\n"
                      : "# Tyrian 2000 — datacubes\n");
        sb.Append('\n').Append(what)
          .Append(filter.Length > 0 ? $" matching \"{MdInline(filter)}\"" : "")
          .Append(", in the order they sit in the episode's cubetxt file.\n");

        foreach (var (ep, cubes) in shown)
        {
            if (!one) sb.Append($"\n## Episode {ep.Number}\n");
            foreach (var cube in cubes)
                WriteCubeMd(sb, ep, cube, one ? 2 : 3, numbered: true, withEpisode: false);
        }

        StartCubeMdExport(one ? $"tyrian2000 episode {shown[0].Ep.Number} datacubes.md"
                              : "tyrian2000 datacubes.md",
            sb.ToString(), what);
    }

    /// <summary>
    /// One reading, laid out the way the reader shows it: the title, the line of small print
    /// the badges stand for, where the cube turns up, then the reading itself.
    /// </summary>
    private void WriteCubeMd(StringBuilder sb, EpisodeInfo ep, DataCube cube, int level,
        bool numbered, bool withEpisode)
    {
        sb.Append('\n').Append('#', level).Append(' ');
        if (numbered) sb.Append(cube.Index).Append(". ");
        sb.Append(MdSpans(SpansOf(cube.Title))).Append('\n');

        var sites = CubeSites(ep, cube.Index);
        var bits = new List<string>();
        if (cube.Header.Length > 0) bits.Add(MdInline(cube.Header));
        if (withEpisode) bits.Add($"episode {ep.Number}");
        bits.Add($"cube {cube.Index}");
        foreach (var g in sites.Select(s => s.Gate).Distinct()) bits.Add(GateWord(g));
        if (sites.Count == 0) bits.Add("never offered");
        sb.Append('\n').Append(string.Join("  ·  ", bits)).Append('\n');

        sb.Append('\n');
        foreach (var site in sites)
            sb.Append("- ").Append(site.Gate switch
            {
                CubeGate.Stocked => $"always stocked at the outpost before {MdInline(site.Level)}",
                CubeGate.NeedsPickup => $"before {MdInline(site.Level)}, but only if you found " +
                                        "datacubes in the level before it",
                _ => $"named by the outpost before {MdInline(site.Level)}, but unreachable",
            }).Append('\n');
        if (sites.Count == 0)
            sb.Append("- written for this episode, but no outpost's ']?' list names it\n");

        sb.Append("\n---\n\n");
        for (int i = 0; i < cube.Lines.Count; i++)
        {
            string line = MdSpans(cube.Lines[i]).TrimEnd();
            if (line.Length == 0) { sb.Append('\n'); continue; }
            sb.Append(MdBlockSafe(line));
            // Markdown's own hard break, so the reading keeps the line it was written on. Not
            // spent on the last line of a paragraph, where the blank line below ends it anyway.
            if (i + 1 < cube.Lines.Count && cube.Lines[i + 1].Count > 0) sb.Append("  ");
            sb.Append('\n');
        }
    }

    // =====================================================================
    // The game's text as Markdown
    // =====================================================================

    /// <summary>
    /// One source line, span by span. A '~' run becomes bold, with any space it happened to
    /// enclose moved outside the markers -- "** x **" is not emphasis at all, it is four
    /// asterisks and a space.
    /// </summary>
    private static string MdSpans(List<CubeSpan> spans)
    {
        var sb = new StringBuilder();
        foreach (var span in spans)
        {
            string t = span.Text;
            if (!span.Highlight) { sb.Append(MdInline(t)); continue; }
            int a = 0, b = t.Length;
            while (a < b && t[a] == ' ') a++;
            while (b > a && t[b - 1] == ' ') b--;
            sb.Append(t, 0, a);
            if (b > a) sb.Append("**").Append(MdInline(t[a..b])).Append("**");
            sb.Append(t, b, t.Length - b);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The characters a renderer would read as syntax rather than as themselves. '&lt;' is the
    /// one that really matters: several readings carry an aside like "&lt;cough&gt;", and left
    /// alone a renderer takes that for an HTML tag and swallows it whole.
    /// </summary>
    private static string MdInline(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c is '\\' or '`' or '*' or '_' or '<') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// What a whole line needs on top of that.
    ///
    /// Leading spaces are spelled out as entities, because four ordinary ones at the head of a
    /// line is an indented code block and the letter home in Episode 1 is indented like a
    /// letter. A literal no-break space keeps the code block away too, but every renderer then
    /// strips it off the front of the paragraph; the entity is the only form that reaches the
    /// page still indented.
    ///
    /// A line that would otherwise open a block of its own -- a bullet, a heading, a rule, a
    /// quote, a numbered item -- gets a backslash, so it stays the words it was written as.
    /// The jammed transmission in Episode 3 opens on a dash and means nothing by it.
    /// </summary>
    private static string MdBlockSafe(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        if (i >= line.Length) return line;

        int d = i;
        while (d < line.Length && char.IsAsciiDigit(line[d])) d++;
        bool block = line[i] is '-' or '+' or '#' or '>' or '=' or '|'
                     || (d > i && d < line.Length && line[d] is '.' or ')');

        return string.Concat(Enumerable.Repeat(MdIndent, i)) + (block ? "\\" : "") + line[i..];
    }
}
