using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The three other ways a special weapon reaches a ship, spelled out under a special's detail:
///
/// * <b>Twiddle codes</b> -- the "street fighter" input combos (keyboardCombos, varz.c:123) that
///   fire a special without it being equipped. Each ship carries up to three of them
///   (shipCombos, varz.c:158); the combo costs shield or armour when it goes off
///   (varz.c:809-845, the special's own <c>pwr</c>).
///
/// * <b>Random enemy drops</b> -- event 33 with dat 533 retargets a linked formation's death
///   drop to a random member of the six-strong pool enemyDat 829..834 (Specials 1..6), on a
///   chance that climbs with the run's <c>lives</c> counter (tyrian2.c:6734-6737). So killing
///   that formation can leave any of those six behind.
///
/// * <b>Super-Arcade ship loadouts</b> -- each arcade ship comes fitted with one special from
///   SASpecialWeapon / SASpecialWeaponB (varz.c:47-49), and a front-weapon pickup in that mode
///   also swaps in a fixed special (specialArcadeWeapon, varz.c:63).
///
/// The twiddle table is also read the other way round, under a hull's stats in the Ships tab:
/// <see cref="DrawShipTwiddleBlock"/> lists the up-to-three codes that ship can fly, with the
/// special each one fires and what it costs. Ships and specials therefore point at each other.
///
/// All of it is drawn with the same rounded rows as the rest of the browser, and every level or
/// enemy named is a click through to it.
/// </summary>
public sealed unsafe partial class App
{
    // ---- ported tables (varz.c) ----------------------------------------

    /// <summary>keyboardCombos[26][8] (varz.c:123): a direction/fire sequence (codes 1..9, see
    /// <see cref="TwiddleDir"/>) ending in the special id + 100 the twiddle fires.</summary>
    private static readonly byte[][] KeyboardCombos =
    {
        new byte[] { 2, 1, 2, 5, 137 },      // Invulnerability
        new byte[] { 4, 3, 2, 5, 138 },      // Atom Bomb
        new byte[] { 3, 4, 6, 139 },         // Seeker Bombs
        new byte[] { 2, 5, 142 },            // Ice Blast
        new byte[] { 6, 2, 6, 143 },         // Auto Repair
        new byte[] { 6, 7, 5, 8, 6, 7, 5, 112 }, // Spin Wave
        new byte[] { 7, 8, 101 },            // Repulsor
        new byte[] { 1, 7, 6, 146 },         // Protron Field
        new byte[] { 8, 6, 7, 1, 120 },      // Minefield
        new byte[] { 3, 6, 8, 5, 121 },      // Post-It Blast
        new byte[] { 1, 2, 7, 8, 119 },      // Drone Ship
        new byte[] { 3, 4, 3, 6, 123 },      // Repair Player 2
        new byte[] { 6, 7, 5, 8, 124 },      // Super Bomb
        new byte[] { 1, 6, 125 },            // Hot Dog
        new byte[] { 9, 5, 126 },            // Lightning UP
        new byte[] { 1, 7, 127 },            // Lightning UP+LEFT
        new byte[] { 1, 8, 128 },            // Lightning UP+RIGHT
        new byte[] { 9, 7, 129 },            // Lightning LEFT
        new byte[] { 9, 8, 130 },            // Lightning RIGHT
        new byte[] { 4, 2, 3, 5, 131 },      // Warfly
        new byte[] { 3, 1, 2, 8, 132 },      // FrontBlaster
        new byte[] { 2, 4, 5, 133 },         // Gerund
        new byte[] { 3, 4, 2, 8, 134 },      // FireBomb
        new byte[] { 1, 4, 6, 135 },         // Indigo
        new byte[] { 1, 3, 6, 137 },         // Invulnerability (easier)
        new byte[] { 1, 4, 3, 4, 7, 136 },   // D-Media Protron Drone
    };

    /// <summary>shipCombos[19][3] (varz.c:158): up to three 1-based <see cref="KeyboardCombos"/>
    /// rows each ship can perform. Indexed by ship id (the Ships table index); 0 = no combo.
    /// Rows past <see cref="TwiddleShipMax"/> are never read -- see there.</summary>
    private static readonly byte[][] ShipCombos =
    {
        new byte[] { 5, 4, 7 },   // 0 (2nd Player ship)
        new byte[] { 1, 2, 0 },   // 1 USP Talon
        new byte[] { 14, 4, 0 },  // 2 Super Carrot
        new byte[] { 4, 5, 0 },   // 3 Gencore Phoenix
        new byte[] { 6, 5, 0 },   // 4 Gencore Maelstrom
        new byte[] { 7, 8, 0 },   // 5 MicroCorp Stalker
        new byte[] { 7, 9, 0 },   // 6 MicroCorp Stalker-B
        new byte[] { 10, 3, 5 },  // 7 Prototype Stalker-C
        new byte[] { 5, 8, 9 },   // 8 Stalker
        new byte[] { 1, 3, 0 },   // 9 USP Fang
        new byte[] { 7, 16, 17 }, // 10 U-Ship
        new byte[] { 2, 11, 12 }, // 11 (1st Player ship)
        new byte[] { 3, 8, 10 },  // 12 Nort ship
        new byte[] { 0, 0, 0 },   // 13 Stalker 21.126 (dummy)
        new byte[] { 1, 0, 0 },   // 14 Storm
        new byte[] { 4, 0, 0 },   // 15 Red Dragon
        new byte[] { 5, 9, 2 },   // 16 Gencore II
        new byte[] { 0, 0, 0 },   // 17 PeteZoomer
        new byte[] { 0, 0, 0 },   // 18 Rum Bottle
    };

    /// <summary>shipCombosB[21] (varz.c:153): Super Tyrian mode swaps a ship's own three combos for
    /// these 21 (mainint.c:4718-4723). It always flies <see cref="SuperTyrianShip"/>, so in practice
    /// these are the Stalker 21.126's move list rather than "every ship's".</summary>
    private static readonly byte[] ShipCombosB =
        { 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 7, 8, 5, 25, 14, 4, 6, 3, 9, 2, 26 };

    /// <summary>The ship Super Tyrian always puts you in: The Stalker 21.126, both for the ']e'
    /// levels (tyrian2.c:4262) and for the ENGAGE title code (tyrian2.c:5540). Its own shipCombos
    /// row is empty -- the 21 <see cref="ShipCombosB"/> twiddles ARE what it can do.</summary>
    private const int SuperTyrianShip = 13;

    /// <summary>The ship ids the twiddle reader looks at combos for at all. JE_SFCodes returns
    /// before it reads a single key when <c>ship >= 15</c> (mainint.c:4680-4687), so the last four
    /// hulls cannot twiddle whatever <see cref="ShipCombos"/> holds for them -- and it holds rows
    /// for two of them. The same guard folds player 2 onto row 0 (the "2nd Player ship" row)
    /// whichever hull they are actually flying.</summary>
    private const int TwiddleShipMax = 15;

    /// <summary>The shipCombos row player 2 is folded onto, whatever they fly.</summary>
    private const int PlayerTwoCombos = 0;

    // Super-Arcade (SA = 9): the arcade ships and the special each carries (varz.c:46-61).
    private static readonly byte[] SAShip = { 3, 1, 5, 10, 2, 11, 12, 15, 17 };
    private static readonly ushort[] SASpecialWeapon = { 7, 8, 9, 10, 11, 12, 13, 48, 47 };
    private static readonly ushort[] SASpecialWeaponB = { 37, 6, 15, 40, 16, 14, 41, 48, 47 };

    /// <summary>SAWeapon[9][5] (varz.c:50): the five front weapons each arcade ship gets from the
    /// Red/Blue/Black/Green/Purple balls in turn.</summary>
    private static readonly ushort[,] SAWeapon =
    {
        {  9, 31, 32, 33, 34 },  // Stealth Ship
        { 19,  8, 22, 41, 34 },  // StormWind
        { 27,  5, 20, 42, 31 },  // Techno
        { 15,  3, 28, 22, 12 },  // Enemy
        { 23, 35, 25, 14,  6 },  // Weird
        {  2,  5, 21,  4,  7 },  // Unknown
        { 40, 38, 37, 41, 36 },  // NortShip Z
        { 47, 45, 19, 33, 19 },  // Dragon
        { 44, 26, 46, 26,  1 },  // Pretzel Pete
    };

    /// <summary>specialArcadeWeapon[42] (varz.c:63): the special a front-weapon pickup swaps in
    /// during arcade play, indexed by front-port id - 1. 0 = none.</summary>
    private static readonly byte[] SpecialArcadeWeapon =
    {
        17, 17, 18, 0, 0, 0, 10, 0, 0, 0, 0, 0, 44, 0, 10, 0, 19, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 45, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    };

    /// <summary>The six-strong random-drop pool, enemyDat 829..834 = Specials 1..6.</summary>
    private const int DropPoolLo = 1, DropPoolHi = 6;

    // ---- twiddle helpers ----------------------------------------------

    private static string TwiddleDir(int code) => code switch
    {
        1 => "Up", 2 => "Down", 3 => "Left", 4 => "Right",
        5 => "Up+Fire", 6 => "Down+Fire", 7 => "Left+Fire", 8 => "Right+Fire",
        9 => "release", _ => $"?{code}",
    };

    /// <summary>The special id a keyboardCombos row triggers (its entry &gt;100), or 0.</summary>
    private static int TwiddleSpecialId(byte[] combo)
    {
        foreach (byte v in combo)
            if (v > 100) return v - 100;
        return 0;
    }

    /// <summary>The direction sequence of a combo, up to its special-id terminator.</summary>
    private static string TwiddleSequence(byte[] combo)
    {
        var parts = new List<string>();
        foreach (byte v in combo)
        {
            if (v > 100 || v == 0) break;
            parts.Add(TwiddleDir(v));
        }
        return string.Join("  ", parts);
    }

    /// <summary>What a twiddle costs when it fires: the special's <c>pwr</c> read the way the
    /// fire code reads it (varz.c:816-844) -- shield, or armour past 100.</summary>
    private static string TwiddleCost(int pwr) => pwr switch
    {
        0 => "free",
        98 => "all shield",
        99 => "half shield",
        < 98 => $"{pwr} shield",
        _ => $"{pwr - 100} armour",
    };

    /// <summary>Every combo that fires this special: the row, the ships whose own three include it,
    /// and whether it is also one of the 21 Super Tyrian combos (see <see cref="SuperTyrianLevels"/>
    /// for where those actually go off).</summary>
    private List<(byte[] Combo, List<int> ShipIds, bool SuperTyrian)> TwiddlesForSpecial(int specialId)
    {
        var result = new List<(byte[], List<int>, bool)>();
        if (specialId <= 0) return result;
        for (int r = 0; r < KeyboardCombos.Length; r++)
        {
            if (TwiddleSpecialId(KeyboardCombos[r]) != specialId) continue;
            int comboId = r + 1;   // shipCombos / shipCombosB store 1-based rows
            var ships = new List<int>();
            for (int s = 1; s < ShipCombos.Length; s++)   // skip 0 = the 2nd-player default
                if (Array.IndexOf(ShipCombos[s], (byte)comboId) >= 0) ships.Add(s);
            bool superT = Array.IndexOf(ShipCombosB, (byte)comboId) >= 0;
            result.Add((KeyboardCombos[r], ships, superT));
        }
        return result;
    }

    /// <summary>The other way round: the <see cref="KeyboardCombos"/> rows one hull can perform --
    /// its own up-to-three (<see cref="ShipCombos"/>), or the 21 <see cref="ShipCombosB"/> for the
    /// Stalker 21.126, whose own row is empty because Super Tyrian's list is what it flies with.
    /// Says nothing about whether the reader ever gets there; that is <see cref="TwiddleShipMax"/>.
    /// </summary>
    private static List<byte[]> TwiddlesForShip(int shipId)
    {
        byte[] combos = shipId == SuperTyrianShip ? ShipCombosB
            : shipId >= 0 && shipId < ShipCombos.Length ? ShipCombos[shipId]
            : Array.Empty<byte>();
        var rows = new List<byte[]>();
        foreach (byte c in combos)
            if (c > 0 && c <= KeyboardCombos.Length) rows.Add(KeyboardCombos[c - 1]);
        return rows;
    }

    /// <summary>Whether one of player 2's three fires this special. They are folded onto
    /// shipCombos row 0 whatever hull they picked, so their move list never changes.</summary>
    private static bool PlayerTwoTwiddles(int specialId) =>
        ShipCombos[PlayerTwoCombos].Any(c => c > 0 && TwiddleSpecialId(KeyboardCombos[c - 1]) == specialId);

    private static string ShipName(ItemData d, int id)
    {
        string n = id >= 0 && id < d.Ships.Length && d.Ships[id] != null ? d.Ships[id].Name.Trim() : "";
        return n.Length > 0 ? n : $"ship {id}";
    }

    private static bool SpecialExists(ItemData d, int id) =>
        id > 0 && id < d.Specials.Length && d.Specials[id] != null;

    /// <summary>Whether a special has a shop icon at all. Most of the twiddle-only ones have
    /// none (they are never on a shelf to need one), and a column of empty gutters reads as
    /// broken art rather than as "no icon" -- so a list of them drops the gutter instead.
    /// The test is <see cref="DrawEnemyFrameCentered"/>'s own.</summary>
    private static bool SpecialHasIcon(ItemData d, int id) =>
        SpecialExists(d, id) && d.Specials[id].ItemGraphic is > 0 and not 999;

    private static string SpecialName(ItemData d, int id)
    {
        string n = SpecialExists(d, id) ? d.Specials[id].Name.Trim() : "";
        return n.Length > 0 ? n : $"special {id}";
    }

    /// <summary>A twiddle's cost line, or "free" left as it reads.</summary>
    private static string TwiddleCostText(ItemData d, int specialId)
    {
        if (!SpecialExists(d, specialId)) return "cost unknown";
        string cost = TwiddleCost(d.Specials[specialId].Pwr);
        return cost == "free" ? "free" : $"costs {cost}";
    }

    /// <summary>The campaign levels a shipCombosB twiddle can actually go off in. The flow graph's
    /// ']e' flag (engageMode, which turns superTyrian on) marks three -- ** ALE **, TIME WAR and
    /// SQUADRON -- but the other two carry ']g' as well, and galaga mode skips JE_doSpecialShot
    /// outright (mainint.c:7234), so nothing a twiddle arms there ever fires. That leaves TIME WAR.
    /// Past the campaign, Super Tyrian is a whole-game mode -- the ENGAGE title code, unlocked by
    /// beating TIME WAR (newSuperTyrianGame, tyrian2.c:5497) -- and every level plays that way.</summary>
    private List<(int EpisodeIdx, int FileNum, string Name)> SuperTyrianLevels()
    {
        var levels = new List<(int, int, string)>();
        if (_gd == null) return levels;
        for (int e = 0; e < _gd.Episodes.Count; e++)
        {
            var g = _gd.GetGraph(_gd.Episodes[e]);
            if (g == null) continue;
            foreach (var n in g.Nodes)
                if (n.Engage && !n.Galaga && n.Kind == GraphNodeKind.Level &&
                    !levels.Any(l => l.Item1 == e && l.Item2 == n.LvlFileNum))
                    levels.Add((e, n.LvlFileNum, n.Title.Trim()));
        }
        return levels;
    }

    /// <summary>The arcade ships fitted with this special, and whether it is the base loadout or
    /// the mode-B (super-arcade) one.</summary>
    private List<(int ShipId, bool ModeB)> ArcadeShipsForSpecial(int specialId)
    {
        var result = new List<(int, bool)>();
        for (int i = 0; i < SAShip.Length; i++)
        {
            if (SASpecialWeapon[i] == specialId) result.Add((SAShip[i], false));
            else if (SASpecialWeaponB[i] == specialId) result.Add((SAShip[i], true));
        }
        return result;
    }

    /// <summary>The front-weapon ports whose arcade pickup swaps in this special.</summary>
    private List<int> ArcadeFrontPortsForSpecial(int specialId)
    {
        var ports = new List<int>();
        for (int p = 0; p < SpecialArcadeWeapon.Length; p++)
            if (SpecialArcadeWeapon[p] == specialId) ports.Add(p + 1);   // 1-based port id
        return ports;
    }

    // ---- random enemy-drop index --------------------------------------

    /// <summary>One place the random-special pool can drop: the formation event 33 (dat 533)
    /// retargets, in a level, at a time. The carriers share the event's linknum, so seeking to
    /// the moment lands on them; the pickiest of them (most armour) names the row.</summary>
    private readonly record struct DropSite(int EpisodeIdx, int Episode, int FileNum, string Level,
        ushort Time, int LinkNum, int BossId, int[] Carriers);

    private List<DropSite>? _specialDropSites;

    /// <summary>Every event-33/dat-533 drop across the shown game -- the same list serves all
    /// six pool specials, since the drop picks one of them at random.</summary>
    private List<DropSite> SpecialDropSites()
    {
        if (_specialDropSites != null) return _specialDropSites;
        var sites = new List<DropSite>();
        if (_gd == null) { _specialDropSites = sites; return sites; }

        for (int e = 0; e < _gd.Episodes.Count; e++)
        {
            var ep = _gd.Episodes[e];
            EnemyData ed;
            try { ed = _gd.GetEnemyData(ep); } catch { continue; }
            foreach (var li in ep.Levels)
            {
                Level lv;
                try { lv = _gd.LoadLevel(ep, li.FileNum); } catch { continue; }
                string name = string.IsNullOrWhiteSpace(li.Name) ? "(unnamed)" : li.Name.Trim();

                foreach (var ev in lv.Events)
                {
                    if (ev.Type != 33 || ev.Dat != 533) continue;   // 533 is the normal-mode pool
                    int link = ev.Dat4;
                    // Enemies given this linknum by a spawn event are the ones whose death is
                    // retargeted (GameSim sets e.linknum = ev.Dat4).
                    var carriers = new List<int>();
                    foreach (var sp in lv.Events)
                    {
                        if (sp.Type == 33 || sp.Dat4 != link) continue;
                        if (!ObjectPlacer.IsSpawn(sp.Type, out _, out _) && sp.Type != 12) continue;
                        for (int k = 0; k < (sp.Type == 12 ? 4 : 1); k++)
                            if (sp.Dat + k > 0 && !carriers.Contains(sp.Dat + k)) carriers.Add(sp.Dat + k);
                    }
                    if (carriers.Count == 0) continue;
                    // The boss part -- most armour -- names the row; ties keep the first.
                    int boss = carriers[0];
                    foreach (int c in carriers) if (ed.Get(c).Armor > ed.Get(boss).Armor) boss = c;
                    sites.Add(new DropSite(e, ep.Number, li.FileNum, name, ev.Time, link,
                        boss, carriers.ToArray()));
                }
            }
        }
        _specialDropSites = sites;
        return sites;
    }

    // ---- UI ------------------------------------------------------------

    private static readonly uint AcTwiddle = Gfx.Rgba(120, 210, 250);   // cyan, the player's input
    private static readonly uint AcArcade = Gfx.Rgba(185, 150, 255);    // violet, the arcade modes

    /// <summary>The three extra routes to a special, under its found-in block. Every ship named
    /// opens in the Ships tab; every drop opens its level at the moment the drop is armed.</summary>
    private void DrawSpecialExtras(ItemData d, int specialId)
    {
        if (specialId <= 0 || specialId >= d.Specials.Length) return;

        if (specialId is >= DropPoolLo and <= DropPoolHi)
        {
            ImGui.Dummy(new Vector2(0, 4f));
            DrawSpecialDropBlock();
        }

        var twiddles = TwiddlesForSpecial(specialId);
        if (twiddles.Any(t => t.ShipIds.Count > 0 || t.SuperTyrian))
        {
            ImGui.Dummy(new Vector2(0, 4f));
            DrawTwiddleBlock(d, specialId, twiddles, TwiddleCost(d.Specials[specialId].Pwr));
        }

        var arcade = ArcadeShipsForSpecial(specialId);
        var frontPorts = ArcadeFrontPortsForSpecial(specialId);
        if (arcade.Count > 0 || frontPorts.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4f));
            DrawArcadeBlock(d, arcade, frontPorts);
        }
    }

    private void DrawSpecialDropBlock()
    {
        var shown = new HashSet<int>(ShownEpisodes());
        var sites = SpecialDropSites().Where(s => shown.Contains(s.EpisodeIdx))
            .OrderBy(s => s.Episode).ThenBy(s => s.FileNum).ThenBy(s => s.Time).ToList();

        UiSection("randomly dropped by enemies", AcEnemy, sites.Count == 0 ? "none"
            : $"{sites.Count} drop{(sites.Count == 1 ? "" : "s")}");

        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(ColorOf(UiFaint),
            "One of Specials 1-6 at random when the marked formation is destroyed. The chance climbs " +
            "with the run's lives counter -- nil at 0, certain at 11.");
        ImGui.PopTextWrapPos();
        if (sites.Count == 0) return;

        const float rowH = 34f;
        BeginRowScroll("##specdroplist", sites.Count, rowH, reserve: 110f);   // twiddles + arcade follow
        foreach (var s in sites)
        {
            var box = UiRow($"##drop{s.EpisodeIdx}_{s.FileNum}_{s.Time}_{s.LinkNum}", false, AcEnemy, rowH);
            if (box.Clicked)
            {
                SelectLevelFile(s.EpisodeIdx, s.FileNum);
                _pendingJump = new MapJump(s.Time, s.Carriers);
            }
            if (box.Hovered) ImGui.SetTooltip("open this level at the frame the drop is armed\n(the formation is on screen then)");
            // The formation's biggest part, so the row shows what to shoot rather than just its id.
            DrawEnemyThumb(ImGui.GetWindowDrawList(), s.EpisodeIdx, s.BossId,
                new Vector2(box.Min.X + 7f, box.Min.Y + 1f), new Vector2(box.Min.X + 47f, box.Max.Y - 1f));
            string epTag = _allEpisodes ? $"  ·  Ep {s.Episode}" : "";
            RowText(box, 52f, $"{s.Level}  #{s.FileNum:00}",
                $"enemy #{s.BossId}  ·  link {s.LinkNum}  ·  armed at t={s.Time}{epTag}", AcEnemy, false, 14f);
        }
        ImGui.EndChild();
    }

    private void DrawTwiddleBlock(ItemData d, int specialId,
        List<(byte[] Combo, List<int> ShipIds, bool SuperTyrian)> twiddles, string cost)
    {
        UiSection("twiddle codes", AcTwiddle, $"costs {cost}");
        var engage = SuperTyrianLevels();
        string stalker = SuperTyrianShip < d.Ships.Length ? d.Ships[SuperTyrianShip].Name.Trim() : "";
        if (stalker.Length == 0) stalker = "The Stalker 21.126";
        // The one campaign level it works in, then the mode at large.
        string where = engage.Count > 0
            ? string.Join(", ", engage.Select(l => l.Name)) + ", any level in Super Tyrian mode"
            : "any level in Super Tyrian mode";

        const float rowH = 34f;
        foreach (var (combo, ships, superT) in twiddles)
        {
            string seq = TwiddleSequence(combo);
            foreach (int shipId in ships)
            {
                // The table gives two hulls past the reader's cut-off a row apiece. Naming them
                // without saying so would send you off to fly a combo that cannot go off.
                bool dead = shipId >= TwiddleShipMax;
                uint ac = dead ? UiDim : AcTwiddle;
                var box = UiRow($"##tw{shipId}_{seq.GetHashCode()}", false, ac, rowH);
                if (box.Clicked) ShowItemTab(0, shipId);
                if (box.Hovered) ImGui.SetTooltip(dead
                    ? "shipCombos gives this hull the combo, but the reader gives up on any ship\n" +
                      "past #14 before it looks at a key -- it can never fire\nopens the ship in the Ships tab"
                    : "open this ship in the Ships tab");
                string trail = dead ? "never fires" : "";
                RowText(box, 10f, ShipName(d, shipId), seq, ac, false,
                    dead ? TrailRoom(trail) + 14f : 14f);
                if (dead) RowTrail(box, trail, Shade(ac, 0.95f));
            }
            // Super Tyrian swaps whatever ship's three combos for these 21 -- and it always flies
            // the Stalker 21.126, so the combo belongs to that ship there, not to "any ship".
            if (superT)
            {
                var box = UiRow($"##twst{seq.GetHashCode()}", false, AcGo, rowH);
                if (box.Clicked) ShowItemTab(0, SuperTyrianShip);
                if (box.Hovered) ImGui.SetTooltip(
                    "Super Tyrian (ENGAGE mode) flies the Stalker 21.126 with all 21 combos\n" +
                    (engage.Count > 0 ? $"in-campaign that is {engage[0].Name}; " : "") +
                    "the ENGAGE title code plays the whole game that way\nopens the ship in the Ships tab");
                RowText(box, 10f, $"{stalker}  ·  Super Tyrian", $"{seq}   ·   {where}", AcGo, false, 14f);
            }
        }

        // The rows above are hulls, and player 2 never flies their list -- so a special on the
        // 2nd-player row is one player 2 always has, on whatever they picked in the shop.
        if (PlayerTwoTwiddles(specialId))
        {
            ImGui.Dummy(new Vector2(0, 3f));
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorOf(UiFaint),
                "Player 2 can twiddle it whatever they fly: the reader folds them onto the 2nd-player " +
                "row, which is the same three combos for every hull.");
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>
    /// The same table read from the ship's side, under a hull's stats: the up-to-three twiddles it
    /// can fly, each naming the special it fires and what that costs. Three hulls are not the plain
    /// case -- the Stalker 21.126 borrows Super Tyrian's 21, ships past #14 are cut off by the
    /// reader's own guard, and row 0 is player 2's list rather than a hull's.
    /// </summary>
    private void DrawShipTwiddleBlock(ItemData d, int shipId)
    {
        var rows = TwiddlesForShip(shipId);
        bool superT = shipId == SuperTyrianShip;
        bool unreachable = shipId >= TwiddleShipMax;
        bool p2 = shipId == PlayerTwoCombos;
        uint ac = unreachable ? UiDim : AcTwiddle;

        ImGui.Dummy(new Vector2(0, 4f));
        UiSection("twiddle codes", ac, unreachable || rows.Count == 0 ? "none"
            : $"{rows.Count} code{(rows.Count == 1 ? "" : "s")}");

        ImGui.PushTextWrapPos(0f);
        if (unreachable)
            ImGui.TextColored(ColorOf(UiFaint), rows.Count > 0
                ? "The reader gives up on any ship past #14 before it looks at a single key, so this hull " +
                  "cannot twiddle at all -- the rows shipCombos still carries for it are dead data, listed " +
                  "here as that and nothing more."
                : "The reader gives up on any ship past #14 before it looks at a single key, and shipCombos " +
                  "has nothing for this hull either. It cannot twiddle.");
        else if (p2)
            ImGui.TextColored(ColorOf(UiFaint),
                "This row of shipCombos is not a hull anyone buys: player 2 is folded onto it whatever ship " +
                "they are flying, so these three are player 2's whole move list for the game.");
        else if (superT)
            ImGui.TextColored(ColorOf(UiFaint),
                "Its own shipCombos row is empty. This is the ship Super Tyrian always puts you in, and " +
                "Super Tyrian swaps any hull's three combos for these 21 -- so this list is what it flies " +
                "with, and the only place the 21 are ever reachable.");
        else if (rows.Count == 0)
            ImGui.TextColored(ColorOf(UiFaint),
                "shipCombos has no rows for this hull, so it fires only the special it has equipped.");
        else
            ImGui.TextColored(ColorOf(UiFaint),
                "Fly the sequence and the special goes off without being equipped, paid for out of shield " +
                "or armour. A step is a direction, a direction with fire held, or letting go of everything.");
        ImGui.PopTextWrapPos();
        if (rows.Count == 0) return;

        const float rowH = 34f;
        // Most twiddle-only specials have no shop icon; keep the gutter only if one of them does.
        bool icons = rows.Any(c => SpecialHasIcon(d, TwiddleSpecialId(c)));
        float textX = icons ? 50f : 10f;
        // The Stalker's 21 would otherwise push the whole "where you get it" block off the pane.
        BeginRowScroll("##shiptwiddles", rows.Count, rowH, reserve: 92f);
        for (int i = 0; i < rows.Count; i++)
        {
            int sid = TwiddleSpecialId(rows[i]);
            var box = UiRow($"##shtw{i}", false, ac, rowH);
            if (box.Clicked && SpecialExists(d, sid)) ShowItemTab(5, sid);
            if (box.Hovered)
                ImGui.SetTooltip(unreachable
                    ? "the table gives this hull the combo, but it can never fire it\nopens the special in the Specials tab"
                    : "open this special in the Specials tab");
            if (icons && SpecialHasIcon(d, sid))
                DrawItemIconFor(ImGui.GetWindowDrawList(), d, 5, sid,
                    new Vector2(box.Min.X + 7f, box.Min.Y + 2f),
                    new Vector2(box.Min.X + 45f, box.Max.Y - 2f), 1f);
            string trail = unreachable ? "never fires" : $"#{sid}";
            RowText(box, textX, SpecialName(d, sid),
                $"{TwiddleSequence(rows[i])}   ·   {TwiddleCostText(d, sid)}",
                ac, false, TrailRoom(trail) + 12f);
            RowTrail(box, trail, Shade(ac, unreachable ? 0.95f : 1.1f));
        }
        ImGui.EndChild();

        // Player 2 never gets the hull's own list, which is easy to miss when the pane is a ship.
        if (!p2 && !unreachable)
        {
            var p2Rows = TwiddlesForShip(PlayerTwoCombos)
                .Select(c => SpecialName(d, TwiddleSpecialId(c))).ToList();
            ImGui.Dummy(new Vector2(0, 3f));
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorOf(UiFaint),
                "In two players this is player 1's list only -- player 2 twiddles " +
                string.Join(", ", p2Rows) + " out of the 2nd-player row whatever they fly.");
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawArcadeBlock(ItemData d, List<(int ShipId, bool ModeB)> arcade, List<int> frontPorts)
    {
        UiSection("super-arcade loadouts", AcArcade,
            arcade.Count > 0 ? $"{arcade.Count} ship{(arcade.Count == 1 ? "" : "s")}" : "");

        const float rowH = 30f;
        foreach (var (shipId, modeB) in arcade)
        {
            string ship = shipId < d.Ships.Length ? d.Ships[shipId].Name.Trim() : $"ship {shipId}";
            if (ship.Length == 0) ship = $"ship {shipId}";
            var box = UiRow($"##arc{shipId}_{modeB}", false, AcArcade, rowH);
            if (box.Clicked) ShowItemTab(0, shipId);
            if (box.Hovered) ImGui.SetTooltip("open this ship in the Ships tab");
            RowText(box, 10f, ship, modeB ? "fitted in Super-Arcade (mode B)" : "fitted in Arcade",
                AcArcade, false, 14f);
        }

        if (frontPorts.Count > 0)
        {
            var names = frontPorts.Select(p => p < d.Ports.Length && d.Ports[p].Name.Trim().Length > 0
                ? d.Ports[p].Name.Trim() : $"port #{p}");
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorOf(UiFaint),
                "In arcade play a front-weapon pickup for " + string.Join(", ", names) + " swaps this special in.");
            ImGui.PopTextWrapPos();
        }
    }
}
