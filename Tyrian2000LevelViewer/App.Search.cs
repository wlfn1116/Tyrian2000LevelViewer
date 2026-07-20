using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;

namespace T2LV;

/// <summary>
/// One box over the whole data set: levels, enemies, shop items, datacubes and sprite banks.
/// Everything the other windows browse is reachable by typing part of its name, and every
/// result opens the window that owns it.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showSearch;
    private bool _searchFocus;
    private readonly byte[] _searchBuf = new byte[96];

    /// <summary>What a hit points at, which decides its colour and where it opens.</summary>
    private enum HitKind { Level, Enemy, Assembly, Item, Cube, Bank }

    private readonly record struct SearchHit(HitKind Kind, string Title, string Detail, Action Open);

    private void OpenSearch()
    {
        _showSearch = true;
        _searchFocus = true;
    }

    /// <summary>The "--search &lt;text&gt;" entry point: open already showing a query's results.</summary>
    public void ShowSearch(string query)
    {
        _showSearch = true;
        int n = System.Text.Encoding.UTF8.GetBytes(query, 0, Math.Min(query.Length, _searchBuf.Length - 1),
            _searchBuf, 0);
        _searchBuf[n] = 0;
    }

    private static uint HitColor(HitKind k) => k switch
    {
        HitKind.Level => Gfx.Rgba(255, 190, 90),
        HitKind.Enemy => Gfx.Rgba(255, 120, 120),
        HitKind.Assembly => Gfx.Rgba(255, 150, 90),
        HitKind.Item => Gfx.Rgba(255, 210, 120),
        HitKind.Cube => Gfx.Rgba(180, 120, 255),
        _ => Gfx.Rgba(150, 200, 255),
    };

    private static string HitLabel(HitKind k) => k switch
    {
        HitKind.Level => "level",
        HitKind.Enemy => "enemy",
        HitKind.Assembly => "group",
        HitKind.Item => "item",
        HitKind.Cube => "datacube",
        _ => "sprites",
    };

    private void DrawSearchWindow()
    {
        if (!_showSearch || _gd == null) return;

        ImGui.SetNextWindowSize(new Vector2(680, 520), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(420, 240), new Vector2(float.MaxValue, float.MaxValue));
        bool open = _showSearch;
        if (!ImGui.Begin("Search###search", ref open)) { ImGui.End(); _showSearch = open; return; }
        _showSearch = open;

        if (_searchFocus) { ImGui.SetKeyboardFocusHere(); _searchFocus = false; }
        FilterBox("##searchbox", "levels, enemies, items, datacubes, sprite banks...",
            _searchBuf, ImGui.GetContentRegionAvail().X);

        string q = BufText(_searchBuf).Trim();
        ImGui.Separator();
        if (q.Length < 2)
        {
            ImGui.TextDisabled("Type at least two characters.\n\n" +
                "Names and numbers both work: \"savara\", \"351\", \"pulse cannon\", \"microsol\".\n" +
                "Results search the episodes the viewer is currently browsing.");
            ImGui.End();
            return;
        }

        var hits = Search(q);
        ImGui.TextDisabled($"{hits.Count} result{(hits.Count == 1 ? "" : "s")}");
        ImGui.BeginChild("searchresults", new Vector2(0, 0));

        HitKind? group = null;
        foreach (var hit in hits)
        {
            if (group != hit.Kind)
            {
                group = hit.Kind;
                ImGui.SeparatorText(HitLabel(hit.Kind) + "s");
            }
            var mn = ImGui.GetCursorScreenPos();
            if (ImGui.Selectable($"##hit{hit.Title}{hit.Detail}", false, ImGuiSelectableFlags.None,
                    new Vector2(0, 30f)))
                hit.Open();
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(mn, new Vector2(mn.X + 2.5f, mn.Y + 28f), HitColor(hit.Kind), 1f);
            dl.AddText(new Vector2(mn.X + 8f, mn.Y), Gfx.Rgba(228, 232, 242), hit.Title);
            dl.AddText(new Vector2(mn.X + 8f, mn.Y + 14f), Gfx.Rgba(140, 146, 162), hit.Detail);
        }
        if (hits.Count == 0) ImGui.TextDisabled("Nothing found.");

        ImGui.EndChild();
        ImGui.End();
    }

    /// <summary>Cap per kind, so one broad match cannot bury the others.</summary>
    private const int PerKindMax = 25;

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
                if (!Matches(q, item.Name, item.FileNum.ToString())) continue;
                int file = item.FileNum;
                string name = string.IsNullOrWhiteSpace(item.Name) ? "(unnamed)" : item.Name.Trim();
                hits.Add(new SearchHit(HitKind.Level, name,
                    $"episode {ep.Number}, level {file}", () => SelectLevelFile(epIdx, file)));
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
                if (!Matches(q, cube.Title, cube.Header, body)) continue;
                int idx = cube.Index;
                hits.Add(new SearchHit(HitKind.Cube, StripMarks(cube.Title),
                    $"episode {ep.Number}, cube {idx}  ·  {cube.Header}", () => OpenCubes(epIdx, idx)));
                n++;
            }

            // --- Shop items ---
            var items = _gd.GetItems(ep);
            if (items.Loaded)
            {
                n = 0;
                void ItemHits<T>(T[] table, int tab, string kind, Func<T, string> name, Func<T, int> cost)
                {
                    for (int i = 0; i < table.Length && n < PerKindMax; i++)
                    {
                        string nm = name(table[i]);
                        if (nm.Length == 0 || !Matches(q, nm)) continue;
                        int idx = i, t = tab;
                        int c = cost(table[i]);
                        hits.Add(new SearchHit(HitKind.Item, nm,
                            $"{kind}" + (c > 0 ? $"  ·  {c:n0} credits" : ""),
                            () => { _showItems = true; _itemTab = t; _itemSelected = idx; }));
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
                    if (!Matches(q, i.ToString(), $"bank {d.ShapeBank}", ObjectPlacer.CategoryName(cat))) continue;
                    int id = i;
                    hits.Add(new SearchHit(HitKind.Enemy, $"enemy #{id}",
                        $"{ObjectPlacer.CategoryName(cat).ToLowerInvariant()}  ·  armour {d.Armor}  ·  bank {d.ShapeBank}",
                        () => OpenEnemy(epIdx, id)));
                    n++;
                }
            }
        }

        // --- Sprite banks (episode-independent) ---
        int b = 0;
        foreach (var src in AllSpriteSources())
        {
            if (b >= PerKindMax) break;
            if (!Matches(q, src.Title, src.ShortName)) continue;
            var s = src;
            hits.Add(new SearchHit(HitKind.Bank, src.ShortName, src.Title, () => OpenSprite(s, -1)));
            b++;
        }

        hits.Sort((x, y) => x.Kind.CompareTo(y.Kind));
        return hits;
    }
}
