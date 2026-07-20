using System.Numerics;
using System.Runtime.Versioning;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;
using SdlNs = Hexa.NET.SDL2;

namespace T2LV;

/// <summary>The Dear ImGui application: navigation, layer/object toggles and the level canvas.</summary>
public sealed unsafe partial class App
{
    private readonly SdlNs.SDLRendererPtr _renderer;

    private GameData? _gd;
    private string _dataDir = "";
    private string _status = "";

    private int _episodeIdx;
    private bool _allEpisodes;                 // level list / tree / reader span every episode
    private int _levelIdx;                     // index into _browse, not into an episode
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

    // Data-folder picker / PNG Save-As box.
    private enum PickPurpose { DataDir, SavePng }
    private bool _showDirInput;
    private readonly byte[] _dirBuf = new byte[1024];
    private volatile bool _pickActive, _pickDone;   // native file dialog runs on its own STA thread
    private volatile string? _pickResult, _pickError;
    private PickPurpose _pickPurpose = PickPurpose.DataDir;

    // Background PNG write (level export or playback screenshot). The image is snapshotted when
    // the button is pressed, not when the dialog closes, so what gets saved is what was on
    // screen at that moment -- playback keeps running while the modal box is open.
    private volatile bool _exportActive;
    private volatile string? _exportDone;
    private uint[]? _pendingPixels;
    private int _pendingW, _pendingH;
    private string _exportDir = "";                 // last folder saved into, persisted

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
    private int _simLoopCycles = 2;          // boss-gate / route-loop repeats kept in the preview
    private float _playZoom;                 // 0 = fit to the panel automatically
    private Vector2 _playPan;                // manual pan offset, screen px
    private bool _fitAroundHud;              // "UI fit": fit the slice the controls HUD leaves free
    private bool _hudPinRight;               // dock the controls HUD to the view's right edge
    private bool _hudDocked;                 // ... and this frame's layout actually had room for it
    private Vector2 _hudPos, _hudSize;       // its rect last frame -- what "UI fit" fits around
    private bool _simExtendedView;           // show beyond the in-game screen
    private bool _widescreen;                // true-widescreen playback (356px playfield, wider bounds)
    private bool _expandedParallax;          // widescreen sub-option: wider all-layer parallax sweep (commit edd8118)
    private bool _mirrorLayers = true;       // widescreen sub-option: mirror layers past their side edges (commit 1f7ba83)
    private bool _wideStarfield = true;      // the widescreen build's rewritten starfield (either mode)
    private bool _showScreenFilter = true;   // event-44 hue/brightness filter
    private bool _showTerrainSmoothies = true; // lava/water/ice/blur
    private bool _showSpotlight = true;       // light-cone presentation
    private bool _showScreenFlip = true;      // vertical-flip presentation
    private bool _showBossBars = true;        // boss armor readout bars
    private bool _playerSimMode;              // show/drag the phantom-player marker
    private bool _pivotInvisible;             // hide the marker+guide but keep the position live
    private int _simPlayerX = 100, _simPlayerY = 150;  // phantom player pos (sticky; drives parallax/aim)
    private bool _clickKill;                  // left-click damages the enemy under the cursor
    private bool _clickKillInstant = true;    // ... for everything it has, whatever its armour
    private int _clickKillDamage = 10;        // ... or for this much
    private bool _clickKillExplosions = true; // spawn the death débris, or just vanish
    private Vector2? _clickKillPress;         // press point, to tell a click from a pan drag
    private bool _draggingPlayer;             // a player-position drag is in progress
    // Which button holds that drag: Left grabbed the marker, Right is the aim-anywhere
    // gesture that works with the marker hidden (or player mode off entirely).
    private ImGuiMouseButton _playerDragButton = ImGuiMouseButton.Left;
    // Which HUD sections are unfolded, indexed by PbSec. The HUD window is NoSavedSettings
    // (it is placed over the view, not by the .ini), so this is ours to keep and persist.
    private readonly bool[] _pbOpen = { true, false, false, false, true, false, true };
    // Playfield crop width for the current mode (widescreen 299 / vanilla 264) and the phantom-
    // player X range derived from it. Vanilla keeps the viewer's historical 36..260; widescreen
    // uses the widescreen build's ACTUAL ship clamp [SHIP_LEFT_MARGIN, PLAYFIELD_WIDTH -
    // SHIP_RIGHT_MARGIN] = [29, 303] -- the expanded-parallax sweep normalizes over exactly this
    // travel, so both walls must be reachable. Field-based (not sim-based) so it is valid before
    // a playback exists (constructor --player clamp).
    private int PlayfieldW => _widescreen ? GameSim.WideViewW : GameSim.ViewW;
    private int PlayerXMin => _widescreen ? 29 : 36;
    private int PlayerXMax => _widescreen ? GameSim.WideViewW + 4 : GameSim.ViewW - 4;   // 303 / 260
    private readonly GameViewImage _gameView = new();
    private static readonly float[] SpeedSteps = { 0.25f, 0.5f, 1f, 2f, 4f, 8f };
    private static readonly string[] DifficultyNames =
        { "Wimp", "Easy", "Normal", "Hard", "Impossible", "Insanity",
          "Suicide", "Maniacal", "Zinglon", "Nortaneous", "Nortaneous 2" };

    private readonly SdlNs.SDLWindowPtr _window;
    private bool _scrollLevelListToSelection;      // keep the selection visible after keyboard/startup selects
    private bool _minimapDragging;
    private float _minimapGrab;                    // cursor offset inside the viewport indicator while dragging
    private Vector2 _canvasAvail;                  // canvas size last frame, for key-driven jumps

    // Persisted UI state.
    private readonly AppSettings _settings;
    private int _levelFileNum = 1;             // currently selected level (survives reload)
    private int _pendingLevelIdx;              // >=0: select by list index once (CLI --start)
    private float _levelsHeight = 170f;        // resizable panel sections
    private float _layersHeight;               // 0 = fit to content
    private bool _restoreView;

    public App(SdlNs.SDLRendererPtr renderer, AppSettings settings, int cliEp = -1, int cliLevelIdx = -1,
        SdlNs.SDLWindowPtr window = default, int cliPlaybackTick = -1, int cliPlayerX = -1, int cliPlayerY = -1)
    {
        _renderer = renderer;
        _settings = settings;
        _window = window;
        if (cliPlaybackTick >= 0) _playbackMode = true;
        _widescreen = settings.Widescreen;   // needed before the --player clamp (PlayerXMax) below
        _expandedParallax = settings.ExpandedParallax;
        _mirrorLayers = settings.MirrorLayers;
        _wideStarfield = settings.WideStarfield;
        if (cliPlayerX >= 0)   // --player x y: start in phantom-player mode at a fixed spot (testing)
        {
            _playerSimMode = true;
            _simPlayerX = Math.Clamp(cliPlayerX, PlayerXMin, PlayerXMax);
            _simPlayerY = Math.Clamp(cliPlayerY, 0, 170);
        }

        _exportDir = settings.ExportDir ?? "";
        _palette = Math.Max(0, settings.Palette);
        _objMode = Math.Clamp(settings.ObjMode, 0, 2);
        _gameLayerOrder = settings.GameLayerOrder;
        _simExtendedView = settings.SimExtendedView;
        _showScreenFilter = settings.ShowScreenFilter;
        _showTerrainSmoothies = settings.ShowSmoothies;
        _showSpotlight = settings.ShowSpotlight ?? settings.ShowSmoothies;
        _showScreenFlip = settings.ShowScreenFlip ?? settings.ShowSmoothies;
        _showBossBars = settings.ShowBossBars;
        _clickKill = settings.ClickKill;
        _clickKillInstant = settings.ClickKillInstant;
        _clickKillDamage = Math.Clamp(settings.ClickKillDamage, 1, 254);
        _clickKillExplosions = settings.ClickKillExplosions;
        _showTree = settings.ShowTree;
        _showCubes = settings.ShowCubes;
        _cubeByLevel = settings.CubesByLevel;
        if (settings.CubeListWidth > 100f) _cubeListW = settings.CubeListWidth;
        _showSprites = settings.ShowSprites;
        _showEnemies = settings.ShowEnemies;
        _showItems = settings.ShowItems;
        _showAnalysis = settings.ShowAnalysis;
        _enemyMode = Math.Clamp(settings.EnemyBrowseMode, 0, 1);
        _asmUnique = settings.AssembliesUnique ?? true;
        _sprGapless = settings.SpritesGapless;
        _sprCols = Math.Clamp(settings.SpritesColumns, 0, 40);
        _sprCheckerboard = settings.SpritesCheckerboard ?? true;
        if (settings.SpriteListWidth > 100f) _sprListW = settings.SpriteListWidth;
        if (settings.EnemyListWidth > 100f) _enemyListW = settings.EnemyListWidth;
        if (settings.ItemListWidth > 100f) _itemListW = settings.ItemListWidth;
        _allEpisodes = settings.AllEpisodes;
        if (settings.TreeEdgeMask != 0) _treeEdgeMask = settings.TreeEdgeMask;   // 0 = never saved
        if (settings.PbSections >= 0)   // -1 = never saved: keep the defaults above
            for (int i = 0; i < _pbOpen.Length; i++)
                _pbOpen[i] = (settings.PbSections & (1 << i)) != 0;
        _hudPinRight = settings.PbPinRight;
        _fitAroundHud = settings.PbFitAroundHud;
        _levelsHeight = settings.LevelsHeight > 30 ? settings.LevelsHeight : 170f;
        _layersHeight = settings.LayersHeight > 30 ? settings.LayersHeight : 0f;  // 0 = fit to content
        ApplyLayerSettings(settings.Layers);

        _episodeIdx = cliEp >= 0 ? cliEp : settings.EpisodeIdx;
        // --start names a level by its index within one episode, so it implies that episode.
        if (cliEp >= 0) _allEpisodes = false;
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
            ResetBrowserCaches();
            _gd = new GameData(dir);
            _dataDir = dir;
            SetDirBuf(dir);
            _episodeIdx = Math.Clamp(_episodeIdx, 0, Math.Max(0, _gd.Episodes.Count - 1));
            _status = $"Loaded {_gd.Episodes.Count} episodes.";
            _showDirInput = false;
            RebuildBrowseList();
            if (_browse.Count > 0)
            {
                int idx;
                if (_pendingLevelIdx >= 0) { idx = _pendingLevelIdx; _pendingLevelIdx = -1; }
                else { idx = _levelIdx; }
                SelectLevel(Math.Clamp(idx, 0, _browse.Count - 1), ensureVisible: true);
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
        s.ExportDir = _exportDir.Length > 0 ? _exportDir : s.ExportDir;
        s.EpisodeIdx = _episodeIdx;
        s.LevelFileNum = _levelFileNum;
        s.Palette = _palette;
        s.ObjMode = _objMode;
        s.GameLayerOrder = _gameLayerOrder;
        s.SimExtendedView = _simExtendedView;
        s.Widescreen = _widescreen;
        s.ExpandedParallax = _expandedParallax;
        s.MirrorLayers = _mirrorLayers;
        s.WideStarfield = _wideStarfield;
        s.ShowScreenFilter = _showScreenFilter;
        s.ShowSmoothies = _showTerrainSmoothies;
        s.ShowSpotlight = _showSpotlight;
        s.ShowScreenFlip = _showScreenFlip;
        s.ShowBossBars = _showBossBars;
        s.ClickKill = _clickKill;
        s.ClickKillInstant = _clickKillInstant;
        s.ClickKillDamage = _clickKillDamage;
        s.ClickKillExplosions = _clickKillExplosions;
        s.ShowTree = _showTree;
        s.ShowCubes = _showCubes;
        s.CubesByLevel = _cubeByLevel;
        s.CubeListWidth = _cubeListW;
        s.ShowSprites = _showSprites;
        s.ShowEnemies = _showEnemies;
        s.ShowItems = _showItems;
        s.ShowAnalysis = _showAnalysis;
        s.SpriteListWidth = _sprListW;
        s.EnemyListWidth = _enemyListW;
        s.ItemListWidth = _itemListW;
        s.EnemyBrowseMode = _enemyMode;
        s.AssembliesUnique = _asmUnique;
        s.SpritesGapless = _sprGapless;
        s.SpritesColumns = _sprCols;
        s.SpritesCheckerboard = _sprCheckerboard;
        s.AllEpisodes = _allEpisodes;
        s.TreeEdgeMask = _treeEdgeMask;
        int secBits = 0;
        for (int i = 0; i < _pbOpen.Length; i++) if (_pbOpen[i]) secBits |= 1 << i;
        s.PbSections = secBits;
        s.PbPinRight = _hudPinRight;
        s.PbFitAroundHud = _fitAroundHud;
        s.LevelsHeight = _levelsHeight;
        s.LayersHeight = _layersHeight;
        s.HasView = _viewInitialized;
        s.Zoom = _zoom; s.ScrollX = _scroll.X; s.ScrollY = _scroll.Y;
        s.Layers = _layers.Select(l => new LayerState { Id = l.Id, Visible = l.Visible, Alpha = l.Alpha }).ToList();
    }

    /// <summary>A full-height vertical splitter bar; drag it to resize the column at its left.
    /// Goes between two SameLine'd children.</summary>
    private void VSplitter(string id, ref float width, float min, float max)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Gfx.Rgba(95, 95, 120, 160));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Gfx.Rgba(130, 130, 175, 230));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Gfx.Rgba(160, 160, 210, 255));
        ImGui.Button(id, new Vector2(6, -1));
        if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        if (ImGui.IsItemActive())
            width = Math.Clamp(width + ImGui.GetIO().MouseDelta.X, min, max);
        ImGui.PopStyleColor(3);
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

    /// <summary>One row of the level list: which episode it belongs to and where in it.
    /// In single-episode mode every row is the current episode; in "All episodes" the
    /// list runs straight through all five.</summary>
    private readonly record struct BrowseItem(int Episode, int Level);

    private readonly List<BrowseItem> _browse = new();

    /// <summary>Refill the level list for the current episode selection. Returns false if
    /// the selected level fell out of the list, so the caller knows to load another.</summary>
    private bool RebuildBrowseList()
    {
        int wantEp = _levelIdx >= 0 && _levelIdx < _browse.Count ? _browse[_levelIdx].Episode : _episodeIdx;
        int wantFile = _levelFileNum;
        _browse.Clear();
        if (_gd == null) return false;

        if (_allEpisodes)
            for (int e = 0; e < _gd.Episodes.Count; e++)
                for (int l = 0; l < _gd.Episodes[e].Levels.Count; l++) _browse.Add(new BrowseItem(e, l));
        else if (_episodeIdx < _gd.Episodes.Count)
            for (int l = 0; l < _gd.Episodes[_episodeIdx].Levels.Count; l++) _browse.Add(new BrowseItem(_episodeIdx, l));

        int found = _browse.FindIndex(b =>
            b.Episode == wantEp && _gd.Episodes[b.Episode].Levels[b.Level].FileNum == wantFile);
        _levelIdx = Math.Max(0, found);
        return found >= 0;
    }

    private string BrowseLabel(BrowseItem b) =>
        (_allEpisodes ? $"E{_gd!.Episodes[b.Episode].Number}  " : "") +
        _gd!.Episodes[b.Episode].Levels[b.Level].Display;

    /// <summary>
    /// The episode picker, shared by the side panel and the tree/datacube windows so the
    /// three always agree. "All episodes" widens the level list to the whole game and lets
    /// the tree and the reader show every episode at once.
    /// </summary>
    private void EpisodeCombo(string id)
    {
        if (_gd == null) return;
        string label = _allEpisodes ? "All episodes" : $"Episode {_gd.Episodes[_episodeIdx].Number}";
        if (!ImGui.BeginCombo(id, label)) return;

        if (ImGui.Selectable("All episodes", _allEpisodes)) SetEpisode(true, _episodeIdx);
        for (int i = 0; i < _gd.Episodes.Count; i++)
            if (ImGui.Selectable($"Episode {_gd.Episodes[i].Number}", !_allEpisodes && i == _episodeIdx))
                SetEpisode(false, i);
        ImGui.EndCombo();
    }

    private void SetEpisode(bool all, int idx)
    {
        _allEpisodes = all;
        if (!all) _episodeIdx = idx;
        // Widening the list keeps whatever was loaded; narrowing to another episode does not.
        if (RebuildBrowseList()) _scrollLevelListToSelection = true;
        else SelectLevel(_levelIdx, ensureVisible: true);
    }

    /// <summary>Select a level in whatever the viewer is browsing; in "All episodes" this
    /// also makes that level's episode the active one.</summary>
    private void SelectLevel(int browseIdx, bool ensureVisible = false)
    {
        if (_gd == null || browseIdx < 0 || browseIdx >= _browse.Count) return;
        var item = _browse[browseIdx];
        _episodeIdx = item.Episode;
        var ep = _gd.Episodes[item.Episode];
        _levelIdx = browseIdx;
        _scrollLevelListToSelection |= ensureVisible;
        int fileNum = ep.Levels[item.Level].FileNum;
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
            string name = ep.Levels[item.Level].Name.Trim();
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

    /// <summary>Select the previous/next level in the browsed list (keyboard).</summary>
    private void SelectLevelStep(int delta)
    {
        if (_browse.Count == 0) return;
        int idx = Math.Clamp(_levelIdx + delta, 0, _browse.Count - 1);
        if (idx != _levelIdx) SelectLevel(idx, ensureVisible: true);
    }

    /// <summary>Select a level by episode and level-file number, wherever it sits in the list.</summary>
    private void SelectLevelFile(int episodeIdx, int fileNum)
    {
        int idx = _browse.FindIndex(b => b.Episode == episodeIdx &&
            _gd!.Episodes[b.Episode].Levels[b.Level].FileNum == fileNum);
        if (idx < 0) return;
        // Already the open level: reloading it would reset the view and throw away the built
        // playback, and rebuilding that runs the whole level again. Browsing to a second place
        // in the level you are already watching should cost nothing.
        if (idx == _levelIdx && _level != null &&
            _episodeIdx == episodeIdx && _levelFileNum == fileNum)
        {
            _scrollLevelListToSelection = true;
            return;
        }
        SelectLevel(idx, ensureVisible: true);
    }

    /// <summary>
    /// Where a browser's "open" link wants the viewer to land. <see cref="Time"/> is a map
    /// position, and both canvases honour it their own way: the map scrolls to the objects
    /// authored there, playback seeks to the tick the sim spawned them on. The entry ids narrow
    /// down which of the enemies alive around that moment are the ones that were asked for.
    /// </summary>
    private readonly record struct MapJump(ushort Time, IReadOnlyCollection<int>? EnemyIds);

    /// <summary>Pending target for the canvas, taken once the level it belongs to is loaded.</summary>
    private MapJump? _pendingJump;

    /// <summary>
    /// Centre the canvas on where the events at <paramref name="time"/> put their objects. The
    /// event's own coordinate is a map position, not a canvas row -- what turns one into the
    /// other is the level's timeline, which the placed objects already carry, so read it back
    /// off them rather than redoing the walk.
    /// </summary>
    private void ScrollToEventTime(ushort time, Vector2 avail)
    {
        if (_level == null || _objects.Count == 0) return;
        float? top = null;
        foreach (var o in _objects)
            if (o.Time == time)
            {
                float y = LevelRenderer.ObjectCanvasY(o, _timeline, CanvasHeight(), ObjYOffset());
                top = top == null ? y : Math.Min(top.Value, y);
            }
        if (top == null) return;
        float min = Math.Min(0f, avail.Y - CanvasHeight() * _zoom);
        _scroll.Y = Math.Clamp(-top.Value * _zoom + avail.Y * 0.5f, min, 0f);
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

        // Ahead of the playback branch's early return, so search is reachable in either mode.
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F)) OpenSearch();

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
                Widescreen = _widescreen,
                ExpandedParallax = _widescreen && _expandedParallax,   // widescreen-only sub-option
                MirrorLayers = _widescreen && _mirrorLayers,           // widescreen-only sub-option (draw-only)
                WideStarfield = _wideStarfield,                        // available in both modes
                ShowScreenFilter = _showScreenFilter,
                ShowTerrainSmoothies = _showTerrainSmoothies,
                ShowSpotlight = _showSpotlight,
                ShowScreenFlip = _showScreenFlip,
                ShowBossBars = _showBossBars,
                PreviewLoopCycles = _simLoopCycles,   // boss-gate / route-loop repeats kept
                PlayerX = _simPlayerX,   // sticky: the phantom player keeps its last position
                PlayerY = _simPlayerY,   // whether or not the drag marker is currently shown

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
        if (_pendingJump is { } jump) { _pendingJump = null; SeekPlaybackTo(jump); }
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
                GameSim.OX + GameSim.ViewX, GameSim.OY, _playback.Sim.PlayfieldWidth, GameSim.ViewH);
    }

    /// <summary>How far past the spawn frame to look for the group, ~2 s.</summary>
    private const int JumpSettleTicks = 70;

    private readonly List<GameSim.EnemyView> _jumpEnemies = new();
    private readonly HashSet<int> _slotsBefore = new(), _slotsSpawned = new();

    /// <summary>
    /// Send playback to the moment a browsed group is spawned, so the browsers' "open" links
    /// land on it here just as they scroll the map to it there.
    /// </summary>
    private void SeekPlaybackTo(MapJump jump)
    {
        var pb = _playback;
        if (pb == null || _level == null) return;

        int spawn = PlaybackTickForMapTime(jump.Time, out bool exact);
        if (spawn < 0) { _status = $"t={jump.Time}: the run recorded no events to seek to."; return; }

        pb.SeekTo(spawn);
        _playing = false;
        int landed = SettleOnGroup(jump, spawn);
        // ASCII only: the ImGui font is Latin-1, so a dash here has to be a plain hyphen.
        _status = exact
            ? $"t={jump.Time} - playback at {SimPlayback.FormatTime(landed)} (tick {landed})" +
              (landed != spawn ? $", {landed - spawn} ticks into the spawn" : "")
            : $"t={jump.Time} is never reached on this route; went to the nearest point that is, " +
              $"{SimPlayback.FormatTime(landed)} (tick {landed}).";
    }

    /// <summary>
    /// The earliest tick on which the events authored at map position <paramref name="time"/>
    /// ran. Map position is not a clock — route jumps, boss gates and scroll-speed changes all
    /// decide when a given one is reached, and some are never reached at all — so the answer
    /// comes from the precompute's own event log rather than from a conversion. When the route
    /// skips the position entirely this falls back to the nearest position it did run,
    /// reporting that through <paramref name="exact"/>; -1 only if the log is empty.
    /// </summary>
    private int PlaybackTickForMapTime(ushort time, out bool exact)
    {
        exact = false;
        if (_playback == null || _level == null) return -1;
        var evs = _level.Events;
        int hit = -1, near = -1, nearGap = int.MaxValue;
        foreach (var e in _playback.Events)
        {
            if ((uint)e.Index >= (uint)evs.Length) continue;
            int gap = evs[e.Index].Time - time;
            if (gap == 0) { if (hit < 0 || e.Tick < hit) hit = e.Tick; continue; }
            gap = Math.Abs(gap);
            if (gap < nearGap || (gap == nearGap && e.Tick < near)) { nearGap = gap; near = e.Tick; }
        }
        exact = hit >= 0;
        return exact ? hit : near;
    }

    /// <summary>
    /// Roll forward from the spawn frame to the one that actually shows what was asked for, and
    /// return the tick landed on. Event enemies are created off the top of the playfield
    /// (CreateNewEventEnemy starts them at ey = -28) and fly in over the next second or two, so
    /// the spawn frame itself is usually still empty sky — DELIANI's boss is only halfway in
    /// 50 ticks later.
    ///
    /// The group is identified by the enemy slots the spawn tick filled, taken by diffing the
    /// live set across it. Entry ids and link numbers are both reused all level long, and
    /// matching on those settles on whatever unrelated enemy happens to share one; the ids are
    /// kept only to reject a slot freed and refilled inside the window. Finding nothing leaves
    /// the playhead on the spawn frame, which is still the right answer for a group that never
    /// becomes visible at all.
    /// </summary>
    private int SettleOnGroup(MapJump jump, int spawnTick)
    {
        var pb = _playback!;
        if (spawnTick <= 1) return spawnTick;   // no frame before it to diff against

        pb.SeekTo(spawnTick - 1);
        pb.Sim.CollectLiveSlots(_slotsBefore);
        pb.Advance(1);
        pb.Sim.CollectLiveSlots(_slotsSpawned);
        _slotsSpawned.ExceptWith(_slotsBefore);
        if (_slotsSpawned.Count == 0) return spawnTick;   // nothing was created here

        // CollectEnemies' own gate is horizontal only, so the vertical span is tested here. The
        // frame worth stopping on is the one holding the whole group: DELIANI's boss enters over
        // 25 ticks and reads as half a ship for most of them. Parts that are already gone or
        // never enter would make that unreachable, so the first frame with any part wholly in,
        // and then the first with any part half in, stand in for it.
        bool byId = jump.EnemyIds is { Count: > 0 };
        int anyIn = -1, partial = -1;
        for (int n = 0; ; n++)
        {
            pb.Sim.CollectEnemies(_jumpEnemies);
            int seen = 0, whole = 0;
            foreach (var e in _jumpEnemies)
            {
                if (!_slotsSpawned.Contains(e.Slot) || !e.OnScreen) continue;
                if (byId && !jump.EnemyIds!.Contains(e.EnemyId)) continue;
                seen++;
                if (e.ScreenY >= 0 && e.ScreenY < GameSim.ViewH) whole++;
                else if (partial < 0 && e.ScreenY + e.HalfH > 0 && e.ScreenY < GameSim.ViewH)
                    partial = pb.CurrentTick;
            }
            if (seen > 0 && whole == seen) return pb.CurrentTick;
            if (whole > 0 && anyIn < 0) anyIn = pb.CurrentTick;
            if (n >= JumpSettleTicks || pb.AtEnd) break;
            pb.Advance(1);
        }
        int landed = anyIn > 0 ? anyIn : partial > 0 ? partial : spawnTick;
        pb.SeekTo(landed);
        return landed;
    }

    public void Render()
    {
        // Apply a completed native file pick (the dialog runs on a background STA thread).
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
                else WritePendingPng(picked);
            }
            else
            {
                _pendingPixels = null;   // cancelled: drop the snapshot
                if (!string.IsNullOrEmpty(error)) _status = "File chooser failed: " + error;
            }
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

        // Pinned, the playback HUD is a column of its own on the right -- the mirror of the
        // controls column -- so the canvas gives up that width instead of being covered by it.
        // Too narrow a window to spare it and the HUD falls back to floating, rather than
        // leaving a squeezed canvas or no HUD at all. Settled here, before the canvas draws,
        // because the overlay inside it has to know which of the two forms is in play.
        _hudDocked = _playbackMode && _playback != null && _hudPinRight &&
                     ImGui.GetContentRegionAvail().X > HudColW + MinCanvasW;
        float canvasW = _hudDocked ? -(HudColW + ImGui.GetStyle().ItemSpacing.X) : 0f;
        ImGui.BeginChild("canvas", new Vector2(canvasW, 0), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        DrawCanvas();
        ImGui.EndChild();
        if (_hudDocked)
        {
            ImGui.SameLine();
            DrawSimColumn();
        }
        ImGui.End();

        DrawTreeWindow();
        DrawCubeWindow();
        DrawSpriteWindow();
        DrawEnemyWindow();
        DrawItemWindow();
        DrawAnalysisWindow();
        DrawSearchWindow();
    }

    /// <summary>
    /// The reference browsers: everything the data set holds that is not the level in the
    /// viewport. They are toggles rather than a menu because each is a window you leave open
    /// beside the viewer. The first pair reads the episode the picker above is on; the rest
    /// span the whole data set.
    /// </summary>
    private void DrawReferenceButtons()
    {
        ImGui.SeparatorText("Reference");
        float w = (ImGui.GetContentRegionAvail().X - 5f) / 2f;
        if (Chip("Level tree", _showTree, AcRoutes, w,
                "The episode's level tree: which level leads to which, including the\n" +
                "outpost route choices, the secret warps hidden in the level data, and\n" +
                "the forks that depend on difficulty, player count or a boss timer."))
            _showTree = !_showTree;
        ImGui.SameLine(0, 5);
        if (Chip("Datacubes", _showCubes, AcDisplay, w,
                "Read the datacubes the outposts hand out, portrait and all.\n" +
                "Some are always on the shelf; the rest only unlock if you picked\n" +
                "datacubes up in the level just before that outpost."))
            _showCubes = !_showCubes;

        if (Chip("Enemies", _showEnemies, AcEnemy, w,
                "Every enemyDat entry, animated the way the engine animates it,\n" +
                "plus the multi-part formations and bosses the levels assemble\nout of them."))
            _showEnemies = !_showEnemies;
        ImGui.SameLine(0, 5);
        if (Chip("Sprites", _showSprites, AcSprite, w,
                "Every sprite bank in the data set: the 36 enemy banks, the\n" +
                "tyrian.shp sub-tables the menus draw from, and the terrain tiles."))
            _showSprites = !_showSprites;

        if (Chip("Ships & items", _showItems, AcBuild, w,
                "The shop's tables -- ships, front and rear guns, sidekicks,\n" +
                "shields, generators and special weapons, with their in-game icons."))
            _showItems = !_showItems;
        ImGui.SameLine(0, 5);
        if (Chip("Analysis", _showAnalysis, AcSim, w,
                "What a level is made of: spawn density, incoming fire, armour\n" +
                "per segment, and how the levels rank against each other."))
            _showAnalysis = !_showAnalysis;

        if (Chip("Search  (Ctrl+F)", _showSearch, AcPlayer, -1f,
                "One box over levels, enemies, items, datacubes and sprites."))
            OpenSearch();
    }

    private void DrawControls()
    {
        // --- Data folder (always available) ---
        ImGui.SeparatorText("Tyrian folder");
        string shown = _dataDir.Length == 0 ? "(none)" : Shorten(_dataDir, 38);
        ImGui.TextWrapped(shown);
        ImGui.BeginDisabled(_pickActive);
        if (ImGui.Button("Browse...")) StartBrowse();
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
        ImGui.SetNextItemWidth(-1);
        EpisodeCombo("##episode");

        DrawReferenceButtons();

        // --- Levels (resizable) ---
        ImGui.SeparatorText($"Levels ({_browse.Count})");
        ImGui.BeginChild("levellist", new Vector2(0, _levelsHeight), ImGuiChildFlags.Borders);
        int shownEp = -1;
        for (int i = 0; i < _browse.Count; i++)
        {
            if (_allEpisodes && _browse[i].Episode != shownEp)
            {
                shownEp = _browse[i].Episode;
                ImGui.SeparatorText($"Episode {_gd.Episodes[shownEp].Number}");
            }
            if (ImGui.Selectable(BrowseLabel(_browse[i]) + $"##lvl{i}", i == _levelIdx))
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
        // Wrapped: the playback line is wider than the 340px panel, and TextDisabled would
        // otherwise run straight out through the clip rect where it can never be read.
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(_playbackMode
            ? "playback: visibility applies · order/opacity are the engine's"
            : "drag a name or use arrows · top = front");
        ImGui.PopTextWrapPos();
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

        // In playback the map image is not what is being looked at, so the same button saves the
        // game view instead -- whatever the simulation is currently showing.
        bool shot = _playbackMode && _playback != null;
        ImGui.BeginDisabled(_exportActive || _pickActive || (shot ? _gameView.W <= 0 : _level == null));
        if (ImGui.Button(shot ? "Take screenshot..." : "Save level PNG..."))
        {
            if (shot) BeginSaveScreenshot(); else BeginSaveLevelPng();
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(shot
                ? "Save the current frame exactly as shown: palette, layer visibility,\nwidescreen and extended view all included, at 1:1 pixels."
                : "Write the whole composited level (current layers/palette) as a PNG.");
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
        _pickPurpose = PickPurpose.DataDir;
        if (!OperatingSystem.IsWindows()) { _showDirInput = true; return; }
        StartPickWindows("Select your Tyrian 2000 folder", null);
    }

    /// <summary>Snapshot the composited level and ask where to put it.</summary>
    private void BeginSaveLevelPng()
    {
        if (_exportActive || _pickActive || _level == null || CurEpisode == null || _img.Width <= 0) return;
        _pendingPixels = (uint[])_img.Pixels.Clone();
        _pendingW = _img.Width; _pendingH = _img.Height;
        StartSavePng("Save the level PNG", LevelFileStem() + ".png");
    }

    /// <summary>
    /// Snapshot the playback frame and ask where to put it. The source is the texture the view
    /// is drawing, so widescreen, extended view, palette and layer visibility all come along;
    /// the ImGui overlays on top of it (markers, boxes, OSD) do not.
    /// </summary>
    private void BeginSaveScreenshot()
    {
        if (_exportActive || _pickActive || _playback == null || CurEpisode == null || _gameView.W <= 0) return;
        _pendingPixels = _gameView.Snapshot();
        _pendingW = _gameView.W; _pendingH = _gameView.H;
        string name = LevelFileStem() + $"_t{_playback.CurrentTick}" +
            (_widescreen ? "_wide" : "") + (_simExtendedView ? "_ext" : "") + ".png";
        StartSavePng("Save the screenshot", name);
    }

    /// <summary>"ep2_05_Camanis": what both saves name their file after.</summary>
    private string LevelFileStem()
    {
        var ep = CurEpisode!;
        // Look the name up by file number, not by _levelIdx -- that indexes _browse, which
        // spans every episode in All-episodes mode and would name the file after the wrong level.
        string name = ep.Levels.FirstOrDefault(l => l.FileNum == _levelFileNum)?.Name.Trim() ?? "";
        if (name.Length == 0) name = "unnamed";
        name = name.Replace(' ', '_');
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return $"ep{ep.Number}_{_levelFileNum:00}_{name}";
    }

    private void StartSavePng(string title, string defaultName)
    {
        _pickPurpose = PickPurpose.SavePng;
        // No common file dialog off Windows: fall back to the working directory, as before.
        if (!OperatingSystem.IsWindows())
        {
            WritePendingPng(Path.Combine(Environment.CurrentDirectory, defaultName));
            return;
        }
        StartPickWindows(title, defaultName);
    }

    /// <param name="saveName">null = folder chooser (data dir); otherwise the Save-As box.</param>
    [SupportedOSPlatform("windows")]
    private void StartPickWindows(string title, string? saveName)
    {
        _pickActive = true; _pickDone = false; _pickResult = null; _pickError = null;
        string init = saveName == null ? _dataDir : DefaultExportDir();
        IntPtr owner = NativeFileDialog.ForegroundWindow();
        var th = new Thread(() =>
        {
            try
            {
                _pickResult = saveName == null
                    ? NativeFileDialog.PickFolderBlocking(init, owner, title)
                    : NativeFileDialog.SaveFileBlocking(init, saveName, owner, title);
            }
            catch (Exception ex)
            {
                _pickError = ex.Message;
                Console.Error.WriteLine("File chooser failed: " + ex);
            }
            finally { _pickDone = true; }
        }) { IsBackground = true, Name = "FileDialog" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
    }

    /// <summary>Where the Save-As box opens: wherever the last save went, else Pictures.</summary>
    private string DefaultExportDir() => _exportDir.Length > 0 && Directory.Exists(_exportDir)
        ? _exportDir
        : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    /// <summary>Write the snapshot taken when the button was pressed, off the UI thread.</summary>
    private void WritePendingPng(string path)
    {
        var pixels = _pendingPixels;
        _pendingPixels = null;
        if (_exportActive || pixels == null) return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
        _exportDir = Path.GetDirectoryName(path) ?? _exportDir;

        int w = _pendingW, h = _pendingH;
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
            DrawPlaybackCanvas();     // a pending jump is a seek there, taken in UpdatePlayback
            return;
        }

        var canvasPos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 16 || avail.Y < 16) return;
        _canvasAvail = avail;

        if (!_viewInitialized && _level != null) { FitWidth(); _viewInitialized = true; }
        // After the fit, so a jump requested while another level was loaded is not undone by it.
        if (_pendingJump is { } jumpTo) { _pendingJump = null; ScrollToEventTime(jumpTo.Time, avail); }

        ImGui.InvisibleButton("canvas_btn", avail,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();
        bool canvasActive = ImGui.IsItemActive();
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();

        var minimap = MinimapRect(canvasPos, avail);
        bool inMinimap = minimap is { } mm &&
            mouse.X >= mm.Min.X && mouse.X < mm.Max.X && mouse.Y >= mm.Min.Y && mouse.Y < mm.Max.Y;

        UpdateMinimapDrag(minimap, inMinimap, hovered, avail);
        // While the strip owns the mouse the canvas must stay out of it: no pan, no zoom,
        // no picking - and that holds for the whole drag, even once the cursor wanders off.
        bool minimapOwns = inMinimap || _minimapDragging;
        bool panActive = canvasActive && !_minimapDragging;

        if (hovered && !minimapOwns)
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

            if (_objMode == 1) DrawMarkers(dl, origin, mouse, hovered && !minimapOwns);
            else if (_objMode == 0 && hovered && !minimapOwns && !panActive)
                DrawSpriteHover(dl, origin, mouse);
            UpdateHoverInfo(origin, mouse, hovered && !minimapOwns);
            DrawMinimap(dl, minimap, avail, hovered && minimapOwns);
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
    //  Transport-bar vector glyphs. The default ImGui font has no media
    //  symbols, so the play / step / skip icons are drawn straight into the
    //  button rect from simple triangles and bars.
    // =====================================================================
    private enum Glyph { JumpStart, Rewind, Play, Pause, FastFwd, JumpEnd }

    private static void TriRight(ImDrawListPtr dl, Vector2 c, float r, uint col)
        => dl.AddTriangleFilled(
            new Vector2(c.X - r * 0.60f, c.Y - r),
            new Vector2(c.X + r * 0.82f, c.Y),
            new Vector2(c.X - r * 0.60f, c.Y + r), col);

    private static void TriLeft(ImDrawListPtr dl, Vector2 c, float r, uint col)
        => dl.AddTriangleFilled(
            new Vector2(c.X + r * 0.60f, c.Y - r),
            new Vector2(c.X - r * 0.82f, c.Y),
            new Vector2(c.X + r * 0.60f, c.Y + r), col);

    private static void VBar(ImDrawListPtr dl, float cx, float cy, float halfH, float halfW, uint col)
        => dl.AddRectFilled(new Vector2(cx - halfW, cy - halfH), new Vector2(cx + halfW, cy + halfH), col, 1f);

    /// <summary>A transport button with a vector-drawn icon; returns true on click.</summary>
    private bool TransportBtn(string id, Glyph g, string tip, Vector2 size,
        bool primary = false, bool active = false, float gap = 4f)
    {
        int pushed = 0;
        if (primary)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Gfx.Rgba(58, 104, 182));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Gfx.Rgba(78, 128, 212));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Gfx.Rgba(98, 150, 232));
            pushed = 3;
        }
        else if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Gfx.Rgba(150, 92, 52));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Gfx.Rgba(184, 116, 68));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Gfx.Rgba(206, 138, 84));
            pushed = 3;
        }

        bool hit = ImGui.Button(id, size);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);

        var mn = ImGui.GetItemRectMin();
        var mx = ImGui.GetItemRectMax();
        var c = new Vector2((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f);
        float r = MathF.Max(3f, MathF.Round((mx.Y - mn.Y) * 0.22f));
        float tk = MathF.Max(1.5f, r * 0.28f);
        uint col = Gfx.Rgba(241, 241, 247);
        var dl = ImGui.GetWindowDrawList();
        switch (g)
        {
            case Glyph.JumpStart:
                VBar(dl, c.X - r * 1.02f, c.Y, r, tk, col);
                TriLeft(dl, new Vector2(c.X + r * 0.42f, c.Y), r, col);
                break;
            case Glyph.Rewind:
                TriLeft(dl, new Vector2(c.X - r * 0.52f, c.Y), r, col);
                TriLeft(dl, new Vector2(c.X + r * 0.78f, c.Y), r, col);
                break;
            case Glyph.Play:
                TriRight(dl, new Vector2(c.X + r * 0.14f, c.Y), r * 1.14f, col);
                break;
            case Glyph.Pause:
                VBar(dl, c.X - r * 0.46f, c.Y, r, MathF.Max(1.5f, r * 0.32f), col);
                VBar(dl, c.X + r * 0.46f, c.Y, r, MathF.Max(1.5f, r * 0.32f), col);
                break;
            case Glyph.FastFwd:
                TriRight(dl, new Vector2(c.X - r * 0.78f, c.Y), r, col);
                TriRight(dl, new Vector2(c.X + r * 0.52f, c.Y), r, col);
                break;
            case Glyph.JumpEnd:
                TriRight(dl, new Vector2(c.X - r * 0.42f, c.Y), r, col);
                VBar(dl, c.X + r * 1.02f, c.Y, r, tk, col);
                break;
        }

        if (pushed > 0) ImGui.PopStyleColor(pushed);
        ImGui.SameLine(0, gap);
        return hit;
    }

    // =====================================================================
    //  The playback HUD: a draggable, collapsible window floated over the
    //  playfield, anchored top-right on first show.
    //
    //  It carries some twenty-five controls now, so it is built to fold down
    //  small: every group is a collapsible section whose header keeps a live
    //  badge -- difficulty, width, player position, effect count -- so a shut
    //  section still says what it is set to, and the flags are chips instead
    //  of a column of checkboxes. Which sections are open is ours to remember
    //  (the window is NoSavedSettings) and persists across runs.
    // =====================================================================
    private enum PbSec { Sim, Build, Player, Enemies, Display, Routes, Status }

    private const float HudW = 258f;      // fixed content width, so the chip grid comes out even
    // Pinned, the HUD is a column: its content width plus the padding either side and room for
    // a scrollbar, so unfolding every section never squeezes the chip grid.
    private const float HudColW = HudW + 40f;
    // What the canvas must keep for the column to be worth giving up the width: the transport
    // row's own floor, every control on it at its shortest (measured: ~442px at the default
    // font). Under that the HUD goes back to floating rather than leave a row that clips.
    private const float MinCanvasW = 460f;
    private static readonly uint HudBg = Gfx.Rgba(15, 17, 23, 236);
    private static readonly uint AcSim     = Gfx.Rgba(255, 190,  90);
    private static readonly uint AcBuild   = Gfx.Rgba(110, 225, 195);
    private static readonly uint AcPlayer  = Gfx.Rgba(120, 210, 250);   // the player glyph's cyan
    private static readonly uint AcEnemy   = Gfx.Rgba(255, 120, 120);
    private static readonly uint AcDisplay = Gfx.Rgba(185, 150, 255);
    private static readonly uint AcRoutes  = Gfx.Rgba(255, 150,  90);   // the timeline's gate orange
    private static readonly uint AcStatus  = Gfx.Rgba(150, 162, 185);
    private static readonly uint AcIdle    = Gfx.Rgba(146, 154, 172);
    private static readonly uint AcGo      = Gfx.Rgba(120, 220, 140);

    /// <summary>Scale a packed colour's channels (Gfx.Rgba order), optionally re-alpha'd.</summary>
    private static uint Shade(uint c, float f, byte a = 255)
    {
        static byte Ch(uint c, int shift, float f) => (byte)Math.Clamp(((c >> shift) & 0xFF) * f, 0f, 255f);
        return Gfx.Rgba(Ch(c, 0, f), Ch(c, 8, f), Ch(c, 16, f), a);
    }

    /// <summary>A small rounded status pill drawn inline, for the always-visible header row.</summary>
    private static void Pill(string text, uint accent)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var pad = new Vector2(6f, 2f);
        var box = ImGui.CalcTextSize(text) + pad * 2f;
        dl.AddRectFilled(p, p + box, Shade(accent, 0.30f, 225), 3f);
        dl.AddRect(p, p + box, Shade(accent, 0.72f, 170), 3f);
        dl.AddText(p + pad, Shade(accent, 1.05f), text);
        ImGui.Dummy(box);
    }

    /// <summary>
    /// A toggle chip: a button that lights up in its section's accent when the flag is on,
    /// with a lit left edge so a row of them reads at a glance. Chips that re-run the whole
    /// simulation carry a small dot, because those cost a rebuild and the draw-only ones don't.
    /// </summary>
    private bool Chip(string label, bool on, uint accent, float w, string tip, bool rebuilds = false)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, on ? Shade(accent, 0.42f, 235) : Gfx.Rgba(40, 42, 52, 220));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, on ? Shade(accent, 0.58f, 245) : Gfx.Rgba(58, 62, 76, 235));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, on ? Shade(accent, 0.74f) : Gfx.Rgba(74, 80, 96));
        ImGui.PushStyleColor(ImGuiCol.Text, on ? Gfx.Rgba(246, 249, 255) : Gfx.Rgba(150, 154, 168));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        bool hit = ImGui.Button(label, new Vector2(w, ImGui.GetFrameHeight()));
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        var mn = ImGui.GetItemRectMin();
        var mx = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        if (on) dl.AddRectFilled(mn, new Vector2(mn.X + 2.5f, mx.Y), accent, 2f);
        if (rebuilds)
            dl.AddCircleFilled(new Vector2(mx.X - 5f, mn.Y + 5f), 1.8f,
                Gfx.Rgba(255, 205, 125, on ? (byte)235 : (byte)140));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tip + (rebuilds ? "\n\n(dot: rebuilds the timeline)" : ""));
        return hit;
    }

    /// <summary>
    /// One collapsible HUD section: an accent bar in the window padding, the title, and a
    /// right-aligned live badge that keeps the section legible while it is folded shut.
    /// </summary>
    private bool Section(PbSec id, string title, uint accent, string badge = "")
    {
        int i = (int)id;
        ImGui.Dummy(new Vector2(0, 1));
        ImGui.SetNextItemOpen(_pbOpen[i], ImGuiCond.Always);
        ImGui.PushStyleColor(ImGuiCol.Header, Shade(accent, 0.30f, 70));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Shade(accent, 0.40f, 125));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Shade(accent, 0.55f, 165));
        ImGui.PushStyleColor(ImGuiCol.Text, accent);
        bool open = ImGui.CollapsingHeader($"{title}##sec{i}");
        ImGui.PopStyleColor(4);
        _pbOpen[i] = open;

        var mn = ImGui.GetItemRectMin();
        var mx = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(new Vector2(mn.X - 7f, mn.Y + 1f), new Vector2(mn.X - 4f, mx.Y - 1f), accent);
        if (badge.Length > 0)
        {
            var sz = ImGui.CalcTextSize(badge);
            dl.AddText(new Vector2(mx.X - sz.X - 6f, mn.Y + (mx.Y - mn.Y - sz.Y) * 0.5f),
                Shade(accent, 0.95f, 170), badge);
        }
        return open;
    }

    /// <summary>The floating overlay: the HUD as a draggable window over the playfield. Used
    /// while it is not pinned -- pinned, DrawSimColumn puts the same body in a real column.</summary>
    private void DrawSimOverlay(Vector2 viewPos, Vector2 viewSize)
    {
        if (_playback == null || _hudDocked) return;
        var pb = _playback;

        ImGui.SetNextWindowPos(new Vector2(viewPos.X + viewSize.X - 12f, viewPos.Y + 12f),
            ImGuiCond.FirstUseEver, new Vector2(1f, 0f));
        // Auto-sized, but never taller than the view it floats over: unfold every section on a
        // short window and the HUD scrolls instead of running off the bottom of the screen.
        ImGui.SetNextWindowSizeConstraints(Vector2.Zero,
            new Vector2(4096f, Math.Max(220f, viewSize.Y - 24f)));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, HudBg);
        ImGui.PushStyleColor(ImGuiCol.Border, Gfx.Rgba(92, 104, 140, 210));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Gfx.Rgba(28, 36, 56, 235));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Gfx.Rgba(44, 60, 92, 245));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        PushHudMetrics();

        bool hudOpen = ImGui.Begin("Playback controls##pbhud",
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoNavInputs | ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoFocusOnAppearing);
        // Its rect, collapsed or not, is what "UI fit" keeps the game view clear of.
        _hudPos = ImGui.GetWindowPos();
        _hudSize = ImGui.GetWindowSize();
        if (hudOpen) DrawHudBody(pb);
        ImGui.End();
        PopHudMetrics();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);
    }

    /// <summary>
    /// The pinned form: a fixed, square-cornered column down the right edge of the window,
    /// the mirror of the controls column on the left. Drawn from the root layout rather than
    /// from the canvas, so it owns its width instead of covering the view.
    /// </summary>
    private void DrawSimColumn()
    {
        var pb = _playback!;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, HudBg);
        PushHudMetrics();
        ImGui.BeginChild("pbcolumn", new Vector2(HudColW, 0), ImGuiChildFlags.Borders);
        // Nothing overlaps the view in this mode, but keep the rect live so an unpin lands
        // "UI fit" on a truthful rectangle on the very next frame.
        _hudPos = ImGui.GetWindowPos();
        _hudSize = ImGui.GetWindowSize();
        DrawHudBody(pb);
        ImGui.EndChild();
        PopHudMetrics();
        ImGui.PopStyleColor();
    }

    /// <summary>The tighter metrics the HUD's chip grid is laid out against, in either form.</summary>
    private static void PushHudMetrics()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(11, 9));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5, 2));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(7, 4));
    }

    private static void PopHudMetrics() => ImGui.PopStyleVar(3);

    /// <summary>The HUD's contents: header, then every collapsible section.</summary>
    private void DrawHudBody(SimPlayback pb)
    {
        var sim = pb.Sim;

        // Pin the width so folding sections never reflows the chip grid.
        ImGui.Dummy(new Vector2(HudW, 0));
        DrawHudHeader(pb);

        if (Section(PbSec.Sim, "SIMULATION", AcSim,
                $"{DifficultyNames[_simDifficulty]} · x{_simScrollMult:0.##}"))
            DrawSimSection();

        if (Section(PbSec.Build, "ENGINE BUILD", AcBuild,
                _widescreen ? $"widescreen {GameSim.WideViewW}" : $"vanilla {GameSim.ViewW}"))
            DrawBuildSection(sim);

        if (Section(PbSec.Player, "PLAYER", AcPlayer, $"{_simPlayerX}, {_simPlayerY}"))
            DrawPlayerSection();

        if (Section(PbSec.Enemies, "ENEMIES", AcEnemy,
                !_clickKill ? "click: off" : _clickKillInstant ? "instant kill" : $"{_clickKillDamage} dmg"))
            DrawEnemySection();

        if (Section(PbSec.Display, "DISPLAY", AcDisplay, $"{FxCount()}/6 on"))
            DrawDisplaySection();

        if (Section(PbSec.Routes, "ROUTES & GATES", AcRoutes, $"{pb.LoopRegions.Count}"))
            DrawRoutesAndGates(pb);

        if (Section(PbSec.Status, "STATUS", AcStatus, $"{sim.EnemyOnScreen} on screen"))
            DrawStatusSection(pb, sim);
    }

    /// <summary>Always-visible header: what is being simulated, what the transport is doing,
    /// where the playhead is -- then the one-click presets.</summary>
    private void DrawHudHeader(SimPlayback pb)
    {
        Pill(_widescreen ? $"WIDE {GameSim.WideViewW}" : $"VANILLA {GameSim.ViewW}",
            _widescreen ? AcBuild : AcIdle);
        ImGui.SameLine(0, 4);
        Pill(_playing ? _playDirection > 0 ? $">> x{_playSpeed:0.##}" : $"<< x{_playSpeed:0.##}" : "paused",
            _playing ? AcGo : AcIdle);
        ImGui.SameLine(0, 4);
        Pill($"{SimPlayback.FormatTime(pb.CurrentTick)} / {SimPlayback.FormatTime(pb.Duration)}", AcStatus);

        // Presets. Each is lit while the current flags already match it, so the row doubles
        // as a readout of which build you are watching.
        float w = (HudW - 10f) / 3f;
        bool vanillaNow = !_widescreen && !_wideStarfield;
        bool wideNow = _widescreen && !_expandedParallax && _mirrorLayers && _wideStarfield;
        bool cleanNow = FxLayerCount() == 0;

        if (Chip("Vanilla", vanillaNow, AcSim, w,
                "The DOS game exactly: 264px playfield and the original starfield.\n" +
                "With these two off, playback is byte-for-byte the original.", true))
        { _wideStarfield = false; SetWidescreen(false); }
        ImGui.SameLine(0, 5);
        if (Chip("Widescreen", wideNow, AcBuild, w,
                "The widescreen build: 299px playfield, mirrored layers and the\n" +
                "rewritten full-height starfield. Extra parallax is left off --\n" +
                "it re-spaces every layer, so it is opt-in from ENGINE BUILD.", true))
        {
            _expandedParallax = false;
            _mirrorLayers = _wideStarfield = true;
            SetWidescreen(true);
        }
        ImGui.SameLine(0, 5);
        if (Chip("Clean", cleanNow, AcDisplay, w,
                "Strip the presentation down to terrain and sprites: boss bars,\n" +
                "smoothies, colour fades, spotlight and screen flip all off, for\n" +
                "reading the level itself. Press it again to put them all back.\n" +
                "Draw-only -- the simulation is untouched."))
            SetAllFx(cleanNow);
    }

    private void DrawSimSection()
    {
        ImGui.PushItemWidth(HudW - 84f);
        int dif = _simDifficulty;
        if (ImGui.Combo("difficulty", &dif, DifficultyNames, DifficultyNames.Length))
        { _simDifficulty = dif; BuildPlayback(); }

        float mult = _simScrollMult;
        if (ImGui.SliderFloat("scroll speed", &mult, 0.25f, 3f, "x%.2f"))
            _simScrollMult = mult;
        if (ImGui.IsItemDeactivatedAfterEdit()) BuildPlayback();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("What-if terrain scroll multiplier. The level's own variable\nscroll-rate events still apply on top; the event clock follows\nthe terrain, so spawn pacing changes with it.");

        int cap = _simMaxMinutes;
        if (ImGui.SliderInt("cap (min)", &cap, 1, 30))
            _simMaxMinutes = cap;
        if (ImGui.IsItemDeactivatedAfterEdit()) BuildPlayback();

        int loops = _simLoopCycles;
        if (ImGui.SliderInt("loop cycles", &loops, 1, 8))
            _simLoopCycles = loops;
        if (ImGui.IsItemDeactivatedAfterEdit()) BuildPlayback();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How many times each boss gate / route loop repeats in the\npreview before it continues as if the gate was cleared. The\nloop still plays once on the way in. Higher = watch more\nrepeats, but a longer build and timeline.");
        ImGui.PopItemWidth();

        if (Chip("enemy fire", _simFire, AcSim, HudW,
                "Simulate enemy turrets firing and launching.", true))
        { _simFire = !_simFire; BuildPlayback(); }
    }

    /// <summary>Which engine the playback is: playfield width and the widescreen build's
    /// three enhancements. Everything here except mirroring changes the simulation.</summary>
    private void DrawBuildSection(GameSim sim)
    {
        float w = (HudW - 5f) / 2f;

        if (Chip("widescreen", _widescreen, AcBuild, w,
                "Play in true widescreen (299px playfield vs the vanilla 264),\n" +
                "exactly like the widescreen game build: wider view and player\n" +
                "range, parallax, spotlight, terrain filters and starfield across\n" +
                "the full 356px surface, and enemies / shots that persist across\n" +
                "the widened edges. The build's bigger starfield draws from the\n" +
                "same RNG as the level, so spawns differ from vanilla -- as they\n" +
                "do in the real build.", true))
            SetWidescreen(!_widescreen);
        ImGui.SameLine(0, 5);
        // Not a widescreen sub-option: vanilla's starfield stops seven rows above the
        // playfield bottom at either width, so the rewrite is worth having in both modes.
        if (Chip("tall starfield", _wideStarfield, AcBuild, w,
                "Widescreen build's rewritten starfield: 330 stars that only\n" +
                "ever move down, filling the playfield to its bottom edge and out\n" +
                "to the full screen width, recycling above the top instead of\n" +
                "popping in. Off = vanilla's 100 stars on a 16-bit position, which\n" +
                "stop 7 rows short of the bottom and jump sideways as they wrap.\n" +
                "Works in vanilla mode too -- the only enhancement that does, so\n" +
                "leaving it on is the one way vanilla playback is not byte-for-byte.\n" +
                "The two seed different counts from the level RNG, so spawns shift\n" +
                "-- as they do between the real builds.", true))
        { _wideStarfield = !_wideStarfield; BuildPlayback(); }

        // The remaining two are widescreen-only: shown disabled rather than hidden, so the
        // build's full feature set stays visible from vanilla mode.
        ImGui.BeginDisabled(!_widescreen);
        if (Chip("extra parallax", _widescreen && _expandedParallax, AcBuild, w,
                "Wider parallax (widescreen build's Extra Parallax): the terrain\n" +
                "layer pans edge-to-edge across its full 336px map over the\n" +
                "player's travel -- nothing left hidden off either side -- and the\n" +
                "mid/deep layers sweep proportionally further (uncovering their\n" +
                "edges at far-left). Bound ground enemies ride the same offsets\n" +
                "and slide much further too.", true))
        { _expandedParallax = !_expandedParallax; BuildPlayback(); }
        ImGui.SameLine(0, 5);
        if (Chip("mirror layers", _widescreen && _mirrorLayers, AcBuild, w,
                "Widescreen build's Mirrored Layers: where the parallax pans a\n" +
                "background layer past its own side edge, the layer continues as\n" +
                "a seamless mirror image of itself instead of wrapping into the\n" +
                "adjacent map row. Also carries each row one clipped tile past\n" +
                "its right end, off-screen, so the lava / water smoothies (which\n" +
                "sample to the right) have terrain to read instead of black fill\n" +
                "-- without it they saw-tooth the right edge on levels like\n" +
                "ASSASSIN and LAVA RUN. Off = the original edge wrap."))
        {
            // Draw-only (tile reads/flips at blit time), so no timeline rebuild needed.
            _mirrorLayers = !_mirrorLayers;
            sim.MirrorLayers = _widescreen && _mirrorLayers;
            _playback!.RedrawCurrent();
        }
        ImGui.EndDisabled();
        if (!_widescreen) ImGui.TextDisabled("(those two are widescreen-only)");
    }

    /// <summary>The phantom player: the marker toggles, and its position with the jump
    /// buttons. The position drives parallax / aim whether or not the marker is drawn,
    /// so the readout stays available in every mode.</summary>
    private void DrawPlayerSection()
    {
        float w = (HudW - 5f) / 2f;
        if (Chip("drag player", _playerSimMode, AcPlayer, w,
                "Show a draggable player marker on the view. The parallax\nbackground scroll, the light cone and enemy aim/fire all\nfollow it, exactly as the engine derives them in-game.\nThe position sticks when you turn the marker back off.\nRight-drag on the view moves the player whether or not\nthe marker is shown."))
        {
            // The position is sticky, so toggling only shows / hides the marker;
            // the sim already holds the last dragged spot — no rebuild needed.
            _playerSimMode = !_playerSimMode;
            _draggingPlayer = false;
        }
        ImGui.SameLine(0, 5);
        ImGui.BeginDisabled(!_playerSimMode);
        if (Chip("hide marker", _playerSimMode && _pivotInvisible, AcPlayer, w,
                "Hide the marker and its guide line but keep the\nplayer position active (parallax / aim still follow it).\nRight-drag on the view still moves it -- watch the\nparallax rather than the marker."))
            _pivotInvisible = !_pivotInvisible;
        ImGui.EndDisabled();

        // Playfield-centre targets, from the phantom-player -> buffer mapping.
        // cX widens with the playfield (149 vanilla / 166 widescreen).
        int cX = GameSim.OX + GameSim.ViewX + PlayfieldW / 2 - PlayerBufOX;
        const int cY = GameSim.OY + GameSim.ViewH / 2 - PlayerBufOY;                  // 80
        void SetPlayer(int px, int py)
        {
            _simPlayerX = Math.Clamp(px, PlayerXMin, PlayerXMax);
            _simPlayerY = Math.Clamp(py, 0, 170);
            BuildPlayback();
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"x {_simPlayerX}  y {_simPlayerY}");
        ImGui.SameLine(0, 10);
        ImGui.TextDisabled("center");
        ImGui.SameLine(0, 6);
        if (ImGui.SmallButton("X##ctr")) SetPlayer(cX, _simPlayerY);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Center the player on the X axis.");
        ImGui.SameLine(0, 4);
        if (ImGui.SmallButton("Y##ctr")) SetPlayer(_simPlayerX, cY);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Center the player on the Y axis.");
        ImGui.SameLine(0, 4);
        if (ImGui.SmallButton("both##ctr")) SetPlayer(cX, cY);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Center the player on both axes.");
        ImGui.SameLine(0, 8);
        if (ImGui.SmallButton("reset##pl")) SetPlayer(100, 150);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Return the phantom player to its default spot.");
    }

    private void DrawEnemySection()
    {
        float w = (HudW - 5f) / 2f;
        if (Chip("click damages", _clickKill, AcEnemy, w,
                "Click an enemy in the view to shoot it. Killing one segment of\n" +
                "a linked enemy destroys the whole formation, and whatever it\n" +
                "drops still spawns -- the engine's own shot-collision outcome.\n" +
                "A survivor takes Tyrian's damage flash for a frame.\n" +
                "While this is on the left button is the trigger: pan with the\n" +
                "middle button, and use Fit instead of double-click.\n" +
                "The hit lands on the live frame: playing on keeps it, seeking\n" +
                "back restores the keyframe and the enemy with it."))
        { _clickKill = !_clickKill; _clickKillPress = null; }

        ImGui.SameLine(0, 5);
        ImGui.BeginDisabled(!_clickKill);
        if (Chip("instant kill", _clickKill && _clickKillInstant, AcEnemy, w,
                "One click destroys whatever it hits, past any armor value\n" +
                "the level data can hold. Off = the damage set below, so a\n" +
                "boss takes as many clicks as it would take hits."))
            _clickKillInstant = !_clickKillInstant;

        if (Chip("death explosions", _clickKill && _clickKillExplosions, AcEnemy, w,
                "Spawn the enemy's own death explosion (the big multi-stage\n" +
                "one for 2x2 enemies). Off = it simply vanishes, which keeps\n" +
                "the frame clean when you are clearing enemies to see the\n" +
                "terrain behind them."))
            _clickKillExplosions = !_clickKillExplosions;
        ImGui.EndDisabled();

        if (_clickKill && !_clickKillInstant)
        {
            ImGui.SameLine(0, 5);
            ImGui.PushItemWidth(w - 56f);
            int dmg = _clickKillDamage;
            if (ImGui.SliderInt("damage", &dmg, 1, 254))
                _clickKillDamage = dmg;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Armor removed per click. Enemy armor tops out at 254; 255\n" +
                    "means invulnerable, which only an instant kill gets through.");
            ImGui.PopItemWidth();
        }
    }

    /// <summary>How many of the five effect layers are on (what "Clean" clears).</summary>
    private int FxLayerCount() =>
        (_showBossBars ? 1 : 0) + (_showTerrainSmoothies ? 1 : 0) + (_showScreenFilter ? 1 : 0) +
        (_showSpotlight ? 1 : 0) + (_showScreenFlip ? 1 : 0);

    /// <summary>...plus extended view, which is framing rather than an effect: the DISPLAY badge.</summary>
    private int FxCount() => FxLayerCount() + (_simExtendedView ? 1 : 0);

    /// <summary>Push every presentation flag into the sim and redraw the current frame.
    /// All of these are draw-only, so the timeline itself never has to be rebuilt.</summary>
    private void ApplyDisplayFlags()
    {
        var sim = _playback!.Sim;
        sim.ShowBossBars = _showBossBars;
        sim.ShowTerrainSmoothies = _showTerrainSmoothies;
        sim.ShowScreenFilter = _showScreenFilter;
        sim.ShowSpotlight = _showSpotlight;
        sim.ShowScreenFlip = _showScreenFlip;
        sim.ExtendedDraw = _simExtendedView;
        _playback.RedrawCurrent();
    }

    /// <summary>The "Clean" preset: the five effect layers off (or all back on).</summary>
    private void SetAllFx(bool on)
    {
        _showBossBars = _showTerrainSmoothies = _showScreenFilter =
            _showSpotlight = _showScreenFlip = on;
        ApplyDisplayFlags();
    }

    /// <summary>Switch playfield width. The player range and the fitted view both change with
    /// it, and so does the simulation (parallax, cull bounds, starfield), so it rebuilds.</summary>
    private void SetWidescreen(bool ws)
    {
        _widescreen = ws;
        _draggingPlayer = false;
        _simPlayerX = Math.Clamp(_simPlayerX, PlayerXMin, PlayerXMax);  // keep the marker inside the new bounds
        _playZoom = 0; _playPan = Vector2.Zero;                         // refit the view to the new width
        BuildPlayback();
    }

    private void DrawDisplaySection()
    {
        float w = (HudW - 5f) / 2f;
        void Fx(string label, ref bool field, string tip)
        {
            if (Chip(label, field, AcDisplay, w, tip)) { field = !field; ApplyDisplayFlags(); }
        }

        Fx("boss bars", ref _showBossBars,
            "The boss / enemy armor readout bars drawn across the top of the screen.");
        ImGui.SameLine(0, 5);
        Fx("smoothies", ref _showTerrainSmoothies,
            "Lava, water, iced-blur, and blur feedback effects.");

        Fx("color / fades", ref _showScreenFilter,
            "Full-screen event-44 color, darken, lighten, and fade effects.");
        ImGui.SameLine(0, 5);
        Fx("spotlight", ref _showSpotlight,
            "The light-cone presentation only; terrain smoothies stay unchanged.");

        Fx("screen flip", ref _showScreenFlip,
            "The event-driven upside-down playfield presentation.");
        ImGui.SameLine(0, 5);
        if (Chip("extended view", _simExtendedView, AcDisplay, w,
                "Zoom out beyond the in-game screen: full map width plus\nenemies/terrain before they scroll in. The yellow rectangle\nmarks what the player actually sees."))
        {
            _simExtendedView = !_simExtendedView;
            ApplyDisplayFlags();
            _playZoom = 0;
            _playPan = Vector2.Zero;
        }

        if (ImGui.SmallButton("effects on")) SetAllFx(true);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turn the five effect layers on (extended view is left alone).");
        ImGui.SameLine(0, 5);
        if (ImGui.SmallButton("effects off")) SetAllFx(false);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turn the five effect layers off (extended view is left alone).");
        ImGui.SameLine(0, 8);
        ImGui.TextDisabled("draw-only");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Nothing in this section touches the simulation, so none of it\ncosts a rebuild -- the current frame is simply redrawn.");
    }

    /// <summary>
    /// Live status: where the playhead is, what the engine is doing there, and a sparkline of
    /// enemies on screen for the seconds either side of it (drawn straight from the precomputed
    /// density, so it is just as valid while scrubbing or rewinding as it is while playing).
    /// </summary>
    private void DrawStatusSection(SimPlayback pb, GameSim sim)
    {
        var dl = ImGui.GetWindowDrawList();
        const float gh = 40f;
        var p = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(HudW, gh));
        dl.AddRectFilled(p, p + new Vector2(HudW, gh), Gfx.Rgba(22, 24, 31, 235), 3f);

        const int half = 160;                        // ~4.5 s of game time either side
        int t0 = pb.CurrentTick - half, t1 = pb.CurrentTick + half;
        int peak = 4;   // a floor, so a quiet stretch doesn't scale two enemies to full height
        for (int t = Math.Max(1, t0); t <= Math.Min(pb.Density.Length, t1); t++)
            if (pb.Density[t - 1] > peak) peak = pb.Density[t - 1];

        // The top band stays clear for the caption, so a busy stretch can't swallow it.
        const float top = 15f, floorY = gh - 2f;
        for (int x = 0; x < (int)HudW; x++)
        {
            int t = t0 + (int)((long)x * (t1 - t0) / (int)HudW);
            if (t < 1 || t > pb.Density.Length) continue;
            float bh = pb.Density[t - 1] / (float)peak * (floorY - top);
            dl.AddLine(new Vector2(p.X + x, p.Y + floorY - bh), new Vector2(p.X + x, p.Y + floorY),
                t <= pb.CurrentTick ? Gfx.Rgba(110, 180, 250, 205) : Gfx.Rgba(88, 106, 145, 150));
        }
        float cx = p.X + HudW * 0.5f;
        dl.AddLine(new Vector2(cx, p.Y + top - 2f), new Vector2(cx, p.Y + gh - 1f), Gfx.Rgba(255, 235, 130, 225));
        dl.AddText(p + new Vector2(5, 2), Gfx.Rgba(138, 146, 166), "enemies on screen");
        string peakTag = $"now {sim.EnemyOnScreen} · peak {peak}";
        dl.AddText(new Vector2(p.X + HudW - ImGui.CalcTextSize(peakTag).X - 5f, p.Y + 2f),
            Gfx.Rgba(138, 146, 166), peakTag);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Enemies on screen over the {half * 2 / (int)GameSim.TicksPerSecond} s " +
                "around the playhead\n(centre line), from the precomputed density.");

        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled($"tick {pb.CurrentTick}/{pb.Duration}   loc {sim.CurLoc}   " +
            $"enemies {sim.EnemyOnScreen}");
        var (b1, b2, b3) = sim.BackMoves;
        ImGui.TextDisabled($"scroll {b1}/{b2}/{b3}   built in {pb.PrecomputeMs} ms");
        ImGui.TextDisabled(pb.EndedNaturally
            ? pb.LoopDetected ? $"ends naturally; {pb.LoopSummary}" : "ends naturally"
            : pb.LoopDetected ? pb.LoopSummary : "capped (no natural end)");
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// The loop/gate inventory the precompute found: every boss gate, enemy hold and route
    /// loop, with its start time and retained-cycle count (set by "loop cycles"). Each row
    /// seeks to that section on click; the section under the playhead is highlighted. The
    /// textual companion to the hatched regions on the timeline.
    /// </summary>
    private void DrawRoutesAndGates(SimPlayback pb)
    {
        if (pb.LoopRegions.Count == 0)
        {
            ImGui.TextDisabled(pb.EndedNaturally
                ? "none - level plays straight through"
                : "none detected (capped)");
            return;
        }

        static string Mmss(int tick)
        {
            int s = (int)Math.Round(tick / GameSim.TicksPerSecond);
            return $"{s / 60}:{s % 60:00}";
        }

        var dl = ImGui.GetWindowDrawList();
        float h = ImGui.GetTextLineHeight();
        for (int i = 0; i < pb.LoopRegions.Count; i++)
        {
            var r = pb.LoopRegions[i];
            bool active = pb.CurrentTick >= r.StartTick && pb.CurrentTick <= r.EndTick;
            int n = r.CycleEnds.Length;
            (string kind, string amount, uint swatch) = r.Kind switch
            {
                SimPlayback.HoldLoopKind.ScriptedLoop =>
                    ("boss gate", $"x{n} kept (until destroyed)", Gfx.Rgba(255, 150, 90)),
                SimPlayback.HoldLoopKind.RouteLoop =>
                    ("route loop", $"x{n} cycles", Gfx.Rgba(120, 200, 255)),
                _ => ("enemy hold", "holds until destroyed", Gfx.Rgba(230, 120, 210)),
            };

            // colour swatch (boss = orange, route = blue, hold = magenta), then the row.
            var p = ImGui.GetCursorScreenPos();
            dl.AddRectFilled(new Vector2(p.X, p.Y + 2), new Vector2(p.X + h, p.Y + h), swatch);
            ImGui.Dummy(new Vector2(h, h));
            ImGui.SameLine(0, 4);

            if (ImGui.Selectable($"{kind,-10} {Mmss(r.StartTick),5}  {amount}##g{i}", active))
            { pb.SeekTo(r.StartTick); _playing = false; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    $"{kind} at {SimPlayback.FormatTime(r.StartTick)} - {SimPlayback.FormatTime(r.EndTick)}\n" +
                    $"{amount}\nclick to jump here");
        }
    }

    // The phantom player (GameSim.PlayerX/Y) maps to the buffer via the engine's own
    // spotlight geometry: bufX = PlayerX + 79, bufY = PlayerY + 140.
    private const int PlayerBufOX = 79, PlayerBufOY = 140;

    /// <summary>
    /// Buffer-space to view-space mapping for the playback canvas, presentation flip included.
    /// JE_starShowVGA's code 1 mirrors the playfield band in place before the frame is shown
    /// (GameSim.PreparePresent), so everything laid over it — enemy markers, the hover box and
    /// its tooltip, the tile readout, the player glyph, click-to-kill — has to travel through
    /// the same mirror the pixels did, or it addresses the reflection instead of the thing.
    /// Reflection is its own inverse, so one function serves both directions.
    /// </summary>
    private readonly struct PlayView
    {
        private readonly Vector2 _imgPos;
        private readonly float _scale, _texOX, _texOY;
        private readonly int _vw, _vh;
        private readonly float _fx0, _fx1;   // the mirrored column span

        public PlayView(GameSim sim, bool extended, Vector2 imgPos, float scale, int vw, int vh)
        {
            _imgPos = imgPos; _scale = scale; _vw = vw; _vh = vh;
            // The texture is the whole buffer in extended view, else the cropped playfield.
            _texOX = extended ? 0 : GameSim.OX + GameSim.ViewX;
            _texOY = extended ? 0 : GameSim.OY;
            Flipped = sim.ScreenFlipped;
            _fx0 = GameSim.OX + GameSim.ViewX;
            _fx1 = _fx0 + sim.PlayfieldWidth;
        }

        /// <summary>True when this frame is presented upside-down.</summary>
        public bool Flipped { get; }

        /// <summary>Does the pixel at this buffer point take part in the flip? Only the
        /// playfield band is copied mirrored, so in extended view the margins around it stay
        /// where they were and a point out there must not be moved.</summary>
        private bool Flips(float bx, float by) =>
            Flipped && bx >= _fx0 && bx < _fx1 &&
            by >= GameSim.OY && by <= GameSim.OY + GameSim.ViewH;

        /// <summary>Reflect within the band. Continuous rather than per-pixel-index: a buffer
        /// coordinate here is a position — a sprite's centre, the cursor — and this form is its
        /// own exact inverse at any fraction, so a drag still tracks the cursor to the pixel.</summary>
        private static float Mirror(float by) => 2f * GameSim.OY + GameSim.ViewH - by;

        private Vector2 Place(float bx, float by) =>
            _imgPos + new Vector2(bx - _texOX, by - _texOY) * _scale;

        /// <summary>Where a buffer point shows up on the presented frame.</summary>
        public Vector2 ToScreen(float bx, float by) =>
            Place(bx, Flips(bx, by) ? Mirror(by) : by);

        /// <summary>Which buffer point a point on the presented frame is over.</summary>
        public Vector2 ToBuffer(Vector2 pt)
        {
            var b = (pt - _imgPos) / _scale + new Vector2(_texOX, _texOY);
            return new Vector2(b.X, Flips(b.X, b.Y) ? Mirror(b.Y) : b.Y);
        }

        /// <summary>Screen-space bounds of a buffer-space box, back in min/max order after the
        /// mirror swaps its top and bottom. The flip is decided once from the centre, so a box
        /// straddling the band's edge in extended view still comes out a box.</summary>
        public (Vector2 Min, Vector2 Max) Box(float bx0, float by0, float bx1, float by1) =>
            Flips((bx0 + bx1) * 0.5f, (by0 + by1) * 0.5f)
                ? (Place(bx0, Mirror(by1)), Place(bx1, Mirror(by0)))
                : (Place(bx0, by0), Place(bx1, by1));

        /// <summary>Is this buffer point inside the drawn texture? Tested before the flip,
        /// which maps the band onto itself and so can never move a point in or out.</summary>
        public bool InView(float bx, float by) =>
            bx >= _texOX && by >= _texOY && bx < _texOX + _vw && by < _texOY + _vh;
    }

    /// <summary>
    /// Player-position mode: a draggable marker at the phantom player. Dragging it feeds
    /// GameSim.PlayerX/Y, so the parallax, light cone and enemy aim re-derive live; a
    /// full rebuild on release makes the whole timeline coherent with the new position.
    ///
    /// Two gestures. Left-click grabs the marker itself, so it needs the marker on screen.
    /// Holding the RIGHT button aims the player straight at the cursor from anywhere in the
    /// view, and stays available with the marker hidden or player mode off — the parallax
    /// swinging under the cursor is the feedback then, which is the point of hiding it.
    /// </summary>
    private void HandlePlayerMarker(ImDrawListPtr dl, in PlayView view,
        Vector2 mouse, bool viewHovered, Vector2 viewPos, Vector2 viewSize)
    {
        if (_playback == null) return;
        bool markerShown = _playerSimMode && !_pivotInvisible;

        // When the playfield is presented upside-down (screen flip), the marker must sit on the
        // flipped image, so it goes out through PlayView and the drag comes back in the same
        // way. The mirror is its own inverse, so the glyph still tracks the cursor exactly.
        Vector2 mk = view.ToScreen(_simPlayerX + PlayerBufOX, _simPlayerY + PlayerBufOY);
        bool over = markerShown && viewHovered && Vector2.Distance(mouse, mk) <= 15f;

        if (!_draggingPlayer && viewHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        { _draggingPlayer = true; _playerDragButton = ImGuiMouseButton.Right; }
        else if (!_draggingPlayer && over && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        { _draggingPlayer = true; _playerDragButton = ImGuiMouseButton.Left; }

        if (_draggingPlayer)
        {
            if (ImGui.IsMouseDown(_playerDragButton))
            {
                var mb = view.ToBuffer(mouse);
                int nx = Math.Clamp((int)MathF.Round(mb.X - PlayerBufOX), PlayerXMin, PlayerXMax);
                int ny = Math.Clamp((int)MathF.Round(mb.Y - PlayerBufOY), 0, 170);
                if (nx != _simPlayerX || ny != _simPlayerY)
                {
                    _simPlayerX = nx; _simPlayerY = ny;
                    _playback.Sim.PlayerX = nx; _playback.Sim.PlayerY = ny;
                    _playback.RedrawCurrent();
                    mk = view.ToScreen(nx + PlayerBufOX, ny + PlayerBufOY);
                }
            }
            else
            {
                _draggingPlayer = false;
                BuildPlayback();   // make the entire timeline coherent with the new position
            }
        }

        if (!markerShown) return;   // hidden on purpose — the right-drag above still moved it

        // A faint guide line at the player's X marks the parallax pivot.
        dl.AddLine(new Vector2(mk.X, viewPos.Y), new Vector2(mk.X, viewPos.Y + viewSize.Y),
            Gfx.Rgba(120, 220, 255, 45));
        DrawPlayerGlyph(dl, mk, _draggingPlayer || over, view.Flipped);
    }

    /// <summary>
    /// "Left-click damages enemies": shoot the enemy under the cursor for the configured
    /// damage. Armed on press and fired on release only if the cursor stayed put, so a
    /// left-drag still pans the view; <see cref="HandlePlayerMarker"/> runs first, so a press
    /// that grabbed the player marker never doubles as a shot.
    /// </summary>
    private void HandleClickKill(in PlayView view, Vector2 mouse, bool viewHovered)
    {
        if (!_clickKill || _playback == null) return;

        if (viewHovered && !_draggingPlayer && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _clickKillPress = mouse;
        if (_clickKillPress is not { } press || !ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            return;

        _clickKillPress = null;
        if (Vector2.Distance(mouse, press) > 4f) return;   // that was a pan, not a click

        // Through PlayView, so a shot on an upside-down screen hits the enemy the cursor is
        // over rather than its reflection across the playfield's middle.
        var b = view.ToBuffer(press);
        int slot = _playback.Sim.PickEnemyAt(
            (int)MathF.Floor(b.X), (int)MathF.Floor(b.Y), _objCatMask);
        if (slot < 0) return;

        int damage = _clickKillInstant ? GameSim.InstantKillDamage : _clickKillDamage;
        if (!_playback.Sim.DamageEnemy(slot, damage, _clickKillExplosions)) return;

        // Nothing of this shows in the frame already on screen: the sprite goes away and the
        // explosion is spawned for the next drawn tick either way. Stepping one is also what
        // the hit would have cost in-game. RedrawCurrent is deliberately NOT used — it re-seeks
        // from the nearest keyframe, which would put the enemy straight back.
        _playback.Advance(1);
    }

    /// <summary>A reticle + ship glyph marking the draggable phantom player. The ship
    /// points down when the playfield is presented flipped, matching the mirrored view.</summary>
    private static void DrawPlayerGlyph(ImDrawListPtr dl, Vector2 c, bool hot, bool flipped)
    {
        float r = hot ? 11f : 9f;
        float sy = flipped ? -1f : 1f;   // point the ship the same way the playfield reads
        uint fill = hot ? Gfx.Rgba(150, 232, 255) : Gfx.Rgba(96, 202, 245);
        uint edge = Gfx.Rgba(10, 20, 30, 235);
        uint ring = Gfx.Rgba(150, 235, 255, (byte)(hot ? 240 : 175));
        dl.AddCircle(c, r + 3f, ring, 0, hot ? 2f : 1.5f);
        var p1 = new Vector2(c.X, c.Y - r * sy);
        var p2 = new Vector2(c.X - r * 0.88f, c.Y + r * 0.78f * sy);
        var p3 = new Vector2(c.X + r * 0.88f, c.Y + r * 0.78f * sy);
        dl.AddTriangleFilled(p1, p2, p3, fill);
        dl.AddTriangle(p1, p2, p3, edge, 1.5f);
        dl.AddCircleFilled(c, 1.7f, edge);
        dl.AddLine(new Vector2(c.X - r - 7, c.Y), new Vector2(c.X - r - 1, c.Y), ring);
        dl.AddLine(new Vector2(c.X + r + 1, c.Y), new Vector2(c.X + r + 7, c.Y), ring);
    }

    /// <summary>
    /// The slice of the view an auto-fit aims at, view-relative. Normally the whole panel --
    /// the HUD is a floating overlay and the frame is free to sit under it. With "UI fit" armed
    /// the band the HUD covers is carved off whichever side it sits on, so the two end up
    /// side by side and no part of the game view is hidden behind the controls.
    /// </summary>
    private (Vector2 Off, Vector2 Size) FitRegion(Vector2 viewPos, Vector2 viewSize)
    {
        if (!_fitAroundHud || _hudSize.X <= 0f) return (Vector2.Zero, viewSize);

        const float gap = 8f, minLeft = 120f;   // never squeeze the frame down to nothing
        float l = _hudPos.X - viewPos.X;        // the HUD's edges, view-relative
        float r = l + _hudSize.X;
        // Pinned it is a column beside the view, not over it: nothing to fit around.
        if (r <= 0f || l >= viewSize.X) return (Vector2.Zero, viewSize);
        if (l + _hudSize.X * 0.5f > viewSize.X * 0.5f)      // HUD on the right half
        {
            float w = Math.Clamp(l - gap, minLeft, viewSize.X);
            return (Vector2.Zero, new Vector2(w, viewSize.Y));
        }
        float x = Math.Clamp(r + gap, 0f, Math.Max(0f, viewSize.X - minLeft));
        return (new Vector2(x, 0f), new Vector2(viewSize.X - x, viewSize.Y));
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
        var (fitOff, fitSize) = FitRegion(pos, viewSize);
        float fit = MathF.Min(fitSize.X / vw, fitSize.Y / vh);
        if (fit >= 1f) fit = MathF.Floor(fit);   // pixel-perfect integer fit
        float scale = _playZoom > 0 ? _playZoom : fit;

        // Right is claimed too, so a right-drag of the player keeps capture once it leaves
        // the view; panning below still only listens for Left / Middle.
        ImGui.InvisibleButton("playview", viewSize, ImGuiButtonFlags.MouseButtonLeft |
            ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
        bool viewHovered = ImGui.IsItemHovered();
        bool viewActive = ImGui.IsItemActive();
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();

        Vector2 CenterOff(float s) => fitOff + new Vector2(
            MathF.Floor((fitSize.X - vw * s) * 0.5f),
            MathF.Floor((fitSize.Y - vh * s) * 0.5f));
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
        // With click-to-kill armed the left button belongs to the guns: pan with the middle
        // button only, and drop the left double-click refit, which would otherwise fire twice
        // and yank the view out from under the shot. (The Fit button below still refits.)
        bool leftIsTrigger = _clickKill;
        if (viewActive && !_draggingPlayer &&
            ((!leftIsTrigger && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) ||
             ImGui.IsMouseDragging(ImGuiMouseButton.Middle)))
            _playPan += io.MouseDelta;
        if (viewHovered && !_draggingPlayer && !leftIsTrigger &&
            ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
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
            var vp1 = vp0 + new Vector2(PlayfieldW * scale, GameSim.ViewH * scale);
            dl.AddRect(vp0, vp1, Gfx.Rgba(255, 225, 120, 200), 0f, 0, 1.5f);
            dl.AddText(vp0 + new Vector2(4, 2), Gfx.Rgba(255, 225, 120, 170), "screen");
        }
        // One mapping for everything laid over the frame, so markers, hover, the readout and
        // the guns all agree with the picture — including when it is presented upside-down.
        var view = new PlayView(pb.Sim, _simExtendedView, imgPos, scale, vw, vh);
        DrawPlaybackOverlay(dl, pb, view, scale, mouse,
            viewHovered && !viewActive, pos, viewSize);
        HandlePlayerMarker(dl, view, mouse, viewHovered, pos, viewSize);
        HandleClickKill(view, mouse, viewHovered);
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
        // A soft backdrop keeps the readout legible over bright terrain.
        var osdAt = pos + new Vector2(8, 6);
        var osdSz = ImGui.CalcTextSize(osd);
        dl.AddRectFilled(osdAt - new Vector2(5, 3), osdAt + osdSz + new Vector2(6, 3),
            Gfx.Rgba(12, 12, 16, 145), 4f);
        dl.AddText(osdAt, Gfx.Rgba(238, 238, 246), osd);
        dl.PopClipRect();

        // Floating controls HUD over the playfield: a real draggable/collapsible
        // window, so it leaves the canvas layout cursor untouched.
        DrawSimOverlay(pos, viewSize);

        // --- transport row ---
        float fh = ImGui.GetFrameHeight();
        var bsz = new Vector2(MathF.Round(fh * 1.5f), fh);        // step / skip buttons
        var psz = new Vector2(MathF.Round(fh * 2.1f), fh);        // hero play / pause

        // A plain text button matched to the icon buttons' height.
        bool TextBtn(string label, Vector2 size, string tip, float gap = 4f)
        {
            bool hit = ImGui.Button(label, size);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
            ImGui.SameLine(0, gap);
            return hit;
        }

        bool fwd = _playing && _playDirection > 0;
        bool rewinding = _playing && _playDirection < 0;

        if (TransportBtn("##pbstart", Glyph.JumpStart, "Jump to start", bsz))
        { pb.SeekTo(1); _playing = false; }
        if (TransportBtn("##pbrewind", Glyph.Rewind, rewinding ? "Stop rewind" : "Play backwards",
                bsz, active: rewinding))
        {
            if (rewinding) _playing = false;
            else { _playing = true; _playDirection = -1; _playAccum = 0; }
        }
        if (TextBtn("-1", bsz, "Step one tick back (Left)"))
        { pb.SeekTo(pb.CurrentTick - 1); _playing = false; }

        if (TransportBtn("##pbplay", fwd ? Glyph.Pause : Glyph.Play,
                fwd ? "Pause (Space)" : "Play (Space)", psz, primary: true))
        {
            if (fwd) _playing = false;
            else { if (pb.AtEnd) pb.SeekTo(1); _playing = true; _playDirection = 1; _playAccum = 0; }
        }

        if (TextBtn("+1", bsz, "Step one tick forward (Right)"))
        { pb.SeekTo(pb.CurrentTick + 1); _playing = false; }
        if (TransportBtn("##pbff", Glyph.FastFwd,
                "Fast-forward (cycles 2x / 4x / 8x; Play resets to 1x)", bsz,
                active: fwd && _playSpeed > 1f))
        {
            _playing = true; _playDirection = 1; _playAccum = 0;
            _playSpeed = _playSpeed switch { < 2f => 2f, < 4f => 4f, < 8f => 8f, _ => 1f };
        }
        if (TransportBtn("##pbend", Glyph.JumpEnd, "Jump to end", bsz, gap: 12f))
        { pb.SeekTo(pb.Duration); _playing = false; }

        // The tail of the row has to survive a narrow canvas -- pinning the HUD into its own
        // column takes ~300px out of it. Nothing here wraps or scrolls, so instead the parts
        // give way in order of how well they earn their space, and the controls never move.
        const float comboW = 64f;
        float fitW = MathF.Round(fh * 1.7f);
        float W(string s) => ImGui.CalcTextSize(s).X;
        float ChipW(string s) => MathF.Ceiling(W(s) + ImGui.GetStyle().FramePadding.X * 2f + 10f);

        // First to give: the layout chips' labels, down to a word each, once the full ones
        // would crowd out the speed control. The ## ids stay fixed so the buttons do not
        // lose their hover/active state as the labels change.
        bool roomy = ImGui.GetContentRegionAvail().X >=
            fitW + 5f + ChipW("UI fit") + 4f + ChipW("Pin right") + 12f + comboW;
        string uiLbl = roomy ? "UI fit" : "UI", pinLbl = roomy ? "Pin right" : "Pin";

        if (TextBtn("Fit", new Vector2(fitW, fh),
                "Fit the whole panel, zoom and pan reset (or double-click the view).\n" +
                "The frame is free to sit under the controls HUD.\nwheel = zoom, drag = pan", 5f))
        { _fitAroundHud = false; _playZoom = 0; _playPan = System.Numerics.Vector2.Zero; }

        // The two layout toggles: fit clear of the HUD, and keep the HUD somewhere to be clear of.
        // Sized off the label rather than a multiple of the row height, which clipped them.
        if (Chip($"{uiLbl}##uifit", _fitAroundHud, AcDisplay, ChipW(uiLbl),
                "Fit the view into what the floating \"Playback controls\" HUD leaves free,\n" +
                "so the frame sits beside the panel instead of under it. Stays armed:\n" +
                "resizes and folding HUD sections re-fit to the space actually left.\n" +
                "Pinned right, the HUD is already out of the view and this does nothing."))
        { _fitAroundHud = !_fitAroundHud; _playZoom = 0; _playPan = System.Numerics.Vector2.Zero; }
        ImGui.SameLine(0, 4);
        if (Chip($"{pinLbl}##pinright", _hudPinRight, AcDisplay, ChipW(pinLbl),
                "Turn the \"Playback controls\" HUD into a fixed column down the right edge\n" +
                "of the window -- the mirror of the controls column on the left -- instead\n" +
                "of a panel floating over the playfield. The view keeps the rest."))
        { _hudPinRight = !_hudPinRight; _playZoom = 0; _playPan = System.Numerics.Vector2.Zero; }
        ImGui.SameLine(0, 12);

        // What is actually left for the tail, measured rather than modelled.
        float left = ImGui.GetContentRegionAvail().X;
        string endTag = pb.LoopDetected
            ? $"  [{pb.LoopRegions.Count} loop/hold section{(pb.LoopRegions.Count == 1 ? "" : "s")} previewed]"
            : pb.EndedNaturally ? "" : "  [capped]";
        string ticks = $"{pb.CurrentTick}/{pb.Duration}";
        float TailW(bool word, string tick) => (word ? W("speed") + 6f : 0f) + comboW
            + (tick.Length > 0 ? 14f + W(tick) : 0f);

        // Richest tail that fits. The loop/cap tag goes first (the timeline marks those
        // sections anyway), then the labelling words, then the tick counter itself -- the
        // OSD over the view still has the clock. The last entry is the combo alone, which
        // always fits: the fallback to a floating HUD keeps the canvas wider than that.
        var tails = new (bool Word, string Tick)[]
        {
            (true,  $"tick {ticks}{endTag}"),
            (true,  $"tick {ticks}"),
            (true,  ticks),
            (false, ticks),
            (false, ""),
        };
        var tail = tails.FirstOrDefault(t => TailW(t.Word, t.Tick) <= left, tails[^1]);

        if (tail.Word)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("speed");
            ImGui.SameLine(0, 6);
        }
        ImGui.SetNextItemWidth(comboW);
        int speedIdx = Array.IndexOf(SpeedSteps, _playSpeed);
        if (speedIdx < 0) speedIdx = 2;
        var speedNames = SpeedSteps.Select(s => $"x{s:0.##}").ToArray();
        if (ImGui.Combo("##speed", &speedIdx, speedNames, speedNames.Length))
            _playSpeed = SpeedSteps[speedIdx];
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Playback speed (game runs at 35 ticks/s)");

        if (tail.Tick.Length > 0)
        {
            ImGui.SameLine(0, 14);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(tail.Tick);
            if (endTag.Length > 0 && !tail.Tick.EndsWith(']') && ImGui.IsItemHovered())
                ImGui.SetTooltip(endTag.Trim());
        }

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

    /// <summary>Screen-Y span of the viewport indicator on the minimap strip.</summary>
    private (float Y0, float Y1) MinimapIndicator((Vector2 Min, Vector2 Max) r, Vector2 avail)
    {
        int H = CanvasHeight();
        float stripH = r.Max.Y - r.Min.Y;
        float vis0 = Math.Clamp(-_scroll.Y / _zoom / H, 0f, 1f);
        float vis1 = Math.Clamp((avail.Y - _scroll.Y) / _zoom / H, 0f, 1f);
        float y0 = r.Min.Y + vis0 * stripH;
        return (y0, Math.Max(r.Min.Y + vis1 * stripH, y0 + 3));
    }

    /// <summary>
    /// Minimap dragging, resolved before the canvas' own pan so only one of them acts.
    /// Grabbing the viewport indicator keeps the offset under the cursor, so the level
    /// tracks the hand instead of snapping; pressing elsewhere on the strip centres the
    /// viewport on the click first, then drags on from there. The drag is driven straight
    /// off the mouse rather than an ImGui item: the canvas button covers the strip and,
    /// being submitted first, would win hover over anything laid on top of it.
    /// </summary>
    private void UpdateMinimapDrag((Vector2 Min, Vector2 Max)? rect, bool inMinimap,
        bool hovered, Vector2 avail)
    {
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _minimapDragging = false;
        if (rect is not { } r || _level == null) { _minimapDragging = false; return; }

        float stripH = r.Max.Y - r.Min.Y;
        if (!_minimapDragging)
        {
            if (!hovered || !inMinimap || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
            var (y0, y1) = MinimapIndicator(r, avail);
            float my = ImGui.GetMousePos().Y;
            _minimapGrab = my >= y0 && my <= y1 ? my - y0 : (y1 - y0) * 0.5f;
            _minimapDragging = true;
        }

        // Place the indicator's top where the grab says it should be, and read the scroll back out.
        float top = ImGui.GetMousePos().Y - _minimapGrab;
        _scroll.Y = -(top - r.Min.Y) / stripH * CanvasHeight() * _zoom;
    }

    /// <summary>
    /// Whole-level overview strip at the canvas' right edge: the composited level squashed
    /// vertically, the current viewport marked; click or drag to jump.
    /// </summary>
    private void DrawMinimap(ImDrawListPtr dl, (Vector2 Min, Vector2 Max)? rect, Vector2 avail, bool hot)
    {
        if (rect is not { } r) return;

        dl.AddRectFilled(r.Min, r.Max, Gfx.Rgba(8, 8, 10, 235));
        _img.DrawInRect(dl, r.Min, r.Max);
        dl.AddRect(r.Min, r.Max, Gfx.Rgba(95, 95, 110));

        var (y0, y1) = MinimapIndicator(r, avail);
        dl.AddRectFilled(new Vector2(r.Min.X, y0), new Vector2(r.Max.X, y1),
            Gfx.Rgba(255, 255, 255, (byte)(hot ? 56 : 32)));
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
    private void DrawPlaybackOverlay(ImDrawListPtr dl, SimPlayback pb, in PlayView view,
        float scale, Vector2 mouse, bool hovered, Vector2 viewPos, Vector2 viewSize)
    {
        if (!hovered) _hoverInfo = "";
        if (_objMode == 2 && !hovered) return;

        pb.Sim.CollectEnemies(_playEnemies, _objCatMask);

        // Buffer space is what the sim reports and what it picks in, so the cursor is taken
        // there once — through the presentation flip, if there is one — and everything below
        // compares in that one space, upside-down screen or not.
        var m = view.ToBuffer(mouse);
        bool canPick = hovered && _objMode != 2;
        float r = Math.Clamp(3.5f * scale, 3f, 9f);
        GameSim.EnemyView? pick = null;
        for (int i = _playEnemies.Count - 1; i >= 0; i--)   // front-most band first
        {
            var e = _playEnemies[i];
            if (!view.InView(e.CenterX, e.CenterY)) continue;
            if (canPick && pick == null &&
                Math.Abs(m.X - e.CenterX) <= e.HalfW && Math.Abs(m.Y - e.CenterY) <= e.HalfH)
                pick = e;
            if (_objMode != 1) continue;
            var p = view.ToScreen(e.CenterX, e.CenterY);
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
            var (b0, b1) = view.Box(hit.CenterX - hit.HalfW, hit.CenterY - hit.HalfH,
                                    hit.CenterX + hit.HalfW, hit.CenterY + hit.HalfH);
            dl.AddRect(b0 - one, b1 + one, ObjectPlacer.CategoryColor(hit.Category));
            ShowEnemyTooltip(hit);
        }

        int bx = (int)MathF.Floor(m.X), by = (int)MathF.Floor(m.Y);
        if (!view.InView(bx, by)) { _hoverInfo = ""; return; }
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
        _cubeFace.Dispose();
    }
}
