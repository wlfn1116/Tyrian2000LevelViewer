using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The datacube reader: the readings the outposts hand out between levels, with the
/// portrait the game shows beside them. Which cubes an outpost stocks comes from the
/// script's ']?' list; how many of its four slots are open comes from cubeMax, which the
/// engine zeroes at every level start and the datacubes you pick up during a level raise —
/// so ']!'/']+' are what an outpost guarantees on their own. See <see cref="EpisodeGraph"/>.
///
/// The right-hand pane is built to be read rather than to be inspected: the reading is set in
/// a column of a fixed comfortable width instead of running the whole way across a wide
/// window, and the portrait sits in a bezel with the terms of the cube -- stocked, gated,
/// unreachable -- stated as badges rather than buried in a tooltip.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showCubes;
    private int _cubeEpisodeIdx = -1;     // which episode the selected cube belongs to
    private int _cubeSelected = -1;       // 1-based cube index within that episode
    private bool _cubeScrollToSelection;
    private bool _cubeByLevel;            // list levels and what their outpost stocks
    private float _cubeListW = 330f;      // drag the splitter to widen for long titles
    private readonly byte[] _cubeFilter = new byte[64];
    private readonly SpriteImage _cubeFace = new();
    private (int Episode, int Face, int Palette) _cubeFaceKey = (-1, -1, -1);

    /// <summary>The window's own colour, shared with its launcher chip. See AcAnalysis.</summary>
    private static uint AcCube => AcDisplay;
    private static readonly uint CubeTextCol = Gfx.Rgba(211, 217, 231);
    private static readonly uint CubeMarkCol = Gfx.Rgba(255, 208, 120);   // the '~' emphasis
    private static readonly uint CubeFreeCol = Gfx.Rgba(150, 190, 245);
    private static readonly uint CubeLockCol = Gfx.Rgba(210, 170, 255);
    private static readonly uint CubeDropCol = Gfx.Rgba(255, 165, 100);   // named but unreachable
    private static readonly uint CubeNoneCol = Gfx.Rgba(130, 134, 146);

    /// <summary>How wide a column of prose stays readable. Past this the eye loses the start
    /// of the next line, which is exactly what a maximised window used to do to a reading.</summary>
    private const float CubeReadW = 780f;

    /// <summary>Open the reader on a particular cube; also the "--showcubes N" entry point,
    /// which is how a specific outpost's shelf gets framed for a screenshot.</summary>
    public void ShowCube(int episodeIdx, int cubeIndex) => OpenCubes(episodeIdx, cubeIndex);

    /// <summary>Open the reader on a particular cube (the tree's right-click).</summary>
    private void OpenCubes(int episodeIdx, int cubeIndex)
    {
        _showCubes = true;
        _cubeEpisodeIdx = episodeIdx;
        _cubeSelected = cubeIndex;
        _cubeScrollToSelection = true;
    }

    /// <summary>How an outpost holds a cube.</summary>
    private enum CubeGate
    {
        Stocked,      // on the shelf as soon as you get there
        NeedsPickup,  // the slot only opens once you are carrying enough datacubes
        Dropped,      // its ']?' line names the cube, but the engine can never read it
    }

    /// <summary>Where a cube turns up, and on what terms.</summary>
    private readonly record struct CubeSite(string Level, CubeGate Gate);

    private List<CubeSite> CubeSites(EpisodeInfo ep, int cubeIndex)
    {
        var sites = new List<CubeSite>();
        var graph = _gd?.GetGraph(ep);
        if (graph == null) return sites;
        foreach (var node in graph.Nodes)
            foreach (var stop in node.CubeStops)
            {
                CubeGate gate;
                if (stop.Cubes.Contains(cubeIndex))
                    gate = stop.IsFree(cubeIndex) ? CubeGate.Stocked : CubeGate.NeedsPickup;
                else if (stop.Dropped.Contains(cubeIndex)) gate = CubeGate.Dropped;
                else continue;

                if (!sites.Any(s => s.Level == node.Title && s.Gate == gate))
                    sites.Add(new CubeSite(node.Title, gate));
            }
        return sites;
    }

    private static uint GateColor(CubeGate g) => g switch
    {
        CubeGate.Stocked => CubeFreeCol,
        CubeGate.NeedsPickup => CubeLockCol,
        _ => CubeDropCol,
    };

    private static string GateWord(CubeGate g) => g switch
    {
        CubeGate.Stocked => "stocked",
        CubeGate.NeedsPickup => "gated",
        _ => "unreachable",
    };

    // =====================================================================

    private void DrawCubeWindow()
    {
        if (!_showCubes || _gd == null || CurEpisode == null) return;

        if (!RefBegin("Datacubes", "datacubes", ref _showCubes, AcCube,
                new Vector2(1000, 680), new Vector2(560, 340))) return;

        // Open on something rather than an empty reader, and follow the episode picker
        // when the cube on show is no longer among the ones listed.
        bool inView = _cubeEpisodeIdx >= 0 && (_allEpisodes || _cubeEpisodeIdx == _episodeIdx);
        if (!inView || _cubeSelected < 0)
        {
            var first = _gd.GetCubes(_gd.Episodes[_episodeIdx]).FirstOrDefault(c => !c.IsEmpty);
            _cubeEpisodeIdx = _episodeIdx;
            _cubeSelected = first?.Index ?? -1;
        }

        DrawCubeBand();

        // The reader needs room too, so the list can never take the whole window.
        float maxList = Math.Max(240f, ImGui.GetContentRegionAvail().X - 320f);
        _cubeListW = Math.Clamp(_cubeListW, 210f, maxList);

        WellBegin("cubelist", new Vector2(_cubeListW, ImGui.GetContentRegionAvail().Y), AcCube);
        if (_cubeByLevel) DrawCubeListByLevel(); else DrawCubeListByCube();
        WellEnd();

        ImGui.SameLine(0, 3);
        VSplitter("##cubesplit", ref _cubeListW, 210f, maxList);
        ImGui.SameLine(0, 3);

        WellBegin("cubereader", ImGui.GetContentRegionAvail(), AcCube, 12f, 10f);
        DrawCubeReader();
        WellEnd();

        RefEnd(AcCube);
    }

    private void DrawCubeBand()
    {
        BandBegin("cubeband", AcCube);
        BandLabel("episode");
        ImGui.SetNextItemWidth(126);
        EpisodeCombo("##cubeepisode");

        BandDivider();
        int mode = _cubeByLevel ? 1 : 0;
        if (SegBar("##cubemode", ref mode, AcCube, 208f,
                ("By cube", "Every reading in the episode, in file order."),
                ("By level", "Group them under the level whose outpost hands them out,\n" +
                             "in the order the campaign reaches those levels.")))
            _cubeByLevel = mode == 1;

        BandDivider();
        UiFilter("##cubefilter", "filter readings", _cubeFilter, 200f, AcCube);

        BandDivider();
        ImGui.AlignTextToFramePadding();
        int n = ShownEpisodes().Sum(e => _gd!.GetCubes(_gd.Episodes[e]).Count(c => !c.IsEmpty));
        ImGui.TextColored(ColorOf(UiFaint), $"{n} readings");

        BandDivider();
        bool windows = OperatingSystem.IsWindows();
        if (UiButton("export all .md", AcCube,
                "Write every reading the list is showing to one Markdown file, in file\n" +
                "order -- the episode picker and the filter decide what goes in it. The\n" +
                "'~' emphasis becomes bold and the outposts that stock a cube are named\n" +
                "under it, so nothing the reader shows is lost on the way out.",
                0f, CubeExportBusy || n == 0 || !windows) && windows)
            ExportListedCubes();
        BandEnd();
    }

    private bool CubeMatches(DataCube cube) =>
        Matches(BufText(_cubeFilter).Trim(), StripMarks(cube.Title), cube.Header, cube.Index.ToString());

    /// <summary>Every cube in the shown episodes, in file order, tagged by how you come by it.</summary>
    private void DrawCubeListByCube()
    {
        bool any = false;
        foreach (int e in ShownEpisodes())
        {
            var ep = _gd!.Episodes[e];
            var cubes = _gd.GetCubes(ep).Where(c => !c.IsEmpty && CubeMatches(c)).ToList();
            if (cubes.Count == 0) continue;
            any = true;
            UiSection(_allEpisodes ? $"Episode {ep.Number}" : "readings", AcCube, cubes.Count.ToString());

            foreach (var cube in cubes)
            {
                var sites = CubeSites(ep, cube.Index);
                CubeRow(e, cube, "c", SiteColor(sites), SiteSummary(sites));
            }
        }
        if (!any) ImGui.TextDisabled("Nothing matches that filter.");
    }

    /// <summary>
    /// The same cubes hung off the level whose outpost hands them out, in the order the
    /// campaign reaches those levels — the tree's depth. A cube stocked at several outposts
    /// appears under each of them, and the ones no outpost carries get their own group so
    /// nothing drops out of the list.
    /// </summary>
    private void DrawCubeListByLevel()
    {
        foreach (int e in ShownEpisodes())
        {
            var ep = _gd!.Episodes[e];
            var cubes = _gd.GetCubes(ep);
            var graph = _gd.GetGraph(ep);
            if (cubes.Count == 0 || graph == null) continue;
            if (_allEpisodes) UiSection($"Episode {ep.Number}", AcCube);

            var seen = new HashSet<int>();
            foreach (var node in graph.Nodes
                         .Where(n => n.Kind == GraphNodeKind.Level && n.CubeStops.Count > 0)
                         .OrderBy(n => n.Depth).ThenBy(n => n.Title))
            {
                for (int si = 0; si < node.CubeStops.Count; si++)
                {
                    var stop = node.CubeStops[si];
                    // A level reachable two ways sits behind two outposts with different
                    // shelves, so each gets its own group rather than one merged list.
                    var shelf = stop.Cubes.Concat(stop.Dropped).ToList();
                    var rows = new List<(int Idx, CubeGate Gate, DataCube? Cube, int Dup)>();
                    for (int k = 0; k < shelf.Count; k++)
                    {
                        int idx = shelf[k];
                        seen.Add(idx);
                        var cube = cubes.FirstOrDefault(c => c.Index == idx);
                        var gate = k >= stop.Cubes.Count ? CubeGate.Dropped
                            : stop.IsFree(idx) ? CubeGate.Stocked : CubeGate.NeedsPickup;
                        rows.Add((idx, gate, cube, shelf.Count(c => c == idx)));
                    }
                    if (!rows.Any(r => r.Cube != null && !r.Cube.IsEmpty && CubeMatches(r.Cube))) continue;

                    string via = node.CubeStops.Count > 1 ? $"  (route {si + 1})" : "";
                    CubeLevelHeader($"before {node.Title}  #{node.LvlFileNum}{via}",
                        $"lv{e}_{node.Id}_{si}", rows.Count, () => SelectLevelFile(e, node.LvlFileNum));

                    for (int k = 0; k < rows.Count; k++)
                    {
                        var (idx, gate, cube, dup) = rows[k];
                        if (cube == null || cube.IsEmpty)
                        {
                            ImGui.Indent(12f);
                            ImGui.TextDisabled($"{idx,3}  (empty slot)");
                            ImGui.Unindent(12f);
                            continue;
                        }
                        if (!CubeMatches(cube)) continue;
                        string slots = dup > 1 ? $", in {dup} of the outpost's slots" : "";
                        CubeRow(e, cube, $"n{node.Id}_{si}_{k}", GateColor(gate), gate switch
                        {
                            CubeGate.Stocked => $"always stocked here{slots}",
                            CubeGate.NeedsPickup => $"needs a datacube found in the level before{slots}",
                            _ => DroppedWhy(stop),
                        }, 12f);
                    }
                }
            }

            var loose = cubes.Where(c => !c.IsEmpty && !seen.Contains(c.Index) && CubeMatches(c)).ToList();
            if (loose.Count == 0) continue;
            CubeLevelHeader("no ']?' list names these", $"loose{e}", loose.Count, null);
            foreach (var cube in loose)
                CubeRow(e, cube, "loose", CubeNoneCol,
                    "written for this episode, but no outpost's ']?' list names it", 12f);
        }
    }

    /// <summary>A level heading in the by-level list: the level, how many readings hang off
    /// it, and a click through to the level itself.</summary>
    private static void CubeLevelHeader(string text, string id, int count, Action? open)
    {
        ImGui.Dummy(new Vector2(0, 3f));
        var p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        float h = ImGui.GetTextLineHeight() + 6f;

        bool hit = ImGui.InvisibleButton($"##{id}", new Vector2(w, h));
        bool hot = ImGui.IsItemHovered() && open != null;
        var dl = ImGui.GetWindowDrawList();
        var q = new Vector2(p.X + w, p.Y + h);
        dl.AddRectFilled(p, q, hot ? Gfx.Rgba(44, 40, 62) : Gfx.Rgba(32, 30, 44), 5f);
        dl.AddRectFilled(p, new Vector2(p.X + 2.5f, q.Y), Shade(AcCube, 1f, 220), 2f);
        string n = count.ToString();
        var nsz = ImGui.CalcTextSize(n);
        ClipText(dl, new Vector2(p.X + 9f, p.Y + 3f), w - 26f - nsz.X,
            hot ? Gfx.Rgba(250, 250, 255) : Gfx.Rgba(232, 234, 244), text);
        dl.AddText(new Vector2(q.X - nsz.X - 8f, p.Y + 3f), UiFaint, n);
        if (hot) ImGui.SetTooltip("open this level in the atlas");
        if (hit) open?.Invoke();
    }

    private IEnumerable<int> ShownEpisodes()
    {
        for (int e = 0; e < _gd!.Episodes.Count; e++)
            if (_allEpisodes || e == _episodeIdx) yield return e;
    }

    /// <summary>One list entry: the title over how you come by it, with the gate carried by
    /// the row's accent as well as by the words. <paramref name="rowId"/> must be unique
    /// across the whole list — the same cube can legitimately appear more than once, both
    /// across outposts and within one shelf.</summary>
    private void CubeRow(int episodeIdx, DataCube cube, string rowId, uint col, string note, float indent = 0f)
    {
        bool selected = episodeIdx == _cubeEpisodeIdx && cube.Index == _cubeSelected;
        var box = UiRow($"##{rowId}|{episodeIdx}|{cube.Index}", selected, col, 32f, indent);
        if (box.Clicked)
        {
            _cubeEpisodeIdx = episodeIdx;
            _cubeSelected = cube.Index;
        }
        if (box.Hovered) ImGui.SetTooltip($"{cube.Header}\n{note}");
        if (selected && _cubeScrollToSelection) { ImGui.SetScrollHereY(0.4f); _cubeScrollToSelection = false; }

        var dl = ImGui.GetWindowDrawList();
        float lh = ImGui.GetTextLineHeight();
        float top = box.Min.Y + (box.Max.Y - box.Min.Y - lh * 2f - 1f) * 0.5f;
        // The index sits in its own dim column so titles all start at the same x.
        dl.AddText(new Vector2(box.Min.X + 9f, top), UiFaint, $"{cube.Index,3}");
        float x = box.Min.X + 9f + ImGui.CalcTextSize("000").X + 7f;
        float room = box.Max.X - x - 10f;
        ClipText(dl, new Vector2(x, top), room,
            selected ? Gfx.Rgba(250, 252, 255) : UiText, StripMarks(cube.Title));
        ClipText(dl, new Vector2(x, top + lh + 1f), room, Shade(col, 1f, 190), note);
    }

    /// <summary>Why an outpost names a cube it can't actually hand over. Both cases are
    /// quirks of the original data, not of the walk — see the ']?' handler in EpisodeGraph.</summary>
    private static string DroppedWhy(CubeStop stop) =>
        stop.Cubes.Count >= 4
            ? "named 5th on the list, past the engine's four cube slots"
            : "named on the list, but past its own entry count";

    /// <summary>The best terms any outpost offers a cube on, which is what the row colours.</summary>
    private static CubeGate BestGate(List<CubeSite> sites) =>
        sites.Any(s => s.Gate == CubeGate.Stocked) ? CubeGate.Stocked
        : sites.Any(s => s.Gate == CubeGate.NeedsPickup) ? CubeGate.NeedsPickup
        : CubeGate.Dropped;

    private static uint SiteColor(List<CubeSite> sites) =>
        sites.Count == 0 ? CubeNoneCol : GateColor(BestGate(sites));

    private static string SiteSummary(List<CubeSite> sites)
    {
        if (sites.Count == 0) return "no outpost's list names it";
        int n = sites.Count;
        string how = BestGate(sites) switch
        {
            CubeGate.Stocked => sites.Any(s => s.Gate != CubeGate.Stocked) ? "stocked or earned" : "always stocked",
            CubeGate.NeedsPickup => "needs a datacube first",
            _ => "named, but unreachable",
        };
        return n > 1 ? $"{how}  ·  at {n} outposts" : how;
    }

    // =====================================================================
    // The reader
    // =====================================================================

    /// <summary>The non-empty cubes of the episode on show, which is what prev/next walk.</summary>
    private List<DataCube> ReaderCubes() =>
        _cubeEpisodeIdx < 0 || _gd == null || _cubeEpisodeIdx >= _gd.Episodes.Count
            ? new List<DataCube>()
            : _gd.GetCubes(_gd.Episodes[_cubeEpisodeIdx]).Where(c => !c.IsEmpty).ToList();

    private void DrawCubeReader()
    {
        if (_cubeEpisodeIdx < 0 || _cubeEpisodeIdx >= _gd!.Episodes.Count)
        {
            UiEmpty("Pick a datacube to read it", "The outposts hand these out between levels.", AcCube);
            return;
        }
        var ep = _gd.Episodes[_cubeEpisodeIdx];
        var all = ReaderCubes();
        var cube = all.FirstOrDefault(c => c.Index == _cubeSelected);
        if (cube == null)
        {
            UiEmpty("Pick a datacube to read it", "The outposts hand these out between levels.", AcCube);
            return;
        }
        int at = all.IndexOf(cube);

        DrawCubeMasthead(ep, cube, all, at);

        // --- The reading itself, set in a column narrow enough to follow. ---
        ImGui.Dummy(new Vector2(0, 6f));
        var avail = ImGui.GetContentRegionAvail();
        avail.Y = Math.Max(60f, avail.Y);   // a tall masthead must not leave a negative column
        // Grows a little with the pane, but never past the point where the eye loses the start
        // of the next line -- on a maximised 2560px window the reading is a column on a desk,
        // not a paragraph two thousand pixels wide.
        float colW = Math.Min(Math.Clamp(avail.X * 0.62f, 470f, CubeReadW), avail.X);
        float pad = MathF.Round((avail.X - colW) * 0.5f);
        var bp = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var colMin = new Vector2(bp.X + pad, bp.Y);
        var colMax = new Vector2(bp.X + pad + colW, bp.Y + avail.Y);
        dl.AddRectFilled(colMin, colMax, Gfx.Rgba(15, 14, 22), 6f);
        dl.AddRect(colMin, colMax, Shade(AcCube, 0.35f, 90), 6f);

        // Inset, so a reading scrolling past the top edge is cut inside the frame rather than
        // over it.
        var (ip, isz) = WellInner(colMin, new Vector2(colW, avail.Y));
        ImGui.SetCursorScreenPos(ip);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18f - WellInset, 14f - WellInset));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 3f));
        ImGui.BeginChild("cubetext", isz, ImGuiChildFlags.AlwaysUseWindowPadding);
        foreach (var line in cube.Lines)
        {
            if (line.Count == 0) { ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight() * 0.55f)); continue; }
            DrawCubeSpans(line);
        }
        ImGui.Dummy(new Vector2(0, 10f));
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
    }

    /// <summary>Portrait, title, terms and the walk buttons: everything about the cube that
    /// is not the reading.</summary>
    private void DrawCubeMasthead(EpisodeInfo ep, DataCube cube, List<DataCube> all, int at)
    {
        var face = _gd!.Main.Faces?.Get(cube.FaceSprite);
        const float scale = 2f;
        float faceW = face != null && face.W > 0 ? face.W * scale : 0f;
        float faceH = face != null && face.H > 0 ? face.H * scale : 0f;

        var start = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        if (faceW > 0f)
        {
            int pal = DataCubes.PaletteFor(cube.FaceSprite);
            var key = (_cubeEpisodeIdx, cube.FaceSprite, pal);
            if (key != _cubeFaceKey)
            {
                _cubeFace.Update(_renderer, face!, _gd.Palettes.Get(pal));
                _cubeFaceKey = key;
            }
            // A bezel rather than a hairline box: the portrait is the one piece of art in the
            // window and it should look mounted, not cropped.
            var bez = start;
            var bezMax = start + new Vector2(faceW + 10f, faceH + 10f);
            FlatRect(dl, bez, bezMax, Mix(UiPanel, AcCube, 0.14f), Mix(UiPanelHi, AcCube, 0.32f), 6f);
            dl.AddRect(bez, bezMax, Shade(AcCube, 0.65f, 170), 6f);
            var inner = bez + new Vector2(5f, 5f);
            dl.AddRectFilled(inner, inner + new Vector2(faceW, faceH), Gfx.Rgba(8, 8, 13), 2f);
            _cubeFace.Draw(dl, inner, scale);
            // Corner brackets, in the reader's violet.
            uint bc = Shade(AcCube, 1.1f, 220);
            const float t = 7f;
            dl.AddRectFilled(bez + new Vector2(1f, 1f), bez + new Vector2(t, 2.5f), bc);
            dl.AddRectFilled(bez + new Vector2(1f, 1f), bez + new Vector2(2.5f, t), bc);
            dl.AddRectFilled(new Vector2(bezMax.X - t, bezMax.Y - 2.5f), bezMax - new Vector2(1f, 1f), bc);
            dl.AddRectFilled(new Vector2(bezMax.X - 2.5f, bezMax.Y - t), bezMax - new Vector2(1f, 1f), bc);

            ImGui.Dummy(new Vector2(faceW + 10f, faceH + 10f));
            ImGui.SameLine(0, 14f);
        }

        ImGui.BeginGroup();
        // The title keeps its '~' emphasis, which is how the game itself sets these headings.
        DrawCubeSpans(SpansOf(cube.Title));
        // The rule goes under the whole title, so it is placed from the cursor once the title
        // has been laid out rather than from the last fragment's own item rect.
        var rule = ImGui.GetCursorScreenPos();
        dl.AddRectFilled(rule, new Vector2(rule.X + 96f, rule.Y + 1.5f), Shade(AcCube, 1f, 150));
        ImGui.Dummy(new Vector2(96f, 5f));

        ImGui.TextColored(ColorOf(UiDim), cube.Header);
        ImGui.Dummy(new Vector2(0, 2f));

        Badge($"episode {ep.Number}", Gfx.Rgba(150, 162, 185));
        ImGui.SameLine(0, 5f);
        Badge($"cube {cube.Index}", Gfx.Rgba(150, 162, 185));
        var sites = CubeSites(ep, cube.Index);
        foreach (var g in sites.Select(s => s.Gate).Distinct())
        {
            ImGui.SameLine(0, 5f);
            Badge(GateWord(g), GateColor(g));
        }
        if (sites.Count == 0)
        {
            ImGui.SameLine(0, 5f);
            Badge("never offered", CubeNoneCol);
        }

        // Prev / next walk the episode's readings, so the reader can be read straight through.
        ImGui.Dummy(new Vector2(0, 4f));
        if (UiButton("< previous", AcCube, "the reading before this one, in file order", 0f, at <= 0))
            { _cubeSelected = all[at - 1].Index; _cubeScrollToSelection = true; }
        ImGui.SameLine(0, 5f);
        if (UiButton("next >", AcCube, "the reading after this one, in file order", 0f,
                at < 0 || at >= all.Count - 1))
            { _cubeSelected = all[at + 1].Index; _cubeScrollToSelection = true; }
        ImGui.SameLine(0, 8f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ColorOf(UiFaint), $"{at + 1} of {all.Count}");

        ImGui.SameLine(0, 10f);
        bool windows = OperatingSystem.IsWindows();
        if (UiButton("export .md", AcCube,
                "Save this reading on its own as a Markdown file: the title, where the\n" +
                "cube turns up, and the text on the lines it was written on, with the\n" +
                "'~' emphasis as bold.", 0f, CubeExportBusy || !windows) && windows)
            ExportOneCube(ep, cube);
        ImGui.EndGroup();

        // --- Where it turns up, spelled out under the whole masthead. ---
        ImGui.Dummy(new Vector2(0, 4f));
        ImGui.PushTextWrapPos(0f);
        foreach (var site in sites)
            ImGui.TextColored(ColorOf(GateColor(site.Gate)), site.Gate switch
            {
                CubeGate.Stocked => $"·  always stocked at the outpost before {site.Level}",
                CubeGate.NeedsPickup => $"·  before {site.Level}, but only if you found datacubes in the level before it",
                _ => $"·  named by the outpost before {site.Level}, but unreachable",
            });
        if (sites.Count == 0)
            ImGui.TextColored(ColorOf(UiFaint),
                "Written for this episode, but no outpost's ']?' list names it.");
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// Lay one line out span by span so the '~' emphasis keeps its own colour, breaking
    /// between words once the reader is narrower than the line. ImGui's own wrapping is no
    /// use here: a line is several separate text items, and each would wrap on its own
    /// without knowing what already sits before it on the row.
    /// </summary>
    private static void DrawCubeSpans(List<CubeSpan> spans)
    {
        if (spans.Count == 0) { ImGui.NewLine(); return; }

        // Measured from where the line starts, which inside a group is the group's left
        // edge — so wrapped rows line up under the first one rather than under the window.
        float wrap = Math.Max(ImGui.GetContentRegionAvail().X, 1f);
        float x = 0f;            // width of what is already on this row
        bool join = false;       // the next item continues that row rather than starting one
        bool rowWord = false;    // the row holds a word, so breaking before the next one helps

        // A run is emitted as one text item, exactly as a whole span used to be: ImGui
        // rounds each item's width up to a pixel, so per-word items would drift right.
        void Flush(Vector4 col, string s)
        {
            if (s.Length == 0) return;
            float w = ImGui.CalcTextSize(s).X;
            if (join) ImGui.SameLine(0, 0);
            // A word wider than the whole row has nowhere to break: hand that one to
            // ImGui, which splits it mid-word rather than letting it run off the edge.
            bool hard = !join && w > wrap;
            if (hard) ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
            ImGui.TextColored(col, s);
            if (hard) ImGui.PopTextWrapPos();
            x = hard ? 0f : x + w;
            join = !hard;
        }

        foreach (var span in spans)
        {
            var col = ColorOf(span.Highlight ? CubeMarkCol : CubeTextCol);
            string t = span.Text;
            int run = 0;         // start of the part of this span not yet emitted
            int i = 0;
            while (i < t.Length)
            {
                int gap = i;
                while (gap < t.Length && t[gap] == ' ') gap++;
                int end = gap;
                while (end < t.Length && t[end] != ' ') end++;

                if (rowWord && x + ImGui.CalcTextSize(t[run..end]).X > wrap)
                {
                    Flush(col, t[run..i]);     // everything that still fitted
                    run = gap;                 // the break is spent on the leading spaces
                    x = 0f;
                    join = false;
                    rowWord = false;
                }
                if (end > gap) rowWord = true;
                i = end;
            }
            Flush(col, t[run..]);
        }
    }

    private static List<CubeSpan> SpansOf(string s)
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
}
