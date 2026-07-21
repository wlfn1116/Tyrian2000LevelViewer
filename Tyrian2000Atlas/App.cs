using System.Numerics;
using System.Runtime.Versioning;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;
using SdlNs = Hexa.NET.SDL2;

namespace T2A;

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
    // Named because they are also what a right-click on the slider goes back to -- see
    // SliderReset. A default that only exists as a literal in a field initialiser is one the
    // control cannot offer you.
    private const int DefaultMaxMinutes = 10, DefaultLoopCycles = 2, DefaultHoldSeconds = 20;
    private const int DefaultClickKillDamage = 10;
    private int _simMaxMinutes = DefaultMaxMinutes;
    private int _simLoopCycles = DefaultLoopCycles;   // boss-gate / route-loop repeats kept
    private int _simHoldSeconds = DefaultHoldSeconds; // enemy-gated hold watched (and kept)
    private float _playZoom;                 // 0 = fit to the panel automatically
    private Vector2 _playPan;                // manual pan offset, screen px
    private bool _fitAroundHud;              // "UI fit": fit the slice the controls HUD leaves free
    private bool _hudPinRight;               // dock the controls HUD to the view's right edge
    private bool _hudDocked;                 // ... and this frame's layout actually had room for it
    private Vector2 _hudPos, _hudSize;       // its rect last frame -- what "UI fit" fits around
    private bool _simExtendedView;           // show beyond the in-game screen
    private bool _engaged;                // Engaged playback (356px playfield, wider bounds)
    private bool _expandedParallax;          // Engaged sub-option: wider all-layer parallax sweep (commit edd8118)
    private bool _mirrorLayers = true;       // Engaged sub-option: mirror layers past their side edges (commit 1f7ba83)
    private bool _tallStarfield = true;      // the Engaged build's rewritten starfield (either mode)
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
    private int _clickKillDamage = DefaultClickKillDamage;   // ... or for this much
    private bool _clickKillExplosions = true; // spawn the death débris, or just vanish
    // The enemy the overlay's hover box was last drawn around, and the cursor position it was
    // resolved at. This is the trigger's fallback aim -- see ResolveClickKillTarget.
    private GameSim.EnemyView? _hoverPick;
    private Vector2 _hoverPickAt;
    // How far (screen px, squared) the cursor may sit from where a hover pick was latched and
    // still fall back to it. Generous on purpose: the fallback only fires when the live pick
    // already found nothing under the cursor, and the overlay only latches a slot while the
    // cursor is actually over it, so this just guards against the cursor having jumped clear.
    private const float HoverLatchRadiusSq = 40f * 40f;
    private bool _draggingPlayer;             // a player-position drag is in progress
    // Which button holds that drag: Left grabbed the marker, Right is the aim-anywhere
    // gesture that works with the marker hidden (or player mode off entirely).
    private ImGuiMouseButton _playerDragButton = ImGuiMouseButton.Left;
    // Which HUD sections are unfolded, indexed by PbSec. The HUD window is NoSavedSettings
    // (it is placed over the view, not by the .ini), so this is ours to keep and persist.
    private readonly bool[] _pbOpen = { true, false, false, false, true, false, true, true };
    // Playfield crop width for the current mode (Engaged 299 / vanilla 264) and the phantom-
    // player X range derived from it. Vanilla keeps the atlas's historical 36..260; Engaged
    // mode uses the build's ACTUAL ship clamp [SHIP_LEFT_MARGIN, PLAYFIELD_WIDTH -
    // SHIP_RIGHT_MARGIN] = [29, 303] -- the expanded-parallax sweep normalizes over exactly this
    // travel, so both walls must be reachable. Field-based (not sim-based) so it is valid before
    // a playback exists (constructor --player clamp).
    private int PlayfieldW => _engaged ? GameSim.EngagedViewW : GameSim.ViewW;
    private int PlayerXMin => _engaged ? 29 : 36;
    private int PlayerXMax => _engaged ? GameSim.EngagedViewW + 4 : GameSim.ViewW - 4;   // 303 / 260
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
        _engaged = settings.Engaged;   // needed before the --player clamp (PlayerXMax) below
        _expandedParallax = settings.ExpandedParallax;
        _mirrorLayers = settings.MirrorLayers;
        _tallStarfield = settings.TallStarfield;
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
        _analysisDifficulty = Math.Clamp(settings.AnalysisDifficulty, 0, 10);
        _enemyMode = Math.Clamp(settings.EnemyBrowseMode, 0, 1);
        _asmUnique = settings.AssembliesUnique ?? true;
        _sprGapless = settings.SpritesGapless;
        _sprCols = Math.Clamp(settings.SpritesColumns, 0, 40);
        _sprCheckerboard = settings.SpritesCheckerboard ?? true;
        _sprNumbers = settings.SpritesNumbers;
        _enemyMotion = settings.EnemyMotion ?? true;
        _itemFork = settings.ItemsFork ?? true;
        if (settings.SpriteListWidth > 100f) _sprListW = settings.SpriteListWidth;
        if (settings.EnemyListWidth > 100f) _enemyListW = settings.EnemyListWidth;
        if (settings.ItemListWidth > 100f) _itemListW = settings.ItemListWidth;
        _allEpisodes = settings.AllEpisodes;
        _levelOrder = Math.Clamp(settings.LevelOrder, FileOrder, PlayOrder);
        if (settings.TreeEdgeMask != 0) _treeEdgeMask = settings.TreeEdgeMask;   // 0 = never saved
        if (settings.PbSections >= 0)   // -1 = never saved: keep the defaults above
        {
            // Only as many bits as the build that wrote them had sections; a section added
            // since then keeps its own default rather than reading a 0 that means "absent".
            int saved = settings.PbSectionCount > 0 ? settings.PbSectionCount : 7;
            for (int i = 0; i < Math.Min(saved, _pbOpen.Length); i++)
                _pbOpen[i] = (settings.PbSections & (1 << i)) != 0;
        }
        LoadAudioSettings(settings);
        _hudPinRight = settings.PbPinRight;
        _fitAroundHud = settings.PbFitAroundHud;
        _levelsHeight = settings.LevelsHeight > 30 ? settings.LevelsHeight : 170f;
        _layersHeight = settings.LayersHeight > 30 ? settings.LayersHeight : 0f;  // 0 = fit to content
        // Clamped on the way in: the ranges can move between builds, and the layout only
        // re-clamps the left column against the live window -- the HUD's floating form has no
        // window to clamp against at all.
        if (settings.ControlsWidth > 0)
            _controlsW = Math.Clamp(settings.ControlsWidth, ControlsWMin, ControlsWMax);
        if (settings.HudWidth > 0)
            _hudW = Math.Clamp(settings.HudWidth, HudWMin, HudWMax);
        if (settings.HudColumnWidth > 0)
            _hudColW = Math.Clamp(settings.HudColumnWidth, HudColWMin, HudColWMax);
        ApplyLayerSettings(settings.Layers);
        LoadRefWindows(settings.RefWindows);

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
            InitAudio(dir);
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
        s.Engaged = _engaged;
        s.ExpandedParallax = _expandedParallax;
        s.MirrorLayers = _mirrorLayers;
        s.TallStarfield = _tallStarfield;
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
        s.AnalysisDifficulty = _analysisDifficulty;
        s.SpriteListWidth = _sprListW;
        s.EnemyListWidth = _enemyListW;
        s.ItemListWidth = _itemListW;
        s.EnemyBrowseMode = _enemyMode;
        s.AssembliesUnique = _asmUnique;
        s.SpritesGapless = _sprGapless;
        s.SpritesColumns = _sprCols;
        s.SpritesCheckerboard = _sprCheckerboard;
        s.SpritesNumbers = _sprNumbers;
        s.EnemyMotion = _enemyMotion;
        s.ItemsFork = _itemFork;
        s.AllEpisodes = _allEpisodes;
        s.LevelOrder = _levelOrder;
        s.TreeEdgeMask = _treeEdgeMask;
        int secBits = 0;
        for (int i = 0; i < _pbOpen.Length; i++) if (_pbOpen[i]) secBits |= 1 << i;
        s.PbSections = secBits;
        s.PbSectionCount = _pbOpen.Length;
        SaveAudioSettings(s);
        s.PbPinRight = _hudPinRight;
        s.PbFitAroundHud = _fitAroundHud;
        s.LevelsHeight = _levelsHeight;
        s.LayersHeight = _layersHeight;
        s.ControlsWidth = _controlsW;
        s.HudWidth = _hudW;
        s.HudColumnWidth = _hudColW;
        s.HasView = _viewInitialized;
        s.Zoom = _zoom; s.ScrollX = _scroll.X; s.ScrollY = _scroll.Y;
        s.Layers = _layers.Select(l => new LayerState { Id = l.Id, Visible = l.Visible, Alpha = l.Alpha }).ToList();
        s.RefWindows = SaveRefWindows();
    }

    /// <summary>A full-height vertical splitter bar; drag it to resize the column at its left.
    /// Goes between two SameLine'd children. <paramref name="sign"/> -1 resizes the column at
    /// its <em>right</em> instead, which is what the pinned playback column needs.</summary>
    private void VSplitter(string id, ref float width, float min, float max, float sign = 1f)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Gfx.Rgba(95, 95, 120, 160));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Gfx.Rgba(130, 130, 175, 230));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Gfx.Rgba(160, 160, 210, 255));
        ImGui.Button(id, new Vector2(6, -1));
        if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        if (ImGui.IsItemActive())
            width = Math.Clamp(width + ImGui.GetIO().MouseDelta.X * sign, min, max);
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

    /// <summary>
    /// One row of the level list: which episode it belongs to and where in it. In
    /// single-episode mode every row is the current episode; in "All episodes" the list runs
    /// straight through all five.
    ///
    /// The last two are filled in only in play order (see <see cref="AddEpisodeRows"/>) and
    /// come from the episode's flow graph: which layer of the route the level sits on, and
    /// whether a save point is passed on the way in. Deliberately not the outpost the graph
    /// also knows about — nearly every level has one, so the tag was on nearly every row and
    /// told you nothing about the one you were reading.
    /// </summary>
    private readonly record struct BrowseItem(int Episode, int Level,
        int Stage = 0, bool Save = false);

    private readonly List<BrowseItem> _browse = new();

    /// <summary>The two orderings the level list offers; the index <see cref="_levelOrder"/>
    /// holds and settings.json persists.</summary>
    private const int FileOrder = 0, PlayOrder = 1;
    private int _levelOrder;

    /// <summary>Refill the level list for the current episode selection. Returns false if
    /// the selected level fell out of the list, so the caller knows to load another.</summary>
    private bool RebuildBrowseList()
    {
        int wantEp = _levelIdx >= 0 && _levelIdx < _browse.Count ? _browse[_levelIdx].Episode : _episodeIdx;
        int wantFile = _levelFileNum;
        _browse.Clear();
        if (_gd == null) return false;

        if (_allEpisodes)
            for (int e = 0; e < _gd.Episodes.Count; e++) AddEpisodeRows(e);
        else if (_episodeIdx < _gd.Episodes.Count)
            AddEpisodeRows(_episodeIdx);

        int found = _browse.FindIndex(b =>
            b.Episode == wantEp && _gd.Episodes[b.Episode].Levels[b.Level].FileNum == wantFile);
        _levelIdx = Math.Max(0, found);
        return found >= 0;
    }

    /// <summary>
    /// One episode's rows, in whichever order is asked for.
    ///
    /// File order is what tyrian%d.lvl holds, which is the order the levels were authored in
    /// and has nothing much to do with playing them: Episode 1 opens on #1 TYRIAN and then
    /// runs #3, #4, #6, #7 as secrets nobody reaches before #2. Play order asks the episode
    /// script instead, through the same flow graph the level tree draws — nodes taken layer by
    /// layer, and left to right within a layer, so a fork's branches sit side by side in the
    /// order the tree shows them and a route rejoining is listed once, where it is first
    /// reached.
    ///
    /// A level the script reaches twice (two ']L' lines, or one level on two routes) is listed
    /// at the first of them; one no route reaches at all still gets a row, at the end, because
    /// the file holds it and the atlas's job is to show what the file holds.
    /// </summary>
    private void AddEpisodeRows(int e)
    {
        var ep = _gd!.Episodes[e];
        EpisodeGraph? g = null;
        if (_levelOrder == PlayOrder)
            try { g = _gd.GetGraph(ep); }
            catch { /* an episode whose script won't resolve simply stays in file order */ }

        if (g == null)
        {
            for (int l = 0; l < ep.Levels.Count; l++) _browse.Add(new BrowseItem(e, l));
            return;
        }

        var indexOf = new Dictionary<int, int>();
        for (int l = 0; l < ep.Levels.Count; l++) indexOf.TryAdd(ep.Levels[l].FileNum, l);

        // Campaign cuts first, whatever layer they sit on, then the Timed Battle arenas. An
        // arena hangs straight off the episode start, so by depth alone it would sort in among
        // the opening levels as though the campaign began with it -- and where one level is
        // both (Episode 1's #5 is arena DELI at layer 1 and the campaign's DELIANI at layer
        // 10), the first cut reached is the one the dedupe keeps, so sorting by depth alone
        // filed the level under the arena and lost the stage it is really played at.
        var placed = new bool[ep.Levels.Count];
        foreach (var node in g.Nodes
                     .Where(nd => nd.Kind == GraphNodeKind.Level)
                     .OrderBy(nd => g.OnCampaignRoute(nd) ? 0 : 1)
                     .ThenBy(nd => nd.Depth).ThenBy(nd => nd.X))
        {
            if (!indexOf.TryGetValue(node.LvlFileNum, out int l) || placed[l]) continue;
            placed[l] = true;
            _browse.Add(g.OnCampaignRoute(node)
                ? new BrowseItem(e, l, node.Depth, node.SavePoint)
                : new BrowseItem(e, l));   // no stage: it is not on the route at all
        }
        for (int l = 0; l < ep.Levels.Count; l++)
            if (!placed[l]) _browse.Add(new BrowseItem(e, l));
    }

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

    /// <summary>Select a level in whatever the atlas is browsing; in "All episodes" this
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
            // Both are answers about the level being left behind: the flags the stack asked the
            // old simulation for, and the order that simulation was last seen drawing in.
            _layerOrderFlags = null;
            _layerLiveSeenValid = false;
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
                    $"{(name.Length > 0 ? name : "(unnamed)")} · Episode {ep.Number} #{fileNum} - Tyrian 2000 Atlas");
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
    /// Where a browser's "open" link wants the atlas to land. <see cref="Time"/> is a map
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
    /// Atlas-wide shortcuts: Up/Down switch levels, PageUp/PageDown pan the canvas a
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

        // A focused audio browser owns space and the arrows: they are that window's transport
        // and its own list. Ahead of the level stepping below, which would otherwise scroll
        // the level list out from under it. The focus flag is a frame stale, which is fine --
        // focus does not change between key repeats.
        if (_audioWindowFocused)
        {
            HandleAudioWindowKeys();
            return;
        }

        // The search palette walks its own results with the arrows while it holds the focus,
        // so stepping the level here as well would scroll the list out from under it.
        if (!(_showSearch && _searchOwnsKeys))
        {
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true)) SelectLevelStep(1);
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true)) SelectLevelStep(-1);
        }

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
            if (ImGui.IsKeyPressed(ImGuiKey.End)) _playback.SeekTo(_playback.DisplayEnd);
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
        // A rebuild is a fresh run of the level, so a live branch cannot survive it -- say so
        // rather than let the LIVE mark disappear off the bar with no explanation when a
        // loop-cycle or hold-length nudge quietly re-predicts everything.
        bool hadBranch = _playback?.Branched ?? false;
        _hoverPick = null;   // a slot number from the old run means nothing in the new one
        try
        {
            var sim = new GameSim(_gd, CurEpisode, _level, _shapes)
            {
                Difficulty = _simDifficulty,
                ScrollMult = _simScrollMult,
                FireEnabled = _simFire,
                ExtendedDraw = _simExtendedView,
                Engaged = _engaged,
                ExpandedParallax = _engaged && _expandedParallax,   // Engaged-only sub-option
                MirrorLayers = _engaged && _mirrorLayers,           // Engaged-only sub-option (draw-only)
                TallStarfield = _tallStarfield,                        // available in both modes
                ShowScreenFilter = _showScreenFilter,
                ShowTerrainSmoothies = _showTerrainSmoothies,
                ShowSpotlight = _showSpotlight,
                ShowScreenFlip = _showScreenFlip,
                ShowBossBars = _showBossBars,
                PreviewLoopCycles = _simLoopCycles,   // boss-gate / route-loop repeats kept
                PreviewHoldSeconds = _simHoldSeconds, // enemy-gated hold watched and kept
                PlayerX = _simPlayerX,   // sticky: the phantom player keeps its last position
                PlayerY = _simPlayerY,   // whether or not the drag marker is currently shown

            };
            _playback = new SimPlayback(sim, Math.Max(1, _simMaxMinutes) * 60 * 35);
            _playback.OnLiveTick = OnSimAudioTick;
            _playback.SeekTo(Math.Clamp(keepTick, 1, _playback.Duration));
            _status = $"Playback ready: {SimPlayback.FormatTime(_playback.Duration)} " +
                (_playback.EndedNaturally ? _playback.LoopDetected
                        ? $"(level end; {_playback.LoopSummary})" : "(level end)"
                    : _playback.LoopDetected ? $"({_playback.LoopSummary})" : "(capped)") +
                $", built in {_playback.PrecomputeMs} ms" +
                (hadBranch ? " -- the live branch was discarded with the old run" : "");
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
    ///
    /// The stack's <em>order</em> goes across too, through <see cref="SyncPlaybackLayerOrder"/>
    /// — see App.Layers.cs for why that is four engine flags rather than a permutation.
    /// </summary>
    private void SyncPlaybackVisibility()
    {
        var sim = _playback!.Sim;
        bool redraw = SyncPlaybackLayerOrder(sim);
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

        if (!redraw && sim.ShowBg1 == bg1 && sim.ShowBg2 == bg2 && sim.ShowBg3 == bg3 &&
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
        UpdateGameAudio();
        // Self-healing: the flag is set from the timeline, which is not drawn in every mode,
        // so a drag that ends somewhere else must not leave the clock held for good.
        if (_tlScrubbing && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) _tlScrubbing = false;
        if (_playing && !_tlScrubbing)
        {
            _playAccum += ImGui.GetIO().DeltaTime * (float)GameSim.TicksPerSecond * _playSpeed;
            int n = (int)_playAccum;
            if (n > 0)
            {
                _playAccum -= n;
                n = Math.Min(n, (int)GameSim.TicksPerSecond * 8);   // avoid catch-up spirals
                if (_playDirection > 0)
                {
                    _playback.Advance(n, audio: true);
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
        PumpSpriteExportAll();   // one bank's sheet a frame, once its folder has been chosen

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

        SyncAudioVolumes();
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

        // Both side columns are dragged rather than fixed, so they are clamped against the
        // window that is actually there first -- a width dragged out on a wide screen, or
        // restored from settings, must not survive into a small window as a canvas of nothing.
        // Whether the HUD gets its own column is settled in the same breath: it is the first
        // thing to give, since a floating HUD still shows everything and a squeezed canvas
        // shows nothing. Both before the canvas draws, because the overlay inside it has to
        // know which of the two HUD forms is in play.
        float room = ImGui.GetContentRegionAvail().X;
        _hudDocked = _playbackMode && _playback != null && _hudPinRight &&
                     room - _controlsW - SplitW * 2 > _hudColW + MinCanvasW;
        float rightSide = _hudDocked ? _hudColW + SplitW : 0f;
        float controlsMax = Math.Max(ControlsWMin,
            Math.Min(ControlsWMax, room - SplitW - rightSide - MinCanvasW));
        _controlsW = Math.Clamp(_controlsW, ControlsWMin, controlsMax);

        ImGui.BeginChild("controls", new Vector2(_controlsW, 0), ImGuiChildFlags.Borders);
        DrawControls();
        ImGui.EndChild();
        ImGui.SameLine(0, 0);
        VSplitter("##colsplit", ref _controlsW, ControlsWMin, controlsMax);
        ImGui.SameLine(0, 0);

        // Pinned, the playback HUD is a column of its own on the right -- the mirror of the
        // controls column -- so the canvas gives up that width instead of being covered by it.
        ImGui.BeginChild("canvas", new Vector2(-rightSide, 0), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        DrawCanvas();
        ImGui.EndChild();
        if (_hudDocked)
        {
            ImGui.SameLine(0, 0);
            // Mirrored: this strip has the column on its right, so dragging it right makes
            // that column narrower -- hence the inverted sign.
            float hudMax = Math.Min(HudColWMax, room - _controlsW - SplitW * 2 - MinCanvasW);
            VSplitter("##hudsplit", ref _hudColW, HudColWMin, Math.Max(HudColWMin, hudMax), -1f);
            ImGui.SameLine(0, 0);
            DrawSimColumn();
        }
        ImGui.End();

        DrawTreeWindow();
        DrawCubeWindow();
        DrawSpriteWindow();
        DrawEnemyWindow();
        DrawItemWindow();
        DrawAnalysisWindow();
        DrawMusicWindow();
        DrawSoundWindow();
        // Space and the arrows belong to whichever audio browser has the focus; HandleKeys
        // reads this next frame, before either window has drawn.
        _audioWindowFocused = _musicFocused || _soundFocused;
        DrawSearchWindow();
    }

    /// <summary>
    /// The reference browsers: everything the data set holds that is not the level in the
    /// viewport. They are toggles rather than a menu because each is a window you leave open
    /// beside the atlas. The first pair reads the episode the picker above is on; the rest
    /// span the whole data set.
    /// </summary>
    private void DrawReferenceButtons()
    {
        UiSection("Reference", AcData, "windows");
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

        // Only while the window is shut: open, the chip is already lit and the player is on show.
        bool musicLive = !_showMusic && _musicOwner == 2 && _audio?.Player is { IsPlaying: true };
        if (Chip("Music", _showMusic, AcMusic, w,
                "Every song in music.mus laid out like a DAW clip: all nine Loudness\n" +
                "channels, their notes, where each song is used, and a choice of\n" +
                "AdLib, SoundFont or the OS synthesizer to hear it through.",
                playing: musicLive))
            _showMusic = !_showMusic;
        ImGui.SameLine(0, 5);
        if (Chip("Sounds", _showSounds, AcSound, w,
                "All forty clips -- the thirty-one effects and the nine announcer\n" +
                "lines -- with their waveforms and every weapon and level event\n" +
                "that fires them."))
            _showSounds = !_showSounds;

        if (Chip("Search  (Ctrl+F)", _showSearch, AcPlayer, -1f,
                "One box over levels, enemies, items, pickups, outposts, datacubes and sprites."))
            OpenSearch();
    }

    /// <summary>
    /// The playback launch. Every other switch in this column changes how the level is
    /// <em>drawn</em>; this one hands the level to the engine and lets it run, which is a
    /// different order of thing entirely -- and as a checkbox filed between the object display
    /// mode and the palette it read as one more of them. So it is a slab instead: the column's
    /// one big key, sized and lit to say that pressing it changes what the atlas <em>is</em>.
    ///
    /// Armed, it keeps carrying the run -- transport state, speed, position in the timeline --
    /// so the column still reports what the simulation is doing with the playback HUD closed.
    /// </summary>
    private void DrawPlaybackLaunch()
    {
        bool ready = _level != null && _shapes != null;
        float w = ImGui.GetContentRegionAvail().X;
        float h = ImGui.GetTextLineHeight() * 3f + 22f;   // a 2x title row, a caption row, padding

        ImGui.Dummy(new Vector2(0, 2f));
        var p = ImGui.GetCursorScreenPos();
        // Hit-tested by hand rather than through BeginDisabled, the way the rest of this UI's
        // own controls are: a slab that cannot be pressed still has to say why, and
        // BeginDisabled swallows the hover the tooltip hangs off.
        bool hit = ImGui.InvisibleButton("##pblaunch", new Vector2(w, h)) && ready;
        bool hot = ready && ImGui.IsItemHovered();
        bool held = ready && ImGui.IsItemActive();
        if (hit)
        {
            _playbackMode = !_playbackMode;
            _playing = false;
            if (_playbackMode && _playback == null) BuildPlayback();   // may clear the flag again
        }

        bool on = _playbackMode && ready;
        var pb = on ? _playback : null;
        var q = p + new Vector2(w, h);
        var dl = ImGui.GetWindowDrawList();
        float t = (float)ImGui.GetTime();
        uint ac = AcLaunch;
        const float round = 7f;

        // Flat, not a gradient: the house rule is that a surface this big shades into a smear.
        // It also stays a dark panel in every state, engaged included. Lighting a slab this size
        // in the accent does not read as "lit", it reads as a brown block, and it held the eye
        // for as long as playback was on -- beside it the level list looked switched off. So the
        // accent is spent only where it is small enough to stay a colour instead of becoming a
        // background: the border, the glyph and the readout.
        // Idle sits higher up the ramp than an amber slab needed to. Amber was the one warm
        // thing in a cool column, so at rest it separated itself; a blue slab is in the same
        // family as the seven chips above it and reads as one more of them unless the accent is
        // actually present at rest. Hence a resting border and glyph that are properly lit --
        // engaged still pulls clear of it on the border, the title and the readout.
        float lit = !ready ? 0f : on ? 0.15f : hot ? 0.11f : 0.07f;
        FlatRect(dl, p, q, Mix(ready ? UiPanel : Gfx.Rgba(21, 23, 30), ac, lit),
            Mix(UiPanelHi, ac, lit + 0.12f), round);
        // One border, one weight, the state in its brightness -- and it is the whole of the
        // slab's lit edge. The chips carry a separate accent rail because their own border is
        // neutral; here the border is the accent already, so a rail put a second amber vertical
        // three pixels inside the first, and a 3px sliver cannot take the slab's corner radius
        // (ImGui clamps a corner to the rect's own width) so it squared off exactly where the
        // panel curved away. Thickening the border on engage was the other half of that mess:
        // it moved the slab's edge outwards by a pixel, which read as the panel swelling.
        dl.AddRect(p, q, !ready ? Gfx.Rgba(36, 40, 51) : on ? Shade(ac, 0.95f, 235)
            : hot ? Shade(ac, 0.78f, 200) : Mix(UiLineSoft, ac, 0.44f), round);
        if (held) dl.AddRectFilled(p, q, Gfx.Rgba(255, 255, 255, 14), round);

        // The play glyph, and while the run is live one soft ring expanding off it -- the single
        // thing on this panel that moves because the simulation is moving. It used to be three
        // at once: this ring, a breathing outline around the whole slab, and a light sweeping
        // the top lip. That is two more than "it is running" needs, and between them they left
        // pausing as the only way to get a calm look at the panel.
        var c = new Vector2(p.X + 28f, (p.Y + q.Y) * 0.5f);
        dl.AddCircleFilled(c, 13f, on ? Shade(ac, 0.30f, 245) : Gfx.Rgba(28, 32, 41), 28);
        dl.AddCircle(c, 13f, !ready ? Gfx.Rgba(44, 48, 60) : on ? Shade(ac, 1.15f, 240)
            : hot ? Shade(ac, 0.95f, 225) : Shade(ac, 0.62f, 215), 28, 1.5f);
        if (on && _playing)
        {
            float k = t % 1f;
            dl.AddCircle(c, 13f + k * 9f, Shade(ac, 1.1f, (byte)(105 * (1f - k))), 28, 1.5f);
        }
        dl.AddTriangleFilled(new Vector2(c.X - 3.6f, c.Y - 6.6f), new Vector2(c.X - 3.6f, c.Y + 6.6f),
            new Vector2(c.X + 6.8f, c.Y),
            // Fully lit even at rest -- idle, this is a launch key waiting, not a dead one, and
            // the glyph is small enough to take the accent neat without shouting.
            !ready ? Gfx.Rgba(64, 69, 84) : on ? Gfx.Rgba(242, 248, 255)
            : hot ? Shade(ac, 1.15f) : ac);

        float line = ImGui.GetTextLineHeight();
        float tx = p.X + 48f, rx = q.X - 11f;
        float titleY = p.Y + 9f, capY = titleY + line * 2f + 4f;

        // The clock rides the title row, right-aligned, with the elapsed bright and the length
        // behind it dim. They are one string but not one fact, and set in a single colour they
        // read as a run of digits you have to take apart before either half is any use. The
        // title is clipped to whatever they leave -- a narrowed column loses letters off
        // "PLAYBACK" rather than running the two into each other.
        string now = pb != null ? SimPlayback.FormatTime(pb.CurrentTick) : "";
        string span = pb != null ? $" / {SimPlayback.FormatTime(pb.DisplayEnd)}" : "";
        float spanW = span.Length > 0 ? ImGui.CalcTextSize(span).X : 0f;
        float clockW = now.Length > 0 ? ImGui.CalcTextSize(now).X + spanW : 0f;
        ClipScaled(dl, new Vector2(tx, titleY),
            Math.Max(16f, rx - tx - (clockW > 0f ? clockW + 10f : 0f)),
            !ready ? Gfx.Rgba(72, 78, 94) : on ? Gfx.Rgba(240, 246, 255)
            : hot ? UiText : Gfx.Rgba(178, 185, 202), 2, "PLAYBACK");
        if (clockW > 0f)
        {
            float cy = titleY + line * 0.5f;
            dl.AddText(new Vector2(rx - clockW, cy), Shade(ac, 1.15f), now);
            dl.AddText(new Vector2(rx - spanW, cy), Alpha(UiDim, 200), span);
        }

        if (pb != null)
        {
            // What the run is doing and how far through the level it is, so the column reports
            // the simulation whether or not the HUD is open.
            string state = _playing ? _playDirection > 0 ? "running" : "rewind"
                : pb.AtEnd ? "at end" : "paused";
            dl.AddText(new Vector2(tx, capY),
                _playing ? AcGo : pb.AtEnd ? AcStatus : Shade(ac, 1.05f), state);
            float used = ImGui.CalcTextSize(state).X + 9f;
            if (_playing && Math.Abs(_playSpeed - 1f) > 0.01f)
            {
                string sp = $"{_playSpeed:0.##}x";
                dl.AddText(new Vector2(tx + used, capY), Alpha(UiDim, 220), sp);
                used += ImGui.CalcTextSize(sp).X + 9f;
            }
            if (rx - tx - used > 24f)
                MeterBar(dl, new Vector2(tx + used, capY + 4.5f), new Vector2(rx, capY + 9f),
                    pb.Duration > 0 ? pb.CurrentTick / (float)pb.Duration : 0f, ac);
        }
        else
        {
            ClipText(dl, new Vector2(tx, capY), rx - tx,
                !ready ? Gfx.Rgba(74, 80, 96) : hot ? Alpha(UiText, 235) : UiFaint,
                !ready ? "load a level to enable" : on ? "no timeline - see status"
                : hot ? "click to engage the simulation" : "simulate this level like the game");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(ready
                ? "Simulate the level exactly like in-game: enemies move, fire and\n" +
                  "launch, all level events run, camera locked to the player's view.\n" +
                  "Space = play/pause, Left/Right = step (Shift = 1 s)."
                : "Playback runs the level the atlas has open -- load one first.");
        ImGui.Dummy(new Vector2(0, 2f));
    }

    // The controls column's own accents. The reference chips already carry their windows'
    // colours; these are for the sections that belong to the column itself, so that a heading,
    // its rule and the controls under it read as one block the way they do in every browser.
    private static readonly uint AcData  = Gfx.Rgba(142, 158, 188);   // where the game lives
    private static readonly uint AcLevel = Gfx.Rgba(120, 210, 165);   // the level list
    private static readonly uint AcView  = Gfx.Rgba(168, 176, 202);   // zoom and framing

    /// <summary>
    /// The controls column. Everything in it is drawn in the same visual language as the seven
    /// reference browsers -- <see cref="UiSection"/> headings, wells, chips and drawn buttons --
    /// rather than in ImGui's defaults. It was the last part of the app still speaking the old
    /// dialect: SeparatorText headings that centre their label, a Combo captioned "display", a
    /// bordered child of Selectables. Nothing about the arrangement changed; what changed is
    /// that the column now looks like the windows it opens.
    /// </summary>
    private void DrawControls()
    {
        // The same metrics the playback HUD lays out against, so the two columns either side of
        // the canvas are built to one measure.
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5, 3));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(7, 5));
        DrawControlsBody();
        ImGui.PopStyleVar(2);
    }

    private void DrawControlsBody()
    {
        float w = ImGui.GetContentRegionAvail().X;

        // --- Data folder (always available) ---
        UiSection("Tyrian folder", AcData);
        // Cut from the LEFT, not with ClipText's ellipsis on the right: on a path it is the
        // tail that identifies the folder, and "D:\Projects\Tyrian2000Leve..." says nothing the
        // heading has not already said. Sized to the column rather than to a fixed character
        // count, so widening it shows more of the path.
        float ch = Math.Max(1f, ImGui.CalcTextSize("M").X);
        UiTextClip(_dataDir.Length == 0 ? "(none)" : Shorten(_dataDir, (int)(w / ch)), UiText, w);
        if (_dataDir.Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(_dataDir);

        float bw = (w - 10f) / 3f;
        if (UiButton("Browse...", AcData, "Pick the folder the game's data files are in",
                bw, _pickActive))
            StartBrowse();
        ImGui.SameLine(0, 5);
        if (UiButton(_showDirInput ? "Hide path" : "Type path", AcData,
                "Type or paste the folder instead", bw))
            _showDirInput = !_showDirInput;
        ImGui.SameLine(0, 5);
        if (UiButton("Reload", AcData, "Read every data file again", bw, _gd == null) && _gd != null)
            LoadData(_dataDir);
        if (_pickActive) ImGui.TextColored(ColorOf(UiFaint), "(choosing folder...)");
        if (_showDirInput) DataDirInput();

        if (_gd == null)
        {
            UiSection("Status", AcStatus);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextWrapped(_status);
            ImGui.PopTextWrapPos();
            return;
        }

        // --- Episode ---
        UiSection("Episode", AcData);
        ImGui.SetNextItemWidth(-1);
        EpisodeCombo("##episode");

        DrawReferenceButtons();
        DrawPlaybackLaunch();

        DrawLevelList(w);
        DrawLayersPanel();

        // --- Objects ---
        UiSection("Objects", AcDisplay, "enemies / items");
        int mode = _objMode;
        if (SegBar("##objmode", ref mode, AcDisplay, w,
                ("Sprites", "Draw the objects the map places, as the game draws them."),
                ("Markers", _playbackMode
                    ? "Category dots instead of sprites, live from the simulation."
                    : "A coloured dot per object instead of its sprite -- the colours\nare the swatches in the layer list."),
                ("Off", "Neither. Hovering the view still reports what is under the cursor.")))
        { _objMode = mode; _composeDirty = true; }

        // Playback itself is launched from the slab under the Reference chips, not from here:
        // it is not a display option, and sitting between two of them is what made it look
        // like one.

        // --- Palette ---
        bool game = _palette == AppSettings.GamePalette;
        UiSection("Palette", AcSprite, game ? "in-game" : $"#{_palette}");
        float palW = game ? w : w - 66f;
        ImGui.SetNextItemWidth(palW);
        int pal = _palette;
        if (ImGui.SliderInt("##palidx", &pal, 0, Math.Max(0, _gd.Palettes.Count - 1)))
        { _palette = pal; _composeDirty = true; }
        if (SliderReset(ref pal, AppSettings.GamePalette,
                $"Which of the game's {_gd.Palettes.Count} palettes to decode through.\n" +
                $"Gameplay always runs in palette {AppSettings.GamePalette}.",
                $"{AppSettings.GamePalette} (in-game)"))
        { _palette = pal; _composeDirty = true; }
        if (!game)
        {
            ImGui.SameLine(0, 5);
            if (UiButton("in-game", AcSprite, $"Back to palette {AppSettings.GamePalette}", 61f))
            { _palette = AppSettings.GamePalette; _composeDirty = true; }
        }

        // --- View, level info, status ---
        // None of the three belongs to a running simulation. The map view's zoom and scroll are
        // not what playback draws (it has its own fit / zoom / pin on the transport row), the
        // level PNG is not what is being looked at, and the two readouts are about the run --
        // so in playback the whole block moves to the HUD instead of sitting here inert.
        if (HudLive) return;

        UiSection("View", AcView, $"{_zoom * 100:0}%");
        ImGui.SetNextItemWidth(w);
        float z = _zoom;
        if (ImGui.SliderFloat("##zoom", &z, MinZoom, MaxZoom, "%.2fx", ImGuiSliderFlags.Logarithmic))
            _zoom = Math.Clamp(z, MinZoom, MaxZoom);
        if (SliderReset(ref z, 1f, "How far the map is blown up. Also the wheel over the canvas.", "1:1"))
        { _zoom = 1f; CenterBottom(); }

        float vw = (w - 15f) / 4f;
        if (UiButton("Fit width", AcView, "Scale the whole map into the canvas width", vw)) FitWidth();
        ImGui.SameLine(0, 5);
        if (UiButton("1:1", AcView, "Actual size, at the bottom of the map", vw))
        { _zoom = 1f; CenterBottom(); }
        ImGui.SameLine(0, 5);
        if (UiButton("Top", AcView, "Jump to the top of the map", vw)) _scroll.Y = 0;
        ImGui.SameLine(0, 5);
        if (UiButton("Bottom", AcView, "Jump to the bottom -- where the level starts", vw))
            _scroll.Y = _canvasAvail.Y - CanvasHeight() * _zoom;

        if (UiButton(_exportActive ? "saving..." : "Save level PNG...", AcView,
                "Write the whole composited level (current layers/palette) as a PNG.", w,
                _exportActive || _pickActive || _level == null))
            BeginSaveLevelPng();

        if (_level != null)
        {
            UiSection("Level info", AcLevel);
            DrawLevelInfoLines();
        }
        UiSection("Status", AcStatus);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextWrapped(_status);
        if (_hoverInfo.Length > 0) ImGui.TextColored(ColorOf(Shade(AcView, 1.05f)), _hoverInfo);
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// The level list: a well of two-line rows, the same shape every browser's list has, with
    /// the episode's own name over the file number it loads from. It was a bordered child of
    /// bare Selectables, which is the one list in the app that did not look like the others.
    /// </summary>
    private void DrawLevelList(float w)
    {
        UiSection("Levels", AcLevel, _allEpisodes ? $"{_browse.Count} · all episodes"
            : $"{_browse.Count}");

        int order = _levelOrder;
        if (SegBar("##lvlorder", ref order, AcLevel, w,
                ("file order", "The order the episode's .lvl file holds them in, which is the\n" +
                    "order they were authored rather than the order they are played:\n" +
                    "Episode 1 opens on #1 and then runs four levels nobody reaches\n" +
                    "before #2."),
                ("play order", "The order the episode script actually plays them in, taken from\n" +
                    "the same flow graph the level tree draws: layer by layer down the\n" +
                    "route, branches side by side. Each row says which stage it is on,\n" +
                    "and whether a save point comes just before it.\n\n" +
                    "A level two routes reach is listed where it is first reached; one\n" +
                    "no route reaches at all goes to the end, marked as such.")))
        {
            _levelOrder = order;
            RebuildBrowseList();
            _scrollLevelListToSelection = true;
        }

        float rowH = ImGui.GetTextLineHeight() * 2f + 6f;
        if (_levelsHeight < 40f) _levelsHeight = 170f;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Gfx.Rgba(0, 0, 0, 0));
        WellBegin("levellist", new Vector2(w, _levelsHeight), AcLevel, 4f, 4f);
        int shownEp = -1;
        for (int i = 0; i < _browse.Count; i++)
        {
            var b = _browse[i];
            if (_allEpisodes && b.Episode != shownEp)
            {
                shownEp = b.Episode;
                UiSection($"Episode {_gd!.Episodes[shownEp].Number}", AcLevel);
            }
            var info = _gd!.Episodes[b.Episode].Levels[b.Level];
            var box = UiRow($"##lvl{i}", i == _levelIdx, AcLevel, rowH);
            if (box.Clicked) SelectLevel(i);
            if (_scrollLevelListToSelection && i == _levelIdx) ImGui.SetScrollHereY(0.5f);

            // The second line is only what the name does not already say -- how the level is
            // reached, and whether it is one of the odd ones. Display would have repeated the
            // name here, which is what a two-line row must never do.
            var tags = new List<string>();
            if (_levelOrder == PlayOrder)
            {
                // What the row's position means, spelled out: in play order the sequence is
                // the information, and a row that says nothing about where it sits leaves you
                // counting rows to find out.
                if (b.Stage > 0)
                {
                    tags.Add($"stage {b.Stage}");
                    if (b.Save) tags.Add("save");
                }
                // No stage means no campaign route reaches it. Being an arena is a reason, and
                // the tag below gives it, so saying both would only be saying it twice.
                else if (!info.TimedBattle) tags.Add("off the route");
            }
            if (info.TimedBattle) tags.Add("timed battle");
            if (info.SecretLevel) tags.Add("secret");
            if (info.DifficultyGate.Length > 0) tags.Add(info.DifficultyGate.ToLowerInvariant());
            if (info.BonusLevel) tags.Add("bonus");
            if (info.GalagaMode) tags.Add("galaga");
            string trail = $"#{info.FileNum}";
            RowText(box, 10f, info.Name.Trim().Length > 0 ? info.Name.Trim() : "(unnamed)",
                string.Join(" · ", tags), AcLevel, box.Selected, TrailRoom(trail));
            RowTrail(box, trail, box.Selected ? Shade(AcLevel, 1.1f) : UiFaint);
        }
        _scrollLevelListToSelection = false;
        WellEnd();
        ImGui.PopStyleColor();
        HSplitter("##lvlsplit", ref _levelsHeight, 40f, 600f);
    }

    /// <summary>Playback is live and its HUD is on screen -- the panel that carries the view
    /// controls, the level info and the status line while the simulation runs.</summary>
    private bool HudLive => _playbackMode && _playback != null;

    /// <summary>What the loaded level is made of. Shown in the controls column normally, and
    /// in the HUD's STATUS section during playback -- the same three lines either way.</summary>
    private void DrawLevelInfoLines()
    {
        if (_level == null) return;
        ImGui.Text($"shapes{char.ToLower(_level.ShapeChar)}.dat   events {_level.Events.Length}");
        ImGui.Text($"objects {_objects.Count}   enemy pool {_level.LevelEnemy.Length}");
        if (_timeline?.IsUnrolled == true)
            ImGui.Text($"continuous length {_timeline.Distance:N0} px");
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
    /// is drawing, so Engaged mode, extended view, palette and layer visibility all come along;
    /// the ImGui overlays on top of it (markers, boxes, OSD) do not.
    /// </summary>
    private void BeginSaveScreenshot()
    {
        if (_exportActive || _pickActive || _playback == null || CurEpisode == null || _gameView.W <= 0) return;
        _pendingPixels = _gameView.Snapshot();
        _pendingW = _gameView.W; _pendingH = _gameView.H;
        string name = LevelFileStem() + $"_t{_playback.CurrentTick}" +
            (_engaged ? "_engaged" : "") + (_simExtendedView ? "_ext" : "") + ".png";
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
    // Stop / Loop / MarkA / MarkB were added for the music player's transport; the first six
    // are the playback bar's and are unchanged. StepBack / StepFwd and the three layout marks
    // came with the timeline redesign -- the single-tick steps and the fit/pin toggles were
    // text ("-1", "Fit", "Pin right"), which is what kept crowding the row out on a narrow
    // canvas; the layout marks each show a panel with the shape the toggle gives it.
    private enum Glyph
    {
        JumpStart, Rewind, Play, Pause, FastFwd, JumpEnd, Stop, Loop, MarkA, MarkB,
        StepBack, StepFwd, Fit, FitUi, PinRight, Revert,
    }

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

    /// <summary>
    /// Paint one transport mark, centred on <paramref name="c"/> with half-height
    /// <paramref name="r"/>. Shared by the playback bar's <see cref="TransportBtn"/> and the
    /// music player's accent-coloured keys, so both speak the same shapes.
    /// <paramref name="tint"/> colours the one part of a mark that is not plain ink -- the
    /// pennant on a loop flag.
    /// </summary>
    private static void PaintGlyph(ImDrawListPtr dl, Vector2 c, float r, float tk, uint col,
        uint tint, Glyph g)
    {
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

            case Glyph.Stop:
                dl.AddRectFilled(new Vector2(c.X - r * 0.86f, c.Y - r * 0.86f),
                    new Vector2(c.X + r * 0.86f, c.Y + r * 0.86f), col, 1.5f);
                break;

            case Glyph.Loop:
            {
                // An open ring with an arrowhead on the break: a closed circle would read as
                // a record button.
                float rr = r * 0.95f;
                dl.PathArcTo(c, rr, 0.62f * MathF.PI, 2.28f * MathF.PI, 24);
                dl.PathStroke(col, ImDrawFlags.None, MathF.Max(1.5f, tk));
                var tip = new Vector2(c.X + rr * MathF.Cos(0.62f * MathF.PI),
                    c.Y + rr * MathF.Sin(0.62f * MathF.PI));
                float a = r * 0.62f;
                dl.AddTriangleFilled(tip + new Vector2(-a * 0.9f, -a * 0.1f),
                    tip + new Vector2(a * 0.45f, -a * 0.8f), tip + new Vector2(a * 0.2f, a * 0.7f), col);
                break;
            }

            // A play mark against a bar: one frame that way, as against Rewind's two triangles
            // for continuous motion. The bar is on the side travelled towards, so the pair
            // mirror each other around the hero key between them.
            case Glyph.StepBack:
                TriLeft(dl, new Vector2(c.X + r * 0.30f, c.Y), r * 0.92f, col);
                VBar(dl, c.X - r * 0.86f, c.Y, r * 0.92f, tk * 0.85f, col);
                break;
            case Glyph.StepFwd:
                TriRight(dl, new Vector2(c.X - r * 0.30f, c.Y), r * 0.92f, col);
                VBar(dl, c.X + r * 0.86f, c.Y, r * 0.92f, tk * 0.85f, col);
                break;

            // The three layout marks are the same panel outline with different contents, so
            // they read as a set: the frame filled edge to edge, the frame sharing the space
            // with a floating card, and the card taken out to the edge as a column.
            case Glyph.Fit:
            {
                var a = new Vector2(c.X - r * 1.15f, c.Y - r * 0.86f);
                var b = new Vector2(c.X + r * 1.15f, c.Y + r * 0.86f);
                dl.AddRect(a, b, col, 1.5f, 0, 1.3f);
                dl.AddRectFilled(a + new Vector2(2f, 2f), b - new Vector2(2f, 2f), col, 1f);
                break;
            }
            case Glyph.FitUi:
            {
                var a = new Vector2(c.X - r * 1.15f, c.Y - r * 0.86f);
                var b = new Vector2(c.X + r * 1.15f, c.Y + r * 0.86f);
                dl.AddRect(a, b, col, 1.5f, 0, 1.3f);
                float split = c.X + r * 0.18f;
                dl.AddRectFilled(a + new Vector2(2f, 2f), new Vector2(split - 1.5f, b.Y - 2f),
                    Alpha(col, 120), 1f);
                dl.AddRectFilled(new Vector2(split, a.Y + 1.5f), b - new Vector2(1.5f, 1.5f), col, 1f);
                break;
            }
            case Glyph.PinRight:
            {
                var a = new Vector2(c.X - r * 1.15f, c.Y - r * 0.86f);
                var b = new Vector2(c.X + r * 1.15f, c.Y + r * 0.86f);
                dl.AddRect(a, b, col, 1.5f, 0, 1.3f);
                dl.AddRectFilled(new Vector2(c.X + r * 0.30f, a.Y), b, col, 1.5f);
                break;
            }

            // Undo: the loop ring run the other way, so "put it back" is visibly the reverse
            // of the repeat mark rather than a new idea.
            case Glyph.Revert:
            {
                float rr = r * 0.95f;
                dl.PathArcTo(c, rr, -1.28f * MathF.PI, 0.38f * MathF.PI, 24);
                dl.PathStroke(col, ImDrawFlags.None, MathF.Max(1.5f, tk));
                var tip = new Vector2(c.X + rr * MathF.Cos(0.38f * MathF.PI),
                    c.Y + rr * MathF.Sin(0.38f * MathF.PI));
                float a2 = r * 0.62f;
                dl.AddTriangleFilled(tip + new Vector2(a2 * 0.9f, -a2 * 0.1f),
                    tip + new Vector2(-a2 * 0.45f, -a2 * 0.8f),
                    tip + new Vector2(-a2 * 0.2f, a2 * 0.7f), col);
                break;
            }

            case Glyph.MarkA:
            case Glyph.MarkB:
            {
                // A loop-point flag: an upright with a pennant leaning the way the point cuts.
                bool isA = g == Glyph.MarkA;
                float x = isA ? c.X - r * 0.45f : c.X + r * 0.45f;
                VBar(dl, x, c.Y, r, tk, col);
                float d = isA ? r * 1.05f : -r * 1.05f;
                dl.AddTriangleFilled(new Vector2(x, c.Y - r), new Vector2(x + d, c.Y - r * 0.34f),
                    new Vector2(x, c.Y + r * 0.22f), tint);
                break;
            }
        }
    }

    /// <summary>
    /// A transport button with a vector-drawn icon; returns true on click. Sitting in the
    /// well the transport row draws behind them, the ordinary keys are nearly transparent
    /// and earn their fill by being hovered -- so the eye goes to the two that are coloured
    /// on purpose: the hero play key, and whichever mode is currently running.
    /// </summary>
    private bool TransportBtn(string id, Glyph g, string tip, Vector2 size,
        bool primary = false, bool active = false, float gap = 4f)
    {
        uint face = primary ? Gfx.Rgba(52, 100, 180) : active ? Gfx.Rgba(150, 92, 52)
            : Gfx.Rgba(44, 47, 58, 190);
        ImGui.PushStyleColor(ImGuiCol.Button, face);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Shade(face, 1.32f, 245));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Shade(face, 1.55f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);

        bool hit = ImGui.Button(id, size);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);

        var mn = ImGui.GetItemRectMin();
        var mx = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();

        // A hairline lip along the top edge, and -- on the two keys that mean something is
        // running -- a lit bar along the bottom, which is what carries "this mode is on"
        // once the faces themselves are this quiet.
        dl.AddLine(new Vector2(mn.X + 3f, mn.Y + 0.5f), new Vector2(mx.X - 3f, mn.Y + 0.5f),
            Gfx.Rgba(255, 255, 255, (byte)(primary || active ? 55 : 26)));
        if (primary || active)
            dl.AddRectFilled(new Vector2(mn.X + 3f, mx.Y - 2f), new Vector2(mx.X - 3f, mx.Y - 0.5f),
                Shade(face, 2.1f, 235), 1f);

        var c = new Vector2((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f);
        float r = MathF.Max(3f, MathF.Round((mx.Y - mn.Y) * 0.22f));
        float tk = MathF.Max(1.5f, r * 0.28f);
        uint col = primary || active ? Gfx.Rgba(255, 255, 255) : Gfx.Rgba(214, 218, 232);
        PaintGlyph(dl, c, r, tk, col, col, g);

        ImGui.SameLine(0, gap);
        return hit;
    }

    /// <summary>
    /// <see cref="Chip"/>'s toggle, wearing a glyph instead of a label. The transport row's
    /// layout toggles are the same kind of thing the HUD's chips are -- a flag that lights up
    /// in its section's accent -- but that row has to survive a canvas barely wider than the
    /// controls themselves, and three words were the first thing it had to give up.
    /// </summary>
    private bool IconChip(string id, Glyph g, bool on, uint accent, float w, string tip)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, on ? Shade(accent, 0.42f, 235) : Gfx.Rgba(40, 42, 52, 220));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, on ? Shade(accent, 0.58f, 245) : Gfx.Rgba(58, 62, 76, 235));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, on ? Shade(accent, 0.74f) : Gfx.Rgba(74, 80, 96));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        bool hit = ImGui.Button(id, new Vector2(w, ImGui.GetFrameHeight()));
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);

        var mn = ImGui.GetItemRectMin();
        var mx = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        if (on) dl.AddRectFilled(mn, new Vector2(mn.X + 2.5f, mx.Y), accent, 2f);

        var c = new Vector2((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f);
        // Larger than the transport keys' marks: these are panel diagrams rather than
        // silhouettes, and the difference between them is a few pixels of where the fill is.
        float r = MathF.Max(4f, MathF.Round((mx.Y - mn.Y) * 0.30f));
        uint col = on ? Gfx.Rgba(246, 249, 255) : Gfx.Rgba(158, 163, 178);
        PaintGlyph(dl, c, r, MathF.Max(1.5f, r * 0.28f), col, col, g);
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
    // Appended, never reordered: the persisted fold state is a bitmask indexed by this.
    private enum PbSec { Sim, Build, Player, Enemies, Display, Routes, Status, Audio }

    // The HUD's content width -- everything in it lays out against this, so the chip grid comes
    // out even at any size and fills whatever the panel actually gives it. Measured, never
    // assumed: each form owns its own outer size (the window's edge when floating, _hudColW
    // when pinned) and this is read back off it, which is the only way a chip row and the panel
    // holding it cannot drift apart by a padding or a scrollbar.
    private float _hudW = HudWDefault;
    private const float HudWDefault = 258f, HudWMin = 210f, HudWMax = 620f;
    // Floating, the window is that much wider than its content: PushHudMetrics' padding on
    // both sides. What the width constraints and the first-run size are expressed in.
    private const float HudChromeW = 22f;
    /// <summary>Height the floating HUD's body needed last frame; the next frame's constraint.</summary>
    private float _hudAutoH;
    // Pinned, the HUD is a column, and the column is what its splitter drags -- the content
    // width follows from it, not the other way round. The default is the old fixed column, so
    // the panel is the width it always was; what changed is that the chips now reach its edge.
    private float _hudColW = HudWDefault + 40f;
    private const float HudColWMin = HudWMin + HudChromeW;
    private const float HudColWMax = HudWMax + HudChromeW + 16f;   // + room for the scrollbar
    // What the canvas must keep for the column to be worth giving up the width: the transport
    // row's own floor, every control on it at its shortest (measured: ~442px at the default
    // font). Under that the HUD goes back to floating rather than leave a row that clips.
    private const float MinCanvasW = 460f;

    // The controls column on the left, likewise draggable. Its floor is the widest thing in it
    // that cannot reflow -- the layer rows' arrows and alpha field beside a readable name.
    private float _controlsW = ControlsWDefault;
    private const float ControlsWDefault = 340f, ControlsWMin = 260f, ControlsWMax = 720f;
    private const float SplitW = 6f;   // the drag strip between two columns (VSplitter's width)
    private static readonly uint HudBg = Gfx.Rgba(15, 17, 23, 236);
    private static readonly uint AcSim     = Gfx.Rgba(255, 190,  90);
    // The launch slab's own, deliberately not AcSim: that one is the *subject* accent shared by
    // the Analysis browser, the turret cards and the HUD's SIMULATION section, and the slab is
    // not one more reading about the simulation, it is the switch that starts it. Blue because
    // the transport's own primary key already is (TransportBtn's `primary` face), so the button
    // that engages playback and the button that runs it are finally the same colour.
    private static readonly uint AcLaunch  = Gfx.Rgba(105, 170, 255);
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
    private bool Chip(string label, bool on, uint accent, float w, string tip, bool rebuilds = false,
        bool playing = false)
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
        // A live dot: the browser is shut but its player is still going, so "something is
        // playing and here is where to get back to it" is answerable without opening it.
        if (playing)
        {
            var c = new Vector2(mx.X - 6.5f, mn.Y + 6.5f);
            dl.AddCircle(c, 4.5f, Alpha(AcGo, 70));
            dl.AddCircleFilled(c, 3f, Shade(AcGo, 1.05f));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tip + (rebuilds ? "\n\n(dot: rebuilds the timeline)" : "")
                + (playing ? "\n\n(dot: a song is still playing -- click to open)" : ""));
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
            // Cut to whatever the title leaves. A badge is live text -- a song name, a device,
            // a coordinate -- and right-aligned text that outgrows its room grows leftwards:
            // AUDIO's badge carries the level's song title, and "opl . gyges, will you please
            // help me?" ran straight through the word AUDIO. The header's own arrow is a square
            // of the font size, and the title starts one frame padding after it.
            float titleEnd = mn.X + ImGui.GetFontSize() + ImGui.GetStyle().FramePadding.X * 2f
                + ImGui.CalcTextSize(title).X;
            float lh = ImGui.GetTextLineHeight();
            ClipTextRight(dl, mx.X - 6f, mn.Y + (mx.Y - mn.Y - lh) * 0.5f,
                mx.X - 6f - titleEnd - 8f, Shade(accent, 0.95f, 170), badge);
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
        // Anchored by its right edge, so on a window too narrow to hold it the HUD's own left
        // edge is pushed off the viewport -- and ImGui's own clamp allows that, it only keeps a
        // sliver of a window on screen. Put it back whenever it has ended up outside.
        var hudEdge = ImGui.GetMainViewport().WorkPos;
        if (_hudSize.X > 0f && (_hudPos.X < hudEdge.X || _hudPos.Y < hudEdge.Y))
            ImGui.SetNextWindowPos(Vector2.Max(_hudPos, hudEdge));
        // Width is the user's -- drag an edge like any other window -- while the height still
        // follows the content, which is what makes folding a section actually shrink the panel.
        // AlwaysAutoResize gives the second for free but turns manual resizing off altogether,
        // so the height is pinned through the size constraint instead (min and max the same
        // value), measured off the body's own cursor at the end of the previous frame. Capped at
        // the view it floats over: unfold everything on a short window and the HUD scrolls
        // rather than running off the bottom of the screen.
        float capH = Math.Max(220f, viewSize.Y - 24f);
        float wantH = Math.Clamp(_hudAutoH > 0f ? _hudAutoH : 520f, 120f, capH);
        // Room for a scrollbar on top of the padding, since with every section unfolded there
        // will be one -- without it the default width lands 14px short of the pinned column's
        // and the header pills clip.
        float chrome = HudChromeW + ImGui.GetStyle().ScrollbarSize;
        ImGui.SetNextWindowSize(new Vector2(_hudW + chrome, wantH), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(HudWMin + HudChromeW, wantH), new Vector2(HudWMax + chrome, wantH));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, HudBg);
        ImGui.PushStyleColor(ImGuiCol.Border, Gfx.Rgba(92, 104, 140, 210));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Gfx.Rgba(28, 36, 56, 235));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Gfx.Rgba(44, 60, 92, 245));
        // Shape comes from the house style (ApplyGlobalStyle) so the HUD matches every other
        // movable window; only its own metrics are pushed here.
        PushHudMetrics();

        bool hudOpen = ImGui.Begin("Playback controls##pbhud",
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNavInputs | ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoFocusOnAppearing);
        // Its rect, collapsed or not, is what "UI fit" keeps the game view clear of.
        _hudPos = ImGui.GetWindowPos();
        _hudSize = ImGui.GetWindowSize();
        if (hudOpen)
        {
            DrawHudBody(pb);
            // What the next frame's height constraint is. Cursor-relative, so it is the height
            // the body actually needs whether or not this frame had to scroll to show it.
            _hudAutoH = ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y;
        }
        ImGui.End();
        PopHudMetrics();
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
        ImGui.BeginChild("pbcolumn", new Vector2(_hudColW, 0), ImGuiChildFlags.Borders);
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

        // Everything below lays out against the panel's content width, so take it from the
        // panel -- the window's, floating, the column's when pinned. Safe in both because
        // neither outer size is derived from this: measuring is the whole point, since the
        // column used to hand its chips a fixed width and keep the padding difference as a
        // dead strip down its right edge.
        _hudW = Math.Clamp(ImGui.GetContentRegionAvail().X, HudWMin, HudWMax);
        // Pin the width so folding sections never reflows the chip grid.
        ImGui.Dummy(new Vector2(_hudW, 0));
        DrawHudHeader(pb);

        if (Section(PbSec.Sim, "SIMULATION", AcSim,
                $"{DifficultyNames[_simDifficulty]} · x{_simScrollMult:0.##}"))
            DrawSimSection();

        if (Section(PbSec.Build, "ENGINE BUILD", AcBuild,
                _engaged ? $"engaged {GameSim.EngagedViewW}" : $"vanilla {GameSim.ViewW}"))
            DrawBuildSection(sim);

        if (Section(PbSec.Player, "PLAYER", AcPlayer, $"{_simPlayerX}, {_simPlayerY}"))
            DrawPlayerSection();

        if (Section(PbSec.Enemies, "ENEMIES", AcEnemy,
                !_clickKill ? "click: off" : _clickKillInstant ? "instant kill" : $"{_clickKillDamage} dmg"))
            DrawEnemySection();

        if (Section(PbSec.Display, "DISPLAY", AcDisplay, $"{FxCount()}/6 on"))
            DrawDisplaySection();

        if (Section(PbSec.Audio, "AUDIO", AcMusic, AudioBadge()))
            DrawAudioSection();

        if (Section(PbSec.Routes, "ROUTES & GATES", AcRoutes,
                pb.Branched ? $"{pb.LoopRegions.Count} +{pb.BranchRegions.Count} live"
                            : $"{pb.LoopRegions.Count}"))
            DrawRoutesAndGates(pb);

        if (Section(PbSec.Status, "STATUS", AcStatus, $"{sim.EnemyOnScreen} on screen"))
            DrawStatusSection(pb, sim);
    }

    /// <summary>Always-visible header: what is being simulated, what the transport is doing,
    /// where the playhead is -- then the one-click presets.</summary>
    private void DrawHudHeader(SimPlayback pb)
    {
        // Three pills on one row, and at the narrow end of the HUD's width they do not fit --
        // a pill lays out at the size of its own text, so the third simply ran off the panel.
        // The build word gives up its number first, then the row breaks after the second pill,
        // which is the last thing that still leaves all three readable.
        string build = _engaged ? $"ENGAGED {GameSim.EngagedViewW}" : $"VANILLA {GameSim.ViewW}";
        string state = _playing
            ? _playDirection > 0 ? $">> x{_playSpeed:0.##}" : $"<< x{_playSpeed:0.##}"
            : "paused";
        // Against DisplayEnd, not Duration: a live branch can outrun the predicted length, and
        // this read "9:20.0 / 8:09.1" -- a clock past its own total -- for as long as it did.
        string clock =
            $"{SimPlayback.FormatTime(pb.CurrentTick)} / {SimPlayback.FormatTime(pb.DisplayEnd)}";
        float PillW(string s) => ImGui.CalcTextSize(s).X + 12f;

        if (PillW(build) + PillW(state) + PillW(clock) + 8f > _hudW)
            build = _engaged ? "ENGAGED" : "VANILLA";
        bool oneRow = PillW(build) + PillW(state) + PillW(clock) + 8f <= _hudW;

        Pill(build, _engaged ? AcBuild : AcIdle);
        ImGui.SameLine(0, 4);
        Pill(state, _playing ? AcGo : AcIdle);
        if (oneRow) ImGui.SameLine(0, 4);
        Pill(clock, pb.Branched ? TlLive : AcStatus);

        // Presets. Each is lit while the current flags already match it, so the row doubles
        // as a readout of which build you are watching.
        float w = (_hudW - 10f) / 3f;
        bool vanillaNow = !_engaged && !_tallStarfield;
        bool engagedNow = _engaged && !_expandedParallax && _mirrorLayers && _tallStarfield;
        bool cleanNow = FxLayerCount() == 0;

        if (Chip("Vanilla", vanillaNow, AcSim, w,
                "The DOS game exactly: 264px playfield and the original starfield.\n" +
                "With these two off, playback is byte-for-byte the original.", true))
        { _tallStarfield = false; SetEngaged(false); }
        ImGui.SameLine(0, 5);
        if (Chip("Engaged", engagedNow, AcBuild, w,
                "The Engaged build: 299px playfield, mirrored layers and the\n" +
                "rewritten full-height starfield. Extra parallax is left off --\n" +
                "it re-spaces every layer, so it is opt-in from ENGINE BUILD.", true))
        {
            _expandedParallax = false;
            _mirrorLayers = _tallStarfield = true;
            SetEngaged(true);
        }
        ImGui.SameLine(0, 5);
        if (Chip("Clean", cleanNow, AcDisplay, w,
                "Strip the presentation down to terrain and sprites: boss bars,\n" +
                "smoothies, colour fades, spotlight and screen flip all off, for\n" +
                "reading the level itself. Press it again to put them all back.\n" +
                "Draw-only -- the simulation is untouched."))
            SetAllFx(cleanNow);

        // The one action that belongs to the frame rather than to the run, so it rides with the
        // presets in the always-visible header instead of inside a section that folds shut. The
        // controls column no longer carries it: in playback that column saves the map, and the
        // map is not what is on screen.
        ImGui.BeginDisabled(_exportActive || _pickActive || _gameView.W <= 0);
        if (Chip(_exportActive ? "saving..." : "Take screenshot...", false, AcStatus, _hudW,
                "Save the current frame exactly as shown: palette, layer visibility,\n" +
                "Engaged mode and extended view all included, at 1:1 pixels.\n" +
                "The markers, boxes and OSD drawn over it are not included."))
            BeginSaveScreenshot();
        ImGui.EndDisabled();
    }

    private void DrawSimSection()
    {
        ImGui.PushItemWidth(_hudW - 84f);
        int dif = _simDifficulty;
        if (ImGui.Combo("difficulty", &dif, DifficultyNames, DifficultyNames.Length))
        { _simDifficulty = dif; BuildPlayback(); }

        // Every slider below rebuilds the timeline when it is let go, which is what makes the
        // right-click reset worth having here above all: trying a value costs a rebuild, and
        // getting back from one used to mean remembering the number and dialling it in. The
        // reset rebuilds too -- IsItemDeactivatedAfterEdit never fires for a click that was
        // not a drag, so the rebuild hangs off the reset's own return instead.
        float mult = _simScrollMult;
        if (ImGui.SliderFloat("scroll speed", &mult, 0.25f, 3f, "x%.2f"))
            _simScrollMult = mult;
        bool reset = SliderReset(ref mult, 1f,
            "What-if terrain scroll multiplier. The level's own variable\nscroll-rate events still apply on top; the event clock follows\nthe terrain, so spawn pacing changes with it.", "x1");
        if (reset) _simScrollMult = mult;
        if (reset || ImGui.IsItemDeactivatedAfterEdit()) BuildPlayback();

        int cap = _simMaxMinutes;
        if (ImGui.SliderInt("cap (min)", &cap, 1, 30))
            _simMaxMinutes = cap;
        reset = SliderReset(ref cap, DefaultMaxMinutes,
            "How far the precompute is allowed to run before it gives up\nand calls the timeline capped.", "10 min");
        if (reset) _simMaxMinutes = cap;
        if (reset || ImGui.IsItemDeactivatedAfterEdit()) BuildPlayback();

        int loops = _simLoopCycles;
        if (ImGui.SliderInt("loop cycles", &loops, 1, 8))
            _simLoopCycles = loops;
        reset = SliderReset(ref loops, DefaultLoopCycles,
            "How many times each boss gate / route loop repeats in the\npreview before it continues as if the gate was cleared. The\nloop still plays once on the way in. Higher = watch more\nrepeats, but a longer build and timeline.");
        if (reset) _simLoopCycles = loops;
        if (reset || ImGui.IsItemDeactivatedAfterEdit()) BuildPlayback();

        int hold = _simHoldSeconds;
        if (ImGui.SliderInt("enemy hold", &hold, 1, 120, "%d s"))
            _simHoldSeconds = hold;
        reset = SliderReset(ref hold, DefaultHoldSeconds,
            "The other kind of gate: the map stops with live enemies on it\nand no script left to release them -- it waits for the player to\nkill them. Nothing repeats, so this is simply how long that\nstandoff is watched before the preview destroys them and moves\non, and how much of it the timeline keeps to inspect.", "20 s");
        if (reset) _simHoldSeconds = hold;
        if (reset || ImGui.IsItemDeactivatedAfterEdit()) BuildPlayback();
        ImGui.PopItemWidth();

        if (Chip("enemy fire", _simFire, AcSim, _hudW,
                "Simulate enemy turrets firing and launching.", true))
        { _simFire = !_simFire; BuildPlayback(); }
    }

    /// <summary>Which engine the playback is: playfield width and the Engaged build's
    /// three enhancements. Everything here except mirroring changes the simulation.</summary>
    private void DrawBuildSection(GameSim sim)
    {
        float w = (_hudW - 5f) / 2f;

        if (Chip("engaged", _engaged, AcBuild, w,
                "Play as the Engaged build does: a 299px playfield instead of\n" +
                "the vanilla 264, with wider view and player range, parallax,\n" +
                "spotlight, terrain filters and starfield across the full 356px\n" +
                "surface, and enemies / shots that persist across the widened\n" +
                "edges. The build's bigger starfield draws from the same RNG as\n" +
                "the level, so spawns differ from vanilla -- as they do in the\n" +
                "real build.", true))
            SetEngaged(!_engaged);
        ImGui.SameLine(0, 5);
        // Not an Engaged sub-option: vanilla's starfield stops seven rows above the
        // playfield bottom at either width, so the rewrite is worth having in both modes.
        if (Chip("tall starfield", _tallStarfield, AcBuild, w,
                "Engaged build's rewritten starfield: 330 stars that only\n" +
                "ever move down, filling the playfield to its bottom edge and out\n" +
                "to the full screen width, recycling above the top instead of\n" +
                "popping in. Off = vanilla's 100 stars on a 16-bit position, which\n" +
                "stop 7 rows short of the bottom and jump sideways as they wrap.\n" +
                "Works in vanilla mode too -- the only enhancement that does, so\n" +
                "leaving it on is the one way vanilla playback is not byte-for-byte.\n" +
                "The two seed different counts from the level RNG, so spawns shift\n" +
                "-- as they do between the real builds.", true))
        { _tallStarfield = !_tallStarfield; BuildPlayback(); }

        // The remaining two are Engaged-only: shown disabled rather than hidden, so the
        // build's full feature set stays visible from vanilla mode.
        ImGui.BeginDisabled(!_engaged);
        if (Chip("extra parallax", _engaged && _expandedParallax, AcBuild, w,
                "Wider parallax (Engaged build's Extra Parallax): the terrain\n" +
                "layer pans edge-to-edge across its full 336px map over the\n" +
                "player's travel -- nothing left hidden off either side -- and the\n" +
                "mid/deep layers sweep proportionally further (uncovering their\n" +
                "edges at far-left). Bound ground enemies ride the same offsets\n" +
                "and slide much further too.", true))
        { _expandedParallax = !_expandedParallax; BuildPlayback(); }
        ImGui.SameLine(0, 5);
        if (Chip("mirror layers", _engaged && _mirrorLayers, AcBuild, w,
                "Engaged build's Mirrored Layers: where the parallax pans a\n" +
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
            sim.MirrorLayers = _engaged && _mirrorLayers;
            _playback!.RedrawCurrent();
        }
        ImGui.EndDisabled();
        if (!_engaged) ImGui.TextDisabled("(those two are Engaged-only)");
    }

    /// <summary>The phantom player: the marker toggles, and its position with the jump
    /// buttons. The position drives parallax / aim whether or not the marker is drawn,
    /// so the readout stays available in every mode.</summary>
    private void DrawPlayerSection()
    {
        float w = (_hudW - 5f) / 2f;
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
        // cX widens with the playfield (149 vanilla / 166 Engaged).
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
        float w = (_hudW - 5f) / 2f;
        if (Chip("click damages", _clickKill, AcEnemy, w,
                "Click an enemy in the view to shoot it. Killing one segment of\n" +
                "a linked enemy destroys the whole formation, and whatever it\n" +
                "drops still spawns -- the engine's own shot-collision outcome.\n" +
                "A survivor takes Tyrian's damage flash for a frame.\n" +
                "While this is on the left button is the trigger: pan with the\n" +
                "middle button, and use Fit instead of double-click.\n" +
                "The hit lands on the live frame: playing on keeps it, seeking\n" +
                "back restores the keyframe and the enemy with it."))
            _clickKill = !_clickKill;

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
            if (SliderReset(ref dmg, DefaultClickKillDamage,
                    "Armor removed per click. Enemy armor tops out at 254; 255\n" +
                    "means invulnerable, which only an instant kill gets through."))
                _clickKillDamage = dmg;
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
    private void SetEngaged(bool ws)
    {
        _engaged = ws;
        _draggingPlayer = false;
        _simPlayerX = Math.Clamp(_simPlayerX, PlayerXMin, PlayerXMax);  // keep the marker inside the new bounds
        _playZoom = 0; _playPan = Vector2.Zero;                         // refit the view to the new width
        BuildPlayback();
    }

    private void DrawDisplaySection()
    {
        float w = (_hudW - 5f) / 2f;
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
        ImGui.Dummy(new Vector2(_hudW, gh));
        dl.AddRectFilled(p, p + new Vector2(_hudW, gh), Gfx.Rgba(22, 24, 31, 235), 3f);

        // Read through SimPlayback.DensityAt, so once the run has diverged the sparkline is of
        // the branch the playhead is actually on -- past the predicted end there is no entry in
        // the precomputed array at all, and this drew a flat empty graph beside a screen full
        // of enemies.
        const int half = 160;                        // ~4.5 s of game time either side
        int t0 = pb.CurrentTick - half, t1 = pb.CurrentTick + half;
        int peak = 4;   // a floor, so a quiet stretch doesn't scale two enemies to full height
        for (int t = Math.Max(1, t0); t <= t1; t++)
            peak = Math.Max(peak, pb.DensityAt(t));

        // The top band stays clear for the caption, so a busy stretch can't swallow it.
        const float top = 15f, floorY = gh - 2f;
        for (int x = 0; x < (int)_hudW; x++)
        {
            int t = t0 + (int)((long)x * (t1 - t0) / (int)_hudW);
            int d = pb.DensityAt(t);
            if (d < 0) continue;
            float bh = d / (float)peak * (floorY - top);
            dl.AddLine(new Vector2(p.X + x, p.Y + floorY - bh), new Vector2(p.X + x, p.Y + floorY),
                t > pb.CurrentTick ? Gfx.Rgba(88, 106, 145, 150)
                    : pb.Branched && t >= pb.BranchTick ? Alpha(TlLive, 215)
                    : Gfx.Rgba(110, 180, 250, 205));
        }
        float cx = p.X + _hudW * 0.5f;
        dl.AddLine(new Vector2(cx, p.Y + top - 2f), new Vector2(cx, p.Y + gh - 1f), Gfx.Rgba(255, 235, 130, 225));
        // The caption and its numbers share one line, and on a narrowed HUD they do not both
        // fit: the numbers keep their room and the words give way, since "peak 30" is the part
        // that changes.
        string peakTag = $"now {sim.EnemyOnScreen} · peak {peak}";
        float tagW = ImGui.CalcTextSize(peakTag).X;
        ClipText(dl, p + new Vector2(5, 2), _hudW - tagW - 14f,
            Gfx.Rgba(138, 146, 166), "enemies on screen");
        dl.AddText(new Vector2(p.X + _hudW - tagW - 5f, p.Y + 2f),
            Gfx.Rgba(138, 146, 166), peakTag);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Enemies on screen over the {half * 2 / (int)GameSim.TicksPerSecond} s " +
                "around the playhead\n(centre line), from the precomputed density.");

        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled($"tick {pb.CurrentTick}/{pb.DisplayEnd}   loc {sim.CurLoc}   " +
            $"enemies {sim.EnemyOnScreen}");
        var (b1, b2, b3) = sim.BackMoves;
        ImGui.TextDisabled($"scroll {b1}/{b2}/{b3}   built in {pb.PrecomputeMs} ms");
        ImGui.TextDisabled(pb.EndedNaturally
            ? pb.LoopDetected ? $"ends naturally; {pb.LoopSummary}" : "ends naturally"
            : pb.LoopDetected ? pb.LoopSummary : "capped (no natural end)");
        if (pb.Branched)
            ImGui.TextDisabled($"live since {SimPlayback.FormatTime(pb.BranchTick)} " +
                $"({pb.Interferences} interference{(pb.Interferences == 1 ? "" : "s")}); " +
                (pb.BranchDone
                    ? $"the branch ended at {SimPlayback.FormatTime(pb.BranchEnd)}"
                    : $"recorded to {SimPlayback.FormatTime(pb.BranchEnd)} and still running"));
        ImGui.PopTextWrapPos();

        // What the run is made of, and the atlas's own status line. Both used to sit at the
        // foot of the controls column, where playback leaves them stranded under a stack of
        // map-view controls that no longer do anything -- here they are beside the numbers
        // they belong with.
        if (_level != null)
        {
            ImGui.Separator();
            DrawLevelInfoLines();
        }
        if (_status.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextWrapped(_status);
        }
    }

    /// <summary>
    /// The loop/gate inventory: every boss gate, enemy hold and route loop, with its start
    /// time and retained-cycle count (set by "loop cycles"). Each row seeks to that section on
    /// click; the section under the playhead is highlighted. The textual companion to the
    /// bracketed regions on the timeline.
    ///
    /// Once the run has diverged the two lists are shown together — the prediction's, with
    /// anything the branch has already overtaken struck out to a dimmer row, then whatever the
    /// live run found instead. A gate that was shot away simply has no live counterpart, which
    /// is the readable form of "that standoff is over".
    /// </summary>
    private void DrawRoutesAndGates(SimPlayback pb)
    {
        var rows = pb.LoopRegions.Select(r => (R: r, Live: false))
            .Concat(pb.BranchRegions.Select(r => (R: r, Live: true)))
            .ToList();
        if (rows.Count == 0)
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
        for (int i = 0; i < rows.Count; i++)
        {
            var (r, live) = rows[i];
            // Superseded: a predicted section the branch has already played through differently.
            bool stale = !live && pb.Branched && r.EndTick > pb.BranchTick;
            bool active = !stale && pb.CurrentTick >= r.StartTick && pb.CurrentTick <= r.EndTick;
            int n = r.CycleEnds.Length;
            (string kind, string amount, uint swatch) = r.Kind switch
            {
                SimPlayback.HoldLoopKind.ScriptedLoop =>
                    ("boss gate", $"x{n} kept (until destroyed)", Gfx.Rgba(255, 150, 90)),
                SimPlayback.HoldLoopKind.RouteLoop =>
                    ("route loop", $"x{n} cycles", Gfx.Rgba(120, 200, 255)),
                // A hold shorter than the slider asked for is one the run stopped inside.
                _ => ("enemy hold", SimPlayback.RegionSeconds(r) < pb.Sim.PreviewHoldSeconds
                        ? $"{SimPlayback.RegionSeconds(r)}s watched (cap reached)"
                        : $"{SimPlayback.RegionSeconds(r)}s kept (until destroyed)",
                    Gfx.Rgba(230, 120, 210)),
            };

            // colour swatch (boss = orange, route = blue, hold = magenta), then the row. A live
            // row's swatch is split with the branch green, so which run a section belongs to
            // is answerable without reading the tail of the line.
            var p = ImGui.GetCursorScreenPos();
            dl.AddRectFilled(new Vector2(p.X, p.Y + 2), new Vector2(p.X + h, p.Y + h),
                stale ? Alpha(swatch, 70) : swatch);
            if (live)
                dl.AddRectFilled(new Vector2(p.X, p.Y + 2), new Vector2(p.X + 3f, p.Y + h), TlLive);
            ImGui.Dummy(new Vector2(h, h));
            ImGui.SameLine(0, 4);

            // The row's own text is drawn rather than handed to the Selectable: a Selectable
            // declares its label's width as its item width, so "enemy hold  1:23  20s kept
            // (until destroyed)" quietly asked the HUD to be four hundred pixels wide and got a
            // horizontal scrollbar under the panel instead. Given a width, it stays inside it
            // and the line is cut with an ellipsis like every other value in this UI.
            float rowW = Math.Max(40f, _hudW - h - 4f);
            if (ImGui.Selectable($"##g{i}", active, ImGuiSelectableFlags.None, new Vector2(rowW, h)))
            { pb.SeekTo(r.StartTick); _playing = false; }
            ClipText(dl, ImGui.GetItemRectMin(), rowW - 4f,
                stale ? Gfx.Rgba(110, 116, 132) : live ? Shade(TlLive, 1.05f) : UiText,
                $"{kind,-10} {Mmss(r.StartTick),5}  {amount}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    $"{kind} at {SimPlayback.FormatTime(r.StartTick)} - {SimPlayback.FormatTime(r.EndTick)}\n" +
                    $"{amount}\n" +
                    (live ? "found by the live run, after the divergence\n"
                        : stale ? "predicted, but the live run has already played past it\n" : "") +
                    "click to jump here");
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
    /// damage. The shot lands on the press rather than on the release, because the run does not
    /// stop for a held button and every tick spent waiting moves the target -- see
    /// <see cref="ResolveClickKillTarget"/>, which is also where the aim comes from.
    /// <see cref="HandlePlayerMarker"/> runs first, so a press that grabbed the player marker
    /// never doubles as a shot; with the trigger armed the left button no longer pans the view
    /// (<see cref="DrawPlaybackCanvas"/>), so there is no drag for a press to be mistaken for.
    /// </summary>
    private void HandleClickKill(in PlayView view, Vector2 mouse, bool viewHovered)
    {
        if (!_clickKill || _playback == null || _draggingPlayer) return;
        if (!viewHovered || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;

        int slot = ResolveClickKillTarget(view, mouse);
        if (slot < 0) return;

        int damage = _clickKillInstant ? GameSim.InstantKillDamage : _clickKillDamage;
        if (!_playback.Sim.DamageEnemy(slot, damage, _clickKillExplosions)) return;

        // The run has just stopped being the one the timeline predicted. From here it records
        // itself: the bar branches at this tick and follows what actually happens, including
        // past the predicted end -- which is the whole point of shooting a boss out of a hold.
        _playback.NoteLiveChange();

        // The hit queued its own sound (the short boom, or the low one for a big enemy).
        // Drain it here: the tick below clears the queue before it runs. Sounds only --
        // the music events of whatever tick ran last are still latched.
        DrainSimAudio(music: false);

        // Nothing of this shows in the frame already on screen: the sprite goes away and the
        // explosion is spawned for the next drawn tick either way. Paused, that tick has to be
        // asked for, or the shot looks like it did nothing until playback is resumed.
        // RedrawCurrent is deliberately NOT used — it re-seeks from the nearest keyframe,
        // which would put the enemy straight back.
        //
        // Running, the tick is already coming: UpdatePlayback steps the clock every frame, so
        // stepping one here as well was a tick the timeline gained out of nowhere — the frame
        // the shot landed on jumped forward by one and the scroll skipped with it.
        if (!_playing) _playback.Advance(1, audio: true);
    }

    /// <summary>
    /// The slot a shot at <paramref name="mouse"/> hits, or -1 for empty space. Two answers, and
    /// the shot takes whichever lands on something.
    ///
    /// First a fresh <see cref="GameSim.PickEnemyAt"/> at the cursor. <see cref="UpdatePlayback"/>
    /// steps the sim and re-uploads the frame *before* the UI draws, so the picture on screen,
    /// this pick and the cursor all read the one tick: a live pick is exact whenever the cursor
    /// is over the enemy in the frame actually shown — paused, slow playback, or a cursor tracking
    /// a mover — and it is the only thing that picks a *different* enemy the cursor has moved onto.
    ///
    /// Failing that, the slot the hover overlay last boxed (<see cref="DrawPlaybackOverlay"/>
    /// latches it, because the button going down makes the view active and suppresses the overlay
    /// on the very frame the shot fires). At speed the sim steps several ticks between frames, so
    /// an enemy boxed when the button went down has already jumped out from under a still cursor —
    /// the live pick finds empty space there, and this is what catches it. It stands only while
    /// the slot still holds that same enemy (slots are recycled the instant one dies) and the
    /// cursor has not wandered far from where it was aimed.
    ///
    /// The order matters: latching first, gated by a 2px "cursor hasn't moved" test, dropped the
    /// shot whenever a click jittered a few pixels or the cursor was tracking a moving target —
    /// the tight test rejected the latch and the live fallback was then aimed at where the enemy
    /// had already left, so nothing died. Trying the live pick first and keeping the latch as the
    /// catch is what makes it fire every time.
    /// </summary>
    private int ResolveClickKillTarget(in PlayView view, Vector2 mouse)
    {
        // Through PlayView, so a shot on an upside-down screen hits the enemy the cursor is
        // over rather than its reflection across the playfield's middle.
        var b = view.ToBuffer(mouse);
        int fresh = _playback!.Sim.PickEnemyAt(
            (int)MathF.Floor(b.X), (int)MathF.Floor(b.Y), _objCatMask);
        if (fresh >= 0) return fresh;

        // Nothing under the cursor now -- but a mover can jump clear between frames at speed, so
        // fall back to the slot the overlay last boxed, as long as it still holds that enemy and
        // the cursor is still near where the box was.
        if (_hoverPick is { } h && _playback.Sim.SlotHolds(h.Slot, h.EnemyId, h.LinkNum) &&
            Vector2.DistanceSquared(mouse, _hoverPickAt) <= HoverLatchRadiusSq)
            return h.Slot;

        return -1;
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

        float controlsH = ImGui.GetFrameHeightWithSpacing() + TimelineH + 14f;
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
        // From whichever run owns this tick. A gate shot out of the way stops being reported
        // the moment the branch passes where it used to be, instead of the readout insisting
        // on a standoff the player has just ended.
        var activeGate = pb.RegionAt(pb.CurrentTick);
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
        string osd = $"{SimPlayback.FormatTime(pb.CurrentTick)} / {SimPlayback.FormatTime(pb.DisplayEnd)}" +
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

        // The transport row and the timeline under it: App.Timeline.cs.
        DrawTransportRow(pb);
        DrawTimeline(pb);
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

        // Latched for the trigger, which cannot ask this question for itself: the button going
        // down makes the view active, so the frame a shot is fired on is a frame this overlay
        // is not drawn on at all. What the box was last drawn around is the aim — see
        // ResolveClickKillTarget. Written on every hovered frame, empty space included, so it
        // can never be a target the cursor has since left behind.
        if (hovered) { _hoverPick = pick; _hoverPickAt = mouse; }

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
        ShutdownAudio();
    }
}
