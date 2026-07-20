using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;

namespace T2LV;

/// <summary>
/// The datacube reader: the readings the outposts hand out between levels, with the
/// portrait the game shows beside them. Which cubes an outpost stocks comes from the
/// script's ']?' list; how many of its four slots are open comes from cubeMax, which the
/// engine zeroes at every level start and the datacubes you pick up during a level raise —
/// so ']!'/']+' are what an outpost guarantees on their own. See <see cref="EpisodeGraph"/>.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showCubes;
    private int _cubeEpisodeIdx = -1;     // which episode the selected cube belongs to
    private int _cubeSelected = -1;       // 1-based cube index within that episode
    private bool _cubeScrollToSelection;
    private bool _cubeByLevel;            // list levels and what their outpost stocks
    private float _cubeListW = 330f;      // drag the splitter to widen for long titles
    private readonly SpriteImage _cubeFace = new();
    private (int Episode, int Face, int Palette) _cubeFaceKey = (-1, -1, -1);

    private static readonly uint CubeTextCol = Gfx.Rgba(206, 212, 226);
    private static readonly uint CubeMarkCol = Gfx.Rgba(255, 208, 120);   // the '~' emphasis
    private static readonly uint CubeFreeCol = Gfx.Rgba(150, 190, 245);
    private static readonly uint CubeLockCol = Gfx.Rgba(210, 170, 255);
    private static readonly uint CubeDropCol = Gfx.Rgba(255, 165, 100);   // named but unreachable
    private static readonly uint CubeNoneCol = Gfx.Rgba(130, 134, 146);
    private static readonly uint CubeHeadCol = Gfx.Rgba(240, 242, 248);   // the level headings

    private const float CubeHeadMinW = 220f;   // narrower than this, the heading moves below the portrait

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

    // =====================================================================

    private void DrawCubeWindow()
    {
        if (!_showCubes || _gd == null || CurEpisode == null) return;

        ImGui.SetNextWindowSize(new Vector2(940, 640), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(520, 320), new Vector2(float.MaxValue, float.MaxValue));
        bool open = _showCubes;
        if (!ImGui.Begin("Datacubes###datacubes", ref open))
        {
            ImGui.End();
            _showCubes = open;
            return;
        }
        _showCubes = open;

        ImGui.SetNextItemWidth(140);
        EpisodeCombo("##cubeepisode");
        ImGui.SameLine();
        bool byCube = !_cubeByLevel;
        if (ImGui.RadioButton("By cube", byCube)) _cubeByLevel = false;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Every reading in the episode, in file order.");
        ImGui.SameLine();
        if (ImGui.RadioButton("By level", _cubeByLevel)) _cubeByLevel = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Group them under the level whose outpost hands them out,\nin the order the campaign reaches those levels.");
        ImGui.SameLine();
        ImGui.TextDisabled("· drag the divider to widen the list");
        ImGui.Separator();

        // Open on something rather than an empty reader, and follow the episode picker
        // when the cube on show is no longer among the ones listed.
        bool inView = _cubeEpisodeIdx >= 0 && (_allEpisodes || _cubeEpisodeIdx == _episodeIdx);
        if (!inView || _cubeSelected < 0)
        {
            var first = _gd.GetCubes(_gd.Episodes[_episodeIdx]).FirstOrDefault(c => !c.IsEmpty);
            _cubeEpisodeIdx = _episodeIdx;
            _cubeSelected = first?.Index ?? -1;
        }

        // The reader needs room too, so the list can never take the whole window.
        float maxList = Math.Max(240f, ImGui.GetContentRegionAvail().X - 300f);
        _cubeListW = Math.Clamp(_cubeListW, 200f, maxList);

        ImGui.BeginChild("cubelist", new Vector2(_cubeListW, 0), ImGuiChildFlags.Borders);
        if (_cubeByLevel) DrawCubeListByLevel(); else DrawCubeListByCube();
        ImGui.EndChild();
        ImGui.SameLine(0, 2);
        VSplitter("##cubesplit", ref _cubeListW, 200f, maxList);
        ImGui.SameLine(0, 2);
        ImGui.BeginChild("cubereader", new Vector2(0, 0), ImGuiChildFlags.Borders);
        DrawCubeReader();
        ImGui.EndChild();

        ImGui.End();
    }

    /// <summary>Every cube in the shown episodes, in file order, tagged by how you come by it.</summary>
    private void DrawCubeListByCube()
    {
        foreach (int e in ShownEpisodes())
        {
            var ep = _gd!.Episodes[e];
            var cubes = _gd.GetCubes(ep);
            if (cubes.Count == 0) continue;
            if (_allEpisodes) ImGui.SeparatorText($"Episode {ep.Number}");

            foreach (var cube in cubes)
            {
                if (cube.IsEmpty) { ImGui.TextDisabled($"{cube.Index,3}  (empty slot)"); continue; }
                var sites = CubeSites(ep, cube.Index);
                CubeRow(e, cube, "c", SiteColor(sites), SiteSummary(sites));
            }
        }
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
            if (_allEpisodes) ImGui.SeparatorText($"Episode {ep.Number}");

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
                    string via = node.CubeStops.Count > 1 ? $"  (route {si + 1})" : "";
                    ImGui.PushStyleColor(ImGuiCol.Text, ColorOf(CubeHeadCol));
                    bool open = ImGui.Selectable($"before {node.Title}  #{node.LvlFileNum}{via}##lv{e}_{node.Id}_{si}");
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("open this level in the viewer");
                    if (open) SelectLevelFile(e, node.LvlFileNum);

                    ImGui.Indent(10f);
                    // Keyed by slot, not by cube: Episode 3's New Deli outpost really does
                    // name cube 20 in three of its four slots, so the same cube can repeat.
                    var shelf = stop.Cubes.Concat(stop.Dropped).ToList();
                    for (int k = 0; k < shelf.Count; k++)
                    {
                        int idx = shelf[k];
                        seen.Add(idx);
                        var cube = cubes.FirstOrDefault(c => c.Index == idx);
                        if (cube == null || cube.IsEmpty) { ImGui.TextDisabled($"{idx,3}  (empty slot)"); continue; }

                        var gate = k >= stop.Cubes.Count ? CubeGate.Dropped
                            : stop.IsFree(idx) ? CubeGate.Stocked : CubeGate.NeedsPickup;
                        int dup = shelf.Count(c => c == idx);
                        string slots = dup > 1 ? $", in {dup} of the outpost's slots" : "";
                        CubeRow(e, cube, $"n{node.Id}_{si}_{k}", GateColor(gate), gate switch
                        {
                            CubeGate.Stocked => $"always stocked here{slots}",
                            CubeGate.NeedsPickup => $"needs a datacube found in the level before{slots}",
                            _ => DroppedWhy(stop),
                        });
                    }
                    ImGui.Unindent(10f);
                }
            }

            var loose = cubes.Where(c => !c.IsEmpty && !seen.Contains(c.Index)).ToList();
            if (loose.Count == 0) continue;
            ImGui.PushStyleColor(ImGuiCol.Text, ColorOf(CubeHeadCol));
            ImGui.Selectable($"no ']?' list names these##loose{e}", false, ImGuiSelectableFlags.Disabled);
            ImGui.PopStyleColor();
            ImGui.Indent(10f);
            foreach (var cube in loose)
                CubeRow(e, cube, "loose", CubeNoneCol,
                    "written for this episode, but no outpost's ']?' list names it");
            ImGui.Unindent(10f);
        }
    }

    private IEnumerable<int> ShownEpisodes()
    {
        for (int e = 0; e < _gd!.Episodes.Count; e++)
            if (_allEpisodes || e == _episodeIdx) yield return e;
    }

    /// <summary>One list entry: just the title. How you come by the cube is carried by the
    /// row's colour and spelled out in the reader, rather than doubling every row.
    /// <paramref name="rowId"/> must be unique across the whole list — the same cube can
    /// legitimately appear more than once, both across outposts and within one shelf.</summary>
    private void CubeRow(int episodeIdx, DataCube cube, string rowId, uint col, string tip)
    {
        bool selected = episodeIdx == _cubeEpisodeIdx && cube.Index == _cubeSelected;
        ImGui.PushStyleColor(ImGuiCol.Text, ColorOf(col));
        if (ImGui.Selectable($"{cube.Index,3}  {StripMarks(cube.Title)}##{rowId}|{episodeIdx}|{cube.Index}", selected))
        {
            _cubeEpisodeIdx = episodeIdx;
            _cubeSelected = cube.Index;
        }
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{cube.Header}  ·  {tip}");
        if (selected && _cubeScrollToSelection) { ImGui.SetScrollHereY(0.4f); _cubeScrollToSelection = false; }
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
        return n > 1 ? $"{how} ×{n}" : how;
    }

    private void DrawCubeReader()
    {
        if (_cubeEpisodeIdx < 0 || _cubeEpisodeIdx >= _gd!.Episodes.Count)
        {
            ImGui.TextDisabled("Pick a datacube to read it.");
            return;
        }
        var ep = _gd.Episodes[_cubeEpisodeIdx];
        var cubes = _gd.GetCubes(ep);
        var cube = cubes.FirstOrDefault(c => c.Index == _cubeSelected);
        if (cube == null) { ImGui.TextDisabled("Pick a datacube to read it."); return; }

        // --- Portrait, in the palette the outpost swaps in behind it. ---
        float avail = ImGui.GetContentRegionAvail().X;
        var face = _gd.Main.Faces?.Get(cube.FaceSprite);
        if (face != null && face.W > 0)
        {
            int pal = DataCubes.PaletteFor(cube.FaceSprite);
            var key = (_cubeEpisodeIdx, cube.FaceSprite, pal);
            if (key != _cubeFaceKey)
            {
                _cubeFace.Update(_renderer, face, _gd.Palettes.Get(pal));
                _cubeFaceKey = key;
            }
            const float scale = 2f;
            var at = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(at, at + new Vector2(face.W * scale, face.H * scale), Gfx.Rgba(10, 11, 15), 3f);
            _cubeFace.Draw(dl, at, scale);
            dl.AddRect(at, at + new Vector2(face.W * scale, face.H * scale), Gfx.Rgba(90, 100, 130, 190), 3f);
            ImGui.Dummy(new Vector2(face.W * scale, face.H * scale));
            // Beside the portrait while a readable column is left over; below it once the
            // window is narrow enough that the heading would wrap into a sliver instead.
            if (avail - face.W * scale - 14f >= CubeHeadMinW) ImGui.SameLine(0, 14f);
        }

        ImGui.BeginGroup();
        DrawCubeSpans(SpansOf(cube.Title));
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled($"{cube.Header}    ·    episode {ep.Number}, cube {cube.Index}");

        var sites = CubeSites(ep, cube.Index);
        foreach (var site in sites)
        {
            ImGui.TextColored(ColorOf(GateColor(site.Gate)), site.Gate switch
            {
                CubeGate.Stocked => $"always stocked at the outpost before {site.Level}",
                CubeGate.NeedsPickup => $"before {site.Level}, but only if you found datacubes in the level before it",
                _ => $"named by the outpost before {site.Level}, but unreachable",
            });
        }
        if (sites.Count == 0)
            ImGui.TextDisabled("Written for this episode, but no outpost's ']?' list names it.");
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.Separator();
        ImGui.BeginChild("cubetext", new Vector2(0, 0));
        foreach (var line in cube.Lines)
        {
            if (line.Count == 0) { ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight() * 0.6f)); continue; }
            DrawCubeSpans(line);
        }
        ImGui.EndChild();
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
