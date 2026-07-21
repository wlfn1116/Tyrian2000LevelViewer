using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The enemy browser: every enemyDat entry animated the way the engine animates it, plus the
/// multi-part groups the levels assemble out of them -- formations and bosses, which exist
/// nowhere in the data as single objects. See <see cref="EnemyAssembly"/>.
///
/// The controls sit where what they change is: the window band picks the episode, the mode and
/// the filter, and everything about the preview -- zoom, animation, the damaged form, the frame
/// being held -- lives on a strip attached to the preview stage itself. The old single toolbar
/// carried nine unrelated widgets and made you hunt for the one you wanted.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showEnemies;
    private int _enemyMode;                 // 0 = entries, 1 = assemblies
    private int _enemyEpisodeIdx = -1;
    private int _enemySelected = -1;
    private int _asmSelected;
    private float _enemyListW = 250f;
    private readonly byte[] _enemyFilter = new byte[64];
    private float _enemyZoom = 3f;
    private bool _enemyAnimate = true;
    private float _enemyAnimSpeed = 1f;
    /// <summary>Whether the trigger is held. Only <c>animate 2</c> entries care: they animate
    /// on a shot and rest between shots, exactly as an option-2 sidekick does.</summary>
    private bool _enemyFiring = true;
    private double _enemyClock;
    private int _enemyFrameHold = -1;       // a scrubbed frame, -1 = follow the clock
    private bool _enemyDamaged;             // preview the damaged/wreck graphic instead
    private bool _enemyMotion = true;       // draw the entry's own velocity over the stage
    private bool _enemyScrollToSelection;
    private bool _asmScrollToSelection;
    private bool _asmUnique = true;         // fold repeats of one body into one row
    private int _asmLevelEp = -1;           // level picker: -1 = every level in the episode set
    private int _asmLevelFile = -1;
    private bool _asmAimed;                 // "--showassemblies N" picked the row; don't re-snap
    private bool _asmOpenPending;           // "--asmopen": press the row's "open" link once drawn

    private List<EnemyAssembly>? _assemblies;
    private string _assembliesFor = "";    // the episode set the cache was built for
    private int _asmFollowedLevel = -1;    // the level the selection was last snapped to
    private Dictionary<int, List<(int FileNum, string Name, ushort Time)>>? _appearances;
    private int _appearancesFor = -1;

    private static readonly uint AcBoss = Gfx.Rgba(255, 120, 120);
    private static readonly uint AcLink = Gfx.Rgba(150, 200, 255);

    /// <summary>The "--showenemies N" entry point: frame one entry for a screenshot.</summary>
    public void ShowEnemy(int episodeIdx, int enemyId) => OpenEnemy(episodeIdx, enemyId);

    /// <summary>The "--asmopen" entry point: press the selected group's "open" link. Deferred,
    /// because the list it picks from is only built when the browser first draws.</summary>
    public void OpenSelectedAssembly() { _showEnemies = true; _enemyMode = 1; _asmOpenPending = true; }

    /// <summary>The "--showassemblies N" entry point: frame one group by its row.</summary>
    public void ShowAssembly(int row)
    {
        _showEnemies = true;
        _enemyMode = 1;
        _asmSelected = Math.Max(0, row);
        _asmAimed = true;
        _asmScrollToSelection = true;
    }

    /// <summary>The "--damaged" entry point: preview second forms in both browser modes.</summary>
    public void ShowDamagedForms() => _enemyDamaged = true;

    /// <summary>The "--asmlevel NAME" entry point: set the level picker, by exact name.</summary>
    public void PickAssemblyLevel(string name)
    {
        foreach (var (ep, file, _) in AssemblyLevels())
        {
            string lv = _gd!.Episodes[ep].Levels.First(l => l.FileNum == file).Name.Trim();
            if (!lv.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            (_asmLevelEp, _asmLevelFile) = (ep, file);
            return;
        }
    }

    /// <summary>Open the browser on an enemyDat entry (the sprite browser links in here).</summary>
    private void OpenEnemy(int episodeIdx, int enemyId)
    {
        _showEnemies = true;
        _enemyMode = 0;
        _enemyEpisodeIdx = episodeIdx;
        _enemySelected = enemyId;
        _enemyFrameHold = -1;
        _enemyScrollToSelection = true;
    }

    /// <summary>The episode whose entry the detail pane is showing.</summary>
    private EpisodeInfo? EnemyEpisode
    {
        get
        {
            if (_gd == null) return null;
            int i = _enemyEpisodeIdx >= 0 && _enemyEpisodeIdx < _gd.Episodes.Count ? _enemyEpisodeIdx : _episodeIdx;
            return _gd.Episodes[i];
        }
    }

    private void DrawEnemyWindow()
    {
        if (!_showEnemies || _gd == null || EnemyEpisode == null) return;

        if (!RefBegin("Enemies", "enemies", ref _showEnemies, AcEnemy,
                new Vector2(1100, 760), new Vector2(660, 420))) return;

        if (_enemyAnimate) _enemyClock += ImGui.GetIO().DeltaTime * 35.0 * _enemyAnimSpeed;

        DrawEnemyBand();

        float maxList = Math.Max(190f, ImGui.GetContentRegionAvail().X - 360f);
        _enemyListW = Math.Clamp(_enemyListW, 190f, maxList);

        WellBegin("enlist", new Vector2(_enemyListW, ImGui.GetContentRegionAvail().Y), AcEnemy);
        if (_enemyMode == 0) DrawEnemyList(); else DrawAssemblyList();
        WellEnd();

        ImGui.SameLine(0, 3);
        VSplitter("##ensplit", ref _enemyListW, 190f, maxList);
        ImGui.SameLine(0, 3);

        ImGui.BeginChild("endetail", new Vector2(0, 0));
        if (_enemyMode == 0) DrawEnemyDetail(); else DrawAssemblyDetail();
        ImGui.EndChild();

        RefEnd(AcEnemy);
    }

    /// <summary>What is being browsed -- episode, mode, filter. Nothing about how the preview
    /// is drawn: that belongs to the preview, and lives on its own strip.</summary>
    private void DrawEnemyBand()
    {
        BandBegin("enband", AcEnemy);
        BandLabel("episode");
        ImGui.SetNextItemWidth(126);
        int before = _episodeIdx;
        EpisodeCombo("##enepisode");
        if (before != _episodeIdx) { _enemyEpisodeIdx = _episodeIdx; _enemySelected = -1; _asmSelected = 0; }

        BandDivider();
        SegBar("##enmode", ref _enemyMode, AcEnemy, 230f,
            ("Entries", "Every enemyDat entry on its own."),
            ("Assemblies", "The multi-part groups the levels build out of those entries:\n" +
                           "formations, and the bosses that are really a dozen linked\n" +
                           "enemies tiled on the sprite grid."));

        BandDivider();
        UiFilter("##enfilter", _enemyMode == 0 ? "id, bank or category" : "search levels",
            _enemyFilter, 200f, AcEnemy);

        if (_enemyMode != 1) { BandEnd(); return; }

        BandDivider();
        DrawAssemblyLevelPicker();
        ImGui.SameLine(0, 6);
        if (UiToggle("unique", ref _asmUnique, AcEnemy,
                "Levels re-spawn the same body over and over -- GYGES hangs one\n" +
                "eight-part chain nine times. List each of those once, marked xN,\n" +
                "instead of once per spawn. Same pieces in the same arrangement\n" +
                "counts as the same body; link numbers and map position do not."))
        {
            // Stay on the same body across the toggle, since the row count changes under
            // it: folding moves the selection onto the row that stands for the run,
            // unfolding leaves it on the first spawn of that run.
            var was = ShownAssemblies(!_asmUnique);
            var sel = was.Count > 0 ? was[Math.Clamp(_asmSelected, 0, was.Count - 1)] : null;
            if (_asmUnique) sel = sel?.RepeatOf ?? sel;
            int at = sel == null ? -1 : ShownAssemblies().IndexOf(sel);
            if (at >= 0) _asmSelected = at;
            _asmScrollToSelection = true;
        }
        BandEnd();
    }

    // ================= entries =================

    /// <summary>Every entry the list shows, tagged with the episode it belongs to. Episodes
    /// 1-3 share one enemyDat while 4 and 5 each carry their own, so "All episodes" really
    /// does mean several different tables one after another.</summary>
    private List<(int EpisodeIdx, int Id)> FilteredEnemies()
    {
        string filter = BufText(_enemyFilter).Trim();
        var list = new List<(int, int)>();
        if (_gd == null) return list;

        foreach (int e in ShownEpisodes())
        {
            var ed = TryEnemyDataFor(_gd.Episodes[e]);
            if (ed == null) continue;
            for (int i = 0; i < ed.Enemies.Length; i++)
            {
                var d = ed.Enemies[i];
                if (!d.Loaded || (d.EGraphic[0] == 0 && d.Armor == 0 && d.Value == 0)) continue;
                if (filter.Length > 0 && !Matches(filter, i.ToString(), $"bank {d.ShapeBank}",
                        ObjectPlacer.CategoryName(ObjectPlacer.Classify(d.Armor, d.Value, 0)))) continue;
                list.Add((e, i));
            }
        }
        return list;
    }

    private void DrawEnemyList()
    {
        var rows = FilteredEnemies();
        if (rows.Count == 0) { ImGui.TextDisabled("Nothing matches."); return; }

        // Open on something that can actually draw: slot 0 and a few others name shape bank 0,
        // which resolves to no sheet at all (in-game they inherit whatever the slot held last).
        if (_enemySelected < 0)
        {
            int at = rows.FindIndex(r => EnemyDatOf(r.EpisodeIdx, r.Id).ShapeBank is > 0 and <= 36);
            var (pickEp, pickId) = rows[at >= 0 ? at : 0];
            _enemyEpisodeIdx = pickEp;
            _enemySelected = pickId;
        }

        // UiRow consumes exactly its height, so the stride below is exact.
        const float rowH = 34f;
        float viewH = ImGui.GetWindowSize().Y;

        // Keep the selection reachable when it was set from another window.
        if (_enemyScrollToSelection)
        {
            int at = rows.FindIndex(r => r.Id == _enemySelected && r.EpisodeIdx == _enemyEpisodeIdx);
            if (at >= 0) ImGui.SetScrollY(Math.Max(0, at * rowH - viewH * 0.4f));
            _enemyScrollToSelection = false;
        }

        float scrollY = ImGui.GetScrollY();
        int from = Math.Clamp((int)(scrollY / rowH) - 1, 0, rows.Count - 1);
        int to = Math.Clamp((int)((scrollY + viewH) / rowH) + 1, 0, rows.Count - 1);

        // Zero vertical spacing for the whole list: the two spacers stand in for the rows that
        // were skipped, and any spacing added after them would shift every visible row down by
        // that much -- but only while a leading spacer exists, so the list would jump as it
        // scrolled past the first screen.
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
        if (from > 0) ImGui.Dummy(new Vector2(1, from * rowH));

        for (int k = from; k <= to; k++)
        {
            var (epIdx, id) = rows[k];
            var e = EnemyDatOf(epIdx, id);
            var cat = ObjectPlacer.Classify(e.Armor, e.Value, 0);
            uint col = ObjectPlacer.CategoryColor(cat);

            bool sel = id == _enemySelected && epIdx == _enemyEpisodeIdx;
            var box = UiRow($"##en{epIdx}_{id}", sel, col, rowH);
            if (box.Clicked) { _enemyEpisodeIdx = epIdx; _enemySelected = id; _enemyFrameHold = -1; }

            var dl = ImGui.GetWindowDrawList();
            var atlas = Atlas(EnemySpriteSource(e.ShapeBank), AppSettings.GamePalette);
            if (atlas != null)
                DrawEnemyFrameCentered(dl, atlas, e.EGraphic[0], e.Esize == 1,
                    new Vector2(box.Min.X + 7f, box.Min.Y + 1f),
                    new Vector2(box.Min.X + 41f, box.Max.Y - 1f), 1f);

            string trail = e.Armor > 0 ? $"{e.Armor}" : "";
            RowText(box, 46f,
                _allEpisodes ? $"ep{_gd!.Episodes[epIdx].Number}  #{id}" : $"#{id}",
                ObjectPlacer.CategoryName(cat).ToLowerInvariant(), col, sel,
                trail.Length > 0 ? TrailRoom(trail) : 12f);
            if (trail.Length > 0) RowTrail(box, trail, Shade(col, 1.1f));
        }
        if (to < rows.Count - 1) ImGui.Dummy(new Vector2(1, (rows.Count - 1 - to) * rowH));
        ImGui.PopStyleVar();
    }

    private EnemyDat EnemyDatOf(int episodeIdx, int id)
    {
        if (_gd == null || episodeIdx < 0 || episodeIdx >= _gd.Episodes.Count) return default;
        return TryEnemyDataFor(_gd.Episodes[episodeIdx])?.Get(id) ?? default;
    }

    private EnemyData? TryEnemyData() => TryEnemyDataFor(EnemyEpisode);

    private EnemyData? TryEnemyDataFor(EpisodeInfo? ep)
    {
        if (_gd == null || ep == null) return null;
        try { return _gd.GetEnemyData(ep); } catch { return null; }
    }

    /// <summary>
    /// The frames the entry actually cycles, honouring the engine's rules: <c>animate 0</c>
    /// never leaves frame 1, a 999 entry ends the run (it despawns the enemy), and the
    /// damaged form either loops a different span of the same table or replaces frame 1
    /// outright. Returned as 1-based frame numbers, which is what enemycycle counts in.
    /// </summary>
    private static List<int> FrameRun(in EnemyDat e, bool damaged)
    {
        var run = new List<int>();
        if (damaged)
        {
            if (e.DAni != 0)
            {
                // The flip sets animin = dgr, ani = |dani| and enemycycle = dgr - 1, so the
                // cycle climbs dgr..|dani| and wraps back to dgr (tyrian2.c:2981-2988). When dgr
                // is past |dani| the very first step wraps, which parks it on dgr for good --
                // that is one frame, not none, and it is how the dormant halves of DELIANI's
                // boss (dgr 2, dani -1) show their second sprite.
                int lo = Math.Clamp((int)e.Dgr, 1, 20), hi = Math.Min(20, Math.Abs((int)e.DAni));
                for (int f = lo; f <= hi; f++) run.Add(f);
                if (run.Count == 0) run.Add(lo);
                return run;
            }
            return run;    // dani == 0 is a sprite swap, not a frame span -- handled by the caller
        }

        int max = Math.Clamp((int)e.Ani, 1, 20);
        for (int f = 1; f <= max; f++)
        {
            if (e.EGraphic[f - 1] == 999) break;
            run.Add(f);
        }
        if (run.Count == 0) run.Add(1);
        if (e.Animate == 0) run.RemoveRange(1, run.Count - 1);   // static: frame 1 forever
        return run;
    }

    /// <summary>
    /// Does this entry take part in the link group's damage flip? tyrian2.c:2954-3005: when any
    /// member's armour crosses its dlevel, every live enemy on the link with <c>dlevel &gt; 0</c>
    /// flips at the same instant — which is why a boss changes all over at once rather than
    /// piece by piece. dlevel 0 is "no flip" and -1 is a wreck left behind on death, neither of
    /// which the group cascade touches.
    /// </summary>
    private static bool PartFlips(in EnemyDat e) => e.DLevel > 0;

    /// <summary>What the flip leaves: a dani span switches to that span, a bare dgr swaps
    /// frame 1 for it, and an entry with neither is freed on the spot (no entry in the game
    /// actually has that combination, but the engine's branch is there).</summary>
    private static bool PartSurvivesFlip(in EnemyDat e) => e.Dgr > 0 || e.DAni != 0;

    /// <summary>
    /// How often the entry pulls a trigger, in 35Hz ticks: the shortest of its three turret
    /// reloads and its launch reload, each of which is a countdown reloaded with freq /
    /// elaunchfreq (tyrian2.c:1352, 1591-1595). A launch arms the animation before the
    /// launchtype == 0 test, so an elaunchfreq counts whether or not anything is launched.
    /// 0 means the entry never fires at all. The engine halves a turret reload above Normal
    /// (tyrian2.c:1356-1359); the browser shows the Normal rate.
    /// </summary>
    private static int EnemyFirePeriod(in EnemyDat e)
    {
        int p = 0;
        void Take(int freq, bool armed) { if (armed && freq > 0 && (p == 0 || freq < p)) p = freq; }
        Take(e.Freq0, e.Tur0 != 0);
        Take(e.Freq1, e.Tur1 != 0);
        Take(e.Freq2, e.Tur2 != 0);
        Take(e.ELaunchFreq, true);
        return p;
    }

    /// <summary>
    /// Which of the run's frames the engine would be showing on this tick.
    ///
    /// The frame table steps once per tick, but only while the entry's animation is ARMED, and
    /// that is what the browser used to leave out. <c>animate 1</c> is armed forever;
    /// <c>animate 2</c> is "When Firing Only" (episodes.h:143) — the spawn leaves aniactive at
    /// 2, which the stepper ignores, a shot or a launch sets it to 1 (tyrian2.c:1461, 1604),
    /// and reaching the last frame turns it back off, because animax == ani for these entries
    /// (tyrian2.c:1186-1188). So one shot buys exactly one pass and the body then sits on that
    /// last frame until the next one.
    ///
    /// Free-running an animate-2 table is what made the stage look nothing like the game: those
    /// entries are gun flashes two or three frames long, not idle loops, and repeating one
    /// forever at 35Hz is a strobe. It is the same mistake the sidekick stage used to make with
    /// an option-2 body, and the same cure.
    /// </summary>
    private static int EnemyFrameIndex(in EnemyDat e, int frames, int firePeriod,
        long tick, bool firing)
    {
        if (frames <= 1 || e.Animate == 0) return 0;
        if (e.Animate != 2) return (int)(tick % frames);      // animate 1: always running
        // Never armed: an entry with no weapon and no launch holds the spawn frame for good,
        // and so does one whose trigger the strip has up.
        if (!firing || firePeriod <= 0) return 0;
        // Steady state: the shot lands with the body still parked on the last frame, the table
        // resets on the tick after it, walks to the end and parks there again. A weapon that
        // reloads faster than the table is long simply never gets to rest.
        long phase = tick % Math.Max(firePeriod, frames + 1);
        return phase >= 1 && phase <= frames ? (int)phase - 1 : frames - 1;
    }

    /// <summary>Which frame of the run the clock is on, ignoring the entry browser's hold.</summary>
    private int RunningFrameIndex(in EnemyDat e, int frames, bool damaged)
    {
        if (frames <= 0) return 0;
        // The damaged flip arms the animation itself and clears aniwhenfire (tyrian2.c:2961-
        // 2965, 2985), so a dani span runs free whatever the entry's animate field says.
        if (damaged) return (int)(((long)_enemyClock) % frames);
        return EnemyFrameIndex(e, frames, EnemyFirePeriod(e), (long)_enemyClock, _enemyFiring);
    }

    /// <summary>Which frame of the run is live right now, honouring a scrubbed hold. Only the
    /// entry browser has one to honour — the assembly stage draws whole groups and reads
    /// <see cref="RunningFrameIndex"/> straight.</summary>
    private int LiveFrameIndex(in EnemyDat e, int frames, bool damaged) =>
        frames > 0 && _enemyFrameHold >= 0
            ? Math.Clamp(_enemyFrameHold, 0, frames - 1)
            : RunningFrameIndex(e, frames, damaged);

    /// <summary>The sprite number to draw this instant.</summary>
    private int CurrentSprite(in EnemyDat e, bool damaged)
    {
        // dani == 0 with a dgr means the damaged form is one fixed sprite, not a frame span.
        if (damaged && e.DAni == 0) return e.Dgr > 0 ? e.Dgr : e.EGraphic[0];

        var run = FrameRun(e, damaged);
        if (run.Count == 0) return e.EGraphic[0];
        return e.EGraphic[Math.Clamp(run[LiveFrameIndex(e, run.Count, damaged)] - 1, 0, 19)];
    }

    private void DrawEnemyDetail()
    {
        var ed = TryEnemyData();
        if (ed == null || _enemySelected < 0)
        {
            UiEmpty("Pick an enemy", "Every enemyDat entry, animated the way the engine does it.", AcEnemy);
            return;
        }
        var e = ed.Get(_enemySelected);
        if (!e.Loaded) { UiEmpty("Empty enemyDat slot", "Nothing is stored at this index.", AcEnemy); return; }

        var cat = ObjectPlacer.Classify(e.Armor, e.Value, 0);
        uint catCol = ObjectPlacer.CategoryColor(cat);
        var src = EnemySpriteSource(e.ShapeBank);
        var atlas = Atlas(src, AppSettings.GamePalette);
        bool damaged = _enemyDamaged && (e.Dgr > 0 || e.DAni != 0);

        UiTitle($"ENEMY {_enemySelected}", catCol);
        Badge(ObjectPlacer.CategoryName(cat).ToLowerInvariant(), catCol);
        ImGui.SameLine(0, 5f);
        Badge(e.Armor > 0 ? $"armour {e.Armor}" : "no armour", AcBoss);
        ImGui.SameLine(0, 5f);
        Badge(e.IsGround ? "ground" : "air", Gfx.Rgba(150, 162, 185));
        ImGui.SameLine(0, 5f);
        Badge(e.Esize == 1 ? "2x2 metasprite" : "single sprite", Gfx.Rgba(150, 162, 185));
        if (e.Value != 0)
        {
            ImGui.SameLine(0, 5f);
            Badge($"value {e.Value}", Gfx.Rgba(255, 210, 120));
        }

        ImGui.Dummy(new Vector2(0, 4f));
        DrawEnemyStageStrip(e);

        // --- Stage ---
        const float stageH = 196f;
        var stageMin = ImGui.GetCursorScreenPos();
        var stageMax = new Vector2(stageMin.X + ImGui.GetContentRegionAvail().X, stageMin.Y + stageH);
        var dl = ImGui.GetWindowDrawList();
        StageBegin(dl, stageMin, stageMax);
        if (atlas != null)
        {
            float zoom = MathF.Round(_enemyZoom);
            DrawEnemyFrameCentered(dl, atlas, CurrentSprite(e, damaged), e.Esize == 1,
                stageMin, stageMax, zoom);
            if (_enemyMotion) DrawMotionOverlay(dl, e, stageMin, stageMax);
        }
        else
            dl.AddText(stageMin + new Vector2(10f, 10f), Gfx.Rgba(230, 170, 170),
                $"shape bank {e.ShapeBank} has no sheet of its own -\n" +
                "in-game this entry draws with whatever bank was loaded last.");
        StageEnd(dl);
        ImGui.Dummy(new Vector2(0, stageH + 5f));

        // --- Frame strip ---
        DrawFrameStrip(e, atlas, damaged);

        // --- Facts, side by side. ---
        ImGui.Dummy(new Vector2(0, 4f));
        float half = (ImGui.GetContentRegionAvail().X - 10f) * 0.5f;
        float bodyH = Math.Max(160f, ImGui.GetContentRegionAvail().Y);

        ImGui.BeginChild("enstats", new Vector2(half, bodyH));
        DrawEnemyStats(e, cat, src);
        ImGui.EndChild();
        ImGui.SameLine(0, 10f);
        ImGui.BeginChild("enright", new Vector2(0, bodyH));
        DrawEnemyLinks(e, ed);
        ImGui.Dummy(new Vector2(0, 4f));
        DrawEnemyAppearances(_enemySelected);
        ImGui.EndChild();
    }

    /// <summary>The preview's own controls, on a strip attached to the stage.</summary>
    private void DrawEnemyStageStrip(in EnemyDat e)
    {
        BandBegin("enstage", AcEnemy);
        BandLabel("zoom");
        ImGui.SetNextItemWidth(96);
        ImGui.SliderFloat("##enzoom", ref _enemyZoom, 1f, 8f, "%.0fx");
        SliderReset(ref _enemyZoom, 3f, "How far the stage blows the entry up.", "3x");

        BandDivider(9f);
        if (UiToggle("animate", ref _enemyAnimate, AcEnemy,
                "Run the clock at the engine's 35Hz. Whether the entry steps on every one\n" +
                "of those ticks is its own business -- see \"animate\" in the stats."))
            _enemyFrameHold = -1;
        ImGui.SameLine(0, 5);
        ImGui.SetNextItemWidth(84);
        ImGui.SliderFloat("##enspeed", ref _enemyAnimSpeed, 0.1f, 3f, "x%.2f");
        SliderReset(ref _enemyAnimSpeed, 1f,
            "The engine runs at 35 ticks a second; this scales that.", "x1");

        // An animate-2 entry is idle unless it is shooting, so the trigger is the control that
        // decides whether it animates at all -- the same gate the sidekick stage has.
        if (e.Animate == 2)
        {
            int period = EnemyFirePeriod(e);
            ImGui.SameLine(0, 5);
            UiToggle("holding fire", ref _enemyFiring, AcEnemy, period > 0
                ? $"This one only animates while it is shooting. Firing, it pulls a\n" +
                  $"trigger every {period} tick{(period == 1 ? "" : "s")}, and each shot plays its frames once\n" +
                  "and parks on the last of them; let go and it holds frame 1."
                : "animate 2 with no turret and no launch: nothing ever arms the\n" +
                  "animation, so in game it holds frame 1 forever.",
                0f, period <= 0);
        }

        BandDivider(9f);
        bool canFlip = e.Dgr > 0 || e.DAni != 0;
        UiToggle("damaged", ref _enemyDamaged, AcBoss,
            canFlip
                ? "Show the damaged form instead. An entry with dani != 0 loops a\n" +
                  "different span of its own frames; with dani == 0 it swaps in the\nsingle sprite dgr names."
                : "This entry has no damaged form.", 0f, !canFlip);
        ImGui.SameLine(0, 5);
        UiToggle("motion", ref _enemyMotion, AcLink,
            "Draw the entry's own per-frame velocity and acceleration over the stage.");
        BandEnd();
    }

    /// <summary>
    /// The entry's movement, drawn on the stage: an arrow along xmove/ymove from the sprite's
    /// anchor, and a second, fainter one for the acceleration. It is the one field group that
    /// cannot be read off a still picture, and it is what decides whether a thing dives at you
    /// or drifts past.
    /// </summary>
    private static void DrawMotionOverlay(ImDrawListPtr dl, in EnemyDat e, Vector2 mn, Vector2 mx)
    {
        var c = new Vector2((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f);
        float room = Math.Min(mx.X - mn.X, mx.Y - mn.Y) * 0.42f;

        void Ray(float dx, float dy, uint col, float thick, string label)
        {
            if (dx == 0f && dy == 0f) return;
            var v = new Vector2(dx, dy);
            float len = v.Length();
            // Scaled against the largest component the table ever holds, so two entries can
            // be compared by eye rather than each being normalised to its own arrow.
            var dir = v / len;
            var end = c + dir * Math.Min(room, 10f + len * 9f);
            dl.AddLine(c, end, col, thick);
            var side = new Vector2(-dir.Y, dir.X) * 4.5f;
            dl.AddTriangleFilled(end, end - dir * 9f + side, end - dir * 9f - side, col);
            var lsz = ImGui.CalcTextSize(label);
            var at = end + dir * 6f - lsz * 0.5f;
            dl.AddRectFilled(at - new Vector2(3f, 1f), at + lsz + new Vector2(3f, 1f),
                Gfx.Rgba(10, 11, 16, 200), 3f);
            dl.AddText(at, col, label);
        }

        Ray(e.XAccel, e.YAccel, Gfx.Rgba(255, 190, 90, 190), 1.5f, $"accel {e.XAccel},{e.YAccel}");
        Ray(e.XMove, e.YMove, Shade(AcLink, 1.05f), 2f, $"{e.XMove},{e.YMove}");
        dl.AddCircleFilled(c, 2.5f, Gfx.Rgba(255, 255, 255, 210));
        if (e.XMove == 0 && e.YMove == 0 && e.XAccel == 0 && e.YAccel == 0)
            dl.AddText(new Vector2(mn.X + 9f, mx.Y - ImGui.GetTextLineHeight() - 7f),
                Gfx.Rgba(150, 158, 176), "holds still (no move, no accel)");
    }

    /// <summary>
    /// Open a preview stage: the starfield, and a clip rect just inside it. The clip is the
    /// point -- a sprite is drawn centred at whatever zoom is set, and a big boss part at 8x is
    /// larger than the stage, so without it the sprite spills over the frame and out across
    /// whatever the pane draws next.
    /// </summary>
    private static void StageBegin(ImDrawListPtr dl, Vector2 mn, Vector2 mx)
    {
        DrawStarStage(dl, mn, mx);
        dl.PushClipRect(mn + new Vector2(WellInset, WellInset), mx - new Vector2(WellInset, WellInset), true);
    }

    private static void StageEnd(ImDrawListPtr dl) => dl.PopClipRect();

    /// <summary>A dark backdrop with a scatter of stars, so a sprite reads against something
    /// that looks like the game rather than against a flat panel.</summary>
    private static void DrawStarStage(ImDrawListPtr dl, Vector2 mn, Vector2 mx)
    {
        dl.AddRectFilled(mn, mx, Gfx.Rgba(8, 9, 14), 6f);
        // A fixed hash, so the field is stable frame to frame instead of twinkling randomly.
        for (int i = 0; i < 90; i++)
        {
            uint h = (uint)(i * 2654435761u);
            float x = mn.X + (h % 997) / 997f * (mx.X - mn.X);
            float y = mn.Y + ((h >> 10) % 991) / 991f * (mx.Y - mn.Y);
            byte b = (byte)(70 + (h >> 20) % 130);
            dl.AddRectFilled(new Vector2(x, y), new Vector2(x + 1f, y + 1f), Gfx.Rgba(b, b, (byte)Math.Min(255, b + 30)));
        }
        dl.AddRect(mn, mx, Gfx.Rgba(70, 80, 108, 190), 6f);
    }

    private void DrawFrameStrip(in EnemyDat e, SpriteAtlas? atlas, bool damaged)
    {
        var run = FrameRun(e, damaged);
        if (damaged && e.DAni == 0)
        {
            ImGui.TextColored(ColorOf(UiFaint), e.Dgr > 0
                ? $"damaged: sprite {e.Dgr} replaces frame 1, animation stops"
                : "this entry has no damaged form");
            return;
        }
        if (run.Count == 0) { ImGui.TextColored(ColorOf(UiFaint), "this entry has no damaged form"); return; }
        if (run.Count == 1 && damaged && e.Dgr > Math.Abs((int)e.DAni))
            // dgr past |dani|: the cycle wraps on its first step and parks there.
            ImGui.TextColored(ColorOf(UiFaint), $"damaged: parks on frame {e.Dgr}, animation stops");
        if (run.Count == 1 && !damaged)
        {
            ImGui.TextColored(ColorOf(UiFaint),
                e.Animate == 0 ? "static (animate 0): frame 1 only" : "single frame");
            return;
        }

        int live = LiveFrameIndex(e, run.Count, damaged);

        const float cell = 32f;
        var dl = ImGui.GetWindowDrawList();
        for (int k = 0; k < run.Count; k++)
        {
            if (k > 0) ImGui.SameLine(0, 3);
            var mn = ImGui.GetCursorScreenPos();
            var mx = mn + new Vector2(cell, cell);
            bool hit = ImGui.InvisibleButton($"##fr{k}", new Vector2(cell, cell));
            bool hot = ImGui.IsItemHovered();

            dl.AddRectFilled(mn, mx, k == live ? Gfx.Rgba(30, 34, 46) : Gfx.Rgba(20, 22, 30), 4f);
            if (atlas != null)
                DrawEnemyFrameCentered(dl, atlas, e.EGraphic[Math.Clamp(run[k] - 1, 0, 19)],
                    e.Esize == 1, mn, mx, 1f);
            dl.AddRect(mn, mx, k == live ? Shade(AcLink, 1.1f) : hot ? Gfx.Rgba(255, 255, 255, 120)
                : UiLineSoft, 4f, ImDrawFlags.None, k == live ? 1.8f : 1f);
            if (k == live)
                dl.AddRectFilled(new Vector2(mn.X + 4f, mx.Y - 2.5f), new Vector2(mx.X - 4f, mx.Y - 0.5f),
                    Shade(AcLink, 1.15f), 1f);
            if (hot) ImGui.SetTooltip($"frame {run[k]}  ·  sprite {e.EGraphic[Math.Clamp(run[k] - 1, 0, 19)]}" +
                "\nclick to hold this frame");
            if (hit) { _enemyFrameHold = k; _enemyAnimate = false; }
        }
        ImGui.SameLine(0, 10);
        ImGui.AlignTextToFramePadding();
        if (_enemyFrameHold >= 0)
        {
            if (UiButton("resume", AcEnemy, "back to the running animation"))
                { _enemyFrameHold = -1; _enemyAnimate = true; }
        }
        else ImGui.TextColored(ColorOf(UiFaint), $"{run.Count} frames  ·  click one to hold it");
    }

    private void DrawEnemyStats(in EnemyDat e, ObjCategory cat, SpriteSource src)
    {
        UiSection("movement", AcLink);
        KV("velocity", $"{e.XMove}, {e.YMove}");
        KV("acceleration", $"{e.XAccel}, {e.YAccel}");
        if (e.XCAccel != 0 || e.YCAccel != 0)
            KV("cyclic accel", $"{e.XCAccel}, {e.YCAccel}   reverses at {e.XRev}, {e.YRev}");
        KV("start", $"{e.StartX}, {e.StartY}" +
            (e.StartXC != 0 || e.StartYC != 0 ? $"   ± {e.StartXC}, {e.StartYC}" : ""));

        UiSection("shape", AcSprite);
        KV("animate", $"{e.Animate} ({AnimateName(e.Animate)}), {e.Ani} frames");
        KV("size", e.Esize == 1 ? "2x2 metasprite (24x28)" : "single sprite (12px wide)");
        KV("explosion", $"type {e.ExplosionType >> 1}");
        ImGui.Dummy(new Vector2(0, 3f));
        if (UiButton($"open bank {e.ShapeBank}", AcSprite, $"open {src.Title} in the sprite browser"))
            OpenSprite(src, e.EGraphic[0]);

        // --- Turrets ---
        var turrets = new[]
        {
            (e.Tur0, e.Freq0, "fires down"),
            (e.Tur1, e.Freq1, "rotated right"),
            (e.Tur2, e.Freq2, "rotated left"),
        };
        bool any = turrets.Any(t => t.Item1 != 0 && t.Item2 != 0);
        UiSection("turrets", AcSim, any ? "" : "none");
        if (!any) return;

        // The reload is drawn as a rate rather than a period, so the fastest gun has the
        // longest bar -- "every 4 ticks" and "every 90 ticks" otherwise sort backwards.
        int fastest = turrets.Where(t => t.Item1 != 0 && t.Item2 != 0).Min(t => (int)t.Item2);
        var dl = ImGui.GetWindowDrawList();
        foreach (var (id, freq, dir) in turrets)
        {
            if (id == 0 || freq == 0) continue;
            float w = ImGui.GetContentRegionAvail().X;
            float lh = ImGui.GetTextLineHeight();
            float h = lh * 2f + 12f;
            var p = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton($"##tur{id}_{freq}_{dir}", new Vector2(w, h));
            bool hot = ImGui.IsItemHovered();
            Card(dl, p, p + new Vector2(w, h), AcSim, hot ? 0.14f : 0.07f);
            dl.AddRectFilled(new Vector2(p.X, p.Y + 4f), new Vector2(p.X + 2.5f, p.Y + h - 4f),
                Shade(AcSim, 1f, 210), 2f);
            ClipText(dl, new Vector2(p.X + 10f, p.Y + 5f), w - 20f, UiText, TurretName(id));
            ClipText(dl, new Vector2(p.X + 10f, p.Y + 5f + lh + 1f), w - 112f, UiFaint,
                $"every {freq} ticks  ·  {dir}");
            var bar = new Vector2(p.X + w - 96f, p.Y + h - 11f);
            MeterBar(dl, bar, bar + new Vector2(86f, 6f), fastest / (float)freq, AcSim);
            if (hot && id < 251) ImGui.SetTooltip($"weapons[{id}] - the same table the shop's guns come from");
        }
    }

    private static string AnimateName(byte a) => a switch
    {
        0 => "static",
        1 => "always",
        _ => "only while firing",
    };

    /// <summary>Turret slots hold a weapons[] index, except 251..255 which are hard-coded
    /// behaviours in the fire loop (tyrian2.c:1379).</summary>
    private string TurretName(byte id)
    {
        string special = id switch
        {
            251 => "Suck-O-Magnet (pulls the player in)",
            252 => "twin missile puffs",
            253 => "short-range magnet, pushes right",
            254 => "short-range magnet, pushes left",
            255 => "Magneto RePulse",
            _ => "",
        };
        if (special.Length > 0) return $"#{id} {special}";

        var ep = EnemyEpisode;
        if (_gd != null && ep != null)
        {
            try
            {
                var w = _gd.GetWeapons(ep).Get(id);
                if (w.Loaded) return $"weapon {id}  ({w.Max} shots, {w.Attack[0]} damage)";
            }
            catch { /* fall through to the bare id */ }
        }
        return $"weapon {id}";
    }

    private void DrawEnemyLinks(in EnemyDat e, EnemyData ed)
    {
        UiSection("links", AcLink);
        // The pane is narrow, so let the prose wrap inside it rather than run under the edge.
        ImGui.PushTextWrapPos(0f);

        // launchtype splits into a type and a "special" above 1000 for entries under 1000.
        int launch = e.ELaunchType;
        if (launch > 0 && e.ELaunchFreq > 0)
        {
            int type = _enemySelected > 1000 ? launch : launch % 1000;
            int special = _enemySelected > 1000 ? 0 : launch / 1000;
            EnemyLinkRow("launches", type, ed,
                $"every {e.ELaunchFreq} ticks" + (special > 0 ? $", launch mode {special}" : ""));
        }
        else ImGui.TextColored(ColorOf(UiFaint), "launches nothing");

        if (e.EEnemyDie > 0) EnemyLinkRow("dies into", e.EEnemyDie, ed, "spawned where the corpse fell");
        else ImGui.TextColored(ColorOf(UiFaint), "leaves nothing behind");

        if (e.DLevel == -1)
            ImGui.TextColored(ColorOf(UiDim), $"leaves a wreck (sprite {e.Dgr}): drifts on, cannot be shot");
        else if (e.Dgr > 0 || e.DAni != 0)
            ImGui.TextColored(ColorOf(UiDim), e.DAni < 0
                ? $"starts dormant; wakes at armour <= {e.DLevel}"
                : $"gets wrecked at armour <= {e.DLevel}");
        ImGui.PopTextWrapPos();
    }

    private void EnemyLinkRow(string label, int enemyId, EnemyData ed, string note)
    {
        var target = ed.Get(enemyId);
        float w = ImGui.GetContentRegionAvail().X;
        float lh = ImGui.GetTextLineHeight();
        float h = lh * 2f + 10f;
        var p = ImGui.GetCursorScreenPos();
        bool hit = ImGui.InvisibleButton($"##lnk{label}{enemyId}", new Vector2(w, h));
        bool hot = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        Card(dl, p, p + new Vector2(w, h), AcLink, hot ? 0.16f : 0.07f);
        if (target.Loaded)
        {
            var atlas = Atlas(EnemySpriteSource(target.ShapeBank), AppSettings.GamePalette);
            if (atlas != null)
                DrawEnemyFrameCentered(dl, atlas, target.EGraphic[0], target.Esize == 1,
                    new Vector2(p.X + 5f, p.Y + 3f), new Vector2(p.X + 37f, p.Y + h - 3f), 1f);
        }
        ClipText(dl, new Vector2(p.X + 42f, p.Y + 4f), w - 50f,
            hot ? Gfx.Rgba(248, 250, 255) : UiText, $"{label} #{enemyId}");
        ClipText(dl, new Vector2(p.X + 42f, p.Y + 4f + lh + 1f), w - 50f, UiFaint, note);
        if (hot) ImGui.SetTooltip("open this enemyDat entry");
        if (hit) OpenEnemy(_enemyEpisodeIdx, enemyId);
    }

    /// <summary>Which levels spawn this entry, and when. Built once per episode.</summary>
    private void DrawEnemyAppearances(int enemyId)
    {
        var ep = EnemyEpisode;
        if (_gd == null || ep == null) return;
        if (_appearances == null || _appearancesFor != ep.Number) BuildAppearances(ep);

        if (!_appearances!.TryGetValue(enemyId, out var sites) || sites.Count == 0)
        {
            UiSection("where it turns up", AcRoutes, "nowhere");
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorOf(UiFaint),
                "No level in this episode spawns it directly - it may only appear as a launch or a death drop.");
            ImGui.PopTextWrapPos();
            return;
        }

        var groups = sites.GroupBy(s => s.FileNum).ToList();
        UiSection("where it turns up", AcRoutes, $"{groups.Count} level{(groups.Count == 1 ? "" : "s")}");
        int most = groups.Max(g => g.Count());

        ImGui.BeginChild("enwhere", new Vector2(0, 0));
        foreach (var g in groups)
        {
            var first = g.First();
            ushort firstTime = g.Min(s => s.Time);
            const float rowH = 30f;
            var box = UiRow($"##app{first.FileNum}", false, AcRoutes, rowH);
            if (box.Clicked)
            {
                SelectLevelFile(_enemyEpisodeIdx < 0 ? _episodeIdx : _enemyEpisodeIdx, first.FileNum);
                // One entry, and it is the earliest spawn of it that the row names.
                _pendingJump = new MapJump(firstTime, new[] { enemyId });
            }
            if (box.Hovered)
                ImGui.SetTooltip("open this level at its first spawn of this entry\n" +
                    "(in playback, seek to the frame it flies into)");

            var dl = ImGui.GetWindowDrawList();
            float barW = Math.Max(28f, (box.Max.X - box.Min.X) * 0.28f);
            string trail = $"×{g.Count()}";
            RowText(box, 10f, $"{first.Name}  #{first.FileNum:00}", $"first at t={firstTime}",
                AcRoutes, false, barW + TrailRoom(trail) + 12f);
            var bar = new Vector2(box.Max.X - barW - TrailRoom(trail), box.Min.Y + rowH * 0.5f - 5f);
            MeterBar(dl, bar, bar + new Vector2(barW, 7f), g.Count() / (float)most, AcRoutes);
            RowTrail(box, trail, Shade(AcRoutes, 1.1f));
        }
        ImGui.EndChild();
    }

    private void BuildAppearances(EpisodeInfo ep)
    {
        var map = new Dictionary<int, List<(int, string, ushort)>>();
        foreach (var item in ep.Levels)
        {
            Level lv;
            try { lv = _gd!.LoadLevel(ep, item.FileNum); } catch { continue; }
            string name = string.IsNullOrWhiteSpace(item.Name) ? "(unnamed)" : item.Name.Trim();
            foreach (var ev in lv.Events)
            {
                // Events 49-52 carry a sprite rather than an entry id, so they name no entry
                // to cross-reference; type 12 names four consecutive ones.
                if (ev.Type is >= 49 and <= 52) continue;
                if (!ObjectPlacer.IsSpawn(ev.Type, out _, out _) && ev.Type != 12) continue;
                int span = ev.Type == 12 ? 4 : 1;
                for (int k = 0; k < span; k++)
                {
                    int id = ev.Dat + k;
                    if (!map.TryGetValue(id, out var list)) map[id] = list = new List<(int, string, ushort)>();
                    list.Add((item.FileNum, name, ev.Time));
                }
            }
        }
        _appearances = map;
        _appearancesFor = ep.Number;
    }

    // ================= assemblies =================

    /// <summary>
    /// Pick one level exactly. The text box beside it is a substring search, which cannot say
    /// "TYRIAN but not TYRIAN X" and cannot tell three levels called SAVARA apart; this names
    /// the level file, so it always means one level.
    /// </summary>
    private void DrawAssemblyLevelPicker()
    {
        var levels = AssemblyLevels();
        int at = levels.FindIndex(l => l.Ep == _asmLevelEp && l.File == _asmLevelFile);
        if (_asmLevelEp >= 0 && at < 0) { _asmLevelEp = _asmLevelFile = -1; }   // episode set changed

        ImGui.SetNextItemWidth(168);
        if (!ImGui.BeginCombo("##asmlevelpick", at >= 0 ? levels[at].Label : "any level")) return;
        if (ImGui.Selectable("any level", at < 0))
        {
            _asmLevelEp = _asmLevelFile = -1;
            _asmSelected = 0;
            _asmScrollToSelection = true;
        }
        for (int i = 0; i < levels.Count; i++)
            if (ImGui.Selectable(levels[i].Label + $"##asmlv{i}", i == at))
            {
                (_asmLevelEp, _asmLevelFile) = (levels[i].Ep, levels[i].File);
                _asmSelected = 0;
                _asmScrollToSelection = true;
            }
        ImGui.EndCombo();
    }

    private List<EnemyAssembly> Assemblies()
    {
        if (_gd == null) return new List<EnemyAssembly>();
        string key = string.Join(',', ShownEpisodes());
        if (_assemblies != null && _assembliesFor == key) return _assemblies;

        var all = new List<EnemyAssembly>();
        foreach (int e in ShownEpisodes())
        {
            var ep = _gd.Episodes[e];
            var ed = TryEnemyDataFor(ep);
            if (ed == null) continue;
            // An episode's route can reach one level file from two entries (a fork that
            // rejoins); reading it twice would list every group in it twice.
            var seen = new HashSet<int>();
            foreach (var item in ep.Levels)
            {
                if (!seen.Add(item.FileNum)) continue;
                try
                {
                    var lv = _gd.LoadLevel(ep, item.FileNum);
                    all.AddRange(EnemyAssembly.Find(lv, ed,
                        string.IsNullOrWhiteSpace(item.Name) ? $"level {item.FileNum}" : item.Name.Trim(), e));
                }
                catch { /* a level that will not parse contributes nothing */ }
            }
        }
        // Fold over everything just gathered, so the scope is whatever is being browsed: one
        // episode folds within it, "All episodes" folds over the whole game.
        EnemyAssembly.MarkRepeats(all, acrossLevels: true);
        // Bosses first, then the big structures: that is what anyone opens this list for.
        all.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank)
            : a.EpisodeIdx != b.EpisodeIdx ? a.EpisodeIdx.CompareTo(b.EpisodeIdx)
            : a.LevelFileNum != b.LevelFileNum ? a.LevelFileNum.CompareTo(b.LevelFileNum)
            : a.Time.CompareTo(b.Time));
        _assemblies = all;
        _assembliesFor = key;
        return all;
    }

    /// <summary>Every level the browser is showing groups from, in list order. Drives the level
    /// picker; the episode is part of the identity because names repeat across episodes (SAVARA
    /// is in three of them) and a level file number means nothing on its own.</summary>
    private List<(int Ep, int File, string Label)> AssemblyLevels()
    {
        var list = new List<(int, int, string)>();
        if (_gd == null) return list;
        foreach (int e in ShownEpisodes())
        {
            var ep = _gd.Episodes[e];
            var seen = new HashSet<int>();
            foreach (var item in ep.Levels)
            {
                if (!seen.Add(item.FileNum)) continue;
                string name = string.IsNullOrWhiteSpace(item.Name) ? $"level {item.FileNum}" : item.Name.Trim();
                list.Add((e, item.FileNum, AssemblyLevelLabel(e, item.FileNum, name)));
            }
        }
        return list;
    }

    /// <summary>
    /// How a level is named anywhere in this browser. The file number is not decoration: an
    /// episode can hold two different levels under one name (episode 1 has TYRIAN at #09 and
    /// #15, and SAVARA at #08 and #12), and without it a row, a picker entry and a spawn site
    /// are all ambiguous about which one they mean. Same shape as the main level list.
    /// </summary>
    private string AssemblyLevelLabel(int episodeIdx, int fileNum, string name) =>
        (_allEpisodes ? $"E{_gd!.Episodes[episodeIdx].Number}  " : "") + $"{fileNum:00}  {name}";

    /// <summary>Is this group spawned in the level the picker names? A folded run counts if any
    /// of its spawns is, which is the point of picking a level while "unique" is on.</summary>
    private bool InPickedLevel(EnemyAssembly a)
    {
        if (_asmLevelEp < 0) return true;
        if (a.EpisodeIdx == _asmLevelEp && a.LevelFileNum == _asmLevelFile) return true;
        foreach (var s in a.Sites)
            if (s.EpisodeIdx == _asmLevelEp && s.LevelFileNum == _asmLevelFile) return true;
        return false;
    }

    /// <summary>The rows the assembly list is showing: the browsed episodes narrowed by the
    /// level picker and the filter box, and — with "unique" on — with the repeats of one body
    /// folded onto the first of the run. The list and the detail pane must agree, so both
    /// read this.</summary>
    private List<EnemyAssembly> ShownAssemblies(bool? unique = null)
    {
        bool fold = unique ?? _asmUnique;
        string filter = BufText(_enemyFilter).Trim();
        return Assemblies()
            .Where(a => (!fold || a.RepeatOf == null) && InPickedLevel(a) && MatchesAssembly(a, filter))
            .ToList();
    }

    /// <summary>The typed filter, over the group's own level and every level a folded run
    /// reaches — so a search finds a body wherever it is listed, not only under its first use.</summary>
    private bool MatchesAssembly(EnemyAssembly a, string filter)
    {
        if (filter.Length == 0) return true;
        if (Matches(filter, a.LevelName, a.Title)) return true;
        foreach (var s in a.Sites)
            if (Matches(filter, s.LevelName)) return true;
        return false;
    }

    private void DrawAssemblyList()
    {
        var shown = ShownAssemblies();
        if (shown.Count == 0) { ImGui.TextDisabled("No multi-part groups found."); return; }

        // Follow the atlas: opening this on a level you are looking at should land on that
        // level's groups, not on whatever sorts first in the episode. A row named on the
        // command line wins, once, so a screenshot can be aimed at it.
        if (_asmAimed) { _asmAimed = false; _asmFollowedLevel = _levelFileNum; }
        if (_asmFollowedLevel != _levelFileNum)
        {
            _asmFollowedLevel = _levelFileNum;
            int at = shown.FindIndex(a => a.LevelFileNum == _levelFileNum);
            if (at >= 0) { _asmSelected = at; _asmScrollToSelection = true; }
        }
        _asmSelected = Math.Clamp(_asmSelected, 0, shown.Count - 1);

        string? lastGroup = null;
        for (int i = 0; i < shown.Count; i++)
        {
            var a = shown[i];
            uint col = a.IsBoss ? AcBoss : a.Rank == 1 ? AcRoutes : AcSim;
            if (a.KindPlural != lastGroup)
            {
                UiSection(a.KindPlural, col, shown.Count(x => x.KindPlural == a.KindPlural).ToString());
                lastGroup = a.KindPlural;
            }

            bool sel = i == _asmSelected;
            const float rowH = 34f;
            var box = UiRow($"##asm{i}", sel, col, rowH);
            if (box.Clicked) _asmSelected = i;
            if (sel && _asmScrollToSelection) { ImGui.SetScrollHereY(0.4f); _asmScrollToSelection = false; }

            // Parts and level first: the list is already grouped by kind, so repeating it on
            // every row would say nothing, and several groups share one level name. A run
            // spanning levels is named after the first and counts the rest.
            bool run = _asmUnique && a.RepeatCount > 1;
            // Most-useful first on the second line, because a narrow list column cuts the tail.
            // A folded run drops the link number -- every spawn in it carries a different one.
            RowText(box, 10f,
                $"{a.Parts.Count} part{(a.Parts.Count == 1 ? "" : "s")}  ·  " +
                AssemblyLevelLabel(a.EpisodeIdx, a.LevelFileNum, a.LevelName) +
                (run && a.LevelCount > 1 ? $"  +{a.LevelCount - 1}" : ""),
                (a.TotalArmor > 0 ? $"armour {a.TotalArmor}  ·  " : "") +
                (run ? $"×{a.RepeatCount}" +
                       (a.LevelCount > 1 ? $"  ·  in {a.LevelCount} levels" : $"  ·  from t={a.Time}")
                     : (a.LinkNum != 0 ? $"link {a.LinkNum}  ·  " : "") + $"t={a.Time}"),
                col, sel);
        }
    }

    /// <summary>
    /// Every place a folded run is used, click to go there. The preview and the link notes
    /// above describe the first spawn only, so without this a run of 27 says nothing about
    /// where the other 26 are.
    /// </summary>
    private void DrawAssemblySites(EnemyAssembly asm)
    {
        UiSection(asm.LevelCount > 1
            // No em dash: the ImGui font has Latin-1, so · is safe where — draws as a box.
            ? $"×{asm.RepeatCount} across {asm.LevelCount} levels"
            : $"×{asm.RepeatCount} in this level", AcLink, "click a spawn to open it there");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The same pieces in the same arrangement, spawned again with a fresh\n" +
                "set of link numbers. Turn \"unique\" off to list each one on its own.");

        // Fixed height rather than one row per spawn: some runs are 40 long, and the preview
        // below is the point of the pane.
        float h = Math.Clamp(asm.RepeatCount * 24f + 16f, 48f, 116f);
        WellBegin("asmsites", new Vector2(ImGui.GetContentRegionAvail().X, h), AcLink, 8f, 6f);
        int i = 0;
        foreach (var s in asm.Sites)
        {
            bool here = s.EpisodeIdx == _episodeIdx && s.LevelFileNum == _levelFileNum;
            var box = UiRow($"##site{i++}", here, AcLink, 22f);
            if (box.Clicked) OpenAssemblySite(asm, s);
            var dl = ImGui.GetWindowDrawList();
            string trail = $"t={s.Time}";
            ClipText(dl, new Vector2(box.Min.X + 10f, box.Min.Y + 2f),
                box.Max.X - box.Min.X - 10f - TrailRoom(trail),
                here ? Gfx.Rgba(250, 252, 255) : UiText,
                AssemblyLevelLabel(s.EpisodeIdx, s.LevelFileNum, s.LevelName));
            RowTrail(box, trail, UiFaint);
        }
        WellEnd();
    }

    /// <summary>
    /// The "damaged" checkbox for a group whose parts have a second form — CAMANIS's boss
    /// wrecks all over at armour 30, SAVARA's sits dormant until you get it low enough. Drawn
    /// only for a group that has one, since most of the list has nothing to show. Returns
    /// whether this group can be flipped at all.
    /// </summary>
    private bool DrawAssemblyDamagedToggle(EnemyAssembly asm, EnemyData ed)
    {
        int lo = int.MaxValue, hi = int.MinValue, wakes = 0, flips = 0;
        foreach (var p in asm.Parts)
        {
            if (p.EnemyId == 0) continue;
            var d = ed.Get(p.EnemyId);
            if (!d.Loaded || !PartFlips(d)) continue;
            flips++;
            lo = Math.Min(lo, d.DLevel);
            hi = Math.Max(hi, d.DLevel);
            if (d.DAni < 0) wakes++;
        }
        if (flips == 0) return false;

        UiToggle("damaged", ref _enemyDamaged, AcBoss,
            "Damage is not per part: crossing that armour on any one of them\n" +
            "flips every linked part with a damage level at the same instant\n" +
            "(tyrian2.c:2954). Parts without one keep the form they had.");
        ImGui.SameLine(0, 8);
        ImGui.AlignTextToFramePadding();
        // dani < 0 starts the part in its second form, so for those the flip is the way UP.
        string what = wakes == flips ? "wakes" : wakes == 0 ? "wrecks" : "changes form";
        ImGui.TextColored(ColorOf(UiDim),
            $"the whole link group {what} at armour <= {(lo == hi ? $"{lo}" : $"{lo}..{hi}")}" +
            (flips < asm.Parts.Count ? $"  ({flips} of {asm.Parts.Count} parts)" : ""));
        return true;
    }

    /// <summary>
    /// Open the level a spawn is in and go to it: the map scrolls there, playback seeks to the
    /// frame the group flies into. The parts come along so playback can tell which of the
    /// enemies around that moment are the ones that were asked for.
    /// </summary>
    private void OpenAssemblySite(EnemyAssembly asm, EnemyAssembly.SpawnSite s)
    {
        SelectLevelFile(s.EpisodeIdx, s.LevelFileNum);
        _pendingJump = new MapJump(s.Time,
            asm.Parts.Select(p => p.EnemyId).Where(id => id != 0).Distinct().ToArray());
        // The list follows the atlas's level, but not when the atlas followed the list:
        // re-snapping here would throw the selection off the group that was just opened.
        _asmFollowedLevel = _levelFileNum;
    }

    private void DrawAssemblyDetail()
    {
        var shown = ShownAssemblies();
        if (shown.Count == 0)
        {
            UiEmpty("Nothing to show", "No group matches the level picker and filter.", AcEnemy);
            return;
        }
        var asm = shown[Math.Clamp(_asmSelected, 0, shown.Count - 1)];
        var ep = _gd!.Episodes[asm.EpisodeIdx];
        var ed = TryEnemyDataFor(ep);
        if (ed == null) return;

        // Ask the simulation where the parts really end up. Only for the one on screen: it
        // runs the level, which is far too much to do for every group in the list. The call
        // itself only ever does the work once per group.
        try { asm.ResolveFromSim(_gd, ep, _gd.LoadLevel(ep, asm.LevelFileNum)); }
        catch { /* keep the authored layout */ }

        if (_asmOpenPending)
        {
            _asmOpenPending = false;
            OpenAssemblySite(asm, new EnemyAssembly.SpawnSite(
                asm.EpisodeIdx, asm.LevelFileNum, asm.LevelName, asm.Time));
        }

        bool respawned = _asmUnique && asm.RepeatCount > 1;
        uint col = asm.IsBoss ? AcBoss : AcRoutes;

        UiTitle(asm.LevelName.ToUpperInvariant(), col, asm.Title);
        Badge($"{asm.Parts.Count} parts", col);
        ImGui.SameLine(0, 5f);
        Badge($"armour {asm.TotalArmor}", AcBoss);
        ImGui.SameLine(0, 5f);
        Badge($"{asm.Width:0}x{asm.Height:0} px", Gfx.Rgba(150, 162, 185));
        ImGui.SameLine(0, 5f);
        Badge($"t={asm.Time}", Gfx.Rgba(150, 162, 185));

        ImGui.Dummy(new Vector2(0, 3f));
        ImGui.PushTextWrapPos(0f);
        if (asm.IsBoss && !asm.HasBossBar && !asm.KillsEverything)
            ImGui.TextColored(ColorOf(AcBoss),
                $"{asm.TileCount} distinct tiles: a set piece the level draws in full, " +
                "though it never arms a health bar for it");
        if (asm.KillsEverything)
            ImGui.TextColored(ColorOf(AcBoss),
                "link 254: killing any part clears the screen and can end the level");
        else if (asm.HasBossBar)
            ImGui.TextColored(ColorOf(AcBoss),
                $"link {asm.LinkNum}: the level draws a boss health bar for this group");
        else if (asm.SplitLinks)
            ImGui.TextColored(ColorOf(UiDim),
                $"links {string.Join(", ", asm.Links.Order())}: one body on screen, but the " +
                "cascade does not relate them — each link dies on its own");
        else if (asm.LinkNum >= 100)
            ImGui.TextColored(ColorOf(UiDim), $"link {asm.LinkNum}: killing it also kills group {asm.LinkNum - 100}");
        else if (asm.LinkNum > 40)
            ImGui.TextColored(ColorOf(UiDim),
                $"link {asm.LinkNum}: killing it also kills the lower links in bucket {asm.LinkNum / 20}");
        else if (asm.LinkNum != 0)
            ImGui.TextColored(ColorOf(UiDim), $"link {asm.LinkNum}: the parts die together");
        ImGui.PopTextWrapPos();

        ImGui.Dummy(new Vector2(0, 3f));
        // Always the group's OWN episode, never the browser's: in "All episodes" the list spans
        // the whole game, and taking the episode from the picker opened the same-numbered level
        // file of whatever episode happened to be current.
        if (!respawned && UiButton($"open {asm.LevelName}", col, "open that level at this group's spawn"))
            OpenAssemblySite(asm, new EnemyAssembly.SpawnSite(
                asm.EpisodeIdx, asm.LevelFileNum, asm.LevelName, asm.Time));

        // --- Damaged form, where the group has one ---
        bool showDamaged = _enemyDamaged && DrawAssemblyDamagedToggle(asm, ed);
        if (respawned) DrawAssemblySites(asm);

        // --- The group composited at its own offsets ---
        ImGui.Dummy(new Vector2(0, 3f));
        float stageH = Math.Max(160f, Math.Min(380f, ImGui.GetContentRegionAvail().Y - 170f));
        var mn2 = ImGui.GetCursorScreenPos();
        var mx2 = new Vector2(mn2.X + ImGui.GetContentRegionAvail().X, mn2.Y + stageH);
        var dl = ImGui.GetWindowDrawList();
        StageBegin(dl, mn2, mx2);

        float scale = Math.Max(1f, MathF.Floor(Math.Min(
            (mx2.X - mn2.X - 24f) / Math.Max(1f, asm.Width),
            (mx2.Y - mn2.Y - 24f) / Math.Max(1f, asm.Height))));
        scale = Math.Min(scale, MathF.Round(_enemyZoom));
        var origin = new Vector2(
            MathF.Round((mn2.X + mx2.X - asm.Width * scale) * 0.5f),
            MathF.Round((mn2.Y + mx2.Y - asm.Height * scale) * 0.5f));

        foreach (var p in asm.Parts)
        {
            var atlas = Atlas(EnemySpriteSource(p.ShapeBank), AppSettings.GamePalette);
            if (atlas == null) continue;
            // Animate through the entry's frames where it has some; the parts that events
            // 49-52 synthesise have no entry of their own, so they hold their one sprite.
            int sprite = p.Sprite;
            var d = ed.Get(p.EnemyId);
            if (p.EnemyId != 0 && d.Loaded)
            {
                // Only the parts the cascade reaches change; anything else on the group keeps
                // the form it had, exactly as the engine leaves it.
                bool flipped = showDamaged && PartFlips(d);
                if (flipped && !PartSurvivesFlip(d)) continue;
                if (flipped && d.DAni == 0)
                {
                    sprite = d.Dgr;    // dgr replaces frame 1 and the animation stops
                }
                else
                {
                    // Through the same gate the single-entry stage uses: a group is mostly
                    // guns, and an animate-2 part free-running was the strobe at its worst --
                    // a dozen of them out of step with each other.
                    var run = FrameRun(d, flipped);
                    if (run.Count > 0)
                        sprite = d.EGraphic[Math.Clamp(
                            run[RunningFrameIndex(d, run.Count, flipped)] - 1, 0, 19)];
                }
            }
            DrawEnemyFrame(dl, atlas, sprite, p.Big, origin + new Vector2(p.X * scale, p.Y * scale), scale);
        }
        UiHint(dl, new Vector2(mn2.X + 8f, mx2.Y - ImGui.GetTextLineHeight() - 14f),
            $"drawn at {scale:0}x  ·  the zoom slider caps it", col);
        StageEnd(dl);
        ImGui.Dummy(new Vector2(0, stageH + 5f));

        // --- Parts list ---
        DrawAssemblyParts(asm);
    }

    /// <summary>
    /// What the group is built from, one card per distinct piece. Grouped by what is drawn
    /// rather than by id: the parts events 49-52 synthesise all share id 0 and differ only in
    /// the sprite and bank the event handed them.
    /// </summary>
    private void DrawAssemblyParts(EnemyAssembly asm)
    {
        UiSection("parts", AcLink, "click one to open it");
        ImGui.BeginChild("asmparts", new Vector2(0, 0));

        float wide = ImGui.GetContentRegionAvail().X;
        // Wide enough for the widest line it can carry ("armour 255 · 2x2 · bank 26") at the
        // 40px text inset, and the text is clipped on top of that -- a part with a long line
        // used to print straight out through the side of its card.
        const float cardH = 34f;
        float cardW = Math.Max(150f, Math.Min(232f, wide));
        int cols = Math.Max(1, (int)(wide / (cardW + 5f)));
        int k = 0;
        foreach (var g in asm.Parts.GroupBy(p => (p.EnemyId, p.Sprite, p.ShapeBank))
                     .OrderBy(g => g.Key.EnemyId).ThenBy(g => g.Key.Sprite))
        {
            var (id, sprite, bank) = g.Key;
            var first = g.First();
            if (k % cols != 0) ImGui.SameLine(0, 5f);
            k++;

            var p = ImGui.GetCursorScreenPos();
            bool hit = ImGui.InvisibleButton($"##ap{id}_{sprite}_{bank}", new Vector2(cardW, cardH));
            bool hot = ImGui.IsItemHovered();
            var dl = ImGui.GetWindowDrawList();
            Card(dl, p, p + new Vector2(cardW, cardH), id != 0 ? AcEnemy : AcSprite, hot ? 0.18f : 0.07f);

            var atlas = Atlas(EnemySpriteSource(bank), AppSettings.GamePalette);
            if (atlas != null)
                DrawEnemyFrameCentered(dl, atlas, sprite, first.Big,
                    new Vector2(p.X + 4f, p.Y + 2f), new Vector2(p.X + 36f, p.Y + cardH - 2f), 1f);

            float lh = ImGui.GetTextLineHeight();
            float room = cardW - 46f;
            ClipText(dl, new Vector2(p.X + 40f, p.Y + 3f), room, hot ? Gfx.Rgba(248, 250, 255) : UiText,
                id != 0 ? $"#{id}  ×{g.Count()}" : $"spr {sprite}  ×{g.Count()}");
            ClipText(dl, new Vector2(p.X + 40f, p.Y + 3f + lh + 1f), room, UiFaint,
                $"armour {first.Armor}  ·  {(first.Big ? "2x2" : "1x1")}  ·  bank {bank}");

            if (hot)
                ImGui.SetTooltip(id != 0
                    ? "open this enemyDat entry"
                    : "An event-built part: no enemyDat entry of its own, just\n" +
                      "a sprite, a bank and an armour value carried by the event.");
            if (!hit) continue;
            if (id != 0) OpenEnemy(_enemyEpisodeIdx, id);
            else OpenSprite(EnemySpriteSource(bank), sprite);
        }
        ImGui.EndChild();
    }
}
