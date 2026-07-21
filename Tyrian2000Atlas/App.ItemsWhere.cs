using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// "Where you get it": for every shop item, the outposts that sell it and the levels its pickup
/// turns up in, both as click-throughs that open the level (and, in playback, seek to the frame
/// the pickup flies in on -- see <see cref="MapJump"/>).
///
/// Two data sets feed this, built once and cached:
///
/// * The outposts come from the flow graph, which now carries each ']I' shelf on the level it
///   leads into (<see cref="ShopStop"/>). Seven of a shelf's nine rows are ever sold from, and
///   which row is which upgrade slot is fixed (game_menu.c:182). The Engaged fork's re-added
///   Charge-Laser Cannon is injected here rather than in the graph, so one graph serves both
///   builds -- exactly the shops tyrian2.c:4360 puts it in (Episode 2 sections 3/11/16, Episode
///   3 section 16), as the two sidekick slots.
///
/// * The level pickups come from scanning every level's spawn events for an enemy whose value is
///   a pickup code (mainint.c:7787): front weapon 30000+id, rear 31000+id, sidekick 32000+id,
///   special 32100+id. Death drops count too, so each spawn's enemydie chain is followed the way
///   the secret-warp scan does (<see cref="EpisodeGraph.FindSecretTargets"/>).
///
/// Both are filtered to the episodes the atlas is showing, so a click always lands on a level
/// that is in the browse list.
/// </summary>
public sealed unsafe partial class App
{
    /// <summary>The seven upgrade slots a shop sells, in the engine's own order. Front and rear
    /// weapons are both ports; the two sidekick slots are both options.</summary>
    private enum ShopCat { Ship, FrontWeapon, RearWeapon, Generator, LeftSidekick, RightSidekick, Shield }

    /// <summary>0-based ']I' availability row -> (item tab, upgrade slot). Rows 4 and 7 are read
    /// by the engine but never sold from (they are absent from game_menu.c:182's itemAvailMap).</summary>
    private static readonly (int Row, int Tab, ShopCat Cat)[] ShopRowCats =
    {
        (0, 0, ShopCat.Ship),
        (1, 1, ShopCat.FrontWeapon),
        (2, 1, ShopCat.RearWeapon),
        (3, 4, ShopCat.Generator),
        (5, 2, ShopCat.LeftSidekick),
        (6, 2, ShopCat.RightSidekick),
        (8, 3, ShopCat.Shield),
    };

    /// <summary>What a level pickup gives, by the value band it falls in.</summary>
    private enum PickupKind { Front, Rear, Sidekick, Special }

    /// <summary>One outpost that stocks the item: the level it sits before, and the slot it sells
    /// the item in.</summary>
    private readonly record struct SoldSite(int EpisodeIdx, int Episode, int FileNum, string Level, ShopCat Cat);

    /// <summary>One level pickup of the item: where it is, when it first flies in, what kind of
    /// pickup it is, whether it is a death drop, and the enemy that carries it (so playback can
    /// settle on the right one).</summary>
    private readonly record struct FoundSite(int EpisodeIdx, int Episode, int FileNum, string Level,
        ushort Time, PickupKind Kind, bool ViaDeath, int EnemyId);

    // Built lazily; the sold-at index depends on the vanilla/Engaged switch, the found-in one
    // does not (pickup codes are in the level data, which the fork never touches).
    private Dictionary<(int Tab, int Id), List<SoldSite>>? _itemSoldAt;
    private bool _itemSoldAtFork;
    private Dictionary<(int Tab, int Id), List<FoundSite>>? _itemFoundIn;
    private Dictionary<int, int>? _episodeOutposts;   // episode index -> how many outposts it has
    private static readonly List<SoldSite> NoSold = new();
    private static readonly List<FoundSite> NoFound = new();

    /// <summary>How many outposts an episode has, so an item on every one of them can collapse
    /// to a single "every outpost" row instead of a wall of near-identical lines.</summary>
    private int OutpostCount(int episodeIdx)
    {
        if (_episodeOutposts == null)
        {
            _episodeOutposts = new Dictionary<int, int>();
            if (_gd != null)
                for (int e = 0; e < _gd.Episodes.Count; e++)
                {
                    var g = _gd.GetGraph(_gd.Episodes[e]);
                    if (g == null) continue;
                    var files = new HashSet<int>();
                    foreach (var node in g.Nodes)
                        if (node.Kind == GraphNodeKind.Level && node.ShopStops.Count > 0)
                            files.Add(node.LvlFileNum);
                    _episodeOutposts[e] = files.Count;
                }
        }
        return _episodeOutposts.TryGetValue(episodeIdx, out var n) ? n : 0;
    }

    /// <summary>Every outpost that sells item <paramref name="id"/> of <paramref name="tab"/>,
    /// for the shop the browser is currently showing (vanilla or Engaged).</summary>
    private List<SoldSite> ItemSoldAt(int tab, int id)
    {
        if (_itemSoldAt == null || _itemSoldAtFork != _itemFork)
        {
            _itemSoldAt = BuildSoldAt(_itemFork);
            _itemSoldAtFork = _itemFork;
        }
        return _itemSoldAt.TryGetValue((tab, id), out var l) ? l : NoSold;
    }

    /// <summary>Every level pickup of item <paramref name="id"/> of <paramref name="tab"/>.</summary>
    private List<FoundSite> ItemFoundIn(int tab, int id)
    {
        _itemFoundIn ??= BuildFoundIn();
        return _itemFoundIn.TryGetValue((tab, id), out var l) ? l : NoFound;
    }

    /// <summary>
    /// Decode a pickup value the way the collision handler does (mainint.c:7787, normal mode):
    /// an else-if ladder from the top, so the band an item falls in is fixed. 30000 exactly is
    /// the purple ball, not an item. Returns null for money, the ball, and anything below.
    /// </summary>
    private static (int Tab, int Id, PickupKind Kind)? DecodePickup(int value)
    {
        if (value <= 30000) return null;
        if (value <= 31000) return (1, value - 30000, PickupKind.Front);
        if (value <= 32000) return (1, value - 31000, PickupKind.Rear);
        if (value <= 32100) return (2, value - 32000, PickupKind.Sidekick);
        return (5, value - 32100, PickupKind.Special);
    }

    /// <summary>The Charge-Laser Cannon's home shops in the Engaged fork (tyrian2.c:4360),
    /// keyed on the outpost's own section like the engine is.</summary>
    private static bool ChargeLaserOutpost(int epNum, int section) =>
        (epNum == 2 && (section == 3 || section == 11 || section == 16)) ||
        (epNum == 3 && section == 16);

    /// <summary>The ids an outpost's row sells, plus the fork's Charge-Laser re-add where it
    /// applies -- the engine appends its slot to both sidekick rows (tyrian2.c:4364-4365).</summary>
    private static IEnumerable<int> ShopRowIds(ShopStop stop, int row, int epNum, int chargeLaserSlot)
    {
        foreach (int id in stop.Rows[row]) yield return id;
        if (chargeLaserSlot > 0 && (row == 5 || row == 6) && ChargeLaserOutpost(epNum, stop.Section))
            yield return chargeLaserSlot;
    }

    private static bool ItemExists(ItemData d, int tab, int id) => tab switch
    {
        0 => id > 0 && id < d.Ships.Length && d.Ships[id] != null,
        1 => id > 0 && id < d.Ports.Length && d.Ports[id] != null,
        2 => id > 0 && id < d.Options.Length && d.Options[id] != null,
        3 => id > 0 && id < d.Shields.Length && d.Shields[id] != null,
        4 => id > 0 && id < d.Powers.Length && d.Powers[id] != null,
        5 => id > 0 && id < d.Specials.Length && d.Specials[id] != null,
        _ => false,
    };

    private Dictionary<(int Tab, int Id), List<SoldSite>> BuildSoldAt(bool fork)
    {
        var map = new Dictionary<(int, int), List<SoldSite>>();
        if (_gd == null) return map;
        // One entry per (item, outpost level, slot): a level a route reaches two ways can carry
        // two identical shelves, and two ']L' cuts of one file (a difficulty split) two nodes.
        var seen = new HashSet<(int Tab, int Id, int Ep, int File, ShopCat Cat)>();

        for (int e = 0; e < _gd.Episodes.Count; e++)
        {
            var ep = _gd.Episodes[e];
            var graph = _gd.GetGraph(ep);
            if (graph == null) continue;
            var items = _gd.GetItems(ep, fork);
            if (!items.Loaded) continue;
            int chargeLaserSlot = fork ? items.ChargeLaserSlot : 0;

            foreach (var node in graph.Nodes)
            {
                if (node.Kind != GraphNodeKind.Level || node.ShopStops.Count == 0) continue;
                foreach (var stop in node.ShopStops)
                    foreach (var (row, tab, cat) in ShopRowCats)
                        foreach (int id in ShopRowIds(stop, row, ep.Number, chargeLaserSlot))
                        {
                            if (!ItemExists(items, tab, id)) continue;
                            if (!seen.Add((tab, id, e, node.LvlFileNum, cat))) continue;
                            var key = (tab, id);
                            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<SoldSite>();
                            list.Add(new SoldSite(e, ep.Number, node.LvlFileNum, node.Title.Trim(), cat));
                        }
            }
        }
        return map;
    }

    private Dictionary<(int Tab, int Id), List<FoundSite>> BuildFoundIn()
    {
        var map = new Dictionary<(int, int), List<FoundSite>>();
        if (_gd == null) return map;
        var visited = new HashSet<int>();

        for (int e = 0; e < _gd.Episodes.Count; e++)
        {
            var ep = _gd.Episodes[e];
            EnemyData ed;
            try { ed = _gd.GetEnemyData(ep); } catch { continue; }

            foreach (var levelItem in ep.Levels)
            {
                Level lv;
                try { lv = _gd.LoadLevel(ep, levelItem.FileNum); } catch { continue; }
                string name = string.IsNullOrWhiteSpace(levelItem.Name) ? "(unnamed)" : levelItem.Name.Trim();

                foreach (var ev in lv.Events)
                {
                    // 49-52 carry a sprite in dat rather than an enemyDat id, so they name no
                    // entry to read a value off; every other spawn event does (12 names four).
                    if (ev.Type is >= 49 and <= 52) continue;
                    if (!ObjectPlacer.IsSpawn(ev.Type, out _, out _) && ev.Type != 12) continue;
                    int span = ev.Type == 12 ? 4 : 1;

                    for (int k = 0; k < span; k++)
                    {
                        int root = ev.Dat + k;
                        if (root <= 0) continue;
                        // Breadth-first over this spawn and everything its death chain leaves
                        // behind; depth 0 is the enemy the event places, deeper is a death drop.
                        visited.Clear();
                        var frontier = new List<int> { root };
                        for (int depth = 0; frontier.Count > 0; depth++)
                        {
                            var next = new List<int>();
                            foreach (int id in frontier)
                            {
                                if (!visited.Add(id)) continue;
                                var dat = ed.Get(id);
                                if (!dat.Loaded) continue;
                                var pick = DecodePickup(dat.Value);
                                if (pick is { } p && ItemFoundExists(ep, p.Tab, p.Id))
                                {
                                    var key = (p.Tab, p.Id);
                                    if (!map.TryGetValue(key, out var list)) map[key] = list = new List<FoundSite>();
                                    list.Add(new FoundSite(e, ep.Number, levelItem.FileNum, name,
                                        ev.Time, p.Kind, depth > 0, root));
                                }
                                if (dat.EEnemyDie != 0) next.Add(dat.EEnemyDie);
                            }
                            frontier = next;
                        }
                    }
                }
            }
        }
        return map;
    }

    /// <summary>Whether the pickup names a real item in this episode's tables -- guards against a
    /// stray value in the pickup band mapping to an empty slot. Checked against the Engaged
    /// table (a superset of vanilla's named items) so this index stays fork-independent and can
    /// be built once; the ids are the same in both builds.</summary>
    private bool ItemFoundExists(EpisodeInfo ep, int tab, int id)
    {
        var d = _gd!.GetItems(ep, fork: true);
        return d.Loaded && ItemExists(d, tab, id);
    }

    // =====================================================================
    // UI
    // =====================================================================

    /// <summary>A category's section heading in the Outposts inventory (plural where it reads
    /// better).</summary>
    private static string ShopCatSection(ShopCat c) => c switch
    {
        ShopCat.Ship => "ships",
        ShopCat.FrontWeapon => "front weapons",
        ShopCat.RearWeapon => "rear weapons",
        ShopCat.Generator => "generators",
        ShopCat.LeftSidekick => "left sidekick",
        ShopCat.RightSidekick => "right sidekick",
        ShopCat.Shield => "shields",
        _ => "items",
    };

    private static string ShopCatsText(IReadOnlyCollection<ShopCat> cats)
    {
        var parts = new List<string>();
        if (cats.Contains(ShopCat.Ship)) parts.Add("ship");
        if (cats.Contains(ShopCat.FrontWeapon) && cats.Contains(ShopCat.RearWeapon)) parts.Add("front & rear weapon");
        else if (cats.Contains(ShopCat.FrontWeapon)) parts.Add("front weapon");
        else if (cats.Contains(ShopCat.RearWeapon)) parts.Add("rear weapon");
        if (cats.Contains(ShopCat.Generator)) parts.Add("generator");
        if (cats.Contains(ShopCat.LeftSidekick) && cats.Contains(ShopCat.RightSidekick)) parts.Add("left & right sidekick");
        else if (cats.Contains(ShopCat.LeftSidekick)) parts.Add("left sidekick");
        else if (cats.Contains(ShopCat.RightSidekick)) parts.Add("right sidekick");
        if (cats.Contains(ShopCat.Shield)) parts.Add("shield");
        return parts.Count > 0 ? string.Join(", ", parts) : "sold here";
    }

    private static string FoundKindText(IReadOnlyCollection<PickupKind> kinds)
    {
        bool fr = kinds.Contains(PickupKind.Front) && kinds.Contains(PickupKind.Rear);
        if (fr) return "front & rear pickup";
        var one = kinds.Count > 0 ? kinds.First() : PickupKind.Front;
        return one switch
        {
            PickupKind.Front => "front-weapon pickup",
            PickupKind.Rear => "rear-weapon pickup",
            PickupKind.Sidekick => "sidekick pickup",
            _ => "special pickup",
        };
    }

    /// <summary>
    /// The "where you get it" block under a detail pane: the outposts that stock the item and the
    /// levels its pickup appears in. Both honour the window's episode picker, so every row is a
    /// level the browse list holds and a click always resolves.
    /// </summary>
    private void DrawItemAppearances(ItemData d)
    {
        int tab = _itemTab, id = _itemSelected;
        bool sellable = tab != 5;                 // specials are never in a ']I' list
        bool droppable = tab is 1 or 2 or 5;      // ports, sidekicks and specials have pickup codes

        var shown = new HashSet<int>(ShownEpisodes());
        var sold = sellable
            ? ItemSoldAt(tab, id).Where(s => shown.Contains(s.EpisodeIdx)).ToList()
            : NoSold;
        var found = droppable
            ? ItemFoundIn(tab, id).Where(s => shown.Contains(s.EpisodeIdx)).ToList()
            : NoFound;

        if (sellable) DrawSoldAtBlock(sold);
        if (droppable)
        {
            if (sellable) ImGui.Dummy(new Vector2(0, 4f));
            DrawFoundInBlock(found, tab);
        }
        // A special has three more ways in -- enemy drops, twiddle codes, arcade loadouts.
        if (tab == 5) DrawSpecialExtras(d, id);
    }

    /// <summary>More outposts than this in one episode and the sold-at list folds to a single
    /// summary row -- a ship or starter weapon is on so many that naming each is just noise.</summary>
    private const int SoldCollapse = 6;

    /// <summary>How many levels a found-in list draws before folding the rest into a "+N more"
    /// line, so a pickup dropped by a common enemy cannot fill the whole pane.</summary>
    private const int MaxOutpostRows = 14;

    private void DrawSoldAtBlock(List<SoldSite> sold)
    {
        // Per outpost (episode + level): which slots it sells the item in.
        var outposts = sold
            .GroupBy(s => (s.EpisodeIdx, s.FileNum))
            .Select(g => (g.First().EpisodeIdx, g.First().Episode, g.First().FileNum, g.First().Level,
                Cats: g.Select(s => s.Cat).Distinct().ToList()))
            .ToList();

        UiSection("sold at outposts", AcItem, outposts.Count == 0 ? "none"
            : $"{outposts.Count} outpost{(outposts.Count == 1 ? "" : "s")}");

        if (outposts.Count == 0)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorOf(UiFaint), _allEpisodes
                ? "No outpost in the game stocks it -- it is only found in the field, or comes fitted."
                : "No outpost in this episode stocks it. Try \"All episodes\", or it is a field-only item.");
            ImGui.PopTextWrapPos();
            return;
        }

        const float rowH = 30f;
        foreach (var epGroup in outposts.GroupBy(o => o.EpisodeIdx).OrderBy(g => g.First().Episode))
        {
            var list = epGroup.OrderBy(o => o.FileNum).ToList();
            var cats = list.SelectMany(o => o.Cats).Distinct().ToList();
            var first = list[0];
            int total = OutpostCount(epGroup.Key);
            string epTag = _allEpisodes ? $"  ·  Ep {first.Episode}" : "";

            // Sold at every outpost, or at so many that a full list is just repetition (a ship,
            // a starter weapon): one summary row that still opens the earliest of them.
            bool everywhere = total > 1 && list.Count >= total;
            if (everywhere || list.Count > SoldCollapse)
            {
                var box = UiRow($"##soldmany{epGroup.Key}", false, AcItem, rowH);
                if (box.Clicked) SelectLevelFile(epGroup.Key, first.FileNum);
                if (box.Hovered) ImGui.SetTooltip("sold widely in this episode\nopens the earliest outpost that stocks it");
                string title = everywhere ? $"every outpost  ({list.Count})"
                    : $"{list.Count} outposts  ·  from {first.Level} #{first.FileNum:00}";
                RowText(box, 10f, title, ShopCatsText(cats) + epTag, AcItem, false, 14f);
                continue;
            }
            foreach (var o in list)
            {
                var box = UiRow($"##sold{o.EpisodeIdx}_{o.FileNum}", false, AcItem, rowH);
                if (box.Clicked) SelectLevelFile(o.EpisodeIdx, o.FileNum);
                if (box.Hovered)
                    ImGui.SetTooltip("open the level this outpost sits before\n(the shop itself is between levels, so there is no frame to seek to)");
                RowText(box, 10f, $"before {o.Level}  #{o.FileNum:00}",
                    ShopCatsText(o.Cats) + epTag, AcItem, false, 14f);
            }
        }
    }

    private void DrawFoundInBlock(List<FoundSite> found, int tab)
    {
        var groups = found
            .GroupBy(s => (s.EpisodeIdx, s.FileNum))
            .Select(g => (g.First().EpisodeIdx, g.First().Episode, g.First().FileNum, g.First().Level,
                Time: g.Min(s => s.Time),
                Count: g.Count(),
                Kinds: g.Select(s => s.Kind).Distinct().ToList(),
                AllDeath: g.All(s => s.ViaDeath),
                AnyDeath: g.Any(s => s.ViaDeath),
                Roots: g.Select(s => s.EnemyId).Where(x => x != 0).Distinct().ToArray()))
            .OrderBy(g => g.Episode).ThenBy(g => g.Time)
            .ToList();

        UiSection("found in levels", AcRoutes, groups.Count == 0 ? "none"
            : $"{groups.Count} level{(groups.Count == 1 ? "" : "s")}");

        if (groups.Count == 0)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorOf(UiFaint), tab == 5
                ? "Special weapons aren't sold at outposts and no level in " +
                  (_allEpisodes ? "the game" : "this episode") + " drops this one -- it comes fitted to a ship."
                : "No level in " + (_allEpisodes ? "the game" : "this episode") + " drops it as a pickup.");
            ImGui.PopTextWrapPos();
            return;
        }

        int most = groups.Max(g => g.Count);
        const float rowH = 30f;
        int shownRows = 0;
        foreach (var g in groups)
        {
            if (shownRows >= MaxOutpostRows)
            {
                ImGui.TextColored(ColorOf(UiFaint), $"   +{groups.Count - shownRows} more level{(groups.Count - shownRows == 1 ? "" : "s")}");
                break;
            }
            shownRows++;
            var box = UiRow($"##fnd{g.EpisodeIdx}_{g.FileNum}", false, AcRoutes, rowH);
            if (box.Clicked)
            {
                SelectLevelFile(g.EpisodeIdx, g.FileNum);
                _pendingJump = new MapJump(g.Time, g.Roots);
            }
            if (box.Hovered)
                ImGui.SetTooltip("open this level at its first pickup of this item\n(in playback, seek to the frame it flies in)");

            var dl = ImGui.GetWindowDrawList();
            float barW = Math.Max(24f, (box.Max.X - box.Min.X) * 0.22f);
            string trail = g.Count > 1 ? $"×{g.Count}" : "";
            string death = g.AllDeath ? "  ·  on death" : g.AnyDeath ? "  ·  some on death" : "";
            string epTag = _allEpisodes ? $"  ·  Ep {g.Episode}" : "";
            RowText(box, 10f, $"{g.Level}  #{g.FileNum:00}",
                $"first at t={g.Time}  ·  {FoundKindText(g.Kinds)}{death}{epTag}",
                AcRoutes, false, barW + TrailRoom(trail) + 12f);
            var bar = new Vector2(box.Max.X - barW - TrailRoom(trail), box.Min.Y + rowH * 0.5f - 5f);
            MeterBar(dl, bar, bar + new Vector2(barW, 7f), g.Count / (float)most, AcRoutes);
            if (trail.Length > 0) RowTrail(box, trail, Shade(AcRoutes, 1.1f));
        }
    }
}
