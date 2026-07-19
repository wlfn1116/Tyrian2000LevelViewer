using System.Numerics;
using System.Runtime.Versioning;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;
using SdlNs = Hexa.NET.SDL2;

namespace T2LV;

/// <summary>The Dear ImGui application: navigation, layer/object toggles and the level canvas.</summary>
public sealed unsafe class App
{
    private readonly SdlNs.SDLRendererPtr _renderer;

    private GameData? _gd;
    private string _dataDir = "";
    private string _status = "";

    private int _episodeIdx;
    private int _levelIdx;
    private Level? _level;
    private ShapeTable? _shapes;
    private EnemyData? _enemyData;
    private LevelTimeline? _timeline;
    private List<PlacedObject> _objects = new();

    private readonly CompositeImage _img = new();
    private List<LayerDef> _layers = LayerStack.CreateDefault();  // front-to-back; drag to reorder
    private int _objMode = 0;                  // 0 = sprites, 1 = markers, 2 = off
    private int _palette = AppSettings.GamePalette;
    private bool _gameLayerOrder = true;       // reorder the stack per level to the in-game draw order
    private bool _composeDirty;

    // Canvas view state.
    private float _zoom = 1.0f;
    private Vector2 _scroll;
    private bool _viewInitialized;
    private string _hoverInfo = "";

    // Data-folder / export-folder picker.
    private enum PickPurpose { DataDir, ExportDir }
    private bool _showDirInput;
    private readonly byte[] _dirBuf = new byte[1024];
    private volatile bool _pickActive, _pickDone;   // native folder dialog runs on its own STA thread
    private volatile string? _pickResult, _pickError;
    private PickPurpose _pickPurpose = PickPurpose.DataDir;

    // Background PNG export.
    private volatile bool _exportActive;
    private volatile string? _exportDone;

    // --- Playback (in-game simulation) ---
    private SimPlayback? _playback;
    private bool _playbackMode;
    private readonly List<GameSim.EnemyView> _playEnemies = new();   // refilled per frame
    private int _objCatMask = ~0;      // layer-list category visibility, for markers/hover
    private bool _playing;
    private int _playDirection = 1;          // 1 forward, -1 rewind
    private float _playSpeed = 1f;
    private float _playAccum;
    private float _simScrollMult = 1f;
    private int _simDifficulty = 2;          // normal
    private bool _simFire = true;
    private int _simMaxMinutes = 10;
    private float _playZoom;                 // 0 = fit to the panel automatically
    private Vector2 _playPan;                // manual pan offset, screen px
    private bool _simExtendedView;           // show beyond the in-game screen
    private bool _showScreenFilter = true;   // event-44 hue/brightness filter
    private bool _showTerrainSmoothies = true; // lava/water/ice/blur
    private bool _showSpotlight = true;       // light-cone presentation
    private bool _showScreenFlip = true;      // vertical-flip presentation
    private bool _showBossBars = true;        // boss armor readout bars
    private readonly GameViewImage _gameView = new();
    private static readonly float[] SpeedSteps = { 0.25f, 0.5f, 1f, 2f, 4f, 8f };
    private static readonly string[] DifficultyNames =
        { "Wimp", "Easy", "Normal", "Hard", "Impossible", "Insanity",
          "Suicide", "Maniacal", "Zinglon", "Nortaneous", "Nortaneous 2" };

    private readonly SdlNs.SDLWindowPtr _window;
    private bool _scrollLevelListToSelection;      // keep the selection visible after keyboard/startup selects
    private bool _minimapDragging;
    private Vector2 _canvasAvail;                  // canvas size last frame, for key-driven jumps

    // Persisted UI state.
    private readonly AppSettings _settings;
    private int _levelFileNum = 1;             // currently selected level (survives reload)
    private int _pendingLevelIdx;              // >=0: select by list index once (CLI --start)
    private float _levelsHeight = 170f;        // resizable panel sections
    private float _layersHeight;               // 0 = fit to content
    private bool _restoreView;

    public App(SdlNs.SDLRendererPtr renderer, AppSettings settings, int cliEp = -1, int cliLevelIdx = -1,
        SdlNs.SDLWindowPtr window = default, int cliPlaybackTick = -1)
    {
        _renderer = renderer;
        _settings = settings;
        _window = window;
        if (cliPlaybackTick >= 0) _playbackMode = true;

        _palette = Math.Max(0, settings.Palette);
        _objMode = Math.Clamp(settings.ObjMode, 0, 2);
        _gameLayerOrder = settings.GameLayerOrder;
        _simExtendedView = settings.SimExtendedView;
        _showScreenFilter = settings.ShowScreenFilter;
        _showTerrainSmoothies = settings.ShowSmoothies;
        _showSpotlight = settings.ShowSpotlight ?? settings.ShowSmoothies;
        _showScreenFlip = settings.ShowScreenFlip ?? settings.ShowSmoothies;
        _showBossBars = settings.ShowBossBars;
        _levelsHeight = settings.LevelsHeight > 30 ? settings.LevelsHeight : 170f;
        _layersHeight = settings.LayersHeight > 30 ? settings.LayersHeight : 0f;  // 0 = fit to content
        ApplyLayerSettings(settings.Layers);

        _episodeIdx = cliEp >= 0 ? cliEp : settings.EpisodeIdx;
        _levelFileNum = settings.LevelFileNum;
        _pendingLevelIdx = cliLevelIdx;        // -1 unless --start given
        if (settings.HasView && cliEp < 0)
        {
            _zoom = settings.Zoom > 0 ? settings.Zoom : 1f;
            _scroll = new Vector2(settings.ScrollX, settings.ScrollY);
            _restoreView = true;
        }

        string? dir = (settings.DataDir != null && File.Exists(Path.Combine(settings.DataDir, "tyrian1.lvl")))
            ? settings.DataDir : GameData.FindDataDir();
        if (dir != null) LoadData(dir);
        else { _status = "Select your Tyrian 2000 folder with Browse..."; _showDirInput = true; }

        if (cliPlaybackTick > 0) _playback?.SeekTo(cliPlaybackTick);
    }

    private void LoadData(string dir)
    {
        try
        {
            _gd = new GameData(dir);
            _dataDir = dir;
            SetDirBuf(dir);
            _episodeIdx = Math.Clamp(_episodeIdx, 0, Math.Max(0, _gd.Episodes.Count - 1));
            _status = $"Loaded {_gd.Episodes.Count} episodes.";
            _showDirInput = false;
            if (_gd.Episodes.Count > 0)
            {
                int idx;
                if (_pendingLevelIdx >= 0) { idx = _pendingLevelIdx; _pendingLevelIdx = -1; }
                else { idx = CurEpisode!.Levels.FindIndex(l => l.FileNum == _levelFileNum); if (idx < 0) idx = 0; }
                SelectLevel(Math.Clamp(idx, 0, CurEpisode!.Levels.Count - 1), ensureVisible: true);
                if (_restoreView) _viewInitialized = true;   // keep the restored zoom/scroll
            }
        }
        catch (Exception ex)
        {
            _gd = null;
            _status = "Load failed: " + ex.Message;
            _showDirInput = true;
        }
    }

    /// <summary>Reorder/configure the live layer stack from persisted state (by stable id).</summary>
    private void ApplyLayerSettings(List<LayerState> saved)
    {
        if (saved == null || saved.Count == 0) return;
        var order = new Dictionary<string, int>();
        for (int i = 0; i < saved.Count; i++)
        {
            order[saved[i].Id] = i;
            var ly = _layers.Find(l => l.Id == saved[i].Id);
            if (ly != null) { ly.Visible = saved[i].Visible; ly.Alpha = Math.Clamp(saved[i].Alpha, 0, 255); }
        }
        // stable sort by saved index; layers not present keep their default relative order at the end
        _layers = _layers.OrderBy(l => order.TryGetValue(l.Id, out var i) ? i : int.MaxValue).ToList();
    }

    /// <summary>Gather the current UI state into <paramref name="s"/> for saving on exit.</summary>
    public void PopulateSettings(AppSettings s)
    {
        s.DataDir = _dataDir.Length > 0 ? _dataDir : s.DataDir;
        s.EpisodeIdx = _episodeIdx;
        s.LevelFileNum = _levelFileNum;
        s.Palette = _palette;
        s.ObjMode = _objMode;
        s.GameLayerOrder = _gameLayerOrder;
        s.SimExtendedView = _simExtendedView;
        s.ShowScreenFilter = _showScreenFilter;
        s.ShowSmoothies = _showTerrainSmoothies;
        s.ShowSpotlight = _showSpotlight;
        s.ShowScreenFlip = _showScreenFlip;
        s.ShowBossBars = _showBossBars;
        s.LevelsHeight = _levelsHeight;
        s.LayersHeight = _layersHeight;
        s.HasView = _viewInitialized;
        s.Zoom = _zoom; s.ScrollX = _scroll.X; s.ScrollY = _scroll.Y;
        s.Layers = _layers.Select(l => new LayerState { Id = l.Id, Visible = l.Visible, Alpha = l.Alpha }).ToList();
    }

    /// <summary>A full-width horizontal splitter bar; drag it to resize the section above.</summary>
    private void HSplitter(string id, ref float height, float min, float max)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Gfx.Rgba(95, 95, 120, 160));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Gfx.Rgba(130, 130, 175, 230));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Gfx.Rgba(160, 160, 210, 255));
        ImGui.Button(id, new Vector2(-1, 6));
        if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        if (ImGui.IsItemActive())
            height = Math.Clamp(height + ImGui.GetIO().MouseDelta.Y, min, max);
        ImGui.PopStyleColor(3);
    }

    private EpisodeInfo? CurEpisode => _gd != null && _episodeIdx < _gd.Episodes.Count ? _gd.Episodes[_episodeIdx] : null;

    private void SelectLevel(int levelListIdx, bool ensureVisible = false)
    {
        var ep = CurEpisode;
        if (ep == null || levelListIdx < 0 || levelListIdx >= ep.Levels.Count) return;
        _levelIdx = levelListIdx;
        _scrollLevelListToSelection |= ensureVisible;
        int fileNum = ep.Levels[levelListIdx].FileNum;
        _levelFileNum = fileNum;
        try
        {
            _level = _gd!.LoadLevel(ep, fileNum);
            _shapes = _gd.GetShapeTable(_level.ShapeChar);
            _enemyData = _gd.GetEnemyData(ep);
            _timeline = LevelTimeline.Build(_level);
            _layerScroll = new ObjectPlacer.LayerScroll();
            _objects = ObjectPlacer.Place(_gd, ep, _level, _enemyData, null, _layerScroll);
            if (_gameLayerOrder)
                _layers = LayerStack.GameOrder(_layers, _level.ComputeStartFlags());
            _playback = null;
            _playing = false;
            _playPan = Vector2.Zero;   // keep the zoom level, recenter the view
            if (_playbackMode) BuildPlayback();
            _composeDirty = true;
            _viewInitialized = false;
            string name = ep.Levels[levelListIdx].Name.Trim();
            _status = $"Ep {ep.Number} #{fileNum} '{name}'  ({_level.Events.Length} events, {_objects.Count} objects)";
            if (!_window.IsNull)
                SdlNs.SDL.SetWindowTitle(_window,
                    $"{(name.Length > 0 ? name : "(unnamed)")} · Episode {ep.Number} #{fileNum} - Tyrian 2000 Level Viewer");
        }
        catch (Exception ex)
        {
            _status = "Level load failed: " + ex.Message;
        }
    }

    /// <summary>Select the previous/next level in the current episode (keyboard).</summary>
    private void SelectLevelStep(int delta)
    {
        var ep = CurEpisode;
        if (ep == null || ep.Levels.Count == 0) return;
        int idx = Math.Clamp(_levelIdx + delta, 0, ep.Levels.Count - 1);
        if (idx != _levelIdx) SelectLevel(idx, ensureVisible: true);
    }

    /// <summary>
    /// Viewer-wide shortcuts: Up/Down switch levels, PageUp/PageDown pan the canvas a
    /// screenful, Home/End jump to the level top/bottom. In playback mode Space
    /// plays/pauses and Left/Right step ticks (Shift = 1 second). Inactive while
    /// typing a path or while a combo popup is open.
    /// </summary>
    private void HandleKeys()
    {
        if (_gd == null || _level == null) return;
        var io = ImGui.GetIO();
        if (io.WantTextInput || ImGui.IsAnyItemActive()) return;
        if (ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return;

        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true)) SelectLevelStep(1);
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true)) SelectLevelStep(-1);

        if (_playbackMode && _playback != null)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Space))
            {
                _playing = !_playing;
                _playDirection = 1;
                _playAccum = 0;
            }
            int step = io.KeyShift ? (int)GameSim.TicksPerSecond : 1;
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true))
            { _playing = false; _playback.SeekTo(_playback.CurrentTick + step); }
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true))
            { _playing = false; _playback.SeekTo(_playback.CurrentTick - step); }
            if (ImGui.IsKeyPressed(ImGuiKey.Home)) _playback.SeekTo(1);
            if (ImGui.IsKeyPressed(ImGuiKey.End)) _playback.SeekTo(_playback.Duration);
            return;
        }

        float page = Math.Max(64f, _canvasAvail.Y * 0.85f);
        if (ImGui.IsKeyPressed(ImGuiKey.PageUp, true)) _scroll.Y += page;
        if (ImGui.IsKeyPressed(ImGuiKey.PageDown, true)) _scroll.Y -= page;
        if (ImGui.IsKeyPressed(ImGuiKey.Home)) _scroll.Y = 0;
        if (ImGui.IsKeyPressed(ImGuiKey.End)) _scroll.Y = _canvasAvail.Y - CanvasHeight() * _zoom;
    }

    /// <summary>Build (or rebuild) the level simulation, keeping the timeline position.</summary>
    private void BuildPlayback()
    {
        if (_gd == null || _level == null || _shapes == null || CurEpisode == null) return;
        int keepTick = _playback?.CurrentTick ?? 1;
        try
        {
            var sim = new GameSim(_gd, CurEpisode, _level, _shapes)
            {
                Difficulty = _simDifficulty,
                ScrollMult = _simScrollMult,
                FireEnabled = _simFire,
                ExtendedDraw = _simExtendedView,
                ShowScreenFilter = _showScreenFilter,
                ShowTerrainSmoothies = _showTerrainSmoothies,
                ShowSpotlight = _showSpotlight,
                ShowScreenFlip = _showScreenFlip,
                ShowBossBars = _showBossBars,
            };
            _playback = new SimPlayback(sim, Math.Max(1, _simMaxMinutes) * 60 * 35);
            _playback.SeekTo(Math.Clamp(keepTick, 1, _playback.Duration));
            _status = $"Playback ready: {SimPlayback.FormatTime(_playback.Duration)} " +
                (_playback.EndedNaturally ? _playback.LoopDetected
                        ? $"(level end; {_playback.LoopSummary})" : "(level end)"
                    : _playback.LoopDetected ? $"({_playback.LoopSummary})" : "(capped)") +
                $", built in {_playback.PrecomputeMs} ms";
        }
        catch (Exception ex)
        {
            _playback = null;
            _playbackMode = false;
            _status = "Playback build failed: " + ex.Message;
        }
    }

    /// <summary>Advance/rewind the simulation with wall-clock pacing, then upload the frame.</summary>
    /// <summary>
    /// Mirror the layer stack and the object display mode onto the running simulation.
    /// These are draw-only flags, so a change just needs the current frame redrawn — the
    /// simulation itself is untouched and scrubbing stays exact. Per-layer opacity has no
    /// equivalent on the sim's 8-bit palette surface, so alpha 0 reads as hidden and any
    /// other value as visible.
    /// </summary>
    private void SyncPlaybackVisibility()
    {
        var sim = _playback!.Sim;
        bool bg1 = true, bg2 = true, bg3 = true, star = true;
        int mask = 0;
        foreach (var l in _layers)
        {
            bool on = l.Visible && l.Alpha > 0;
            switch (l.Kind)
            {
                case LayerKind.Background:
                    if (l.Slot == 0) bg1 = on; else if (l.Slot == 1) bg2 = on; else bg3 = on;
                    break;
                case LayerKind.Starfield: star = on; break;
                default: if (on) mask |= 1 << l.Slot; break;
            }
        }
        _objCatMask = mask;
        // Markers replaces the sprites with dots (as in the map view), Off hides both, so
        // in either case the simulation stops drawing them and the overlay takes over.
        if (_objMode != 0) mask = 0;

        if (sim.ShowBg1 == bg1 && sim.ShowBg2 == bg2 && sim.ShowBg3 == bg3 &&
            sim.ShowStarfield == star && sim.ObjectCategoryMask == mask)
            return;

        sim.ShowBg1 = bg1; sim.ShowBg2 = bg2; sim.ShowBg3 = bg3;
        sim.ShowStarfield = star; sim.ObjectCategoryMask = mask;
        _playback.RedrawCurrent();
    }

    private void UpdatePlayback()
    {
        if (_playback == null) return;
        SyncPlaybackVisibility();
        if (_playing)
        {
            _playAccum += ImGui.GetIO().DeltaTime * (float)GameSim.TicksPerSecond * _playSpeed;
            int n = (int)_playAccum;
            if (n > 0)
            {
                _playAccum -= n;
                n = Math.Min(n, (int)GameSim.TicksPerSecond * 8);   // avoid catch-up spirals
                if (_playDirection > 0)
                {
                    _playback.Advance(n);
                    if (_playback.AtEnd) _playing = false;
                }
                else
                {
                    _playback.SeekTo(_playback.CurrentTick - n);
                    if (_playback.CurrentTick <= 1) _playing = false;
                }
            }
        }
        var pal = _gd!.Palettes.Get(_palette);
        _playback.Sim.PreparePresent();
        if (_simExtendedView)
            _gameView.Update(_renderer, _playback.Sim.PresentScreen, pal,
                0, 0, GameSim.BufW, GameSim.BufH);
        else
            _gameView.Update(_renderer, _playback.Sim.PresentScreen, pal,
                GameSim.OX + GameSim.ViewX, GameSim.OY, GameSim.ViewW, GameSim.ViewH);
    }

    public void Render()
    {
        // Apply a completed native folder pick (the dialog runs on a background STA thread).
        if (_pickActive && _pickDone)
        {
            _pickActive = false;
            string? picked = _pickResult;
            string? error = _pickError;
            _pickResult = null;
            _pickError = null;
            if (!string.IsNullOrEmpty(picked))
            {
                if (_pickPurpose == PickPurpose.DataDir) TrySetDataDir(picked);
                else StartExport(picked);
            }
            else if (!string.IsNullOrEmpty(error)) _status = "Folder chooser failed: " + error;
        }

        if (_exportDone != null) { _status = _exportDone; _exportDone = null; }

        HandleKeys();

        if (_composeDirty && _level != null && _shapes != null)
        {
            var pal = _gd!.Palettes.Get(_palette);
            bool sprites = _objMode == 0;
            LevelRenderer.Compose(_img, _level, _shapes, pal, _layers, _objects,
                sprites, null, gameLayerOrder: _gameLayerOrder,
                rawGrids: true, layerScroll: _layerScroll);
            _composeDirty = false;
        }
        _img.Upload(_renderer);

        if (_playbackMode && _playback != null)
            UpdatePlayback();

        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.WorkPos);
        ImGui.SetNextWindowSize(vp.WorkSize);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGui.Begin("root", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNavFocus);
        ImGui.PopStyleVar();

        ImGui.BeginChild("controls", new Vector2(340, 0), ImGuiChildFlags.Borders);
        DrawControls();
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("canvas", new Vector2(0, 0), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        DrawCanvas();
        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawControls()
    {
        // --- Data folder (always available) ---
        ImGui.SeparatorText("Tyrian folder");
        string shown = _dataDir.Length == 0 ? "(none)" : Shorten(_dataDir, 38);
        ImGui.TextWrapped(shown);
        ImGui.BeginDisabled(_pickActive);
        if (ImGui.Button("Browse...")) { _pickPurpose = PickPurpose.DataDir; StartBrowse(); }
        ImGui.EndDisabled();
        ImGui.SameLine(); if (ImGui.Button(_showDirInput ? "Hide path" : "Type path...")) _showDirInput = !_showDirInput;
        if (_gd != null) { ImGui.SameLine(); if (ImGui.Button("Reload")) LoadData(_dataDir); }
        if (_pickActive) ImGui.TextDisabled("(choosing folder...)");
        if (_showDirInput) DataDirInput();

        if (_gd == null)
        {
            ImGui.Separator();
            ImGui.TextWrapped(_status);
            return;
        }

        // --- Episode ---
        ImGui.SeparatorText("Episode");
        var ep = CurEpisode!;
        if (ImGui.BeginCombo("##episode", $"Episode {ep.Number}"))
        {
            for (int i = 0; i < _gd.Episodes.Count; i++)
                if (ImGui.Selectable($"Episode {_gd.Episodes[i].Number}", i == _episodeIdx))
                { _episodeIdx = i; SelectLevel(0); }
            ImGui.EndCombo();
        }

        // --- Levels (resizable) ---
        ImGui.SeparatorText($"Levels ({ep.Levels.Count})");
        ImGui.BeginChild("levellist", new Vector2(0, _levelsHeight), ImGuiChildFlags.Borders);
        for (int i = 0; i < ep.Levels.Count; i++)
        {
            if (ImGui.Selectable(ep.Levels[i].Display + $"##lvl{i}", i == _levelIdx))
                SelectLevel(i);
            if (_scrollLevelListToSelection && i == _levelIdx)
                ImGui.SetScrollHereY(0.5f);
        }
        _scrollLevelListToSelection = false;
        ImGui.EndChild();
        HSplitter("##lvlsplit", ref _levelsHeight, 40f, 600f);

        // --- Layers (resizable, drag to reorder) ---
        ImGui.SeparatorText("Layers");
        bool gameOrder = _gameLayerOrder;
        if (ImGui.Checkbox("In-game draw order", &gameOrder))
        {
            _gameLayerOrder = gameOrder;
            if (gameOrder && _level != null)
            {
                _layers = LayerStack.GameOrder(_layers, _level.ComputeStartFlags());
                _composeDirty = true;
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Order each continuous section the way the level draws it in-game\n(background2over / background3over / topEnemyOver events).\nDragging a layer switches back to manual order.");
        ImGui.TextDisabled(_playbackMode
            ? "playback: visibility applies · order/opacity are the engine's"
            : "drag a name or use arrows · top = front");
        if (_layersHeight <= 30)   // first run: size the list to fit every row
            _layersHeight = _layers.Count * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y)
                + ImGui.GetStyle().WindowPadding.Y * 2f + 2f;
        DrawLayerList();
        HSplitter("##laysplit", ref _layersHeight, 60f, 700f);

        // --- Objects / view ---
        ImGui.SeparatorText("Objects (enemies / items)");
        int mode = _objMode;
        if (ImGui.Combo("display", &mode, new[] { "Sprites", "Markers", "Off" }, 3)) { _objMode = mode; _composeDirty = true; }
        if (ImGui.IsItemHovered() && _playbackMode)
            ImGui.SetTooltip("Markers: category dots instead of sprites, live from the simulation.\nOff hides both. Hovering the game view reports whatever is under the\ncursor in any mode.");

        // --- Playback (in-game simulation) ---
        ImGui.SeparatorText("Playback");
        bool pb = _playbackMode;
        if (ImGui.Checkbox("Playback mode", &pb))
        {
            _playbackMode = pb;
            _playing = false;
            if (pb && _playback == null) BuildPlayback();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Simulate the level exactly like in-game: enemies move, fire and\nlaunch, all level events run, camera locked to the player's view.\nSpace = play/pause, Left/Right = step (Shift = 1 s).");
        if (_playbackMode && _playback != null)
        {
            int dif = _simDifficulty;
            ImGui.PushItemWidth(140);
            if (ImGui.Combo("difficulty", &dif, DifficultyNames, DifficultyNames.Length))
            { _simDifficulty = dif; BuildPlayback(); }

            float mult = _simScrollMult;
            if (ImGui.SliderFloat("scroll speed", &mult, 0.25f, 3f, "x%.2f"))
                _simScrollMult = mult;
            if (ImGui.IsItemDeactivatedAfterEdit()) BuildPlayback();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("What-if terrain scroll multiplier. The level's own variable\nscroll-rate events still apply on top; the event clock follows\nthe terrain, so spawn pacing changes with it.");

            bool fire = _simFire;
            if (ImGui.Checkbox("enemy fire", &fire)) { _simFire = fire; BuildPlayback(); }
            ImGui.SameLine();
            int cap = _simMaxMinutes;
            ImGui.PushItemWidth(90);
            if (ImGui.SliderInt("cap (min)", &cap, 1, 30))
                _simMaxMinutes = cap;
            if (ImGui.IsItemDeactivatedAfterEdit()) BuildPlayback();
            ImGui.PopItemWidth();
            ImGui.PopItemWidth();

            // view-only options: instant, no rebuild
            bool extv = _simExtendedView;
            if (ImGui.Checkbox("extended view", &extv))
            {
                _simExtendedView = extv;
                _playback.Sim.ExtendedDraw = extv;
                _playback.RedrawCurrent();
                _playZoom = 0;
                _playPan = Vector2.Zero;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Zoom out beyond the in-game screen: full map width plus\nenemies/terrain before they scroll in. The yellow rectangle\nmarks what the player actually sees.");

            bool sm = _showTerrainSmoothies;
            if (ImGui.Checkbox("terrain smoothies", &sm))
            {
                _showTerrainSmoothies = sm;
                _playback.Sim.ShowTerrainSmoothies = sm;
                _playback.RedrawCurrent();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Lava, water, iced-blur, and blur feedback effects.");
            ImGui.SameLine();
            bool sf = _showScreenFilter;
            if (ImGui.Checkbox("color / fades", &sf))
            {
                _showScreenFilter = sf;
                _playback.Sim.ShowScreenFilter = sf;
                _playback.RedrawCurrent();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Full-screen event-44 color, darken, lighten, and fade effects.");

            bool spot = _showSpotlight;
            if (ImGui.Checkbox("spotlight", &spot))
            {
                _showSpotlight = spot;
                _playback.Sim.ShowSpotlight = spot;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The light-cone presentation only; terrain smoothies stay unchanged.");
            ImGui.SameLine();
            bool flip = _showScreenFlip;
            if (ImGui.Checkbox("screen flip", &flip))
            {
                _showScreenFlip = flip;
                _playback.Sim.ShowScreenFlip = flip;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The event-driven upside-down playfield presentation.");

            bool bossbars = _showBossBars;
            if (ImGui.Checkbox("boss bars", &bossbars))
            {
                _showBossBars = bossbars;
                _playback.Sim.ShowBossBars = bossbars;
                _playback.RedrawCurrent();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The boss/enemy armor readout bars drawn across the top of the screen.");

            var sim = _playback.Sim;
            ImGui.TextDisabled($"tick {_playback.CurrentTick}/{_playback.Duration}  loc {sim.CurLoc}  " +
                $"enemies {sim.EnemyOnScreen}");
            var (b1, b2, b3) = sim.BackMoves;
            ImGui.TextDisabled($"scroll {b1}/{b2}/{b3}  " +
                (_playback.EndedNaturally ? _playback.LoopDetected
                        ? $"ends naturally; {_playback.LoopSummary}" : "ends naturally"
                    : _playback.LoopDetected ? _playback.LoopSummary : "capped (no natural end)"));
        }

        // --- Palette ---
        ImGui.SeparatorText("Palette");
        int pal = _palette;
        if (ImGui.SliderInt("index", &pal, 0, Math.Max(0, _gd.Palettes.Count - 1)))
        { _palette = pal; _composeDirty = true; }
        if (_palette != AppSettings.GamePalette)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"in-game ({AppSettings.GamePalette})"))
            { _palette = AppSettings.GamePalette; _composeDirty = true; }
        }
        else ImGui.TextDisabled($"palette {AppSettings.GamePalette} = the in-game gameplay palette");

        // --- View ---
        ImGui.SeparatorText("View");
        float z = _zoom;
        if (ImGui.SliderFloat("zoom", &z, MinZoom, MaxZoom, "%.2f", ImGuiSliderFlags.Logarithmic))
            _zoom = Math.Clamp(z, MinZoom, MaxZoom);
        if (ImGui.Button("Fit width")) FitWidth();
        ImGui.SameLine(); if (ImGui.Button("1:1")) { _zoom = 1f; CenterBottom(); }
        ImGui.SameLine(); if (ImGui.Button("Top")) _scroll.Y = 0;
        ImGui.SameLine(); if (ImGui.Button("Bottom")) _scroll.Y = _canvasAvail.Y - CanvasHeight() * _zoom;

        ImGui.BeginDisabled(_level == null || _exportActive);
        if (ImGui.Button("Save level PNG...")) { _pickPurpose = PickPurpose.ExportDir; StartBrowse(); }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Write the whole composited level (current layers/palette)\nas a PNG into a folder you choose.");
        if (_exportActive) { ImGui.SameLine(); ImGui.TextDisabled("(saving...)"); }

        if (_level != null)
        {
            ImGui.SeparatorText("Level info");
            ImGui.Text($"shapes{char.ToLower(_level.ShapeChar)}.dat   events {_level.Events.Length}");
            ImGui.Text($"objects {_objects.Count}   enemy pool {_level.LevelEnemy.Length}");
            if (_timeline?.IsUnrolled == true)
                ImGui.Text($"continuous length {_timeline.Distance:N0} px");
        }
        ImGui.SeparatorText("Status");
        ImGui.TextWrapped(_status);
        if (_hoverInfo.Length > 0) ImGui.TextWrapped(_hoverInfo);
    }

    private void DataDirInput()
    {
        Span<byte> label = stackalloc byte[8];
        int ln = System.Text.Encoding.ASCII.GetBytes("##dir", label); label[ln] = 0;
        fixed (byte* lp = label)
        fixed (byte* p = _dirBuf)
            ImGui.InputText(lp, p, (nuint)_dirBuf.Length);
        if (ImGui.Button("Load folder"))
        {
            int len = Array.IndexOf(_dirBuf, (byte)0);
            if (len < 0) len = _dirBuf.Length;
            string dir = System.Text.Encoding.UTF8.GetString(_dirBuf, 0, len).Trim();
            if (dir.Length > 0) TrySetDataDir(dir);
        }
    }

    /// <summary>Validate a chosen folder (accept it directly, or search within it) and load.</summary>
    private void TrySetDataDir(string dir)
    {
        if (File.Exists(Path.Combine(dir, "tyrian1.lvl"))) LoadData(dir);
        else
        {
            var found = GameData.FindDataDir(dir);
            if (found != null) LoadData(found);
            else { _status = "No Tyrian 2000 data files found in: " + dir; _showDirInput = true; }
        }
    }

    /// <summary>Open the native Windows folder picker on a background STA thread.</summary>
    private void StartBrowse()
    {
        if (_pickActive) return;
        if (!OperatingSystem.IsWindows())
        {
            if (_pickPurpose == PickPurpose.ExportDir) StartExport(Environment.CurrentDirectory);
            else _showDirInput = true;
            return;
        }
        StartBrowseWindows();
    }

    /// <summary>Write the current composited level to a PNG in the chosen folder.</summary>
    private void StartExport(string dir)
    {
        var ep = CurEpisode;
        if (_exportActive || _level == null || ep == null || _img.Width <= 0) return;

        string name = _levelIdx < ep.Levels.Count ? ep.Levels[_levelIdx].Name.Trim() : "";
        if (name.Length == 0) name = "unnamed";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        string path = Path.Combine(dir, $"ep{ep.Number}_{_levelFileNum:00}_{name.Replace(' ', '_')}.png");

        // Snapshot the pixels so palette/layer changes during the write can't tear the file.
        var pixels = (uint[])_img.Pixels.Clone();
        int w = _img.Width, h = _img.Height;
        _exportActive = true;
        _status = "Saving " + path + " ...";
        var th = new Thread(() =>
        {
            try { Util.Png.WriteRgba(path, w, h, pixels); _exportDone = $"Saved {path} ({w}x{h})"; }
            catch (Exception ex) { _exportDone = "PNG save failed: " + ex.Message; }
            finally { _exportActive = false; }
        }) { IsBackground = true, Name = "PngExport" };
        th.Start();
    }

    [SupportedOSPlatform("windows")]
    private void StartBrowseWindows()
    {
        _pickActive = true; _pickDone = false; _pickResult = null; _pickError = null;
        string init = _dataDir;
        string title = _pickPurpose == PickPurpose.ExportDir
            ? "Choose where to save the level PNG"
            : "Select your Tyrian 2000 folder";
        IntPtr owner = NativeFolderDialog.ForegroundWindow();
        var th = new Thread(() =>
        {
            try { _pickResult = NativeFolderDialog.PickBlocking(init, owner, title); }
            catch (Exception ex)
            {
                _pickError = ex.Message;
                Console.Error.WriteLine("Folder chooser failed: " + ex);
            }
            finally { _pickDone = true; }
        }) { IsBackground = true, Name = "FolderPicker" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
    }

    private void SetDirBuf(string s)
    {
        Array.Clear(_dirBuf);
        var b = System.Text.Encoding.UTF8.GetBytes(s);
        Array.Copy(b, _dirBuf, Math.Min(b.Length, _dirBuf.Length - 1));
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : "..." + s[^(max - 3)..];

    /// <summary>
    /// The reorderable layer stack. Each row: visibility checkbox, colour swatch (object
    /// categories), the name (grab and drag it onto another row to reorder), ▲▼ buttons
    /// for one-step moves, and an opacity slider. Top of the list = drawn in front.
    /// </summary>
    private void DrawLayerList()
    {
        ImGui.BeginChild("layerlist", new Vector2(0, _layersHeight), ImGuiChildFlags.Borders);

        int moveFrom = -1, moveTo = -1;   // applied after the loop
        for (int i = 0; i < _layers.Count; i++)
        {
            var ly = _layers[i];
            ImGui.PushID(ly.Id);

            bool vis = ly.Visible;
            if (ImGui.Checkbox("##vis", &vis)) { ly.Visible = vis; _composeDirty = true; }
            ImGui.SameLine(0, 4);

            // colour swatch for object categories (spacer otherwise, to keep names aligned)
            float h = ImGui.GetTextLineHeight();
            var p = ImGui.GetCursorScreenPos();
            if (ly.Kind == LayerKind.Objects)
                ImGui.GetWindowDrawList().AddRectFilled(new Vector2(p.X, p.Y + 2), new Vector2(p.X + h, p.Y + h), ly.Swatch);
            ImGui.Dummy(new Vector2(h, h));
            ImGui.SameLine(0, 4);

            // name = drag handle (drag-and-drop source + target)
            ImGui.Selectable(ly.Name, false, ImGuiSelectableFlags.None, new Vector2(128, 0));
            if (ImGui.IsItemHovered() && !ImGui.IsMouseDragging(ImGuiMouseButton.Left)) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
            {
                int payload = i;
                ImGui.SetDragDropPayload("LAYER", &payload, (nuint)sizeof(int));
                ImGui.Text(ly.Name);            // "ghost" that follows the cursor
                ImGui.EndDragDropSource();
            }
            if (ImGui.BeginDragDropTarget())
            {
                var pl = ImGui.AcceptDragDropPayload("LAYER");
                if (pl.Handle != null && pl.Data != null) { moveFrom = *(int*)pl.Data; moveTo = i; }
                ImGui.EndDragDropTarget();
            }

            // ▲▼ one-step moves (reliable fallback for dragging)
            ImGui.SameLine(0, 4);
            ImGui.BeginDisabled(i == 0);
            if (ImGui.ArrowButton("##up", ImGuiDir.Up)) { moveFrom = i; moveTo = i - 1; }
            ImGui.EndDisabled();
            ImGui.SameLine(0, 2);
            ImGui.BeginDisabled(i == _layers.Count - 1);
            if (ImGui.ArrowButton("##dn", ImGuiDir.Down)) { moveFrom = i; moveTo = i + 1; }
            ImGui.EndDisabled();

            ImGui.SameLine(0, 4);
            int pct = (int)MathF.Round(ly.Alpha / 255f * 100f);
            ImGui.PushItemWidth(52);
            if (ImGui.SliderInt("##op", &pct, 0, 100, "%d%%"))
            { ly.Alpha = (int)MathF.Round(Math.Clamp(pct, 0, 100) / 100f * 255f); _composeDirty = true; }
            ImGui.PopItemWidth();

            ImGui.PopID();
        }
        ImGui.EndChild();

        if (moveFrom >= 0 && moveTo >= 0 && moveFrom != moveTo)
        {
            var item = _layers[moveFrom];
            _layers.RemoveAt(moveFrom);
            _layers.Insert(Math.Clamp(moveTo, 0, _layers.Count), item);
            _gameLayerOrder = false;   // manual order takes over until re-checked
            _composeDirty = true;
        }
    }

    /// <summary>Is the object category visible per the layer stack? (used for markers)</summary>
    private bool CategoryVisible(ObjCategory cat)
    {
        foreach (var ly in _layers)
            if (ly.Kind == LayerKind.Objects && ly.Slot == (int)cat)
                return ly.Visible && ly.Alpha > 0;
        return true;
    }

    /// <summary>Is the given background grid (0/1/2) visible per the layer stack? (used for hover)</summary>
    private bool BgVisible(int bg)
    {
        foreach (var ly in _layers)
            if (ly.Kind == LayerKind.Background && ly.Slot == bg)
                return ly.Visible;
        return true;
    }

    private ObjectPlacer.LayerScroll? _layerScroll;

    private int CanvasHeight() => LevelRenderer.RawHeight(_layerScroll);
    private int ObjYOffset() => 0;   // objects are placed in absolute canvas coordinates

    private const float MinZoom = 0.05f, MaxZoom = 16f;
    private const float MinimapW = 42f, MinimapMargin = 8f;

    /// <summary>Minimap strip rect at the canvas' right edge; null when the level fits the view.</summary>
    private (Vector2 Min, Vector2 Max)? MinimapRect(Vector2 canvasPos, Vector2 avail)
    {
        if (_level == null) return null;
        if (CanvasHeight() * _zoom <= avail.Y * 1.02f) return null;   // whole level visible
        float stripH = avail.Y - MinimapMargin * 2;
        if (stripH < 120 || avail.X < MinimapW * 5) return null;
        var p0 = new Vector2(canvasPos.X + avail.X - MinimapW - MinimapMargin, canvasPos.Y + MinimapMargin);
        return (p0, p0 + new Vector2(MinimapW, stripH));
    }

    private void DrawCanvas()
    {
        if (_playbackMode && _playback != null)
        {
            DrawPlaybackCanvas();
            return;
        }

        var canvasPos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 16 || avail.Y < 16) return;
        _canvasAvail = avail;

        if (!_viewInitialized && _level != null) { FitWidth(); _viewInitialized = true; }

        ImGui.InvisibleButton("canvas_btn", avail,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();
        bool panActive = ImGui.IsItemActive() && !_minimapDragging;
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();

        var minimap = MinimapRect(canvasPos, avail);
        bool inMinimap = minimap is { } mm &&
            mouse.X >= mm.Min.X && mouse.X < mm.Max.X && mouse.Y >= mm.Min.Y && mouse.Y < mm.Max.Y;

        if (hovered && !inMinimap)
        {
            float wheel = io.MouseWheel;
            if (wheel != 0 && io.KeyShift)
            {
                _scroll.Y += wheel * avail.Y * 0.25f;   // fast vertical travel
            }
            else if (wheel != 0)
            {
                float newZoom = Math.Clamp(_zoom * MathF.Pow(1.15f, wheel), MinZoom, MaxZoom);
                Vector2 rel = mouse - canvasPos - _scroll;
                _scroll = mouse - canvasPos - rel * (newZoom / _zoom);
                _zoom = newZoom;
            }
            if (io.MouseWheelH != 0) _scroll.X -= io.MouseWheelH * avail.X * 0.1f;
        }
        // Drag-panning sticks to the canvas item, so it keeps working when the cursor
        // momentarily leaves the window.
        if (panActive &&
            (ImGui.IsMouseDragging(ImGuiMouseButton.Left) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle)))
            _scroll += io.MouseDelta;

        ClampScroll(avail);

        var dl = ImGui.GetWindowDrawList();
        var clipMin = canvasPos;
        var clipMax = canvasPos + avail;
        dl.PushClipRect(clipMin, clipMax, true);
        dl.AddRectFilled(clipMin, clipMax, Gfx.Rgba(18, 18, 22));

        Vector2 origin = canvasPos + _scroll;
        if (_level != null)
        {
            var ext = new Vector2(LevelRenderer.CanvasW, CanvasHeight()) * _zoom;
            dl.AddRectFilled(origin, origin + ext, Gfx.Rgba(8, 8, 10));
            _img.Draw(dl, origin, _zoom);
            dl.AddRect(origin, origin + ext, Gfx.Rgba(70, 70, 80));

            if (_objMode == 1) DrawMarkers(dl, origin, mouse, hovered && !inMinimap);
            else if (_objMode == 0 && hovered && !inMinimap && !panActive)
                DrawSpriteHover(dl, origin, mouse);
            UpdateHoverInfo(origin, mouse, hovered && !inMinimap);
            DrawMinimap(dl, minimap, avail);
        }
        else
        {
            dl.AddText(canvasPos + new Vector2(20, 20), Gfx.Rgba(200, 200, 200), "No level loaded.");
        }

        dl.AddText(new Vector2(clipMin.X + 8, clipMax.Y - 20), Gfx.Rgba(180, 180, 190),
            $"zoom {(_zoom * 100):0}%   |  wheel = zoom · shift+wheel = scroll · drag = pan · Up/Down = level · PgUp/PgDn/Home/End");
        dl.PopClipRect();
    }

    // =====================================================================
    //  Playback canvas: locked in-game view + transport + timeline.
    // =====================================================================
    private void DrawPlaybackCanvas()
    {
        var pb = _playback!;
        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 60 || avail.Y < 100) return;
        _canvasAvail = avail;

        float controlsH = ImGui.GetFrameHeightWithSpacing() + 34f;
        var viewSize = new Vector2(avail.X, Math.Max(60, avail.Y - controlsH));
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        // --- the locked game view (wheel = zoom, drag = pan, double-click = fit) ---
        int vw = _gameView.W, vh = _gameView.H;
        if (vw <= 0 || vh <= 0) return;
        float fit = MathF.Min(viewSize.X / vw, viewSize.Y / vh);
        if (fit >= 1f) fit = MathF.Floor(fit);   // pixel-perfect integer fit
        float scale = _playZoom > 0 ? _playZoom : fit;

        ImGui.InvisibleButton("playview", viewSize,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle);
        bool viewHovered = ImGui.IsItemHovered();
        bool viewActive = ImGui.IsItemActive();
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();

        Vector2 CenterOff(float s) => new(
            MathF.Floor((viewSize.X - vw * s) * 0.5f),
            MathF.Floor((viewSize.Y - vh * s) * 0.5f));
        var imgPos = pos + CenterOff(scale) + _playPan;

        if (viewHovered && io.MouseWheel != 0)
        {
            float newScale = Math.Clamp(scale * MathF.Pow(1.15f, io.MouseWheel), 0.25f, 16f);
            if (MathF.Abs(newScale - MathF.Round(newScale)) < 0.08f)
                newScale = MathF.Round(newScale);   // settle on crisp integer steps
            if (newScale != scale)
            {
                Vector2 rel = (mouse - imgPos) / scale;   // keep the pixel under the cursor
                _playPan = mouse - pos - CenterOff(newScale) - rel * newScale;
                _playZoom = newScale;
                scale = newScale;
            }
        }
        if (viewActive &&
            (ImGui.IsMouseDragging(ImGuiMouseButton.Left) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle)))
            _playPan += io.MouseDelta;
        if (viewHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        { _playZoom = 0; _playPan = Vector2.Zero; scale = fit; }

        // keep at least a sliver of the frame inside the panel
        var imgSize = new Vector2(vw, vh) * scale;
        var cOff = CenterOff(scale);
        const float keep = 40f;
        _playPan.X = Math.Clamp(_playPan.X, keep - imgSize.X - cOff.X, viewSize.X - keep - cOff.X);
        _playPan.Y = Math.Clamp(_playPan.Y, keep - imgSize.Y - cOff.Y, viewSize.Y - keep - cOff.Y);
        imgPos = pos + cOff + _playPan;

        dl.AddRectFilled(pos, pos + viewSize, Gfx.Rgba(10, 10, 12));
        dl.PushClipRect(pos, pos + viewSize, true);
        _gameView.Draw(dl, imgPos, scale);
        dl.AddRect(imgPos - new Vector2(1, 1), imgPos + imgSize + new Vector2(1, 1),
            Gfx.Rgba(80, 80, 95));
        if (_simExtendedView)
        {
            // the actual in-game viewport within the extended field
            var vp0 = imgPos + new Vector2((GameSim.OX + GameSim.ViewX) * scale, GameSim.OY * scale);
            var vp1 = vp0 + new Vector2(GameSim.ViewW * scale, GameSim.ViewH * scale);
            dl.AddRect(vp0, vp1, Gfx.Rgba(255, 225, 120, 200), 0f, 0, 1.5f);
            dl.AddText(vp0 + new Vector2(4, 2), Gfx.Rgba(255, 225, 120, 170), "screen");
        }
        DrawPlaybackOverlay(dl, pb, imgPos, scale, vw, vh, mouse,
            viewHovered && !viewActive, pos, viewSize);
        string loopOsd = "";
        var activeGate = pb.LoopRegions.FirstOrDefault(r =>
            pb.CurrentTick >= r.StartTick && pb.CurrentTick <= r.EndTick);
        if (activeGate != null)
        {
            if (activeGate.Kind == SimPlayback.HoldLoopKind.ScriptedLoop)
            {
                int cycle = 1;
                while (cycle <= activeGate.CycleEnds.Length &&
                       pb.CurrentTick > activeGate.CycleEnds[cycle - 1])
                    cycle++;
                int cycles = activeGate.CycleEnds.Length;
                loopOsd = $"   [enemy gate: cycle {Math.Min(cycle, cycles)}/{cycles}; repeats until destroyed]";
            }
            else if (activeGate.Kind == SimPlayback.HoldLoopKind.RouteLoop)
            {
                int cycle = 1;
                while (cycle <= activeGate.CycleEnds.Length &&
                       pb.CurrentTick > activeGate.CycleEnds[cycle - 1])
                    cycle++;
                int cycles = activeGate.CycleEnds.Length;
                loopOsd = $"   [route loop: cycle {Math.Min(cycle, cycles)}/{cycles}]";
            }
            else loopOsd = "   [enemy gate: holds until destroyed]";
        }
        string osd = $"{SimPlayback.FormatTime(pb.CurrentTick)} / {SimPlayback.FormatTime(pb.Duration)}" +
            (_playing ? _playDirection > 0 ? $"   >> x{_playSpeed:0.##}" : $"   << x{_playSpeed:0.##}" : "   paused") +
            $"   zoom {scale * 100:0}%" + loopOsd;
        dl.AddText(pos + new Vector2(8, 6), Gfx.Rgba(235, 235, 245), osd);
        dl.PopClipRect();

        // --- transport row ---
        bool SmallBtn(string label, string tip)
        {
            bool hit = ImGui.Button(label);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
            ImGui.SameLine(0, 4);
            return hit;
        }

        if (SmallBtn("|<", "Jump to start")) { pb.SeekTo(1); _playing = false; }
        if (SmallBtn("-1", "Step one tick back (Left)")) { pb.SeekTo(pb.CurrentTick - 1); _playing = false; }
        bool rewinding = _playing && _playDirection < 0;
        if (SmallBtn(rewinding ? "||##rew" : "<<", rewinding ? "Pause" : "Play backwards"))
        {
            if (rewinding) _playing = false;
            else { _playing = true; _playDirection = -1; _playAccum = 0; }
        }
        bool fwd = _playing && _playDirection > 0;
        if (SmallBtn(fwd ? "||" : ">", fwd ? "Pause (Space)" : "Play (Space)"))
        {
            if (fwd) _playing = false;
            else
            {
                if (pb.AtEnd) pb.SeekTo(1);
                _playing = true; _playDirection = 1; _playAccum = 0;
            }
        }
        if (SmallBtn(">>", "Fast-forward (cycles 2x/4x/8x; click > for normal speed)"))
        {
            _playing = true; _playDirection = 1; _playAccum = 0;
            _playSpeed = _playSpeed switch { < 2f => 2f, < 4f => 4f, < 8f => 8f, _ => 1f };
        }
        if (SmallBtn("+1", "Step one tick forward (Right)")) { pb.SeekTo(pb.CurrentTick + 1); _playing = false; }
        if (SmallBtn(">|", "Jump to end")) { pb.SeekTo(pb.Duration); _playing = false; }
        if (SmallBtn("Fit", "Reset zoom and pan (or double-click the view)\nwheel = zoom, drag = pan"))
        { _playZoom = 0; _playPan = System.Numerics.Vector2.Zero; }

        ImGui.PushItemWidth(64);
        int speedIdx = Array.IndexOf(SpeedSteps, _playSpeed);
        if (speedIdx < 0) speedIdx = 2;
        var speedNames = SpeedSteps.Select(s => $"x{s:0.##}").ToArray();
        if (ImGui.Combo("##speed", &speedIdx, speedNames, speedNames.Length))
            _playSpeed = SpeedSteps[speedIdx];
        ImGui.PopItemWidth();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Playback speed (game runs at 35 ticks/s)");
        ImGui.SameLine(0, 10);
        string endTag = pb.LoopDetected
            ? $"  [{pb.LoopRegions.Count} loop/hold section{(pb.LoopRegions.Count == 1 ? "" : "s")} previewed]"
            : pb.EndedNaturally ? "" : "  [capped]";
        ImGui.TextDisabled($"tick {pb.CurrentTick}/{pb.Duration}" +
            endTag);

        DrawTimeline(pb);
    }

    /// <summary>The scrubbable duration bar: enemy-density strip, event markers, playhead.</summary>
    private void DrawTimeline(SimPlayback pb)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        const float h = 26f;
        if (w < 40) return;

        ImGui.InvisibleButton("timeline", new Vector2(w, h));
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        var mouse = ImGui.GetMousePos();
        float mouseFrac = Math.Clamp((mouse.X - pos.X) / w, 0f, 1f);

        if (active)
            pb.SeekTo(1 + (int)MathF.Round(mouseFrac * (pb.Duration - 1)));

        dl.AddRectFilled(pos, pos + new Vector2(w, h), Gfx.Rgba(28, 28, 34));

        // enemy-density strip (bottom-anchored bars)
        if (pb.Density.Length > 1)
        {
            int cols = (int)w;
            for (int x = 0; x < cols; x++)
            {
                int t0 = (int)((long)x * pb.Density.Length / cols);
                int t1 = Math.Max(t0 + 1, (int)((long)(x + 1) * pb.Density.Length / cols));
                int peak = 0;
                for (int t = t0; t < t1 && t < pb.Density.Length; t++)
                    if (pb.Density[t] > peak) peak = pb.Density[t];
                if (peak == 0) continue;
                float bh = Math.Min(1f, peak / 24f) * (h - 8);
                dl.AddLine(new Vector2(pos.X + x, pos.Y + h - 1 - bh),
                    new Vector2(pos.X + x, pos.Y + h - 1), Gfx.Rgba(120, 60, 60, 200));
            }
        }

        // Enemy-gated regions are embedded in the complete route. Each hatch stops at
        // the simulated defeat point; the authored post-boss level continues after it.
        float TickX(int t)
        {
            t = Math.Clamp(t, 1, pb.Duration);
            return pos.X + (pb.Duration <= 1 ? 0 : (t - 1) / (float)(pb.Duration - 1)) * w;
        }
        foreach (var region in pb.LoopRegions)
        {
            float lx = TickX(region.StartTick);
            float rx = TickX(region.EndTick);
            dl.AddRectFilled(new Vector2(lx, pos.Y), new Vector2(rx, pos.Y + h),
                Gfx.Rgba(255, 150, 40, 34));
            for (float sx = lx; sx < rx; sx += 9f)   // diagonal hatching
                dl.AddLine(new Vector2(sx, pos.Y + h - 1),
                    new Vector2(Math.Min(sx + 6f, rx), pos.Y + 1),
                    Gfx.Rgba(255, 150, 40, 70));
            dl.AddLine(new Vector2(lx, pos.Y), new Vector2(lx, pos.Y + h), Gfx.Rgba(255, 150, 40, 200));
            dl.AddLine(new Vector2(rx, pos.Y), new Vector2(rx, pos.Y + h), Gfx.Rgba(255, 150, 40, 200));
            foreach (int cycleEnd in region.CycleEnds)
            {
                float cx = TickX(cycleEnd);
                dl.AddLine(new Vector2(cx, pos.Y), new Vector2(cx, pos.Y + h),
                    Gfx.Rgba(255, 205, 120, 170), 1.5f);
            }
            if (rx - lx >= 52)
            {
                string loopLabel = region.Kind == SimPlayback.HoldLoopKind.ScriptedLoop
                    ? $"gate x{region.CycleEnds.Length}"
                    : region.Kind == SimPlayback.HoldLoopKind.RouteLoop
                        ? $"route x{region.CycleEnds.Length}" : "enemy hold";
                dl.AddText(new Vector2(lx + 4, pos.Y + h - 14),
                    Gfx.Rgba(255, 190, 90, 230), loopLabel);
            }
        }

        // progress fill
        float frac = pb.Duration <= 1 ? 0f : (pb.CurrentTick - 1) / (float)(pb.Duration - 1);
        dl.AddRectFilled(pos, new Vector2(pos.X + w * frac, pos.Y + h), Gfx.Rgba(90, 130, 200, 60));

        // event markers (top half ticks, one class per colour)
        foreach (var e in pb.Events)
        {
            uint col;
            switch (e.Type)
            {
                case 2 or 3 or 30: col = Gfx.Rgba(80, 210, 230); break;      // scroll speed
                case 4 or 83: col = Gfx.Rgba(255, 90, 90); break;            // map stop
                case 38 or 54 or 70 or 71 or 75 or 76: col = Gfx.Rgba(255, 170, 60); break; // flow
                case 11 or 36: col = Gfx.Rgba(240, 240, 240); break;         // level end
                case 79: col = Gfx.Rgba(230, 90, 220); break;                // boss bar
                default: continue;
            }
            float ex = TickX(e.Tick);
            dl.AddLine(new Vector2(ex, pos.Y + 1), new Vector2(ex, pos.Y + 9), col);
        }

        // playhead
        float px = pos.X + w * frac;
        dl.AddLine(new Vector2(px, pos.Y), new Vector2(px, pos.Y + h), Gfx.Rgba(255, 235, 130), 2f);
        dl.AddRect(pos, pos + new Vector2(w, h), Gfx.Rgba(90, 90, 105));

        if (hovered || active)
        {
            int t = 1 + (int)MathF.Round(mouseFrac * (pb.Duration - 1));
            var hoverGate = pb.LoopRegions.FirstOrDefault(r => t >= r.StartTick && t <= r.EndTick);
            string loopNote = hoverGate != null
                ? hoverGate.Kind == SimPlayback.HoldLoopKind.ScriptedLoop
                    ? $"\nhatched = enemy-gated script: repeats until the boss/linked enemies die;\n{hoverGate.CycleEnds.Length} cycles are retained, separated by bright lines"
                    : hoverGate.Kind == SimPlayback.HoldLoopKind.RouteLoop
                        ? $"\nhatched = conditional route loop; {hoverGate.CycleEnds.Length} cycles are retained, then playback continues"
                        : "\nhatched = enemy-gated hold: gameplay waits until the boss/linked enemies die;\n20 seconds are retained for inspection"
                : "";
            ImGui.SetTooltip($"{SimPlayback.FormatTime(t)}  (tick {t})\n" +
                "cyan = scroll speed  red = map stop  orange = jump/flow\nwhite = level end  magenta = boss bar" +
                loopNote);
        }
    }

    /// <summary>Keep at least a sliver of the level inside the viewport so it can't be lost.</summary>
    private void ClampScroll(Vector2 avail)
    {
        if (_level == null) return;
        const float keep = 48f;
        float w = LevelRenderer.CanvasW * _zoom, h = CanvasHeight() * _zoom;
        _scroll.X = Math.Clamp(_scroll.X, keep - w, avail.X - keep);
        _scroll.Y = Math.Clamp(_scroll.Y, keep - h, avail.Y - keep);
    }

    /// <summary>
    /// Whole-level overview strip at the canvas' right edge: the composited level squashed
    /// vertically, the current viewport marked; click or drag to jump.
    /// </summary>
    private void DrawMinimap(ImDrawListPtr dl, (Vector2 Min, Vector2 Max)? rect, Vector2 avail)
    {
        _minimapDragging = false;
        if (rect is not { } r) return;
        int H = CanvasHeight();
        float stripH = r.Max.Y - r.Min.Y;

        // Interaction: an invisible button on top of the canvas one (submitted later, so
        // it wins hover). While held, the viewport centre follows the cursor.
        ImGui.SetCursorScreenPos(r.Min);
        ImGui.InvisibleButton("minimap", r.Max - r.Min);
        bool active = ImGui.IsItemActive();
        bool hover = ImGui.IsItemHovered();
        _minimapDragging = active;
        if (active)
        {
            float frac = Math.Clamp((ImGui.GetMousePos().Y - r.Min.Y) / stripH, 0f, 1f);
            _scroll.Y = avail.Y * 0.5f - frac * H * _zoom;
        }

        dl.AddRectFilled(r.Min, r.Max, Gfx.Rgba(8, 8, 10, 235));
        _img.DrawInRect(dl, r.Min, r.Max);
        dl.AddRect(r.Min, r.Max, Gfx.Rgba(95, 95, 110));

        // Viewport indicator.
        float vis0 = Math.Clamp(-_scroll.Y / _zoom / H, 0f, 1f);
        float vis1 = Math.Clamp((avail.Y - _scroll.Y) / _zoom / H, 0f, 1f);
        float y0 = r.Min.Y + vis0 * stripH;
        float y1 = Math.Max(r.Min.Y + vis1 * stripH, y0 + 3);
        dl.AddRectFilled(new Vector2(r.Min.X, y0), new Vector2(r.Max.X, y1),
            Gfx.Rgba(255, 255, 255, (byte)(hover || active ? 56 : 32)));
        dl.AddRect(new Vector2(r.Min.X, y0), new Vector2(r.Max.X, y1), Gfx.Rgba(255, 225, 120, 220));
    }

    /// <summary>Hover hit-test over placed objects in Sprites mode: outline + info tooltip.</summary>
    private void DrawSpriteHover(ImDrawListPtr dl, Vector2 origin, Vector2 mouse)
    {
        Vector2 img = (mouse - origin) / _zoom;
        int yOff = ObjYOffset();
        int H = CanvasHeight();
        PlacedObject? hit = null;
        Vector2 hitMin = default, hitMax = default;
        foreach (var o in _objects)
        {
            if (o.Sheet == null || o.SpriteIndex <= 0 || o.SpriteIndex == 999) continue;
            if (!CategoryVisible(o.Cat)) continue;
            float oy = LevelRenderer.ObjectCanvasY(o, _timeline, H, yOff);
            float x0 = o.Esize == 1 ? o.X - 6 : o.X;
            float y0 = o.Esize == 1 ? oy - 7 : oy;
            float w = o.Esize == 1 ? 24 : 12, h = o.Esize == 1 ? 28 : 14;
            if (img.X >= x0 && img.X < x0 + w && img.Y >= y0 && img.Y < y0 + h)
            {
                hit = o;
                hitMin = origin + new Vector2(x0, y0) * _zoom;
                hitMax = origin + new Vector2(x0 + w, y0 + h) * _zoom;
            }
        }
        if (hit is PlacedObject hm)
        {
            dl.AddRect(hitMin - new Vector2(1, 1), hitMax + new Vector2(1, 1),
                ObjectPlacer.CategoryColor(hm.Cat));
            ShowObjectTooltip(hm);
        }
    }

    /// <summary>
    /// Markers and the hover inspector over the simulated frame. The sim only hands back
    /// what the frame actually drew, so both agree with the picture: a category switched
    /// off in the layer list is neither marked nor picked, and the tile readout follows
    /// the live scroll cursors.
    /// </summary>
    private void DrawPlaybackOverlay(ImDrawListPtr dl, SimPlayback pb, Vector2 imgPos,
        float scale, int vw, int vh, Vector2 mouse, bool hovered,
        Vector2 viewPos, Vector2 viewSize)
    {
        if (!hovered) _hoverInfo = "";
        if (_objMode == 2 && !hovered) return;

        // The texture is the whole buffer in extended view, else the cropped playfield.
        float texOX = _simExtendedView ? 0 : GameSim.OX + GameSim.ViewX;
        float texOY = _simExtendedView ? 0 : GameSim.OY;
        Vector2 ToScreen(float bx, float by) =>
            imgPos + new Vector2(bx - texOX, by - texOY) * scale;
        bool InView(float bx, float by) =>
            bx >= texOX && by >= texOY && bx < texOX + vw && by < texOY + vh;

        pb.Sim.CollectEnemies(_playEnemies, _objCatMask);

        var m = (mouse - imgPos) / scale + new Vector2(texOX, texOY);
        bool canPick = hovered && _objMode != 2;
        float r = Math.Clamp(3.5f * scale, 3f, 9f);
        GameSim.EnemyView? pick = null;
        for (int i = _playEnemies.Count - 1; i >= 0; i--)   // front-most band first
        {
            var e = _playEnemies[i];
            if (!InView(e.CenterX, e.CenterY)) continue;
            if (canPick && pick == null &&
                Math.Abs(m.X - e.CenterX) <= e.HalfW && Math.Abs(m.Y - e.CenterY) <= e.HalfH)
                pick = e;
            if (_objMode != 1) continue;
            var p = ToScreen(e.CenterX, e.CenterY);
            uint col = ObjectPlacer.CategoryColor(e.Category);
            dl.AddCircleFilled(p, r, col);
            dl.AddCircle(p, r, Gfx.Rgba(0, 0, 0, 180));
            if (e.Size == 1)   // 2x2 occupies four cells: ring it so the footprint reads
                dl.AddCircle(p, r + 3f, (col & 0x00FFFFFFu) | (110u << 24));
        }

        if (!hovered) return;
        if (pick is { } hit)
        {
            // Same affordance as the map view's sprite hover: the sprite cell outlined in
            // its category colour (12x14 per blit, 24x28 for a 2x2).
            var one = new Vector2(1, 1);
            dl.AddRect(ToScreen(hit.CenterX - hit.HalfW, hit.CenterY - hit.HalfH) - one,
                       ToScreen(hit.CenterX + hit.HalfW, hit.CenterY + hit.HalfH) + one,
                       ObjectPlacer.CategoryColor(hit.Category));
            ShowEnemyTooltip(hit);
        }

        int bx = (int)MathF.Floor(m.X), by = (int)MathF.Floor(m.Y);
        if (!InView(bx, by)) { _hoverInfo = ""; return; }
        string where = $"screen {bx - GameSim.OX},{by - GameSim.OY}";
        _hoverInfo = pb.Sim.TryPickTile(bx, by, out var t)
            ? $"{where}   BG{t.Layer + 1} col {t.Col} row {t.Row}  cell={t.Cell} shapeId={t.ShapeId}"
            : $"{where}   (no background tile)";
        // The controls panel's Status line is usually scrolled away in playback, so put
        // the readout where the cursor already is.
        dl.AddText(new Vector2(viewPos.X + 8, viewPos.Y + viewSize.Y - 18),
            Gfx.Rgba(190, 200, 215), _hoverInfo);
    }

    private static void ShowEnemyTooltip(in GameSim.EnemyView e)
    {
        ImGui.BeginTooltip();
        ImGui.Text(ObjectPlacer.CategoryName(e.Category));
        ImGui.Text($"enemy id: {e.EnemyId}    slot {e.Slot} (band {e.Band})");
        ImGui.Text(e.ScoreItem || e.ArmorLeft == 255 && e.Value != 0
            ? $"pickup    value {e.Value}"
            : $"armor: {e.ArmorLeft}    value {e.Value}");
        ImGui.Text($"pos {e.ScreenX},{e.ScreenY}    vel {e.Xc},{e.Yc}" +
            (e.XAccel != 0 || e.YAccel != 0 ? $"    accel {e.XAccel},{e.YAccel}" : ""));
        ImGui.Text($"sprite: {e.SpriteIndex} (bank {e.SheetId})   " +
            (e.Size == 1 ? "2x2" : "1x1") + $"   frame {e.AnimFrame}/{e.AnimMax}");
        if (e.LinkNum != 0) ImGui.Text($"link: {e.LinkNum}");
        if (e.Tur0 != 0 || e.Tur1 != 0 || e.Tur2 != 0)
            ImGui.Text($"turrets: {e.Tur0}/{e.Tur1}/{e.Tur2}");
        if (e.LaunchType != 0) ImGui.Text($"launches {e.LaunchType} every {e.LaunchFreq}");
        if (e.Iced != 0) ImGui.Text($"iced: {e.Iced}");
        if (!e.OnScreen) ImGui.TextDisabled("(outside the in-game screen)");
        ImGui.EndTooltip();
    }

    private static void ShowObjectTooltip(in PlacedObject o)
    {
        ImGui.BeginTooltip();
        ImGui.Text(ObjectPlacer.CategoryName(o.Cat));
        ImGui.Text($"enemy id: {o.EnemyId}");
        ImGui.Text($"sprite: {o.SpriteIndex}  esize {o.Esize}");
        ImGui.Text($"band: {o.Band}   time: {o.Time}");
        if (o.ApproxX) ImGui.Text("X: default/random");
        ImGui.EndTooltip();
    }

    private void DrawMarkers(ImDrawListPtr dl, Vector2 origin, Vector2 mouse, bool hovered)
    {
        float r = Math.Clamp(4f * _zoom, 2.5f, 9f);
        PlacedObject? hover = null;
        int yOff = ObjYOffset();
        foreach (var o in _objects)
        {
            if (!CategoryVisible(o.Cat)) continue;
            float oy = LevelRenderer.ObjectCanvasY(
                o, _timeline, CanvasHeight(), yOff);
            var p = origin + new Vector2(o.X + 6, oy + 7) * _zoom;
            uint col = ObjectPlacer.CategoryColor(o.Cat);
            dl.AddCircleFilled(p, r, col);
            dl.AddCircle(p, r, Gfx.Rgba(0, 0, 0, 180));
            if (o.ApproxX) dl.AddLine(p - new Vector2(r + 2, 0), p + new Vector2(r + 2, 0), col);
            if (hovered && Vector2.Distance(p, mouse) <= r + 3) hover = o;
        }
        if (hover is PlacedObject hm) ShowObjectTooltip(hm);
    }

    private void UpdateHoverInfo(Vector2 origin, Vector2 mouse, bool hovered)
    {
        if (!hovered || _level == null) return;
        Vector2 img = (mouse - origin) / _zoom;
        if (img.X < 0 || img.Y < 0 || img.X >= LevelRenderer.CanvasW || img.Y >= CanvasHeight())
        { _hoverInfo = ""; return; }
        int H = CanvasHeight();
        string fb = "";
        for (int layer = 2; layer >= 0; layer--)
        {
            if (!BgVisible(layer)) continue;
            int cols = Level.ColsFor(layer), rows = Level.RowsFor(layer);
            int yOff = H - rows * ShapeTable.TileH
                - (int)Math.Round(_layerScroll?.Anchor[layer] ?? 0);
            int ly = (int)img.Y - yOff;
            if (ly < 0 || ly >= rows * ShapeTable.TileH) continue;
            int col = (int)img.X / ShapeTable.TileW, row = ly / ShapeTable.TileH;
            if (col < 0 || col >= cols) continue;
            byte cell = _level.CellsFor(layer)[row * cols + col];
            int shapeId = _level.ResolveShapeId(layer, cell);
            string info = $"L{layer + 1} col {col} row {row}  cell={cell} shapeId={shapeId}";
            if (fb.Length == 0) fb = info;
            if (shapeId == 0) continue;
            _hoverInfo = info;
            return;
        }
        _hoverInfo = fb;
    }

    private void FitWidth()
    {
        var avail = ImGui.GetContentRegionAvail();
        _zoom = Math.Max(0.05f, avail.X / LevelRenderer.CanvasW);
        CenterBottom();
    }

    private void CenterBottom()
    {
        var avail = ImGui.GetContentRegionAvail();
        _scroll.X = (avail.X - LevelRenderer.CanvasW * _zoom) * 0.5f;
        _scroll.Y = avail.Y - CanvasHeight() * _zoom;
    }

    public void Dispose()
    {
        _img.Dispose();
        _gameView.Dispose();
    }
}
