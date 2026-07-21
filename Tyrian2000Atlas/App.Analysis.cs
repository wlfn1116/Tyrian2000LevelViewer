using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The analysis panel: what a level is actually made of, and how the levels rank against
/// each other.
///
/// It reads from two places. What a level *contains* -- spawn counts, the mix of categories,
/// which entries it leans on -- comes off the event list via <see cref="LevelStats"/>, which is
/// instant and the same at every difficulty. How hard it is comes from <see cref="LevelThreat"/>,
/// which plays each level through the real simulation at the chosen difficulty and measures what
/// comes at you; that is the only way to see the engine's difficulty rules (armour multipliers,
/// halved turret reloads, faster aim) actually applied, and the only way to know how long an
/// enemy really lingers rather than assuming it fires forever.
///
/// Every number the level view shows is placed against its peers -- the same statistic across
/// every level the atlas is browsing -- because "armour 5.8 per tick" means nothing on its
/// own and "5.8, the second heaviest of twenty" means quite a lot.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showAnalysis;
    private int _analysisMode;            // 0 = this level, 1 = all levels
    private int _analysisSort = 2;        // ranking table column being sorted (see RankCols)
    private bool _analysisSortAsc;        // false = biggest first, which is what a ranking wants
    private int _analysisDifficulty = 2;  // 0 wimp .. 10; what the threat readings are taken at
    private readonly Dictionary<(int Ep, int File), LevelStats> _statsCache = new();

    /// <summary>Measured threat per level *and difficulty*: the readings change with it, and a
    /// difficulty the user flips back to should come straight back rather than be re-run.</summary>
    /// <summary>Null means "tried and could not"; a miss means "not tried yet". Without that
    /// distinction a level whose simulation throws would be re-queued forever.</summary>
    private readonly Dictionary<(int Ep, int File, int Diff), LevelThreat?> _threatCache = new();
    /// <summary>Levels still to measure for the current episode set and difficulty.</summary>
    private readonly List<(EpisodeInfo Ep, int FileNum)> _threatQueue = new();
    private string _threatKey = "";
    private int _threatTotal;

    /// <summary>
    /// How many levels to measure per frame. One run of a level is a whole playthrough of it,
    /// which is around 10 ms -- fast enough that spreading the work over frames keeps the window
    /// drawing at full rate with a bar that visibly fills, and slow enough that doing all
    /// seventy at once would be a visible freeze on opening the window or changing difficulty.
    ///
    /// Measuring on the UI thread rather than a worker is deliberate: <see cref="GameData"/>'s
    /// lazy caches are plain dictionaries with no locking, and the simulation reads them.
    /// </summary>
    private const int ThreatPerFrame = 2;

    private static readonly uint AcArmor = Gfx.Rgba(255, 130, 120);
    private static readonly uint AcFire = Gfx.Rgba(255, 195, 90);
    private static readonly uint AcSpawn = Gfx.Rgba(120, 200, 255);
    /// <summary>The window's own colour. A property, not a copy of the literal: a browser and
    /// its launcher chip in the left column must be the same colour, and a second copy of the
    /// value is a second thing to forget to change. (Property, not a static field, because
    /// static initialiser order across the parts of a partial class is not defined.)</summary>
    private static uint AcAnalysis => AcSim;

    /// <summary>The set every level view compares itself against: the same levels the ranking
    /// lists, read once and kept until the browsed episodes or the data folder change.</summary>
    private string _peerKey = "";
    private readonly List<(int EpIdx, int EpNum, LevelStats St)> _peerRows = new();
    private int _peerMaxSpawn;
    /// <summary>The same, for the measured readings. Recomputed as the queue drains, so the
    /// meters grow into their final scale instead of jumping when the last level lands.</summary>
    private (double Diff, double Tracked, double Bullets, double Armor) _threatMax;

    /// <summary>The "--showanalysis N" entry point: 0 = this level, 1 = the cross-level ranking.</summary>
    public void ShowAnalysis(int mode)
    {
        _showAnalysis = true;
        _analysisMode = Math.Clamp(mode, 0, 1);
    }

    private LevelStats? StatsFor(EpisodeInfo ep, int fileNum)
    {
        var key = (ep.Number, fileNum);
        if (_statsCache.TryGetValue(key, out var hit)) return hit;
        if (_gd == null) return null;
        try
        {
            var lv = _gd.LoadLevel(ep, fileNum);
            var name = ep.Levels.FirstOrDefault(l => l.FileNum == fileNum)?.Name.Trim();
            var st = LevelStats.Build(lv, _gd.GetEnemyData(ep), _gd.GetWeapons(ep),
                string.IsNullOrWhiteSpace(name) ? $"level {fileNum}" : name);
            _statsCache[key] = st;
            return st;
        }
        catch { return null; }
    }

    /// <summary>
    /// Read every browsed level once, so both halves of the window have the same set to work
    /// from: the ranking lists it, and the level view scales its meters against its maxima.
    /// </summary>
    private void EnsurePeers()
    {
        string key = string.Join(',', ShownEpisodes());
        // Keyed on the episode set alone, never on "did we find anything": an episode set that
        // legitimately yields no rows would otherwise re-read every level file, every frame.
        if (_peerKey == key) return;
        _peerRows.Clear();
        _peerMaxSpawn = 0;
        foreach (int e in ShownEpisodes())
        {
            var ep = _gd!.Episodes[e];
            foreach (var item in ep.Levels)
            {
                var st = StatsFor(ep, item.FileNum);
                if (st == null || st.SpawnCount == 0) continue;
                if (_peerRows.Any(r => r.EpIdx == e && r.St.FileNum == item.FileNum)) continue;
                _peerRows.Add((e, ep.Number, st));
                _peerMaxSpawn = Math.Max(_peerMaxSpawn, st.SpawnCount);
            }
        }
        _peerKey = key;
    }

    /// <summary>What the level was measured at, for the difficulty now selected; null while it
    /// is still queued.</summary>
    private LevelThreat? ThreatFor(int epNum, int fileNum)
        => _threatCache.TryGetValue((epNum, fileNum, _analysisDifficulty), out var t) ? t : null;

    /// <summary>
    /// Keep the measured set in step with the browsed episodes and the chosen difficulty,
    /// a couple of levels per frame. Changing difficulty re-queues whatever has not been run at
    /// the new one; anything already run at it is still in the cache and comes back at once.
    /// </summary>
    private void EnsureThreat()
    {
        string key = string.Join(',', ShownEpisodes()) + "@" + _analysisDifficulty;
        if (_threatKey != key)
        {
            _threatKey = key;
            _threatQueue.Clear();
            foreach (var (epIdx, _, st) in _peerRows)
            {
                var ep = _gd!.Episodes[epIdx];
                if (!_threatCache.ContainsKey((ep.Number, st.FileNum, _analysisDifficulty)))
                    _threatQueue.Add((ep, st.FileNum));
            }
            // Drained from the back, so the level being looked at goes last in the list to come
            // out first: the panel in front of the user fills in immediately and the rest of the
            // ranking catches up behind it.
            int mine = _threatQueue.FindIndex(
                q => q.FileNum == _levelFileNum && q.Ep.Number == CurEpisode!.Number);
            if (mine >= 0)
                (_threatQueue[mine], _threatQueue[^1]) = (_threatQueue[^1], _threatQueue[mine]);
            _threatTotal = _threatQueue.Count;
            RebuildThreatMax();
        }

        // The level view can be showing something the ranking does not list -- the peer set
        // skips levels that spawn nothing -- so make sure the one on screen is measured whether
        // or not it is in that set.
        var cur = CurEpisode!;
        if (!_threatCache.ContainsKey((cur.Number, _levelFileNum, _analysisDifficulty)) &&
            !_threatQueue.Any(q => q.FileNum == _levelFileNum && q.Ep.Number == cur.Number))
            _threatQueue.Add((cur, _levelFileNum));

        for (int n = 0; n < ThreatPerFrame && _threatQueue.Count > 0; n++)
        {
            var (ep, fileNum) = _threatQueue[^1];
            _threatQueue.RemoveAt(_threatQueue.Count - 1);
            var slot = (ep.Number, fileNum, _analysisDifficulty);
            try
            {
                var lv = _gd!.LoadLevel(ep, fileNum);
                _threatCache[slot] = LevelThreat.Measure(_gd, ep, lv, _analysisDifficulty);
            }
            catch
            {
                // A level whose simulation will not run simply has no reading; the ranking shows
                // it as unmeasured rather than dropping it out of the list. Recorded as a miss so
                // it is not retried on every frame for the rest of the session.
                _threatCache[slot] = null;
            }
        }
        if (_threatQueue.Count > 0 || _threatTotal > 0) RebuildThreatMax();
        if (_threatQueue.Count == 0) _threatTotal = 0;
    }

    private void RebuildThreatMax()
    {
        _threatMax = default;
        foreach (var (_, epNum, st) in _peerRows)
        {
            var t = ThreatFor(epNum, st.FileNum);
            if (t == null) continue;
            _threatMax = (Math.Max(_threatMax.Diff, t.Difficulty01),
                Math.Max(_threatMax.Tracked, t.TrackedFireRate),
                Math.Max(_threatMax.Bullets, t.PeakBulletDensity),
                Math.Max(_threatMax.Armor, t.ArmorDensity));
        }
    }

    private bool ThreatBusy => _threatQueue.Count > 0;

    /// <summary>Where a level sits in the browsed set on one statistic: 1 is the highest.</summary>
    private int PeerRank(LevelStats st, Func<LevelStats, double> of)
    {
        double mine = of(st);
        int above = _peerRows.Count(r => of(r.St) > mine);
        return above + 1;
    }

    /// <summary>The same over the measured readings; levels not yet measured do not count, so
    /// the standing is honest while the queue drains rather than briefly flattering.</summary>
    private int ThreatRank(LevelThreat mine, Func<LevelThreat, double> of)
    {
        double v = of(mine);
        int above = 0, total = 0;
        foreach (var (_, epNum, st) in _peerRows)
        {
            var t = ThreatFor(epNum, st.FileNum);
            if (t == null) continue;
            total++;
            if (of(t) > v) above++;
        }
        return total == 0 ? 1 : above + 1;
    }

    private int ThreatMeasured()
    {
        int n = 0;
        foreach (var (_, epNum, st) in _peerRows)
            if (ThreatFor(epNum, st.FileNum) != null) n++;
        return n;
    }

    private static string Ordinal(int n) => n + (n % 100 is >= 11 and <= 13 ? "th"
        : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    // =====================================================================

    private void DrawAnalysisWindow()
    {
        if (!_showAnalysis || _gd == null || CurEpisode == null) return;

        if (!RefBegin("Analysis", "analysis", ref _showAnalysis, AcAnalysis,
                new Vector2(1000, 680), new Vector2(600, 380))) return;

        BandBegin("anband", AcAnalysis);
        BandLabel("episode");
        ImGui.SetNextItemWidth(126);
        EpisodeCombo("##anepisode");
        BandDivider();
        BandLabel("difficulty");
        ImGui.SetNextItemWidth(112);
        int dif = _analysisDifficulty;
        if (ImGui.Combo("##andiff", ref dif, DifficultyNames, DifficultyNames.Length))
            _analysisDifficulty = Math.Clamp(dif, 0, 10);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Measure every level at this difficulty.\n\n" +
                "The engine changes three things with it (tyrian2.c): enemy armour, from half at\n" +
                "Wimp to eight times at Nortaneous; turret reload, halved above Normal and halved\n" +
                "again above Maniacal; and how fast aimed shots travel, one step faster per level\n" +
                "above Normal.\n\n" +
                "Wimp, Easy and Normal therefore differ only in enemy armour - below Hard the\n" +
                "shooting itself is identical. Separate from the playback difficulty, so comparing\n" +
                "levels here does not disturb the run being watched.");
        BandDivider();
        SegBar("##anmode", ref _analysisMode, AcAnalysis, 220f,
            ("This level", "What the level in the atlas is built out of."),
            ("All levels", "Every browsed level ranked against the others."));
        BandDivider();
        if (ThreatBusy)
        {
            int done = Math.Max(0, _threatTotal - _threatQueue.Count);
            BandNote($"measuring {done}/{_threatTotal} levels at {DifficultyNames[_analysisDifficulty]}...",
                Shade(AcAnalysis, 1.1f));
        }
        else
        {
            BandNote("each level played through once - nothing shoots back, so every turret is counted",
                UiFaint);
        }
        BandEnd();

        EnsurePeers();
        EnsureThreat();
        if (_analysisMode == 0) DrawLevelAnalysis(); else DrawLevelRanking();
        RefEnd(AcAnalysis);
    }

    // =====================================================================
    // One level
    // =====================================================================

    private void DrawLevelAnalysis()
    {
        var ep = CurEpisode!;
        var st = StatsFor(ep, _levelFileNum);
        if (st == null)
        {
            UiEmpty("This level could not be read", "Its event list did not parse.", AcAnalysis);
            return;
        }

        ImGui.BeginChild("anbody", new Vector2(0, 0));

        // --- Heading, with the level's own particulars as badges rather than a run-on line. ---
        UiTitle(st.Name.ToUpperInvariant(), AcAnalysis,
            $"episode {ep.Number}  ·  level file {st.FileNum:00}  ·  {st.Duration:n0} event-clock units");
        Badge($"{st.SpawnCount} spawns", AcSpawn);
        ImGui.SameLine(0, 5f);
        Badge($"{st.TotalArmor:n0} armour", AcArmor);
        if (st.Invulnerable > 0)
        {
            ImGui.SameLine(0, 5f);
            Badge($"{st.Invulnerable} indestructible", Gfx.Rgba(160, 168, 186));
        }
        if (st.BossParts > 0)
        {
            ImGui.SameLine(0, 5f);
            Badge($"{st.BossParts} boss parts", Gfx.Rgba(255, 120, 120));
        }

        var th = ThreatFor(ep.Number, st.FileNum);
        // A level is allowed to move the engine's difficulty out from under the one you picked,
        // and one of the hardest in the game does exactly that. Worth saying out loud: it is
        // otherwise invisible, and it is the whole reason such a level's standing leaps the
        // moment you leave the easy end of the list.
        if (th is { ShiftsDifficulty: true })
        {
            ImGui.SameLine(0, 5f);
            string shift = th.RunAtLow == th.RunAtHigh
                ? $"runs at {DifficultyNames[Math.Clamp(th.RunAtLow, 0, 10)]}"
                : $"runs at {DifficultyNames[Math.Clamp(th.RunAtLow, 0, 10)]}" +
                  $"-{DifficultyNames[Math.Clamp(th.RunAtHigh, 0, 10)]}";
            Badge(shift, Gfx.Rgba(255, 170, 90));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    $"This level changes the difficulty itself (event 46), so picking\n" +
                    $"{DifficultyNames[_analysisDifficulty]} actually plays it at " +
                    $"{DifficultyNames[Math.Clamp(th.RunAtHigh, 0, 10)]}.\n\n" +
                    "The engine floors the result at Easy, so the shift does nothing at the\n" +
                    "bottom of the scale and bites everywhere above it.");
        }

        // --- The four headline rates, each with its standing in the browsed set. ---
        ImGui.Dummy(new Vector2(0, 8f));
        float tileW = (ImGui.GetContentRegionAvail().X - 3 * 8f) / 4f;
        float tileH = StatTileH();
        int peers = Math.Max(1, _peerRows.Count);

        void Tile(string label, string value, uint accent, double mine, double max, int rank, string tip)
        {
            string standing = $"{Ordinal(rank)} of {peers}";
            bool ranked = _peerRows.Count > 1;
            // The standing shares the label's line, so it is only drawn where both fit. The
            // tooltip carries it either way -- on a narrow window the two would print on top
            // of each other, and "fire / 1000 ticks" needs a tile ~210px wide to leave room.
            bool room = ranked &&
                ImGui.CalcTextSize(label).X + ImGui.CalcTextSize(standing).X + 26f <= tileW;
            StatTile(label, value, accent, tileW, tileH,
                tip + (ranked ? $"\n\nThis level is {standing} on this measure." : ""),
                max > 0 ? (float)(mine / max) : 0f);
            if (!room) return;
            var box = ImGui.GetItemRectMin();
            ImGui.GetWindowDrawList().AddText(
                new Vector2(box.X + tileW - ImGui.CalcTextSize(standing).X - 10f, box.Y + 6f),
                UiFaint, standing);
        }

        if (th == null)
        {
            // Still queued. Four empty tiles rather than a jump in layout when it lands.
            for (int i = 0; i < 4; i++)
            {
                if (i > 0) ImGui.SameLine(0, 8f);
                StatTile(i switch { 0 => "difficulty", 1 => "tracked fire", 2 => "bullets", _ => "armour" },
                    "--", AcAnalysis, tileW, tileH, "Measuring this level at " +
                    DifficultyNames[_analysisDifficulty] + "...", 0f);
            }
        }
        else
        {
            Tile("difficulty", $"{th.Difficulty01:0.00}", AcAnalysis, th.Difficulty01, _threatMax.Diff,
                ThreatRank(th, t => t.Difficulty01),
                $"Measured at {DifficultyNames[_analysisDifficulty]} by playing the level through.\n\n" +
                "1.00 is an ordinary campaign level. Mostly fire you have to move out of the way\n" +
                "of -- counted several times over when it is aimed at you rather than fired on a\n" +
                "fixed heading -- plus the space bullets already in the air deny you, and what the\n" +
                "level puts in your way. A ranking key, not an absolute.");
            ImGui.SameLine(0, 8f);
            Tile("tracked fire", $"{th.TrackedFireRate:0.0}", AcFire, th.TrackedFireRate,
                _threatMax.Tracked, ThreatRank(th, t => t.TrackedFireRate),
                "The share of this level's fire that follows you, per 1000 ticks: shots aimed\n" +
                "down the line to where you are standing, and the slower ones that keep steering\n" +
                "after you. Fire on a fixed heading only threatens the lane it was pointed down;\n" +
                "this is what has to be dodged.\n\n" +
                $"{th.TrackedShare * 100:0}% of the {th.Shots:n0} shots this level fires" +
                (th.AimedLaunches > 0 ? $"\n{th.AimedLaunches:n0} missiles launched straight at you." : ""));
            ImGui.SameLine(0, 8f);
            Tile("bullets", $"{th.PeakBulletDensity:0.0}", AcSpawn, th.PeakBulletDensity,
                _threatMax.Bullets, ThreatRank(th, t => t.PeakBulletDensity),
                "Enemy shots in the air at once, averaged over the level's worst ten seconds\n" +
                $"(it averages {th.BulletDensity:0.0} across the whole level).\n\n" +
                "The engine holds 60 at most, so a level near that has saturated it: the shots it\n" +
                "wants to fire are being dropped for want of a slot." +
                (th.Saturation > 0.001 ? $"\nThis one is full {th.Saturation * 100:0.#}% of the time." : ""));
            ImGui.SameLine(0, 8f);
            Tile("armour", $"{th.ArmorDensity:n0}", AcArmor, th.ArmorDensity, _threatMax.Armor,
                ThreatRank(th, t => t.ArmorDensity),
                "Destructible armour standing on screen at any moment, at this difficulty --\n" +
                "how much there is to chew through while everything else is happening.\n" +
                "Indestructible scenery is not counted here; it is never going anywhere." +
                (th.HulkDensity > 0.05 ? $"\n\n{th.HulkDensity:0.0} indestructible bodies on screen " +
                    $"on average, {th.PeakHulkDensity:0.0} at its worst." : ""));
        }

        // --- The shape of the level, start to end. ---
        ImGui.Dummy(new Vector2(0, 8f));
        UiSection("along the level", AcAnalysis, "left is the start  ·  hover for a reading");
        Profile("armour", st.ArmorProfile, st.PeakArmor, AcArmor, st.Duration);
        // The measured pressure where there is one. It is the same idea as the event list's
        // "incoming fire" but counted from the level actually running, so it knows how long each
        // turret was really on screen and weights fire that follows you above fire that does not.
        if (th != null)
            Profile("incoming fire", th.PressureProfile, th.PeakPressure, AcFire, th.Ticks, "tick");
        else
            // Not measured yet. The event list's estimate has the right shape but a different
            // meaning, so it says so rather than passing itself off as the measured strip.
            Profile("incoming fire (estimate)", st.FireProfile, st.PeakFire, AcFire, st.Duration);
        Profile("spawns", st.SpawnProfile, st.PeakSpawn, AcSpawn, st.Duration);

        // --- Composition and the entries that carry it. ---
        ImGui.Dummy(new Vector2(0, 8f));
        float half = (ImGui.GetContentRegionAvail().X - 10f) * 0.5f;
        float lower = Math.Max(150f, ImGui.GetContentRegionAvail().Y - 6f);

        ImGui.BeginChild("anmix", new Vector2(half, lower));
        UiSection("what is in it", AcAnalysis, $"{st.SpawnCount} spawns");
        DrawCategoryMix(st);
        ImGui.EndChild();

        ImGui.SameLine(0, 10f);
        ImGui.BeginChild("antop", new Vector2(0, lower));
        UiSection("most spawned", AcAnalysis, $"top {st.TopEnemies.Count}");
        DrawTopEnemies(st);
        ImGui.EndChild();

        ImGui.EndChild();
    }

    /// <summary>
    /// The level's make-up: one stacked band showing the proportions at a glance, then a bar
    /// per category. The old list of "counts and a colour word" said the same thing but made
    /// you do the arithmetic.
    /// </summary>
    private void DrawCategoryMix(LevelStats st)
    {
        int total = st.ByCategory.Sum();
        if (total == 0) { ImGui.TextDisabled("nothing spawns in this level"); return; }

        var dl = ImGui.GetWindowDrawList();
        float w = ImGui.GetContentRegionAvail().X;
        var p = ImGui.GetCursorScreenPos();
        const float stackH = 16f;

        // The stacked band. Rounded ends only on the outermost pieces, so it reads as one bar.
        float x = p.X;
        for (int c = 0; c < st.ByCategory.Length; c++)
        {
            if (st.ByCategory[c] == 0) continue;
            float seg = w * st.ByCategory[c] / total;
            uint col = ObjectPlacer.CategoryColor((ObjCategory)c);
            dl.AddRectFilled(new Vector2(x, p.Y), new Vector2(x + Math.Max(2f, seg), p.Y + stackH),
                Shade(col, 0.85f, 240));
            x += seg;
        }
        dl.AddRect(p, new Vector2(p.X + w, p.Y + stackH), UiLineSoft, 3f);
        ImGui.Dummy(new Vector2(w, stackH + 8f));

        int max = st.ByCategory.Max();
        for (int c = 0; c < st.ByCategory.Length; c++)
        {
            if (st.ByCategory[c] == 0) continue;
            var cat = (ObjCategory)c;
            uint col = ObjectPlacer.CategoryColor(cat);
            var at = ImGui.GetCursorScreenPos();
            float lh = ImGui.GetTextLineHeight();
            float barW = Math.Max(40f, w - 172f);

            dl.AddRectFilled(new Vector2(at.X, at.Y + 2f), new Vector2(at.X + 3f, at.Y + lh - 1f), col, 1f);
            ClipText(dl, new Vector2(at.X + 9f, at.Y), w - barW - 60f, UiText,
                ObjectPlacer.CategoryName(cat));
            var bar = new Vector2(at.X + w - barW - 44f, at.Y + lh * 0.5f - 4f);
            MeterBar(dl, bar, bar + new Vector2(barW, 8f), max > 0 ? st.ByCategory[c] / (float)max : 0, col);
            string n = st.ByCategory[c].ToString();
            var nsz = ImGui.CalcTextSize(n);
            dl.AddText(new Vector2(at.X + w - nsz.X, at.Y), Shade(col, 1.1f), n);
            ImGui.Dummy(new Vector2(w, lh + 4f));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{st.ByCategory[c]} of {total} spawns  ·  {st.ByCategory[c] * 100f / total:0.#}%");
        }
    }

    private void DrawTopEnemies(LevelStats st)
    {
        var ed = TryEnemyData();
        int max = st.TopEnemies.Count > 0 ? st.TopEnemies.Max(t => t.Count) : 1;

        ImGui.BeginChild("antoplist", new Vector2(0, 0));
        foreach (var (id, count, armor) in st.TopEnemies)
        {
            const float rowH = 30f;
            var box = UiRow($"##top{id}", false, AcEnemy, rowH);
            if (box.Clicked) OpenEnemy(_episodeIdx, id);
            if (box.Hovered) ImGui.SetTooltip("open this enemyDat entry");

            var dl = ImGui.GetWindowDrawList();
            if (ed != null)
            {
                var d = ed.Get(id);
                var atlas = d.Loaded ? Atlas(EnemySpriteSource(d.ShapeBank), AppSettings.GamePalette) : null;
                if (atlas != null)
                    DrawEnemyFrameCentered(dl, atlas, d.EGraphic[0], d.Esize == 1,
                        new Vector2(box.Min.X + 8f, box.Min.Y + 2f),
                        new Vector2(box.Min.X + 38f, box.Max.Y - 2f), 1f);
            }
            string trail = $"×{count}";
            float barW = Math.Max(30f, (box.Max.X - box.Min.X) * 0.3f);
            RowText(box, 44f, $"#{id}", armor > 0 ? $"armour {armor}" : "no armour",
                AcEnemy, false, barW + TrailRoom(trail) + 12f);
            var bar = new Vector2(box.Max.X - barW - TrailRoom(trail), box.Min.Y + rowH * 0.5f - 6f);
            MeterBar(dl, bar, bar + new Vector2(barW, 8f), count / (float)max, AcSpawn);
            RowTrail(box, trail, Shade(AcSpawn, 1.1f));
        }
        ImGui.EndChild();
    }

    /// <summary>
    /// One profile strip: the level start to end left to right, bar height relative to that
    /// profile's own peak so the shape reads even when the scales differ wildly. Hovering
    /// reads out the bucket under the cursor -- which is the only way to turn "there is a
    /// spike two thirds along" into "at t=4100, worth 380 armour".
    /// </summary>
    private void Profile(string label, float[] data, float peak, uint accent, int duration,
        string axis = "t")
    {
        const float h = 54f;
        var mn = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        ImGui.InvisibleButton($"##pf{label}", new Vector2(w, h));
        bool hot = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var mx = mn + new Vector2(w, h);

        Well(dl, mn, mx, accent, 5f);
        dl.AddText(new Vector2(mn.X + 7f, mn.Y + 4f), Shade(accent, 1f, 190), label);

        if (peak <= 0)
        {
            dl.AddText(new Vector2(mn.X + 7f, mn.Y + h * 0.5f - 2f), UiFaint, "none");
            ImGui.Dummy(new Vector2(0, 3f));
            return;
        }

        // A faint mean line: it is what makes a spike read as a spike rather than as the norm.
        float mean = data.Sum() / data.Length;
        float floorY = mx.Y - 4f;
        float span = h - 20f;
        float meanY = floorY - mean / peak * span;
        dl.AddRectFilled(new Vector2(mn.X + 4f, meanY), new Vector2(mx.X - 4f, meanY + 1f),
            Shade(accent, 0.7f, 70));

        float bw = (w - 8f) / data.Length;
        int hover = -1;
        if (hot)
        {
            float rel = ImGui.GetMousePos().X - mn.X - 4f;
            hover = Math.Clamp((int)(rel / Math.Max(0.001f, bw)), 0, data.Length - 1);
        }

        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] <= 0) continue;
            float bh = Math.Max(1.5f, data[i] / peak * span);
            float x0 = mn.X + 4f + i * bw;
            dl.AddRectFilled(new Vector2(x0, floorY - bh), new Vector2(x0 + Math.Max(1f, bw - 0.5f), floorY),
                i == hover ? Shade(accent, 1.25f) : Shade(accent, 0.95f, 235));
        }

        string pk = $"peak {peak:0.##}";
        var psz = ImGui.CalcTextSize(pk);
        dl.AddText(new Vector2(mx.X - psz.X - 7f, mn.Y + 4f), Shade(accent, 1f, 140), pk);

        if (hover >= 0)
        {
            float x0 = mn.X + 4f + hover * bw;
            dl.AddRectFilled(new Vector2(x0, mn.Y + 2f), new Vector2(x0 + Math.Max(1f, bw), mx.Y - 2f),
                Alpha(accent, 40));
            int t0 = duration * hover / data.Length;
            int t1 = duration * (hover + 1) / data.Length;
            ImGui.SetTooltip($"{axis} = {t0}..{t1}\n{label}: {data[hover]:0.##}   (peak {peak:0.##}, mean {mean:0.##})");
        }
        ImGui.Dummy(new Vector2(0, 3f));
    }

    // =====================================================================
    // The ranking
    // =====================================================================

    /// <summary>
    /// The ranking's columns, in table order. Column 0 is the standing and sorts by nothing;
    /// the rest each name one reading and carry its explanation for the header tooltip.
    /// A real ImGui table owns the geometry, so a heading is over its own column by
    /// construction rather than by two sets of hand-laid widths agreeing with each other.
    /// </summary>
    private static readonly (string Label, float Width, string Tip)[] RankCols =
    {
        ("#", 34f, "Where the level stands in whatever sort is on."),
        ("level", 0f, "Episode and level file number.\nSorts into campaign order."),
        ("difficulty", 86f, "The composite threat reading, measured by running the level."),
        ("tracked", 78f, "Fire per second that follows the player, rather than being\n" +
                         "sprayed along a fixed path."),
        ("bullets", 78f, "Enemy shots in the air at once, over the level's worst ten seconds."),
        ("armour", 82f, "Destructible armour standing on screen at any one moment."),
        ("spawns", 78f, "Spawn events in the level's own event list. Read off the file,\n" +
                        "so it is known before anything has been measured."),
    };

    private void DrawLevelRanking()
    {
        if (_peerRows.Count == 0) { UiEmpty("Nothing to rank", "No browsed level spawns anything.", AcAnalysis); return; }

        int measured = ThreatMeasured();
        ImGui.TextColored(ColorOf(UiFaint), measured < _peerRows.Count
            ? $"{_peerRows.Count} levels  ·  {measured} measured so far  ·  " +
              "click a heading to sort by it, again to reverse"
            : $"{_peerRows.Count} levels at {DifficultyNames[_analysisDifficulty]}  ·  " +
              "click a heading to sort by it, again to reverse  ·  click a row to open that level");
        ImGui.Dummy(new Vector2(0, 2f));

        var avail = ImGui.GetContentRegionAvail();
        avail = new Vector2(Math.Max(60f, avail.X), Math.Max(60f, avail.Y));
        var wp = ImGui.GetCursorScreenPos();
        // The frame belongs to this window; the rows below do NOT -- see the note at BeginTable.
        var frameDl = ImGui.GetWindowDrawList();
        Well(frameDl, wp, wp + avail, AcAnalysis);

        var flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoBordersInBodyUntilResize |
            ImGuiTableFlags.PadOuterX;
        // Inside the frame, not on it: a scrolling table clips at its own rect, so a table the
        // size of the well would cut its rows straight over the border, and the frozen header's
        // square-cornered background would sit on top of the well's rounded top corners.
        var (ip, isz) = WellInner(wp, avail);
        ImGui.SetCursorScreenPos(ip);
        if (!ImGui.BeginTable("rank", RankCols.Length, flags, isz)) return;

        // Fetched INSIDE the table, and this matters: a scrolling table draws into a child
        // window of its own, with its own clip rect. Cells drawn into the outer window's list
        // are clipped by nothing, so every row that had scrolled out of view was still being
        // painted -- at the screen position the cursor reported, which for a scrolled-off row
        // is above the table, on top of the toolbar and the title bar.
        var dl = ImGui.GetWindowDrawList();

        // The heading row stays put while the list scrolls -- with seventy levels, a heading
        // you have to scroll back up to read is a heading you cannot use.
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(RankCols[0].Label, ImGuiTableColumnFlags.WidthFixed |
            ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoResize, RankCols[0].Width);
        ImGui.TableSetupColumn(RankCols[1].Label, ImGuiTableColumnFlags.WidthStretch |
            ImGuiTableColumnFlags.PreferSortAscending, 0f);
        for (int c = 2; c < RankCols.Length; c++)
            ImGui.TableSetupColumn(RankCols[c].Label,
                ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending |
                (c == 2 ? ImGuiTableColumnFlags.DefaultSort : ImGuiTableColumnFlags.None),
                RankCols[c].Width);

        // Headers submitted by hand rather than via TableHeadersRow, purely so each one can
        // explain what its reading means on hover.
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (int c = 0; c < RankCols.Length; c++)
        {
            ImGui.TableSetColumnIndex(c);
            ImGui.TableHeader(RankCols[c].Label);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(RankCols[c].Tip +
                    (c == 0 ? "" : "\n\nClick to sort by this, click again to reverse it."));
        }

        // ImGui owns the sort state: which column, and which way round. Both directions are
        // real here -- "which level is gentlest" is as fair a question as "which is worst".
        var specs = ImGui.TableGetSortSpecs();
        if (!specs.IsNull)
        {
            if (specs.SpecsCount > 0)
            {
                var s = specs.Specs[0];
                _analysisSort = s.ColumnIndex;
                _analysisSortAsc = s.SortDirection == ImGuiSortDirection.Ascending;
            }
            specs.SpecsDirty = false;   // the rows below are re-sorted every frame regardless
        }

        Func<LevelThreat, double>? pick = _analysisSort switch
        {
            2 => t => t.Difficulty01,
            3 => t => t.TrackedFireRate,
            4 => t => t.PeakBulletDensity,
            5 => t => t.ArmorDensity,
            _ => null,
        };

        var rows = new List<(int EpIdx, int EpNum, LevelStats St)>(_peerRows);
        rows.Sort((x, y) =>
        {
            int r = 0;
            if (pick != null)
            {
                var tx = ThreatFor(x.EpNum, x.St.FileNum);
                var ty = ThreatFor(y.EpNum, y.St.FileNum);
                // An unmeasured level stays at the bottom whichever way the column is sorted:
                // it is "not known yet", not "lowest", and while the queue drains the order
                // should settle downwards into place rather than head the list.
                if (tx == null || ty == null) r = tx == ty ? 0 : tx == null ? 1 : -1;
                else
                {
                    r = pick(tx).CompareTo(pick(ty));
                    if (!_analysisSortAsc) r = -r;
                }
            }
            else if (_analysisSort == 6)
            {
                r = x.St.SpawnCount.CompareTo(y.St.SpawnCount);
                if (!_analysisSortAsc) r = -r;
            }
            if (r != 0) return r;
            // Campaign order is the tie-break, and the column the level names sorts on.
            int byLevel = x.EpNum != y.EpNum ? x.EpNum.CompareTo(y.EpNum)
                : x.St.FileNum.CompareTo(y.St.FileNum);
            return _analysisSort == 1 && !_analysisSortAsc ? -byLevel : byLevel;
        });

        float lh = ImGui.GetTextLineHeight();
        float rowH = lh + 13f;

        // Value at the cell's left edge, directly under its own heading, with the bar beneath
        // it: the eye drops straight from the label to the number it names.
        void Cell(string text, double val, double max, uint accent)
        {
            if (!ImGui.TableNextColumn()) return;
            var p = ImGui.GetCursorScreenPos();
            float cw = ImGui.GetContentRegionAvail().X;
            ClipText(dl, p, cw, Shade(accent, 1.05f), text);
            var bar = new Vector2(p.X, p.Y + lh + 2f);
            MeterBar(dl, bar, new Vector2(p.X + Math.Max(6f, cw), bar.Y + 4f),
                max > 0 ? (float)(val / max) : 0f, accent, Gfx.Rgba(255, 255, 255, 12));
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var (epIdx, epNum, r) = rows[i];
            var rt = ThreatFor(epNum, r.FileNum);
            bool sel = r.FileNum == _levelFileNum && epIdx == _episodeIdx;

            ImGui.TableNextRow(ImGuiTableRowFlags.None, rowH);
            ImGui.TableSetColumnIndex(0);
            var c0 = ImGui.GetCursorScreenPos();

            // One invisible hit target across the whole row. Its own highlight is turned off
            // and the colour goes through the table's background layer instead, so nothing it
            // draws can land on top of the cell contents.
            ImGui.PushStyleColor(ImGuiCol.Header, 0u);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, 0u);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, 0u);
            bool clicked = ImGui.Selectable($"##rk{epNum}_{r.FileNum}", false,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap,
                new Vector2(0, rowH - 4f));
            ImGui.PopStyleColor(3);
            bool hot = ImGui.IsItemHovered();
            if (clicked) SelectLevelFile(epIdx, r.FileNum);
            if (sel) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, Shade(AcAnalysis, 0.30f, 150));
            else if (hot) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, Gfx.Rgba(255, 255, 255, 16));
            if (hot)
                ImGui.SetTooltip($"{r.Name}  ·  episode {epNum}, file {r.FileNum:00}\n" +
                    (rt == null
                        ? "not measured yet\n"
                        : $"difficulty {rt.Difficulty01:0.00} at {DifficultyNames[_analysisDifficulty]}   ·   " +
                          $"{rt.TrackedShare * 100:0}% of its fire follows you\n" +
                          $"{rt.Shots:n0} shots   ·   up to {rt.PeakBulletDensity:0.0} in the air   ·   " +
                          $"{SimPlayback.FormatTime(rt.Ticks)} long\n") +
                    $"{r.SpawnCount} spawns   ·   {r.TotalArmor:n0} armour authored\n" +
                    "click to open this level in the atlas");

            // Standing on the current sort, so re-sorting renumbers rather than just reorders.
            string place = $"{i + 1}";
            var psz = ImGui.CalcTextSize(place);
            dl.AddText(new Vector2(c0.X + Math.Max(0f, RankCols[0].Width - psz.X - 10f), c0.Y),
                i < 3 ? Shade(AcAnalysis, 1.1f) : UiFaint, place);

            if (ImGui.TableNextColumn())
            {
                var p = ImGui.GetCursorScreenPos();
                ClipText(dl, p, ImGui.GetContentRegionAvail().X,
                    sel ? Gfx.Rgba(250, 252, 255) : UiText,
                    _allEpisodes ? $"E{epNum}  {r.FileNum:00}  {r.Name}" : $"{r.FileNum:00}  {r.Name}");
            }

            if (rt == null)
                // Queued. Dashes rather than zeros: a zero here would read as "measured, and
                // this level throws nothing at you", which is a different claim entirely.
                for (int c = 0; c < 4; c++) Cell("--", 0, 0, UiFaint);
            else
            {
                Cell($"{rt.Difficulty01:0.00}", rt.Difficulty01, _threatMax.Diff, AcAnalysis);
                Cell($"{rt.TrackedFireRate:0.0}", rt.TrackedFireRate, _threatMax.Tracked, AcFire);
                Cell($"{rt.PeakBulletDensity:0.0}", rt.PeakBulletDensity, _threatMax.Bullets, AcSpawn);
                Cell($"{rt.ArmorDensity:n0}", rt.ArmorDensity, _threatMax.Armor, AcArmor);
            }
            Cell($"{r.SpawnCount}", r.SpawnCount, _peerMaxSpawn, Gfx.Rgba(150, 162, 185));
        }
        ImGui.EndTable();
        // Re-consume the frame's whole rect, so the inset does not shorten the pane.
        ImGui.SetCursorScreenPos(wp);
        ImGui.Dummy(avail);
    }
}
