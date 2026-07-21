using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;

namespace T2LV;

/// <summary>
/// The Other tab: every field pickup that is not one of the six shop tables -- weapon power-ups,
/// the datacube, super bomb and HOT DOG swaps, money and gems, armour, the secret-warp orb, the
/// purple ball, and the arcade-mode weapon balls that swap a gun or special in.
///
/// A pickup is dropped far more often than it is placed, and almost always the same way: an
/// event 33 retargets a live formation's <c>enemydie</c> to the pickup, so killing that formation
/// leaves it behind (tyrian2.c:6743). Static <c>enemydie</c>, <c>elaunchtype</c> launches, and the
/// low-armour rescue ship (#560 → #561-3) fill in the rest. This tab reads all of them and names
/// the enemy each pickup comes off, a click through to it.
///
/// Entries are folded hard: the datacube, purple ball, secret orb and the one-off power-ups are a
/// single row each however many table slots carry them; only money, armour and the weapon balls
/// stay split, by their value. Empty table slots (no shape bank) and pickups that never turn up
/// are dropped.
/// </summary>
public sealed unsafe partial class App
{
    private enum OtherKind
    {
        FrontPower, RearPower, DataCube, SuperBomb, HotDog, AsteroidKiller, PurpleBall,
        FrontBall, RearBall, SidekickBall, SpecialBall,
        Money, Armour, SecretWarp,
    }

    /// <summary>The kinds that are one pickup however many table slots carry them.</summary>
    private static bool IsSingletonKind(OtherKind k) => k is OtherKind.FrontPower or OtherKind.RearPower
        or OtherKind.DataCube or OtherKind.SuperBomb or OtherKind.HotDog or OtherKind.AsteroidKiller
        or OtherKind.PurpleBall or OtherKind.SecretWarp;

    private static bool IsBallKind(OtherKind k) => k is OtherKind.FrontBall or OtherKind.RearBall
        or OtherKind.SidekickBall or OtherKind.SpecialBall;

    private static string OtherKindName(OtherKind k) => k switch
    {
        OtherKind.FrontPower => "Front weapon power-up",
        OtherKind.RearPower => "Rear weapon power-up",
        OtherKind.DataCube => "Data cube",
        OtherKind.SuperBomb => "Super Bomb",
        OtherKind.HotDog => "HOT DOG!",
        OtherKind.AsteroidKiller => "Asteroid killer",
        OtherKind.PurpleBall => "Purple ball",
        OtherKind.FrontBall => "Front weapon ball",
        OtherKind.RearBall => "Rear weapon ball",
        OtherKind.SidekickBall => "Sidekick ball",
        OtherKind.SpecialBall => "Special ball",
        OtherKind.Money => "Money / gems",
        OtherKind.Armour => "Armour",
        OtherKind.SecretWarp => "Secret warp",
        _ => "Pickup",
    };

    private static string OtherKindDesc(OtherKind k) => k switch
    {
        OtherKind.FrontPower => "Raises the front weapon one power level, or hands over 1000 credits when it is already maxed (player.c:30).",
        OtherKind.RearPower => "Raises the rear weapon one power level.",
        OtherKind.DataCube => "A datacube for the outpost archive -- picking one up lets the next outpost read one more of its shelf.",
        OtherKind.SuperBomb => "Adds a super bomb to the store, up to ten.",
        OtherKind.HotDog => "Swaps both guns to the HOT DOG loadout (front 25, rear 26) at once.",
        OtherKind.AsteroidKiller => "Fires an orbiting shot that clears asteroids.",
        OtherKind.PurpleBall => "Counts toward the next front-weapon power-up -- several are needed per level -- and in Galaga mode spawns the Dragonwing. In arcade play it turns into one of the front-weapon balls.",
        OtherKind.FrontBall => "Flying into it swaps the front weapon to the one below (arcade / 2-player; mainint.c:7933).",
        OtherKind.RearBall => "Flying into it swaps the rear weapon to the one below (arcade / 2-player).",
        OtherKind.SidekickBall => "Flying into it fits the sidekick below (arcade / 2-player).",
        OtherKind.SpecialBall => "Flying into it swaps in the special below (single player works too; mainint.c:7835).",
        OtherKind.Money => "Cash picked up for score. The denomination is the sprite; the number is what it is worth.",
        OtherKind.Armour => "Restores hull armour by the amount over 20000 in its value. Left by the rescue ship that flies in when your hull drops below 6 (tyrian2.c:3476).",
        OtherKind.SecretWarp => "The orb that warps the run to a secret level -- its value less 10000 is the destination. Different orbs point to different secrets.",
        _ => "",
    };

    /// <summary>Classify a table entry as a field pickup, or null. Slots with no shape bank are
    /// empty table padding (the 1001+ block is all value-1, bank-0), not pickups.</summary>
    private static OtherKind? ClassifyOther(in EnemyDat d)
    {
        if (!d.Loaded || d.ShapeBank == 0) return null;
        int v = d.Value;
        switch (v)
        {
            case 1: return OtherKind.DataCube;
            case -1: return OtherKind.FrontPower;
            case -2: return OtherKind.RearPower;
            case -3: return OtherKind.AsteroidKiller;
            case -4: return OtherKind.SuperBomb;
            case -5: return OtherKind.HotDog;
            case 30000: return OtherKind.PurpleBall;
        }
        if (v is > 30000 and <= 31000) return OtherKind.FrontBall;
        if (v is > 31000 and <= 32000) return OtherKind.RearBall;
        if (v is > 32000 and <= 32100) return OtherKind.SidekickBall;
        if (v > 32100) return OtherKind.SpecialBall;
        if (d.Armor != 0) return null;
        if (v is >= 2 and <= 9999) return OtherKind.Money;
        if (v is > 10000 and <= 20000) return OtherKind.SecretWarp;
        if (v is > 20000 and < 30000) return OtherKind.Armour;
        return null;
    }

    private static (int Tab, int Id) OtherGrant(OtherKind k, int value) => k switch
    {
        OtherKind.FrontBall => (1, value - 30000),
        OtherKind.RearBall => (1, value - 31000),
        OtherKind.SidekickBall => (2, value - 32000),
        OtherKind.SpecialBall => (5, value - 32100),
        _ => (-1, -1),
    };

    private static int LaunchedId(int carrierId, in EnemyDat e)
    {
        if (e.ELaunchFreq == 0 || e.ELaunchType == 0) return 0;
        return carrierId > 1000 ? e.ELaunchType : e.ELaunchType % 1000;
    }

    private enum DropVia { Drop, Launch, ArmorShip }

    private readonly record struct OtherSite(int EpisodeIdx, int Episode, int FileNum, string Level, ushort Time);
    private readonly record struct OtherCarrier(int EpisodeIdx, int Episode, int FileNum, string Level, int CarrierId, DropVia Via);

    private sealed class OtherPickup
    {
        public OtherKind Kind;
        public int Value, Bank, Sprite;
        public EnemyDat Rep;
        public int GrantTab = -1, GrantId = -1;
        public readonly List<(int Ep, int Id)> Sources = new();
        public readonly List<OtherSite> Direct = new();
        public readonly List<OtherCarrier> Carriers = new();
        public readonly SortedSet<int> WarpTargets = new();       // secret warp: value-10000 per source
        public readonly Dictionary<(int, int), int> Hits = new(); // source (ep,id) -> appearance count
        private readonly HashSet<(int, int, ushort)> _directSeen = new();
        private readonly HashSet<(int, int, int, DropVia)> _carrierSeen = new();
        public bool Appears => Direct.Count > 0 || Carriers.Count > 0;

        public void AddDirect(OtherSite s, int srcId)
        {
            if (_directSeen.Add((s.EpisodeIdx, s.FileNum, s.Time))) Direct.Add(s);
            Bump(s.EpisodeIdx, srcId);
        }
        public void AddCarrier(OtherCarrier c, int srcEp, int srcId)
        {
            if (_carrierSeen.Add((c.EpisodeIdx, c.FileNum, c.CarrierId, c.Via))) Carriers.Add(c);
            Bump(srcEp, srcId);
        }
        private void Bump(int ep, int id) => Hits[(ep, id)] = Hits.GetValueOrDefault((ep, id)) + 1;
    }

    private List<OtherPickup>? _otherPickups;

    private List<OtherPickup> OtherPickups()
    {
        if (_otherPickups != null) return _otherPickups;
        var list = new List<OtherPickup>();
        if (_gd == null) { _otherPickups = list; return list; }

        var byKey = new Dictionary<(OtherKind, int), OtherPickup>();
        var byId = new Dictionary<(int, int), OtherPickup>();

        for (int e = 0; e < _gd.Episodes.Count; e++)
        {
            EnemyData ed;
            try { ed = _gd.GetEnemyData(_gd.Episodes[e]); } catch { continue; }
            for (int id = 1; id < ed.Enemies.Length; id++)
            {
                var dat = ed.Get(id);
                var kind = ClassifyOther(dat);
                if (kind == null) continue;
                // Singletons fold to one; money/armour/balls stay split by value.
                var key = (kind.Value, IsSingletonKind(kind.Value) ? 0 : dat.Value);
                if (!byKey.TryGetValue(key, out var p))
                {
                    var (gt, gi) = OtherGrant(kind.Value, dat.Value);
                    p = new OtherPickup
                    {
                        Kind = kind.Value, Value = dat.Value, Bank = dat.ShapeBank,
                        Sprite = dat.EGraphic[0], Rep = dat, GrantTab = gt, GrantId = gi,
                    };
                    byKey[key] = p;
                    list.Add(p);
                }
                p.Sources.Add((e, id));
                byId[(e, id)] = p;
                if (kind == OtherKind.SecretWarp) p.WarpTargets.Add(dat.Value - 10000);
            }
        }

        ScanOtherAppearances(byId);
        AddArmorShipDrops(byId);

        // Rep = the table slot that actually shows up (most attributions), so a merged pickup
        // wears the sprite the game draws, not a garbage or unused sibling. The purple ball is
        // the exception: value 30000 is also the galaga Dragonwing ball (a green sprite), and it
        // is the one the events drop, so hits pick it -- take the low-frame classic ball instead.
        foreach (var p in list)
        {
            (int Ep, int Id) best = p.Sources[0];
            if (p.Kind == OtherKind.PurpleBall)
            {
                int bestFrame = int.MaxValue;
                foreach (var s in p.Sources)
                {
                    int f = SourceSprite(s);
                    if (f > 0 && f < bestFrame) { bestFrame = f; best = s; }
                }
            }
            else
            {
                int bestHits = -1;
                foreach (var s in p.Sources)
                {
                    int h = p.Hits.GetValueOrDefault(s);
                    if (h > bestHits) { bestHits = h; best = s; }
                }
            }
            try { p.Rep = _gd.GetEnemyData(_gd.Episodes[best.Ep]).Get(best.Id); p.Bank = p.Rep.ShapeBank; p.Sprite = p.Rep.EGraphic[0]; }
            catch { /* keep the first */ }
        }

        var refItems = _gd.GetItems(_gd.Episodes[0], fork: true);
        list = list.Where(p => KeepPickup(p, refItems))
            .OrderBy(p => (int)p.Kind)
            .ThenByDescending(p => p.Direct.Count + p.Carriers.Count)
            .ThenBy(p => p.Value)
            .ToList();
        _otherPickups = list;
        return list;
    }

    /// <summary>Which pickups survive the sweep. Front/rear/sidekick balls keep the WHOLE grant-valid
    /// pool (the Arcade tab wants every ball, placed or not); special balls only when they actually
    /// turn up (they belong to the Other tab and appear in the normal game); misc keeps the canonical
    /// standalone types plus anything that appears.</summary>
    private bool KeepPickup(OtherPickup p, ItemData refItems)
    {
        if (IsBallKind(p.Kind))
        {
            bool grant = ItemExists(refItems, p.GrantTab, p.GrantId);
            return p.Kind == OtherKind.SpecialBall ? grant && p.Appears : grant;
        }
        return IsSingletonKind(p.Kind) || p.Appears || (p.Kind == OtherKind.Armour && p.Carriers.Count > 0);
    }

    /// <summary>Kinds the Arcade tab owns rather than Other: the purple ball (it turns into the
    /// front-weapon balls) and the front/rear/sidekick balls.</summary>
    private static bool IsArcadeKind(OtherKind k) => k is OtherKind.PurpleBall
        or OtherKind.FrontBall or OtherKind.RearBall or OtherKind.SidekickBall;

    /// <summary>The Other tab's own set: everything the Arcade tab does not take.</summary>
    private List<OtherPickup> OtherTabPickups() =>
        OtherPickups().Where(p => !IsArcadeKind(p.Kind)).ToList();

    /// <summary>The purple ball plus the front / rear / sidekick ball pool, for the Arcade tab.</summary>
    private List<OtherPickup> ArcadeBallPickups() =>
        OtherPickups().Where(p => IsArcadeKind(p.Kind)).OrderBy(p => (int)p.Kind).ThenBy(p => p.Value).ToList();

    private int SourceSprite((int Ep, int Id) s)
    {
        try { return _gd!.GetEnemyData(_gd.Episodes[s.Ep]).Get(s.Id).EGraphic[0]; }
        catch { return 0; }
    }

    /// <summary>Walk every level's spawns, their death/launch chains, and its event-33 retargets,
    /// crediting each pickup to the levels it is placed in and the enemies that drop it.</summary>
    private void ScanOtherAppearances(Dictionary<(int, int), OtherPickup> byId)
    {
        var visited = new HashSet<int>();
        for (int e = 0; e < _gd!.Episodes.Count; e++)
        {
            var ep = _gd.Episodes[e];
            EnemyData ed;
            try { ed = _gd.GetEnemyData(ep); } catch { continue; }
            foreach (var li in ep.Levels)
            {
                Level lv;
                try { lv = _gd.LoadLevel(ep, li.FileNum); } catch { continue; }
                string name = string.IsNullOrWhiteSpace(li.Name) ? "(unnamed)" : li.Name.Trim();

                // linknum -> the enemies spawned under it, with their times.
                var byLink = new Dictionary<int, List<(int Id, ushort Time)>>();
                foreach (var ev in lv.Events)
                {
                    if (ev.Type == 33 || ev.Type is >= 49 and <= 52) continue;
                    if (!ObjectPlacer.IsSpawn(ev.Type, out _, out _) && ev.Type != 12) continue;
                    if (!byLink.TryGetValue(ev.Dat4, out var l)) byLink[ev.Dat4] = l = new List<(int, ushort)>();
                    for (int k = 0; k < (ev.Type == 12 ? 4 : 1); k++)
                        if (ev.Dat + k > 0) l.Add((ev.Dat + k, ev.Time));
                }

                // Direct placement + static enemydie / launch chains.
                foreach (var ev in lv.Events)
                {
                    if (ev.Type is >= 49 and <= 52) continue;
                    if (!ObjectPlacer.IsSpawn(ev.Type, out _, out _) && ev.Type != 12) continue;
                    for (int k = 0; k < (ev.Type == 12 ? 4 : 1); k++)
                    {
                        int root = ev.Dat + k;
                        if (root <= 0) continue;
                        visited.Clear();
                        var queue = new Queue<int>();
                        queue.Enqueue(root);
                        while (queue.Count > 0 && visited.Count < 400)
                        {
                            int cur = queue.Dequeue();
                            if (!visited.Add(cur)) continue;
                            var dc = ed.Get(cur);
                            if (!dc.Loaded) continue;
                            if (cur == root && byId.TryGetValue((e, cur), out var pd0))
                                pd0.AddDirect(new OtherSite(e, ep.Number, li.FileNum, name, ev.Time), cur);
                            int die = dc.EEnemyDie;
                            if (die != 0)
                            {
                                if (byId.TryGetValue((e, die), out var pd))
                                    pd.AddCarrier(new OtherCarrier(e, ep.Number, li.FileNum, name, cur, DropVia.Drop), e, die);
                                queue.Enqueue(die);
                            }
                            int launch = LaunchedId(cur, dc);
                            if (launch != 0)
                            {
                                if (byId.TryGetValue((e, launch), out var pl))
                                    pl.AddCarrier(new OtherCarrier(e, ep.Number, li.FileNum, name, cur, DropVia.Launch), e, launch);
                                queue.Enqueue(launch);
                            }
                        }
                    }
                }

                // Event 33: retarget the formation with linknum Dat4 to drop enemyDat[Dat].
                foreach (var ev in lv.Events)
                {
                    if (ev.Type != 33 || ev.Dat <= 0) continue;
                    if (!byId.TryGetValue((e, ev.Dat), out var pick)) continue;
                    if (!byLink.TryGetValue(ev.Dat4, out var formation)) continue;
                    // The formation live at the retarget: whatever was spawned latest at or before it.
                    var before = formation.Where(c => c.Time <= ev.Time).ToList();
                    var pool = before.Count > 0 ? before : formation;
                    ushort newest = pool.Max(c => c.Time);
                    foreach (var c in pool)
                        if (c.Time >= newest - 300)   // parts of one formation stagger a little
                            pick.AddCarrier(new OtherCarrier(e, ep.Number, li.FileNum, name, c.Id, DropVia.Drop), e, ev.Dat);
                }
            }
        }
    }

    /// <summary>The low-armour rescue ship (#560) drops one of the three armour pickups
    /// (#561-563) at random when it is shot, in any level -- a code path, not level data
    /// (tyrian2.c:3476-3483), so it is stitched in here.</summary>
    private void AddArmorShipDrops(Dictionary<(int, int), OtherPickup> byId)
    {
        for (int e = 0; e < _gd!.Episodes.Count; e++)
        {
            EnemyData ed;
            try { ed = _gd.GetEnemyData(_gd.Episodes[e]); } catch { continue; }
            if (!ed.Get(560).Loaded) continue;
            for (int id = 561; id <= 563; id++)
                if (byId.TryGetValue((e, id), out var p) && p.Kind == OtherKind.Armour)
                    p.AddCarrier(new OtherCarrier(e, _gd.Episodes[e].Number, 0, "any level", 560, DropVia.ArmorShip), e, id);
        }
    }

    /// <summary>The pickup's sprite this instant, cycled at 35Hz off the item clock.</summary>
    private int PickupSprite(in EnemyDat e)
    {
        var run = FrameRun(e, false);
        if (run.Count == 0) return e.EGraphic[0];
        int idx = run.Count <= 1 ? 0 : (int)(((long)_itemClock) % run.Count);
        return e.EGraphic[Math.Clamp(run[idx] - 1, 0, 19)];
    }

    private string OtherGrantName(ItemData items, OtherPickup p) =>
        p.GrantTab >= 0 && ItemExists(items, p.GrantTab, p.GrantId)
            ? ItemRowFor(items, p.GrantTab, p.GrantId).Name.Trim() : "";

    private string OtherRowTitle(ItemData items, OtherPickup p)
    {
        if (p.GrantTab >= 0)
        {
            string g = OtherGrantName(items, p);
            return g.Length > 0 ? g : $"{OtherKindName(p.Kind)} ({p.Value})";
        }
        return p.Kind switch
        {
            OtherKind.Money => $"{p.Value:n0} score",
            OtherKind.Armour => $"+{p.Value - 20000} armour",
            _ => OtherKindName(p.Kind),
        };
    }

    // =====================================================================

    private void DrawOtherList()
    {
        var pickups = OtherTabPickups();
        var items = _gd!.GetItems(CurEpisode!, _itemFork);
        var shown = new HashSet<int>(ShownEpisodes());
        string filter = BufText(_itemFilter).Trim();
        _otherSel = Math.Clamp(_otherSel, 0, Math.Max(0, pickups.Count - 1));

        bool any = false;
        OtherKind? lastKind = null;
        const float rowH = 36f;
        for (int i = 0; i < pickups.Count; i++)
        {
            var p = pickups[i];
            string title = OtherRowTitle(items, p);
            if (!Matches(filter, title, OtherKindName(p.Kind), $"value {p.Value}")) continue;
            any = true;
            if (lastKind != p.Kind)
            {
                UiSection(OtherKindName(p.Kind), AcShop);
                lastKind = p.Kind;
            }

            bool sel = i == _otherSel;
            var box = UiRow($"##oth{i}", sel, AcShop, rowH);
            if (box.Clicked) _otherSel = i;
            if (sel && _otherScrollToSelection) { ImGui.SetScrollHereY(0.4f); _otherScrollToSelection = false; }

            var dl = ImGui.GetWindowDrawList();
            var atlas = Atlas(EnemySpriteSource(p.Rep.ShapeBank), AppSettings.GamePalette);
            if (atlas != null)
                DrawEnemyFrameCentered(dl, atlas, PickupSprite(p.Rep), p.Rep.Esize == 1,
                    new Vector2(box.Min.X + 7f, box.Min.Y + 2f), new Vector2(box.Min.X + 45f, box.Max.Y - 2f), 1f);

            int lvls = p.Direct.Select(s => (s.EpisodeIdx, s.FileNum))
                .Concat(p.Carriers.Where(c => c.FileNum > 0).Select(c => (c.EpisodeIdx, c.FileNum)))
                .Where(s => shown.Contains(s.EpisodeIdx)).Distinct().Count();
            int droppers = p.Carriers.Where(c => shown.Contains(c.EpisodeIdx)).Select(c => (c.EpisodeIdx, c.CarrierId)).Distinct().Count();
            string sub = droppers > 0 ? $"{lvls} level{(lvls == 1 ? "" : "s")}  ·  {droppers} dropper{(droppers == 1 ? "" : "s")}"
                : $"{lvls} level{(lvls == 1 ? "" : "s")}";

            float lh = ImGui.GetTextLineHeight();
            float top = box.Min.Y + (rowH - 3f - lh * 2f - 1f) * 0.5f;
            float room = box.Max.X - box.Min.X - 60f;
            ClipText(dl, new Vector2(box.Min.X + 50f, top), room, sel ? Gfx.Rgba(250, 252, 255) : UiText, title);
            ClipText(dl, new Vector2(box.Min.X + 50f, top + lh + 1f), room, Shade(AcShop, 1f, 190), sub);
        }
        if (!any) ImGui.TextDisabled(pickups.Count == 0 ? "No field pickups found." : "Nothing matches.");
    }

    private void DrawOtherDetail()
    {
        var pickups = OtherTabPickups();
        if (pickups.Count == 0) { UiEmpty("No field pickups", "Powerups, datacubes, money and the rest.", AcShop); return; }
        _otherSel = Math.Clamp(_otherSel, 0, pickups.Count - 1);
        var p = pickups[_otherSel];
        var items = _gd!.GetItems(CurEpisode!, _itemFork);

        var dl = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        const float box = 116f;
        Well(dl, at, at + new Vector2(box, box), AcShop, 6f);
        var atlas = Atlas(EnemySpriteSource(p.Rep.ShapeBank), AppSettings.GamePalette);
        if (atlas != null)
            DrawEnemyFrameCentered(dl, atlas, PickupSprite(p.Rep), p.Rep.Esize == 1, at, at + new Vector2(box, box), 3f);
        ImGui.Dummy(new Vector2(box, box));
        ImGui.SameLine(0, 12);

        ImGui.BeginGroup();
        UiTitle(OtherRowTitle(items, p), AcShop);
        Badge(OtherKindName(p.Kind), AcShop);
        ImGui.SameLine(0, 5f);
        Badge($"value {p.Value}", AcItem);
        ImGui.Dummy(new Vector2(0, 4f));
        UiToggle("animate", ref _itemAnimate, AcShop, "Run the pickup's frames at the engine's 35Hz.");
        ImGui.SameLine(0, 5);
        ImGui.SetNextItemWidth(96);
        ImGui.SliderFloat("##otherspeed", ref _itemAnimSpeed, 0.1f, 3f, "x%.2f");
        ImGui.EndGroup();

        ImGui.Dummy(new Vector2(0, 4f));
        WellBegin("otherbody", ImGui.GetContentRegionAvail(), AcShop, 12f, 9f);

        UiSection("what it is", AcShop);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(ColorOf(UiText), OtherKindDesc(p.Kind));
        ImGui.PopTextWrapPos();

        if (p.GrantTab >= 0 && ItemExists(items, p.GrantTab, p.GrantId))
        {
            string g = OtherGrantName(items, p);
            if (UiButton($"grants: {g}", AcItem, "open the item this ball swaps in"))
                ShowItemTab(p.GrantTab, p.GrantId);
        }
        if (p.Kind == OtherKind.SecretWarp && p.WarpTargets.Count > 0)
            KV("warps to", "secret level" + (p.WarpTargets.Count == 1 ? " " : "s ") + string.Join(", ", p.WarpTargets), 0, 84f);
        KV("sprite", $"bank {p.Rep.ShapeBank}, frame {p.Sprite}" + (p.Rep.Ani > 1 ? $"  ({p.Rep.Ani} frames)" : ""), 0, 84f);

        DrawOtherDroppers(p);
        DrawOtherDirect(p);
        WellEnd();
    }

    private void DrawOtherDroppers(OtherPickup p)
    {
        var shown = new HashSet<int>(ShownEpisodes());
        var groups = p.Carriers.Where(c => shown.Contains(c.EpisodeIdx))
            .GroupBy(c => (c.EpisodeIdx, c.CarrierId))
            .Select(g => (g.Key.EpisodeIdx, g.Key.CarrierId, Episode: g.First().Episode,
                Launch: g.Any(c => c.Via == DropVia.Launch), ArmorShip: g.Any(c => c.Via == DropVia.ArmorShip),
                Levels: g.Where(c => c.FileNum > 0).Select(c => c.Level).Distinct().ToList()))
            .OrderBy(g => g.Episode).ThenBy(g => g.CarrierId)
            .ToList();

        ImGui.Dummy(new Vector2(0, 4f));
        UiSection("dropped or launched by", AcEnemy, groups.Count == 0 ? "none"
            : $"{groups.Count} enem{(groups.Count == 1 ? "y" : "ies")}");
        if (groups.Count == 0)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorOf(UiFaint), p.Kind is OtherKind.PurpleBall or OtherKind.FrontPower or OtherKind.RearPower
                ? "Handed out in play rather than left by any one enemy."
                : p.GrantTab >= 0 ? "This ball is placed in the field, not dropped by an enemy."
                : "No enemy leaves or launches this one.");
            ImGui.PopTextWrapPos();
            return;
        }

        const float rowH = 30f;
        int shownRows = 0;
        foreach (var g in groups)
        {
            if (shownRows >= MaxOutpostRows)
            {
                ImGui.TextColored(ColorOf(UiFaint), $"   +{groups.Count - shownRows} more");
                break;
            }
            shownRows++;
            var b = UiRow($"##drp{g.EpisodeIdx}_{g.CarrierId}", false, AcEnemy, rowH);
            if (b.Clicked) OpenEnemy(g.EpisodeIdx, g.CarrierId);
            if (b.Hovered) ImGui.SetTooltip("open this enemy -- its own page shows every level it flies in");
            string epTag = _allEpisodes ? $"  ·  Ep {g.Episode}" : "";
            string title = g.ArmorShip ? $"the rescue ship  #{g.CarrierId}" : $"enemy #{g.CarrierId}";
            string sub = g.ArmorShip ? "flies in when your hull is low"
                : (g.Launch ? "launches it" : "drops it on death") +
                  (g.Levels.Count > 0 ? "  ·  " + (g.Levels.Count == 1 ? g.Levels[0] : $"{g.Levels.Count} levels") : "");
            RowText(b, 10f, title, sub + epTag, AcEnemy, false, 14f);
        }
    }

    private void DrawOtherDirect(OtherPickup p)
    {
        var shown = new HashSet<int>(ShownEpisodes());
        var groups = p.Direct.Where(s => shown.Contains(s.EpisodeIdx))
            .GroupBy(s => (s.EpisodeIdx, s.FileNum))
            .Select(g => (g.First().EpisodeIdx, g.First().Episode, g.First().FileNum, g.First().Level,
                Time: g.Min(s => s.Time), Count: g.Count()))
            .OrderBy(g => g.Episode).ThenBy(g => g.Time)
            .ToList();

        ImGui.Dummy(new Vector2(0, 4f));
        UiSection("placed directly in", AcRoutes, groups.Count == 0 ? "none"
            : $"{groups.Count} level{(groups.Count == 1 ? "" : "s")}");
        if (groups.Count == 0)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorOf(UiFaint), p.GrantTab >= 0
                ? "No level places this ball -- it is an arcade-mode entry."
                : p.Carriers.Count > 0 ? "Not placed on its own -- only dropped or launched."
                : "Defined in the data, but no level in the campaign places or drops it -- an unused entry.");
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
                ImGui.TextColored(ColorOf(UiFaint), $"   +{groups.Count - shownRows} more");
                break;
            }
            shownRows++;
            var b = UiRow($"##odir{g.EpisodeIdx}_{g.FileNum}", false, AcRoutes, rowH);
            if (b.Clicked) { SelectLevelFile(g.EpisodeIdx, g.FileNum); _pendingJump = new MapJump(g.Time, Array.Empty<int>()); }
            if (b.Hovered) ImGui.SetTooltip("open this level at the frame it flies in");
            var dl = ImGui.GetWindowDrawList();
            float barW = Math.Max(24f, (b.Max.X - b.Min.X) * 0.22f);
            string trail = g.Count > 1 ? $"x{g.Count}" : "";
            string epTag = _allEpisodes ? $"  ·  Ep {g.Episode}" : "";
            RowText(b, 10f, $"{g.Level}  #{g.FileNum:00}", $"first at t={g.Time}{epTag}",
                AcRoutes, false, barW + TrailRoom(trail) + 12f);
            var bar = new Vector2(b.Max.X - barW - TrailRoom(trail), b.Min.Y + rowH * 0.5f - 5f);
            MeterBar(dl, bar, bar + new Vector2(barW, 7f), g.Count / (float)most, AcRoutes);
            if (trail.Length > 0) RowTrail(b, trail, Shade(AcRoutes, 1.1f));
        }
    }
}
