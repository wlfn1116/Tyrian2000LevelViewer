using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;

namespace T2LV;

/// <summary>
/// One box over the whole data set: levels, enemies, shop items, datacubes and sprite banks.
/// Everything the other windows browse is reachable by typing part of its name, and every
/// result opens the window that owns it.
///
/// It is driven from the keyboard first -- type, arrow down, Enter -- because the point of a
/// search box is not having to reach for the mouse. The kind chips across the top double as
/// the result census and as the filter, so a query that matches four hundred sprites can be
/// narrowed to the one level it also matched without retyping anything.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showSearch;
    private bool _searchFocus;
    private readonly byte[] _searchBuf = new byte[96];
    private int _searchSel;              // row in the flattened result list
    private int _searchKinds = AllKindsMask;   // bitmask over HitKind; all on
    private bool _searchScroll;          // pull the selected row back into view
    /// <summary>Set while the palette owns the arrow keys, so the viewer's own level stepping
    /// stands down instead of scrolling the level list behind it.</summary>
    private bool _searchOwnsKeys;

    /// <summary>The window's own colour, shared with its launcher chip. See AcAnalysis.</summary>
    private static uint AcSearch => AcPlayer;

    /// <summary>What a hit points at, which decides its colour and where it opens.</summary>
    private enum HitKind { Level, Enemy, Assembly, Item, Cube, Bank, Song, Sound }

    private static readonly HitKind[] AllHitKinds =
        { HitKind.Level, HitKind.Enemy, HitKind.Assembly, HitKind.Item, HitKind.Cube, HitKind.Bank,
          HitKind.Song, HitKind.Sound };

    /// <summary>Every kind switched on -- one bit per <see cref="HitKind"/>.</summary>
    private const int AllKindsMask = (1 << 8) - 1;

    /// <summary><paramref name="Rank"/> is how well the query fit: 0 exact, 1 from the start,
    /// 2 at a word boundary, 3 buried in the middle. It orders a kind's own hits.</summary>
    private readonly record struct SearchHit(HitKind Kind, string Title, string Detail, int Rank, Action Open);

    private void OpenSearch()
    {
        _showSearch = true;
        _searchFocus = true;
    }

    /// <summary>The "--search &lt;text&gt;" entry point: open already showing a query's results.</summary>
    public void ShowSearch(string query)
    {
        _showSearch = true;
        SetBuf(_searchBuf, query);
    }

    private static void SetBuf(byte[] buf, string s)
    {
        int n = System.Text.Encoding.UTF8.GetBytes(s, 0, Math.Min(s.Length, buf.Length - 1), buf, 0);
        buf[n] = 0;
    }

    private static uint HitColor(HitKind k) => k switch
    {
        HitKind.Level => Gfx.Rgba(255, 190, 90),
        HitKind.Enemy => Gfx.Rgba(255, 120, 120),
        HitKind.Assembly => Gfx.Rgba(255, 150, 90),
        HitKind.Item => Gfx.Rgba(110, 225, 195),
        HitKind.Cube => Gfx.Rgba(185, 150, 255),
        HitKind.Song => AcMusic,
        HitKind.Sound => AcSound,
        _ => Gfx.Rgba(150, 200, 255),
    };

    /// <summary>The kind's name, singular. <see cref="HitPlural"/> has the other one --
    /// they used to share this, which is where the "spritess" heading came from.</summary>
    private static string HitLabel(HitKind k) => k switch
    {
        HitKind.Level => "level",
        HitKind.Enemy => "enemy",
        HitKind.Assembly => "group",
        HitKind.Item => "item",
        HitKind.Cube => "datacube",
        HitKind.Song => "song",
        HitKind.Sound => "sound",
        _ => "sprite bank",
    };

    private static string HitPlural(HitKind k) => k switch
    {
        HitKind.Enemy => "enemies",
        HitKind.Bank => "sprite banks",
        _ => HitLabel(k) + "s",
    };

    /// <summary>The two-letter tag on a result's colour chip -- what makes a mixed list
    /// scannable without reading a word of any row.</summary>
    private static string HitTag(HitKind k) => k switch
    {
        HitKind.Level => "LV",
        HitKind.Enemy => "EN",
        HitKind.Assembly => "GR",
        HitKind.Item => "IT",
        HitKind.Cube => "DC",
        HitKind.Song => "MU",
        HitKind.Sound => "SFX",
        _ => "SP",
    };

    private bool KindOn(HitKind k) => (_searchKinds & (1 << (int)k)) != 0;

    // =====================================================================

    private void DrawSearchWindow()
    {
        if (!_showSearch || _gd == null) return;

        if (!RefBegin("Search", "search", ref _showSearch, AcSearch,
                new Vector2(760, 560), new Vector2(460, 300)))
        {
            // Collapsed: hand the arrow keys back, or the level list stays frozen behind it.
            _searchOwnsKeys = false;
            return;
        }

        string q = BufText(_searchBuf).Trim();
        var hits = q.Length >= 2 ? Search(q) : new List<SearchHit>();

        var counts = new int[AllHitKinds.Length];
        foreach (var h in hits) counts[(int)h.Kind]++;
        var shown = hits.Where(h => KindOn(h.Kind)).ToList();

        DrawSearchBand(q, counts, shown.Count);

        if (q.Length < 2) DrawSearchHelp();
        else DrawSearchResults(shown, q);

        // The palette owns the arrows only while it is the focused window; the viewer's level
        // stepping reads this next frame and keeps out of the way.
        _searchOwnsKeys = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        RefEnd(AcSearch);
    }

    private void DrawSearchBand(string q, int[] counts, int shownCount)
    {
        // Pack the kind chips before the band opens: the band is a fixed-height child, so it
        // has to be told how many rows they will take.
        int total = counts.Sum();
        var kinds = AllHitKinds.Where(k => total == 0 || counts[(int)k] > 0).ToList();
        var labels = kinds.Select(k => total > 0 ? $"{HitPlural(k)}  {counts[(int)k]}" : HitPlural(k))
            .ToList();
        // Decided once, before the chips are packed: a kind chip below flips the mask in the
        // middle of the frame, and re-reading it afterwards indexes newRow past its end (the
        // same crash the level tree's legend had).
        bool allKindsChip = _searchKinds != AllKindsMask;
        if (allKindsChip) labels.Add("all kinds");
        var newRow = PackChips(labels, BandInnerWidth(), out int chipRows, 5f);

        BandBegin("searchband", AcSearch, 1 + Math.Max(1, chipRows));

        float avail = ImGui.GetContentRegionAvail().X;
        UiFilter("##searchbox", "levels, enemies, items, cubes, sprites, songs, sounds...",
            _searchBuf, Math.Max(200f, avail - 190f), AcSearch, _searchFocus);
        if (_searchFocus) _searchFocus = false;

        ImGui.SameLine(0, 12f);
        ImGui.AlignTextToFramePadding();
        if (q.Length < 2) ImGui.TextColored(ColorOf(UiFaint), "type two characters");
        else if (shownCount == 0) ImGui.TextColored(ColorOf(Gfx.Rgba(235, 150, 150)), "no matches");
        else ImGui.TextColored(ColorOf(Shade(AcSearch, 1.05f)),
            $"{shownCount} result{(shownCount == 1 ? "" : "s")}");

        // --- Kind chips: the census and the filter in one control. ---
        for (int i = 0; i < kinds.Count; i++)
        {
            if (i > 0 && !newRow[i]) ImGui.SameLine(0, 5f);
            bool on = KindOn(kinds[i]);
            if (UiToggle(labels[i], ref on, HitColor(kinds[i]),
                    on ? "Shown - click to leave these out" : "Hidden - click to bring them back"))
            {
                _searchKinds ^= 1 << (int)kinds[i];
                _searchSel = 0;
            }
        }
        if (kinds.Count == 0) ImGui.Dummy(new Vector2(0, ImGui.GetFrameHeight()));

        if (allKindsChip)
        {
            if (kinds.Count > 0 && !newRow[kinds.Count]) ImGui.SameLine(0, 5f);
            if (UiButton("all kinds", AcSearch, "Search everything again",
                    ImGui.CalcTextSize("all kinds").X + 30f))
            { _searchKinds = AllKindsMask; _searchSel = 0; }
        }
        BandEnd();
    }

    /// <summary>The empty state. Rather than a paragraph nobody reads, the examples are live:
    /// click one and it runs.</summary>
    private void DrawSearchHelp()
    {
        var avail = ImGui.GetContentRegionAvail();
        WellBegin("searchhelp", avail, AcSearch, 14f, 10f);
        ImGui.Dummy(new Vector2(0, 6f));
        UiSection("what it looks through", AcSearch);
        foreach (var k in AllHitKinds)
        {
            var mn = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(26f, ImGui.GetTextLineHeight()));
            BadgeAt(ImGui.GetWindowDrawList(), mn, HitTag(k), HitColor(k));
            ImGui.SameLine(0, 8f);
            ImGui.TextColored(ColorOf(UiDim), k switch
            {
                HitKind.Level => "level names and file numbers",
                HitKind.Enemy => "enemyDat entries by id, category or shape bank",
                HitKind.Assembly => "the formations and bosses, once the enemy browser has read them",
                HitKind.Item => "everything the shop sells",
                HitKind.Cube => "datacube titles, headers and the readings themselves",
                HitKind.Song => "the 41 songs, by title or number",
                HitKind.Sound => "the 40 effects and announcer lines, by name or what they say",
                _ => "sprite banks by name",
            });
        }

        ImGui.Dummy(new Vector2(0, 10f));
        UiSection("try", AcSearch);
        string[] examples = { "savara", "351", "pulse cannon", "microsol", "bank 12", "gravitron" };
        for (int i = 0; i < examples.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 6f);
            if (UiButton(examples[i], AcSearch, "run this search")) SetBuf(_searchBuf, examples[i]);
        }

        ImGui.Dummy(new Vector2(0, 10f));
        ImGui.TextColored(ColorOf(UiFaint),
            "Up / Down to walk the results, Enter to open one, Esc to close.\n" +
            "Results cover the episodes the viewer is currently browsing.");
        WellEnd();
    }

    private void DrawSearchResults(List<SearchHit> shown, string q)
    {
        var avail = ImGui.GetContentRegionAvail();

        // --- Keyboard, read before the rows so Enter opens what is on screen. ---
        bool focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (focused && shown.Count > 0)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true)) { _searchSel++; _searchScroll = true; }
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true)) { _searchSel--; _searchScroll = true; }
            if (ImGui.IsKeyPressed(ImGuiKey.PageDown, true)) { _searchSel += 8; _searchScroll = true; }
            if (ImGui.IsKeyPressed(ImGuiKey.PageUp, true)) { _searchSel -= 8; _searchScroll = true; }
            _searchSel = Math.Clamp(_searchSel, 0, shown.Count - 1);
            if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))
                shown[_searchSel].Open();
        }
        if (focused && ImGui.IsKeyPressed(ImGuiKey.Escape)) _showSearch = false;
        _searchSel = shown.Count == 0 ? 0 : Math.Clamp(_searchSel, 0, shown.Count - 1);

        WellBegin("searchresults", avail, AcSearch, 10f, 8f);
        if (shown.Count == 0)
        {
            UiEmpty("Nothing found", $"No {(_searchKinds == AllKindsMask ? "" : "shown ")}entry matches \"{q}\".", AcSearch);
            WellEnd();
            return;
        }

        const float rowH = 34f;
        HitKind? group = null;
        for (int i = 0; i < shown.Count; i++)
        {
            var hit = shown[i];
            if (group != hit.Kind)
            {
                group = hit.Kind;
                int n = shown.Count(h => h.Kind == hit.Kind);
                UiSection(HitPlural(hit.Kind), HitColor(hit.Kind), $"{n}");
            }

            uint col = HitColor(hit.Kind);
            var box = UiRow($"##hit{i}", i == _searchSel, col, rowH);
            if (box.Clicked) { _searchSel = i; hit.Open(); }
            // Hover follows the mouse, but only while the mouse is actually moving: a cursor
            // left resting over the list would otherwise drag the selection back on every
            // arrow-key press.
            if (box.Hovered && !box.Selected && ImGui.GetIO().MouseDelta != Vector2.Zero) _searchSel = i;
            if (i == _searchSel && _searchScroll) { ImGui.SetScrollHereY(0.5f); _searchScroll = false; }

            var dl2 = ImGui.GetWindowDrawList();
            BadgeAt(dl2, new Vector2(box.Min.X + 9f, box.Min.Y + (rowH - 3f - ImGui.GetTextLineHeight()) * 0.5f - 1.5f),
                HitTag(hit.Kind), col, box.Selected ? 1f : 0.85f);
            RowText(box, 42f, hit.Title, hit.Detail, col, box.Selected);
            // "»", not "→": the bundled font stops at Latin-1 and an arrow would draw as a box
            // (and mis-measure, so the right-alignment would be off too).
            if (i == _searchSel) RowTrail(box, "open »", Shade(col, 1.1f, 210));
        }

        // Enough slack under the last row that the selection can always scroll clear of the edge.
        ImGui.Dummy(new Vector2(0, 8f));
        WellEnd();
    }

    // =====================================================================

    /// <summary>Cap per kind, so one broad match cannot bury the others.</summary>
    private const int PerKindMax = 40;

    /// <summary>
    /// How well the query fits, over whichever of a row's fields it is allowed to match.
    /// -1 is no match at all; lower is better, and a hit that starts a name outranks one
    /// buried in the middle of it, so typing "sav" puts SAVARA above MISSION SAVARA RECON.
    /// </summary>
    private static int Rank(string q, params string?[] fields)
    {
        int best = int.MaxValue;
        foreach (var f in fields)
        {
            if (string.IsNullOrEmpty(f)) continue;
            if (f.Equals(q, StringComparison.OrdinalIgnoreCase)) return 0;
            int at = f.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            best = Math.Min(best, at == 0 ? 1 : !char.IsLetterOrDigit(f[at - 1]) ? 2 : 3);
        }
        return best == int.MaxValue ? -1 : best;
    }

    private List<SearchHit> Search(string q)
    {
        var hits = new List<SearchHit>();
        if (_gd == null) return hits;

        foreach (int e in ShownEpisodes())
        {
            var ep = _gd.Episodes[e];
            int epIdx = e;

            // --- Levels ---
            int n = 0;
            foreach (var item in ep.Levels)
            {
                if (n >= PerKindMax) break;
                int rank = Rank(q, item.Name, item.FileNum.ToString());
                if (rank < 0) continue;
                int file = item.FileNum;
                string name = string.IsNullOrWhiteSpace(item.Name) ? "(unnamed)" : item.Name.Trim();
                hits.Add(new SearchHit(HitKind.Level, name,
                    $"episode {ep.Number}  ·  level file {file:00}", rank,
                    () => SelectLevelFile(epIdx, file)));
                n++;
            }

            // --- Datacubes ---
            n = 0;
            foreach (var cube in _gd.GetCubes(ep))
            {
                if (n >= PerKindMax) break;
                if (cube.IsEmpty) continue;
                // Search the body too: the cubes are the closest thing the game has to lore.
                string body = string.Join(' ', cube.Lines.SelectMany(l => l.Select(s => s.Text)));
                int rank = Rank(q, cube.Title, cube.Header);
                // A body-only match is real but weak -- it should never outrank a title.
                if (rank < 0 && Rank(q, body) >= 0) rank = 4;
                if (rank < 0) continue;
                int idx = cube.Index;
                hits.Add(new SearchHit(HitKind.Cube, StripMarks(cube.Title),
                    $"episode {ep.Number}, cube {idx}  ·  {cube.Header}" +
                    (rank == 4 ? "  ·  matched in the reading" : ""), rank,
                    () => OpenCubes(epIdx, idx)));
                n++;
            }

            // --- Shop items ---
            // Whichever shop the browser is showing, so a search result and the row it opens
            // are always the same item.
            var items = _gd.GetItems(ep, _itemFork);
            if (items.Loaded)
            {
                n = 0;
                void ItemHits<T>(T[] table, int tab, string kind, Func<T, string> name, Func<T, int> cost)
                {
                    for (int i = 0; i < table.Length && n < PerKindMax; i++)
                    {
                        string nm = name(table[i]);
                        int rank = nm.Length == 0 ? -1 : Rank(q, nm);
                        if (rank < 0) continue;
                        int idx = i, t = tab;
                        int c = cost(table[i]);
                        hits.Add(new SearchHit(HitKind.Item, nm,
                            kind + (c > 0 ? $"  ·  {c:n0} credits" : ""), rank,
                            () => OpenItem(t, idx)));
                        n++;
                    }
                }
                ItemHits(items.Ships, 0, "ship", s => s.Name, s => s.Cost);
                ItemHits(items.Ports, 1, "weapon port", p => p.Name, p => p.Cost);
                ItemHits(items.Options, 2, "sidekick", o => o.Name, o => o.Cost);
                ItemHits(items.Shields, 3, "shield", s => s.Name, s => s.Cost);
                ItemHits(items.Powers, 4, "generator", p => p.Name, p => p.Cost);
                ItemHits(items.Specials, 5, "special weapon", s => s.Name, _ => 0);
            }

            // --- Enemies, by id or by the bank they draw from ---
            EnemyData? ed = null;
            try { ed = _gd.GetEnemyData(ep); } catch { }
            if (ed != null)
            {
                n = 0;
                for (int i = 0; i < ed.Enemies.Length && n < PerKindMax; i++)
                {
                    var d = ed.Enemies[i];
                    if (!d.Loaded || (d.EGraphic[0] == 0 && d.Armor == 0)) continue;
                    var cat = ObjectPlacer.Classify(d.Armor, d.Value, 0);
                    int rank = Rank(q, i.ToString(), $"bank {d.ShapeBank}", ObjectPlacer.CategoryName(cat));
                    if (rank < 0) continue;
                    int id = i;
                    hits.Add(new SearchHit(HitKind.Enemy, $"enemy #{id}",
                        $"{ObjectPlacer.CategoryName(cat).ToLowerInvariant()}  ·  armour {d.Armor}" +
                        $"  ·  bank {d.ShapeBank}", rank,
                        () => OpenEnemy(epIdx, id)));
                    n++;
                }
            }
        }

        // --- Assemblies, but only when the enemy browser has already read them. Building the
        //     index means parsing every level in the set, which is not something a keystroke
        //     in a search box should set off. ---
        if (_assemblies != null && _assembliesFor == string.Join(',', ShownEpisodes()))
        {
            int n = 0;
            foreach (var a in _assemblies)
            {
                if (n >= PerKindMax) break;
                if (a.RepeatOf != null) continue;
                int rank = Rank(q, a.LevelName, a.Title);
                if (rank < 0) continue;
                var asm = a;
                hits.Add(new SearchHit(HitKind.Assembly, $"{a.Title} in {a.LevelName}",
                    $"{a.Parts.Count} parts  ·  armour {a.TotalArmor}" +
                    (a.RepeatCount > 1 ? $"  ·  ×{a.RepeatCount}" : ""), rank,
                    () => OpenAssemblyFromSearch(asm)));
                n++;
            }
        }

        // --- Sprite banks (episode-independent) ---
        int b = 0;
        foreach (var src in AllSpriteSources())
        {
            if (b >= PerKindMax) break;
            int rank = Rank(q, src.Title, src.ShortName);
            if (rank < 0) continue;
            var s = src;
            hits.Add(new SearchHit(HitKind.Bank, src.ShortName, src.Title, rank, () => OpenSprite(s, -1)));
            b++;
        }

        // --- Songs and sounds (episode-independent; only once the audio device has read them) ---
        if (_audio is { IsOpen: true } audio)
        {
            int m = 0;
            for (int i = 0; i < audio.Music.Tracks.Length && m < PerKindMax; i++)
            {
                var track = audio.Music.Tracks[i];
                int rank = Rank(q, track.Title, (i + 1).ToString());
                if (rank < 0) continue;
                int songIdx = i;
                int uses = Usage?.SongLevelCount(i) ?? 0;
                hits.Add(new SearchHit(HitKind.Song, track.Title,
                    uses > 0 ? $"song {i + 1}  ·  played in {uses} level{(uses == 1 ? "" : "s")}"
                             : $"song {i + 1}",
                    rank, () => OpenTrack(songIdx)));
                m++;
            }

            int s2 = 0;
            foreach (var clip in audio.Sounds.Clips)
            {
                if (clip == null || s2 >= PerKindMax) continue;
                int rank = Rank(q, clip.Title, clip.Symbol, clip.Note, clip.Number.ToString());
                if (rank < 0) continue;
                int number = clip.Number;
                hits.Add(new SearchHit(HitKind.Sound, $"{clip.Number:00}  {clip.Title}",
                    clip.Note.Length > 0 ? clip.Note : clip.Symbol,
                    rank, () => OpenSound(number)));
                s2++;
            }
        }

        hits.Sort((x, y) => x.Kind != y.Kind ? x.Kind.CompareTo(y.Kind)
            : x.Rank != y.Rank ? x.Rank.CompareTo(y.Rank)
            : string.Compare(x.Title, y.Title, StringComparison.OrdinalIgnoreCase));
        return hits;
    }

    /// <summary>Open a shop row: the tab, the row within it, and a scroll so it is on screen.</summary>
    private void OpenItem(int tab, int row)
    {
        _showItems = true;
        _itemTabPending = tab;
        _itemRowPending = row;
        _itemOpened = true;
        _itemScrollToSelection = true;
    }

    /// <summary>Point the enemy browser's assembly list at one group. Its own level picker and
    /// filter are cleared first: leaving them set would hide the very row being opened.</summary>
    private void OpenAssemblyFromSearch(EnemyAssembly asm)
    {
        _showEnemies = true;
        _enemyMode = 1;
        _asmLevelEp = _asmLevelFile = -1;
        _enemyFilter[0] = 0;
        int at = ShownAssemblies().IndexOf(asm);
        if (at >= 0) { _asmSelected = at; _asmScrollToSelection = true; _asmAimed = true; }
    }
}
