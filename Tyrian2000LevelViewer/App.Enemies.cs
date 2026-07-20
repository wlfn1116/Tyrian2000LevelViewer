using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;

namespace T2LV;

/// <summary>
/// The enemy browser: every enemyDat entry animated the way the engine animates it, plus the
/// multi-part groups the levels assemble out of them -- formations and bosses, which exist
/// nowhere in the data as single objects. See <see cref="EnemyAssembly"/>.
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
    private double _enemyClock;
    private int _enemyFrameHold = -1;       // a scrubbed frame, -1 = follow the clock
    private bool _enemyDamaged;             // preview the damaged/wreck graphic instead
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

        ImGui.SetNextWindowSize(new Vector2(1060, 720), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(620, 380), new Vector2(float.MaxValue, float.MaxValue));
        bool open = _showEnemies;
        if (!ImGui.Begin("Enemies###enemies", ref open)) { ImGui.End(); _showEnemies = open; return; }
        _showEnemies = open;

        if (_enemyAnimate) _enemyClock += ImGui.GetIO().DeltaTime * 35.0 * _enemyAnimSpeed;

        DrawEnemyToolbar();
        ImGui.Separator();

        float maxList = Math.Max(180f, ImGui.GetContentRegionAvail().X - 340f);
        _enemyListW = Math.Clamp(_enemyListW, 180f, maxList);

        ImGui.BeginChild("enlist", new Vector2(_enemyListW, 0), ImGuiChildFlags.Borders);
        if (_enemyMode == 0) DrawEnemyList(); else DrawAssemblyList();
        ImGui.EndChild();
        ImGui.SameLine(0, 2);
        VSplitter("##ensplit", ref _enemyListW, 180f, maxList);
        ImGui.SameLine(0, 2);

        ImGui.BeginChild("endetail", new Vector2(0, 0));
        if (_enemyMode == 0) DrawEnemyDetail(); else DrawAssemblyDetail();
        ImGui.EndChild();

        ImGui.End();
    }

    private void DrawEnemyToolbar()
    {
        ImGui.SetNextItemWidth(130);
        int before = _episodeIdx;
        EpisodeCombo("##enepisode");
        if (before != _episodeIdx) { _enemyEpisodeIdx = _episodeIdx; _enemySelected = -1; _asmSelected = 0; }

        ImGui.SameLine(0, 12);
        if (ImGui.RadioButton("Entries", _enemyMode == 0)) _enemyMode = 0;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Every enemyDat entry on its own.");
        ImGui.SameLine();
        if (ImGui.RadioButton("Assemblies", _enemyMode == 1)) _enemyMode = 1;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The multi-part groups the levels build out of those entries:\n" +
                "formations, and the bosses that are really a dozen linked\nenemies tiled on the sprite grid.");

        ImGui.SameLine(0, 12);
        FilterBox("##enfilter", _enemyMode == 0 ? "filter by id / bank" : "search levels", _enemyFilter, 150f);
        if (_enemyMode == 1) { ImGui.SameLine(0, 6); DrawAssemblyLevelPicker(); }

        ImGui.SameLine(0, 12);
        ImGui.SetNextItemWidth(90);
        ImGui.SliderFloat("zoom", ref _enemyZoom, 1f, 8f, "%.0fx");

        ImGui.SameLine(0, 12);
        if (ImGui.Checkbox("animate", ref _enemyAnimate)) _enemyFrameHold = -1;
        ImGui.SameLine(0, 6);
        ImGui.SetNextItemWidth(80);
        ImGui.SliderFloat("##enspeed", ref _enemyAnimSpeed, 0.1f, 3f, "x%.2f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The engine steps one frame per 35Hz tick; this scales that.");

        if (_enemyMode == 1)
        {
            ImGui.SameLine(0, 12);
            if (ImGui.Checkbox("unique", ref _asmUnique))
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
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Levels re-spawn the same body over and over -- GYGES hangs one\n" +
                    "eight-part chain nine times. List each of those once, marked xN,\n" +
                    "instead of once per spawn. Same pieces in the same arrangement\n" +
                    "counts as the same body; link numbers and map position do not.");
        }

        if (_enemyMode != 0) return;
        ImGui.SameLine(0, 12);
        ImGui.Checkbox("damaged", ref _enemyDamaged);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show the damaged form instead. An entry with dani != 0 loops a\n" +
                "different span of its own frames; with dani == 0 it swaps in the\nsingle sprite dgr names.");
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

        const float rowH = 32f;
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
        if (from > 0) ImGui.Dummy(new Vector2(1, from * rowH));

        var dl = ImGui.GetWindowDrawList();
        for (int k = from; k <= to; k++)
        {
            var (epIdx, id) = rows[k];
            var e = EnemyDatOf(epIdx, id);
            var mn = ImGui.GetCursorScreenPos();

            bool sel = id == _enemySelected && epIdx == _enemyEpisodeIdx;
            if (ImGui.Selectable($"##en{epIdx}_{id}", sel, ImGuiSelectableFlags.None,
                    new Vector2(0, rowH - 2f)))
            { _enemyEpisodeIdx = epIdx; _enemySelected = id; _enemyFrameHold = -1; }

            var cat = ObjectPlacer.Classify(e.Armor, e.Value, 0);
            uint col = ObjectPlacer.CategoryColor(cat);
            dl.AddRectFilled(mn, new Vector2(mn.X + 2.5f, mn.Y + rowH - 4f), col, 1f);

            var atlas = Atlas(EnemySpriteSource(e.ShapeBank), AppSettings.GamePalette);
            if (atlas != null)
                DrawEnemyFrameCentered(dl, atlas, e.EGraphic[0], e.Esize == 1,
                    new Vector2(mn.X + 6f, mn.Y), new Vector2(mn.X + 40f, mn.Y + rowH - 4f), 1f);

            dl.AddText(new Vector2(mn.X + 44f, mn.Y + 1f), Gfx.Rgba(226, 230, 240),
                _allEpisodes ? $"ep{_gd!.Episodes[epIdx].Number}  #{id}" : $"#{id}");
            dl.AddText(new Vector2(mn.X + 44f, mn.Y + 15f), Shade(col, 1f, 190),
                e.Armor > 0 ? $"armor {e.Armor}" : ObjectPlacer.CategoryName(cat).ToLowerInvariant());
        }
        if (to < rows.Count - 1) ImGui.Dummy(new Vector2(1, (rows.Count - 1 - to) * rowH));
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

    /// <summary>The sprite number to draw this instant.</summary>
    private int CurrentSprite(in EnemyDat e, bool damaged)
    {
        // dani == 0 with a dgr means the damaged form is one fixed sprite, not a frame span.
        if (damaged && e.DAni == 0) return e.Dgr > 0 ? e.Dgr : e.EGraphic[0];

        var run = FrameRun(e, damaged);
        if (run.Count == 0) return e.EGraphic[0];
        int at = _enemyFrameHold >= 0
            ? Math.Clamp(_enemyFrameHold, 0, run.Count - 1)
            : (int)(((long)_enemyClock) % run.Count);
        return e.EGraphic[Math.Clamp(run[at] - 1, 0, 19)];
    }

    private void DrawEnemyDetail()
    {
        var ed = TryEnemyData();
        if (ed == null || _enemySelected < 0) { ImGui.TextDisabled("Pick an enemy."); return; }
        var e = ed.Get(_enemySelected);
        if (!e.Loaded) { ImGui.TextDisabled("Empty enemyDat slot."); return; }

        var cat = ObjectPlacer.Classify(e.Armor, e.Value, 0);
        DrawGameHeader($"ENEMY {_enemySelected}", ObjectPlacer.CategoryColor(cat));

        var src = EnemySpriteSource(e.ShapeBank);
        var atlas = Atlas(src, AppSettings.GamePalette);
        bool damaged = _enemyDamaged && (e.Dgr > 0 || e.DAni != 0);

        // --- Stage ---
        const float stageH = 190f;
        var stageMin = ImGui.GetCursorScreenPos();
        var stageMax = new Vector2(stageMin.X + ImGui.GetContentRegionAvail().X, stageMin.Y + stageH);
        var dl = ImGui.GetWindowDrawList();
        DrawStarStage(dl, stageMin, stageMax);
        if (atlas != null)
            DrawEnemyFrameCentered(dl, atlas, CurrentSprite(e, damaged), e.Esize == 1,
                stageMin, stageMax, MathF.Round(_enemyZoom));
        else
            dl.AddText(stageMin + new Vector2(10f, 10f), Gfx.Rgba(200, 150, 150),
                $"shape bank {e.ShapeBank} has no sheet of its own -\n" +
                "in-game this entry draws with whatever bank was loaded last.");
        ImGui.Dummy(new Vector2(0, stageH + 4f));

        // --- Frame strip ---
        DrawFrameStrip(e, atlas, damaged);

        ImGui.Separator();
        if (ImGui.BeginTable("enstats", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn(); DrawEnemyStats(e, cat, src);
            ImGui.TableNextColumn(); DrawEnemyLinks(e, ed);
            ImGui.EndTable();
        }

        ImGui.Separator();
        DrawEnemyAppearances(_enemySelected);
    }

    /// <summary>A dark backdrop with a scatter of stars, so a sprite reads against something
    /// that looks like the game rather than against a flat panel.</summary>
    private static void DrawStarStage(ImDrawListPtr dl, Vector2 mn, Vector2 mx)
    {
        dl.AddRectFilled(mn, mx, Gfx.Rgba(8, 9, 14), 4f);
        // A fixed hash, so the field is stable frame to frame instead of twinkling randomly.
        for (int i = 0; i < 90; i++)
        {
            uint h = (uint)(i * 2654435761u);
            float x = mn.X + (h % 997) / 997f * (mx.X - mn.X);
            float y = mn.Y + ((h >> 10) % 991) / 991f * (mx.Y - mn.Y);
            byte b = (byte)(70 + (h >> 20) % 130);
            dl.AddRectFilled(new Vector2(x, y), new Vector2(x + 1f, y + 1f), Gfx.Rgba(b, b, (byte)Math.Min(255, b + 30)));
        }
        dl.AddRect(mn, mx, Gfx.Rgba(70, 80, 108, 190), 4f);
    }

    private void DrawFrameStrip(in EnemyDat e, SpriteAtlas? atlas, bool damaged)
    {
        var run = FrameRun(e, damaged);
        if (damaged && e.DAni == 0)
        {
            ImGui.TextDisabled(e.Dgr > 0
                ? $"damaged: sprite {e.Dgr} replaces frame 1, animation stops"
                : "this entry has no damaged form");
            return;
        }
        if (run.Count == 0)
        {
            ImGui.TextDisabled("this entry has no damaged form");
            return;
        }
        if (run.Count == 1 && damaged && e.Dgr > Math.Abs((int)e.DAni))
        {
            // dgr past |dani|: the cycle wraps on its first step and parks there.
            ImGui.TextDisabled($"damaged: parks on frame {e.Dgr}, animation stops");
        }
        if (run.Count == 1 && !damaged)
        {
            ImGui.TextDisabled(e.Animate == 0 ? "static (animate 0): frame 1 only" : "single frame");
            return;
        }

        int live = _enemyFrameHold >= 0
            ? Math.Clamp(_enemyFrameHold, 0, run.Count - 1)
            : (int)(((long)_enemyClock) % run.Count);

        const float cell = 30f;
        var dl = ImGui.GetWindowDrawList();
        for (int k = 0; k < run.Count; k++)
        {
            if (k > 0) ImGui.SameLine(0, 3);
            var mn = ImGui.GetCursorScreenPos();
            var mx = mn + new Vector2(cell, cell);
            bool hit = ImGui.InvisibleButton($"##fr{k}", new Vector2(cell, cell));
            bool hot = ImGui.IsItemHovered();

            dl.AddRectFilled(mn, mx, Gfx.Rgba(20, 22, 30), 2f);
            if (atlas != null)
                DrawEnemyFrameCentered(dl, atlas, e.EGraphic[Math.Clamp(run[k] - 1, 0, 19)],
                    e.Esize == 1, mn, mx, 1f);
            dl.AddRect(mn, mx, k == live ? AcLink : hot ? Gfx.Rgba(255, 255, 255, 110) : Gfx.Rgba(60, 66, 84), 2f);
            if (hot) ImGui.SetTooltip($"frame {run[k]}  ·  sprite {e.EGraphic[Math.Clamp(run[k] - 1, 0, 19)]}");
            if (hit) { _enemyFrameHold = k; _enemyAnimate = false; }
        }
        ImGui.SameLine(0, 10);
        if (_enemyFrameHold >= 0 && ImGui.SmallButton("resume##fr")) { _enemyFrameHold = -1; _enemyAnimate = true; }
        else if (_enemyFrameHold < 0) ImGui.TextDisabled("click a frame to hold it");
    }

    private void DrawEnemyStats(in EnemyDat e, ObjCategory cat, SpriteSource src)
    {
        ImGui.TextColored(ColorOf(ObjectPlacer.CategoryColor(cat)), ObjectPlacer.CategoryName(cat));
        ImGui.TextDisabled($"armor {e.Armor}   ·   value {e.Value}");
        ImGui.TextDisabled($"move {e.XMove},{e.YMove}   accel {e.XAccel},{e.YAccel}");
        if (e.XCAccel != 0 || e.YCAccel != 0)
            ImGui.TextDisabled($"cyclic accel {e.XCAccel},{e.YCAccel}  reverse at {e.XRev},{e.YRev}");
        ImGui.TextDisabled($"start {e.StartX},{e.StartY}" +
            (e.StartXC != 0 || e.StartYC != 0 ? $"  ± {e.StartXC},{e.StartYC}" : ""));
        ImGui.TextDisabled($"animate {e.Animate} ({AnimateName(e.Animate)}), {e.Ani} frames");
        ImGui.TextDisabled($"{(e.Esize == 1 ? "2x2 metasprite (24x28)" : "single sprite (12px wide)")}");
        ImGui.TextDisabled($"explosion {e.ExplosionType >> 1}, {(e.IsGround ? "ground" : "air")}");

        if (ImGui.SmallButton($"bank {e.ShapeBank}##gobank")) OpenSprite(src, e.EGraphic[0]);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"open {src.Title} in the sprite browser");

        // --- Turrets ---
        var turrets = new[] { (e.Tur0, e.Freq0, "down"), (e.Tur1, e.Freq1, "rotated right"), (e.Tur2, e.Freq2, "rotated left") };
        bool any = turrets.Any(t => t.Item1 != 0 && t.Item2 != 0);
        ImGui.Dummy(new Vector2(0, 3));
        ImGui.TextColored(ColorOf(AcSim), any ? "turrets" : "no turrets");
        for (int k = 0; k < 3; k++)
        {
            var (id, freq, dir) = turrets[k];
            if (id == 0 || freq == 0) continue;
            ImGui.BulletText($"{TurretName(id)}   every {freq} ticks   ({dir})");
            if (ImGui.IsItemHovered() && id < 251)
                ImGui.SetTooltip($"weapons[{id}] - the same table the shop's guns come from");
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
        // The right-hand table cell is narrow, so let the prose wrap inside it rather than
        // running under the window edge.
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(ColorOf(AcLink), "links");

        // launchtype splits into a type and a "special" above 1000 for entries under 1000.
        int launch = e.ELaunchType;
        if (launch > 0 && e.ELaunchFreq > 0)
        {
            int type = _enemySelected > 1000 ? launch : launch % 1000;
            int special = _enemySelected > 1000 ? 0 : launch / 1000;
            EnemyLinkRow("launches", type, ed,
                $"every {e.ELaunchFreq} ticks" + (special > 0 ? $", launch mode {special}" : ""));
        }
        else ImGui.TextDisabled("launches nothing");

        if (e.EEnemyDie > 0) EnemyLinkRow("dies into", e.EEnemyDie, ed, "spawned where the corpse fell");
        else ImGui.TextDisabled("leaves nothing behind");

        if (e.DLevel == -1)
            ImGui.TextDisabled($"leaves a wreck (sprite {e.Dgr}): drifts on, cannot be shot");
        else if (e.Dgr > 0 || e.DAni != 0)
            ImGui.TextDisabled(e.DAni < 0
                ? $"starts dormant; wakes at armor <= {e.DLevel}"
                : $"gets wrecked at armor <= {e.DLevel}");
        ImGui.PopTextWrapPos();
    }

    private void EnemyLinkRow(string label, int enemyId, EnemyData ed, string note)
    {
        var target = ed.Get(enemyId);
        var dl = ImGui.GetWindowDrawList();
        var mn = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(26f, 26f));
        if (target.Loaded)
        {
            var atlas = Atlas(EnemySpriteSource(target.ShapeBank), AppSettings.GamePalette);
            if (atlas != null)
                DrawEnemyFrameCentered(dl, atlas, target.EGraphic[0], target.Esize == 1,
                    mn, mn + new Vector2(26f, 26f), 1f);
        }
        ImGui.SameLine(0, 4);
        ImGui.BeginGroup();
        if (ImGui.SmallButton($"{label} #{enemyId}##lnk{label}{enemyId}")) OpenEnemy(_enemyEpisodeIdx, enemyId);
        ImGui.TextDisabled(note);
        ImGui.EndGroup();
    }

    /// <summary>Which levels spawn this entry, and when. Built once per episode.</summary>
    private void DrawEnemyAppearances(int enemyId)
    {
        var ep = EnemyEpisode;
        if (_gd == null || ep == null) return;
        if (_appearances == null || _appearancesFor != ep.Number) BuildAppearances(ep);

        if (_appearances!.TryGetValue(enemyId, out var sites) && sites.Count > 0)
        {
            ImGui.TextColored(ColorOf(AcRoutes), $"spawned in {sites.Select(s => s.FileNum).Distinct().Count()} level(s)");
            ImGui.BeginChild("enwhere", new Vector2(0, 0));
            foreach (var g in sites.GroupBy(s => s.FileNum))
            {
                var first = g.First();
                ushort firstTime = g.Min(s => s.Time);
                if (ImGui.SmallButton($"{first.Name} #{first.FileNum}##app{first.FileNum}"))
                {
                    SelectLevelFile(_enemyEpisodeIdx < 0 ? _episodeIdx : _enemyEpisodeIdx, first.FileNum);
                    // One entry, and it is the earliest spawn of it that the button names.
                    _pendingJump = new MapJump(firstTime, new[] { enemyId });
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("open this level at its first spawn of this entry\n" +
                        "(in playback, seek to the frame it flies into)");
                ImGui.SameLine(0, 8);
                ImGui.TextDisabled($"{g.Count()}x, first at t={firstTime}");
            }
            ImGui.EndChild();
        }
        else ImGui.TextDisabled("No level in this episode spawns it directly (it may only appear as a launch or death drop).");
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

        ImGui.SetNextItemWidth(160);
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

        // Follow the viewer: opening this on a level you are looking at should land on that
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
            if (a.KindPlural != lastGroup) { ImGui.SeparatorText(a.KindPlural); lastGroup = a.KindPlural; }

            var mn = ImGui.GetCursorScreenPos();
            bool sel = i == _asmSelected;
            if (ImGui.Selectable($"##asm{i}", sel, ImGuiSelectableFlags.None, new Vector2(0, 30f)))
                _asmSelected = i;
            if (sel && _asmScrollToSelection) { ImGui.SetScrollHereY(0.4f); _asmScrollToSelection = false; }
            var dl = ImGui.GetWindowDrawList();
            uint col = a.IsBoss ? AcBoss : a.Rank == 1 ? AcRoutes : AcSim;
            dl.AddRectFilled(mn, new Vector2(mn.X + 2.5f, mn.Y + 28f), col, 1f);
            // Parts and level first: the list is already grouped by kind, so repeating it on
            // every row would say nothing, and several groups share one level name. A run
            // spanning levels is named after the first and counts the rest.
            bool run = _asmUnique && a.RepeatCount > 1;
            dl.AddText(new Vector2(mn.X + 8f, mn.Y), Gfx.Rgba(228, 232, 242),
                $"{a.Parts.Count} part{(a.Parts.Count == 1 ? "" : "s")}  ·  " +
                AssemblyLevelLabel(a.EpisodeIdx, a.LevelFileNum, a.LevelName) +
                (run && a.LevelCount > 1 ? $"  +{a.LevelCount - 1}" : ""));
            // Most-useful first, because a narrow list column clips the tail. A folded run
            // drops the link number -- every spawn in it carries a different one.
            dl.AddText(new Vector2(mn.X + 8f, mn.Y + 14f), Shade(col, 1f, 190),
                (a.TotalArmor > 0 ? $"armour {a.TotalArmor}  ·  " : "") +
                (run ? $"×{a.RepeatCount}" +
                       (a.LevelCount > 1 ? $"  ·  in {a.LevelCount} levels" : $"  ·  from t={a.Time}")
                     : (a.LinkNum != 0 ? $"link {a.LinkNum}  ·  " : "") + $"t={a.Time}"));
        }
    }

    /// <summary>
    /// Every place a folded run is used, click to go there. The preview and the link notes
    /// above describe the first spawn only, so without this a run of 27 says nothing about
    /// where the other 26 are.
    /// </summary>
    private void DrawAssemblySites(EnemyAssembly asm)
    {
        ImGui.TextColored(ColorOf(AcLink), asm.LevelCount > 1
            // No em dash: the ImGui font has Latin-1, so · is safe where — draws as a box.
            ? $"×{asm.RepeatCount} across {asm.LevelCount} levels  ·  click a spawn to open it there"
            : $"×{asm.RepeatCount} in this level  ·  click a spawn to jump to it");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The same pieces in the same arrangement, spawned again with a fresh\n" +
                "set of link numbers. Turn \"unique\" off to list each one on its own.");

        // Fixed height rather than one row per spawn: some runs are 40 long, and the preview
        // below is the point of the pane.
        float h = Math.Clamp(asm.RepeatCount * ImGui.GetTextLineHeightWithSpacing() + 6f, 40f, 108f);
        ImGui.BeginChild("asmsites", new Vector2(0, h), ImGuiChildFlags.Borders);
        int i = 0;
        foreach (var s in asm.Sites)
        {
            bool here = s.EpisodeIdx == _episodeIdx && s.LevelFileNum == _levelFileNum;
            if (ImGui.Selectable(
                    $"{AssemblyLevelLabel(s.EpisodeIdx, s.LevelFileNum, s.LevelName),-24}  t={s.Time}##site{i++}",
                    here))
                OpenAssemblySite(asm, s);
        }
        ImGui.EndChild();
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

        ImGui.Checkbox("damaged##asmdmg", ref _enemyDamaged);
        ImGui.SameLine(0, 8);
        // dani < 0 starts the part in its second form, so for those the flip is the way UP.
        string what = wakes == flips ? "wakes" : wakes == 0 ? "wrecks" : "changes form";
        ImGui.TextDisabled($"the whole link group {what} at armour <= {(lo == hi ? $"{lo}" : $"{lo}..{hi}")}" +
            (flips < asm.Parts.Count ? $"  ({flips} of {asm.Parts.Count} parts)" : ""));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Damage is not per part: crossing that armour on any one of them\n" +
                "flips every linked part with a damage level at the same instant\n" +
                "(tyrian2.c:2954). Parts without one keep the form they had.");
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
        // The list follows the viewer's level, but not when the viewer followed the list:
        // re-snapping here would throw the selection off the group that was just opened.
        _asmFollowedLevel = _levelFileNum;
    }

    private void DrawAssemblyDetail()
    {
        var shown = ShownAssemblies();
        if (shown.Count == 0) { ImGui.TextDisabled("Nothing to show."); return; }
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
        DrawGameHeader(asm.LevelName.ToUpperInvariant(), asm.IsBoss ? AcBoss : AcRoutes);
        ImGui.TextDisabled($"{asm.Title}  ·  spawns at t={asm.Time}  ·  {asm.Width:0}x{asm.Height:0} px  ·  total armor {asm.TotalArmor}");
        if (asm.KillsEverything)
            ImGui.TextColored(ColorOf(AcBoss), "link 254: killing any part clears the screen and can end the level");
        else if (asm.HasBossBar)
            ImGui.TextColored(ColorOf(AcBoss), $"link {asm.LinkNum}: the level draws a boss health bar for this group");
        else if (asm.LinkNum >= 100)
            ImGui.TextDisabled($"link {asm.LinkNum}: killing it also kills group {asm.LinkNum - 100}");
        else if (asm.LinkNum > 40)
            ImGui.TextDisabled($"link {asm.LinkNum}: killing it also kills the lower links in bucket {asm.LinkNum / 20}");
        else if (asm.LinkNum != 0)
            ImGui.TextDisabled($"link {asm.LinkNum}: the parts die together");

        // Always the group's OWN episode, never the browser's: in "All episodes" the list spans
        // the whole game, and taking the episode from the picker opened the same-numbered level
        // file of whatever episode happened to be current.
        if (!respawned && ImGui.SmallButton($"open {asm.LevelName}##asmlvl"))
            OpenAssemblySite(asm, new EnemyAssembly.SpawnSite(
                asm.EpisodeIdx, asm.LevelFileNum, asm.LevelName, asm.Time));
        if (respawned) DrawAssemblySites(asm);

        // --- Damaged form, where the group has one ---
        bool showDamaged = _enemyDamaged && DrawAssemblyDamagedToggle(asm, ed);

        // --- The group composited at its own offsets ---
        float stageH = Math.Max(150f, Math.Min(360f, ImGui.GetContentRegionAvail().Y - 150f));
        var mn2 = ImGui.GetCursorScreenPos();
        var mx2 = new Vector2(mn2.X + ImGui.GetContentRegionAvail().X, mn2.Y + stageH);
        var dl = ImGui.GetWindowDrawList();
        DrawStarStage(dl, mn2, mx2);

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
                    var run = FrameRun(d, flipped);
                    if (run.Count > 0)
                        sprite = d.EGraphic[Math.Clamp(run[(int)(((long)_enemyClock) % run.Count)] - 1, 0, 19)];
                }
            }
            DrawEnemyFrame(dl, atlas, sprite, p.Big, origin + new Vector2(p.X * scale, p.Y * scale), scale);
        }
        ImGui.Dummy(new Vector2(0, stageH + 4f));

        // --- Parts list ---
        ImGui.TextDisabled("parts (click one to open it)");
        ImGui.BeginChild("asmparts", new Vector2(0, 0));
        // Group by what is drawn, not by id: the 49-52 parts all share id 0 and differ only
        // in the sprite and bank the event handed them.
        foreach (var g in asm.Parts.GroupBy(p => (p.EnemyId, p.Sprite, p.ShapeBank))
                     .OrderBy(g => g.Key.EnemyId).ThenBy(g => g.Key.Sprite))
        {
            var (id, sprite, bank) = g.Key;
            var first = g.First();
            if (id != 0)
            {
                if (ImGui.SmallButton($"#{id}##ap{id}_{sprite}")) OpenEnemy(_enemyEpisodeIdx, id);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("open this enemyDat entry");
            }
            else
            {
                if (ImGui.SmallButton($"spr {sprite}##ap0_{sprite}_{bank}"))
                    OpenSprite(EnemySpriteSource(bank), sprite);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("An event-built part: no enemyDat entry of its own, just\n" +
                        "a sprite, a bank and an armour value carried by the event.");
            }
            ImGui.SameLine(0, 8);
            ImGui.TextDisabled($"x{g.Count()}   armour {first.Armor}   " +
                $"{(first.Big ? "2x2" : "1x1")}   bank {bank}");
        }
        ImGui.EndChild();
    }
}
