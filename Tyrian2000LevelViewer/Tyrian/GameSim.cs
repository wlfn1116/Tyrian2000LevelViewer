using System.Runtime.CompilerServices;

namespace T2LV.Tyrian;

[InlineArray(20)] public struct UShort20 { private ushort _e0; }
[InlineArray(3)] public struct Byte3 { private byte _e0; }
[InlineArray(10)] public struct Byte10 { private byte _e0; }
[InlineArray(10)] public struct Bool10 { private bool _e0; }
[InlineArray(9)] public struct Byte9 { private byte _e0; }

/// <summary>
/// A faithful per-tick simulation of a level playing itself: the engine's event system,
/// enemy creation/movement/animation/launching, enemy fire, background scrolling and the
/// per-frame draw order, ported from tyrian2.c/backgrnd.c. There is no player ship; a
/// phantom player position feeds the aim/chase logic. Deterministic (own Mersenne
/// Twister) and snapshottable for timeline scrubbing.
/// </summary>
public sealed class GameSim
{
    public const int ScreenW = 320, ScreenH = 200, Pitch = 320;   // vanilla engine surface
    public const int ViewX = 24, ViewW = 264, ViewH = 184;        // playfield crop (JE_starShowVGA)
    // Extended render buffer: the vanilla surface plus margins so the view can zoom out
    // and show terrain/enemies before they reach the screen. Vanilla (x, y) lives at
    // buffer (x + OX, y + OY).
    public const int OX = 72, OY = 128;
    public const int BufW = 320 + OX + 72, BufH = 200 + OY + 32;  // 464 x 360
    public const double TicksPerSecond = 35.0;
    /// <summary>How many repetitions of a player-gated (boss) loop the preview keeps
    /// before it continues as though the gate was cleared. The loop body still plays
    /// once on the way in, so the retained region spans this many marked cycles.</summary>
    public const int PreviewLoopCycles = 2;

    // --- static level data (not part of snapshots) ---
    private readonly Level _lv;
    private readonly EnemyData _ed;
    private readonly WeaponData _wd;
    private readonly GameData _gd;
    private readonly byte[]?[][] _map = new byte[]?[3][];   // [layer][row*cols] -> 672b tile
    private readonly int _maxEvent;
    private readonly CompShapes? _explosionSheet;

    // --- sim parameters (fixed per run; changing them requires a rebuild) ---
    public int Difficulty = 2;          // 0 wimp .. 10; 2 = normal
    public float ScrollMult = 1f;       // terrain speed what-if multiplier
    public bool FireEnabled = true;     // simulate enemy turrets
    public bool PreviewEnemyGates = true; // keep PreviewLoopCycles boss loops, then continue as if defeated
    public int PlayerX = 100, PlayerY = 180;   // phantom player (aim/chase target)
    public uint RngSeed = 5489;

    // --- view options (draw-only; safe to change without a rebuild + redraw) ---
    public bool ExtendedDraw;           // render beyond the vanilla screen bounds
    public bool ShowScreenFilter = true;   // event-44 hue/brightness filter
    public bool ShowTerrainSmoothies = true; // lava/water/ice/blur feedback filters
    public bool ShowSpotlight = true;       // JE_starShowVGA light cone
    public bool ShowScreenFlip = true;      // JE_starShowVGA vertical flip

    // Layer-stack visibility, mirrored from the viewer's layer list. These gate blitting
    // only — scroll cursors, star positions and enemy logic all still run, so a hidden
    // layer cannot desync the simulation and scrubbing stays exact.
    public bool ShowBg1 = true, ShowBg2 = true, ShowBg3 = true, ShowStarfield = true;
    /// <summary>Bit per <see cref="ObjCategory"/>; cleared bits hide that category.</summary>
    public int ObjectCategoryMask = ~0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CategoryVisible(byte cat) => (ObjectCategoryMask & (1 << cat)) != 0;

    // --- mutable state ---
    private readonly MtRand _rng = new();
    private EventRec[] _ev = Array.Empty<EventRec>();   // sim-local copy; events self-mutate
    private Enemy[] _enemy = new Enemy[100];
    private byte[] _avail = new byte[100];              // 0 active, 1 free, 2 scoreitem
    private Shot[] _shot = new Shot[60];
    private byte[] _shotAvail = new byte[60];           // 1 free
    private Expl[] _expl = new Expl[200];
    private RepExpl[] _repExpl = new RepExpl[20];
    private Star[] _stars = new Star[100];
    private S _s;                                       // all scalar state (value copy = snapshot)
    private int _dat0Egr0, _dat0Armor;                  // events 49-52 mutate enemyDat[0]
    private ushort[] _gateLoopVisits = Array.Empty<ushort>();
    // [eventIndex * PreviewLoopCycles + n] = tick the n-th cycle of that gate ended on.
    private int[] _gateLoopTicks = Array.Empty<int>();
    private int _releaseGateEvent = -1;

    // game_screen: persistent across frames (smoothie filters feed back into it),
    // _screenB: VGAScreen2, the draw target while smoothies are active this frame.
    public byte[] Screen = new byte[BufW * BufH];
    private byte[] _screenB = new byte[BufW * BufH];
    private byte[] _tgt;                                // current draw target (VGAScreen)
    /// <summary>The frame after JE_starShowVGA presentation (flip / spotlight).</summary>
    public byte[] PresentScreen = new byte[BufW * BufH];

    public bool Finished => _s.finished;
    public int CurLoc => _s.curLoc;
    public int EnemyOnScreen => _s.enemyOnScreen;
    public (int b1, int b2, int b3) BackMoves => (_s.backMove, _s.backMove2, _s.backMove3);
    public readonly record struct EventExec(int Tick, byte Type, int Index, bool Backward);
    public enum PreviewKind { EnemyLoop, EnemyHold, RouteLoop }
    public readonly record struct GatePreview(
        int StartTick, int EndTick, int[] CycleEnds, PreviewKind Kind, int EventIndex);
    /// <summary>Set during a pre-run to record executed events.</summary>
    public List<EventExec>? EventLog;
    /// <summary>Set during a pre-run to record finite previews of player-gated sections.</summary>
    public List<GatePreview>? GatePreviewLog;
    public int LogTick;

    // =====================================================================
    //  Live inspection: what the current frame actually contains, in buffer
    //  coordinates, so the viewer can put markers and hover readouts over it.
    // =====================================================================

    /// <summary>One live enemy/pickup as of the frame just drawn. Positions are buffer
    /// coordinates (vanilla + OX/OY); the sprite cell is 12x14 per blit, 24x28 for 2x2.</summary>
    public readonly record struct EnemyView(
        int Slot, int Band, ObjCategory Category, int EnemyId,
        int CenterX, int CenterY, int HalfW, int HalfH,
        int ScreenX, int ScreenY, int SpriteIndex, int SheetId, int Size,
        int ArmorLeft, int Value, int LinkNum, bool ScoreItem, bool OnScreen,
        int Xc, int Yc, int XAccel, int YAccel, int AnimFrame, int AnimMax,
        int LaunchType, int LaunchFreq, int Tur0, int Tur1, int Tur2, int Iced);

    private static int BandForSlot(int slot) =>
        slot < 25 ? 0 : slot < 50 ? 25 : slot < 75 ? 50 : 75;

    private static int BandDrawRank(int band) => band switch { 25 => 0, 75 => 1, 0 => 2, _ => 3 };

    /// <summary>
    /// Every live enemy in the current frame, front-most band last so the viewer can
    /// hit-test in reverse and pick what visually sits on top. <paramref name="categoryMask"/>
    /// is the layer list's own visibility (bit per <see cref="ObjCategory"/>) — deliberately
    /// separate from <see cref="ObjectCategoryMask"/>, which only gates sprite blitting, so
    /// markers can stand in for sprites the simulation was told not to draw.
    /// </summary>
    public void CollectEnemies(List<EnemyView> into, int categoryMask = ~0)
    {
        into.Clear();
        for (int i = 0; i < _enemy.Length; i++)
        {
            if (_avail[i] == 1) continue;
            ref var e = ref _enemy[i];
            if ((categoryMask & (1 << e.objCat)) == 0) continue;
            int cyc = Math.Clamp(e.enemycycle - 1, 0, 19);
            if (e.egr[cyc] == 999) continue;           // the engine frees these on sight
            int sx = e.ex + e.mapoffset;
            bool onScreen = sx > -29 && sx < 300;
            if (!onScreen && !ExtendedDraw) continue;   // not drawn this frame

            bool big = e.size == 1;
            into.Add(new EnemyView(
                Slot: i, Band: BandForSlot(i), Category: (ObjCategory)e.objCat,
                EnemyId: e.enemytype,
                CenterX: sx + 6 + OX, CenterY: e.ey + 7 + OY,
                HalfW: big ? 12 : 6, HalfH: big ? 14 : 7,
                ScreenX: sx, ScreenY: e.ey,
                SpriteIndex: e.egr[cyc], SheetId: SheetIdAt(e.sheetSlot), Size: e.size,
                ArmorLeft: e.armorleft, Value: e.evalue, LinkNum: e.linknum,
                ScoreItem: e.scoreitem, OnScreen: onScreen,
                Xc: e.exc, Yc: e.eyc, XAccel: e.xaccel, YAccel: e.yaccel,
                AnimFrame: e.enemycycle, AnimMax: e.ani,
                LaunchType: e.launchtype, LaunchFreq: e.launchfreq,
                Tur0: e.tur[0], Tur1: e.tur[1], Tur2: e.tur[2], Iced: e.iced));
        }
        // The tick draws the bands ground, ground2, sky, top (DrawEnemy 50/100/25/75, i.e.
        // slot groups 25/75/0/50), so sorting by that rank leaves the front-most last.
        into.Sort((a, b) => BandDrawRank(a.Band).CompareTo(BandDrawRank(b.Band)));
    }

    /// <summary>A background cell under a point, resolved through the live scroll cursors.</summary>
    public readonly record struct TileHit(int Layer, int Row, int Col, int Cell, int ShapeId);

    /// <summary>
    /// Background layers front-to-back as this frame draws them: bg2 and bg3 move around
    /// the stack with background2over / background3over (tyrian2.c gameplay loop, the same
    /// order LayerStack.GameOrder reproduces). A value outside each one's handled range
    /// means the layer isn't drawn at all, so it isn't probed either.
    /// </summary>
    private int[] BgProbeOrder()
    {
        var backToFront = new List<int> { 0 };
        if (_s.background2over is 0 or 3) backToFront.Add(1);
        if (_s.background2over == 1) backToFront.Add(1);
        if (_s.background3over == 2) backToFront.Add(2);
        if (_s.background3over == 0) backToFront.Add(2);
        if (_s.background3over == 1) backToFront.Add(2);
        if (_s.background2over == 2) backToFront.Add(1);
        backToFront.Reverse();
        return backToFront.ToArray();
    }

    /// <summary>
    /// The frontmost visible background tile under a buffer-space point, reversing the
    /// <see cref="DrawBgLayer"/> walk (same flat indexing, so it agrees with what is drawn
    /// even where a band wraps into the next map row).
    /// </summary>
    public bool TryPickTile(int bufX, int bufY, out TileHit hit)
    {
        foreach (int layer in BgProbeOrder())
        {
            bool visible = layer == 0 ? ShowBg1 : layer == 1 ? ShowBg2 : ShowBg3;
            if (!visible) continue;
            (int posIdx, int bp, int xPos, int backPos) = layer switch
            {
                0 => (_s.mapYPos, _s.mapXbp, _s.mapXPos, _s.backPos),
                1 => (_s.mapY2Pos, _s.mapX2bp, _s.mapX2Pos, _s.backPos2),
                _ => (_s.mapY3Pos, _s.mapX3bp, _s.mapX3Pos, _s.backPos3),
            };
            int cols = layer == 2 ? 15 : 14;
            var map = _map[layer];

            int i = FloorDiv(bufY - OY - backPos, 28);
            int t = FloorDiv(bufX - OX - xPos, 24);
            int idx = posIdx + bp - 12 + (i + 1) * cols + t;
            if ((uint)idx >= (uint)map.Length || map[idx] == null) continue;

            int row = FloorDiv(idx, cols);
            hit = new TileHit(layer, row, idx - row * cols,
                _lv.CellsFor(layer)[idx], _lv.ResolveShapeId(layer, _lv.CellsFor(layer)[idx]));
            return true;
        }
        hit = default;
        return false;
    }

    internal struct Enemy
    {
        public int ex, ey;
        public sbyte exc, eyc;
        public sbyte excc, eycc;
        public int exccw, eyccw, exccwmax, eyccwmax, exccadd, eyccadd;
        public int exrev, eyrev;
        public byte armorleft;
        public Byte3 eshotwait, eshotmultipos;
        public byte enemycycle;
        public int ani, animin, animax, aniactive, aniwhenfire;
        public UShort20 egr;
        public int size, linknum;
        public int sheetSlot;         // 0..3 event banks, -2 coins(21), -3 powerups(26), -1 none
        public int enemytype;
        public int edgr, edlevel, edani;
        public byte filter;
        public int evalue, fixedmovey;
        public Byte3 freq, tur;
        public byte launchwait;
        public int launchtype, launchfreq, launchspecial;
        public int xaccel, yaccel;
        public int enemydie;
        public bool enemyground, scoreitem, special, setto, edamaged;
        public int explonum, mapoffset, flagnum, iced;
        public byte objCat;           // ObjCategory, for the viewer's per-category toggles
        public int xminbounce, xmaxbounce, yminbounce, ymaxbounce;
    }

    internal struct Shot
    {
        public int sx, sy, sxm, sym, sxc, syc;
        public byte tx, ty;
        public int sgr, duration, animate, animax;
    }

    internal struct Expl { public int ttl, x, y, sprite, deltaY; public bool fixedPosition; }
    internal struct RepExpl { public int delay, ttl, x, y; public bool big; }
    internal struct Star { public ushort position; public int speed; public byte color; }

    /// <summary>Every scalar of engine state, one value-copyable struct.</summary>
    internal struct S
    {
        public int curLoc, eventLoc, returnLoc;
        public bool returnActive;
        public int backMove, backMove2, backMove3, explodeMove;
        public int map1YDelay, map1YDelayMax, map2YDelay, map2YDelayMax;
        public int backPos, backPos2, backPos3;
        public int mapY, mapY2, mapY3;
        public int mapYPos, mapY2Pos, mapY3Pos;        // flat tile indices (engine pointers)
        public int bkWrap1, bkWrap1to, bkWrap2, bkWrap2to, bkWrap3, bkWrap3to;
        public float carry1, carry2, carry3;           // scroll-multiplier fractions
        public bool enemiesActive, forceEvents, stopBackgrounds;
        public int stopBackgroundNum;
        public bool background3x1, background3x1b;
        public int background3over, background2over;
        public bool topEnemyOver, skyEnemyOverAll, smallEnemyAdjust, starActive;
        public bool background2notTransparent, enemyContinualDamage;
        public int levelEnemyFrequency;
        public bool filterActive, filterFade, filterFadeStart;
        public int levelFilter, levelFilterNew, levelBrightness, levelBrightnessChg;
        public int superEnemy254Jump;
        public int previewLastEventTick;
        public Bool10 globalFlags;
        public Byte10 newPL;
        public Byte9 smoothies, smoothieData;
        public bool levelTimer;
        public int levelTimerCountdown, levelTimerJumpTo;
        public bool previewGateTimerPause;
        public bool randomExplosions, readyToEndLevel, endLevel, finished;
        public int difficultyLevel;
        public int starfieldSpeed;
        public int enemyOnScreen;
        public bool enemyStillExploding;
        public int bossLink0, bossLink1, bossColor0, bossColor1, bossArmor0, bossArmor1;
        public int sheetId0, sheetId1, sheetId2, sheetId3;   // event-5 bank ids
        public int b;                                        // engine global `b`
        public int tickCount;
        public int mapXOfs, mapXPos, mapXbp, mapX2Ofs, mapX2Pos, mapX2bp, mapX3Ofs, mapX3Pos, mapX3bp;
        public int oldMapXOfs, oldMapX3Ofs;
    }

    public sealed class Snapshot
    {
        internal EventRec[] ev = Array.Empty<EventRec>();
        internal Enemy[] enemy = Array.Empty<Enemy>();
        internal byte[] avail = Array.Empty<byte>();
        internal Shot[] shot = Array.Empty<Shot>();
        internal byte[] shotAvail = Array.Empty<byte>();
        internal Expl[] expl = Array.Empty<Expl>();
        internal RepExpl[] repExpl = Array.Empty<RepExpl>();
        internal Star[] stars = Array.Empty<Star>();
        internal S s;
        internal int dat0Egr0, dat0Armor;
        internal ushort[] gateLoopVisits = Array.Empty<ushort>();
        internal int releaseGateEvent;
        internal (uint[] X, int P0, int P1, int Pm) rng;
        public int Tick;
    }

    public Snapshot TakeSnapshot() => new()
    {
        ev = (EventRec[])_ev.Clone(),
        enemy = (Enemy[])_enemy.Clone(),
        avail = (byte[])_avail.Clone(),
        shot = (Shot[])_shot.Clone(),
        shotAvail = (byte[])_shotAvail.Clone(),
        expl = (Expl[])_expl.Clone(),
        repExpl = (RepExpl[])_repExpl.Clone(),
        stars = (Star[])_stars.Clone(),
        s = _s,
        dat0Egr0 = _dat0Egr0,
        dat0Armor = _dat0Armor,
        gateLoopVisits = (ushort[])_gateLoopVisits.Clone(),
        releaseGateEvent = _releaseGateEvent,
        rng = _rng.Snapshot(),
        Tick = _s.tickCount,
    };

    public void RestoreSnapshot(Snapshot sn)
    {
        _ev = (EventRec[])sn.ev.Clone();
        _enemy = (Enemy[])sn.enemy.Clone();
        _avail = (byte[])sn.avail.Clone();
        _shot = (Shot[])sn.shot.Clone();
        _shotAvail = (byte[])sn.shotAvail.Clone();
        _expl = (Expl[])sn.expl.Clone();
        _repExpl = (RepExpl[])sn.repExpl.Clone();
        _stars = (Star[])sn.stars.Clone();
        _s = sn.s;
        _dat0Egr0 = sn.dat0Egr0;
        _dat0Armor = sn.dat0Armor;
        _gateLoopVisits = (ushort[])sn.gateLoopVisits.Clone();
        _releaseGateEvent = sn.releaseGateEvent;
        _rng.Restore(sn.rng);
    }

    public int TickCount => _s.tickCount;

    public GameSim(GameData gd, EpisodeInfo ep, Level lv, ShapeTable shapes)
    {
        _gd = gd;
        _lv = lv;
        _ed = gd.GetEnemyData(ep);
        _wd = gd.GetWeapons(ep);
        _explosionSheet = gd.GetNewshChar('6');
        _tgt = Screen;

        for (int layer = 0; layer < 3; layer++)
        {
            int cols = Level.ColsFor(layer), rows = Level.RowsFor(layer);
            byte[] cells = lv.CellsFor(layer);
            var m = new byte[]?[rows * cols];
            for (int i = 0; i < m.Length; i++)
            {
                int sid = lv.ResolveShapeId(layer, cells[i]);
                m[i] = sid == 0 ? null : shapes.GetById(sid);
            }
            _map[layer] = m;
        }
        _maxEvent = lv.Events.Length;
    }

    /// <summary>Reset to the level start (JE_loadMap tail + JE_main start_level_first).</summary>
    public void Reset()
    {
        _rng.Seed(RngSeed);
        _ev = new EventRec[_maxEvent + 1];
        Array.Copy(_lv.Events, _ev, _maxEvent);
        _ev[_maxEvent] = new EventRec { Time = 65500 };   // engine sentinel

        _enemy = new Enemy[100];
        _avail = new byte[100];
        Array.Fill(_avail, (byte)1);
        for (int i = 0; i < 100; i++) _enemy[i].sheetSlot = -1;
        _shot = new Shot[60];
        _shotAvail = new byte[60];
        Array.Fill(_shotAvail, (byte)1);
        _expl = new Expl[200];
        _repExpl = new RepExpl[20];
        _gateLoopVisits = new ushort[_maxEvent + 1];
        _gateLoopTicks = new int[(_maxEvent + 1) * PreviewLoopCycles];
        _releaseGateEvent = -1;

        var d0 = _ed.Get(0);
        _dat0Egr0 = d0.EGraphic != null ? d0.EGraphic[0] : 0;
        _dat0Armor = d0.Armor;

        _s = default;
        _s.globalFlags = new Bool10();
        _s.mapY = 300 - 8; _s.mapY2 = 600 - 8; _s.mapY3 = 600 - 8;
        _s.mapYPos = _s.mapY * 14 - 1;
        _s.mapY2Pos = _s.mapY2 * 14 - 1;
        _s.mapY3Pos = _s.mapY3 * 15 - 1;
        _s.map1YDelay = 1; _s.map1YDelayMax = 1; _s.map2YDelay = 1; _s.map2YDelayMax = 1;
        _s.backPos = 0; _s.backPos2 = 0; _s.backPos3 = 0;
        _s.starfieldSpeed = 1;
        _s.eventLoc = 1;
        _s.curLoc = HasStartupRouteBootstrap() ? 1 : 0;
        _s.backMove = 1; _s.backMove2 = 2; _s.backMove3 = 3; _s.explodeMove = 2;
        _s.enemiesActive = true;
        _s.stopBackgrounds = false; _s.stopBackgroundNum = 0;
        _s.background3x1 = false; _s.background3x1b = false;
        _s.background3over = 0; _s.background2over = 1;
        _s.topEnemyOver = false; _s.skyEnemyOverAll = false;
        _s.smallEnemyAdjust = false; _s.starActive = true;
        _s.enemyContinualDamage = false;
        _s.levelEnemyFrequency = 96;
        _s.forceEvents = false;
        _s.superEnemy254Jump = 0;
        _s.filterActive = true; _s.filterFade = true; _s.filterFadeStart = false;
        _s.levelFilter = -99; _s.levelBrightness = -14; _s.levelBrightnessChg = 1;
        _s.background2notTransparent = false;
        _s.difficultyLevel = Difficulty;
        // keeps map from scrolling past the top
        _s.bkWrap1 = _s.bkWrap1to = 1 * 14;
        _s.bkWrap2 = _s.bkWrap2to = 1 * 14;
        _s.bkWrap3 = _s.bkWrap3to = 1 * 15;

        for (int i = _stars.Length - 1; i >= 0; i--)
        {
            _stars[i].position = (ushort)(_rng.Next() % 320 + _rng.Next() % 200 * Pitch);
            _stars[i].speed = (int)(_rng.Next() % 3) + 2;
            _stars[i].color = (byte)(_rng.Next() % 16 + 0x90);
        }

        ComputeParallax();
        _s.oldMapXOfs = _s.mapXOfs;
        _s.oldMapX3Ofs = _s.mapX3Ofs;
        ClearScreens();
    }

    /// <summary>Blank all pixel buffers (they are not part of snapshots; a seek clears
    /// them, then draws a few warm-up ticks so feedback filters settle).</summary>
    public void ClearScreens()
    {
        Array.Clear(Screen);
        Array.Clear(_screenB);
        Array.Clear(PresentScreen);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static sbyte S8(int v) => unchecked((sbyte)v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte B8(int v) => unchecked((byte)v);

    /// <summary>
    /// Arcade-style maps can use location 1 only to jump into a 60000-series
    /// route/setup table. The game hides that bootstrap frame behind its opening
    /// fade, but the viewer exposes it when color effects are disabled or the field
    /// is zoomed out. Start those maps at location 1 so the map is re-anchored before
    /// the first inspectable frame.
    /// </summary>
    private bool HasStartupRouteBootstrap()
    {
        for (int i = 0; i < _maxEvent && _ev[i].Time <= 1; i++)
            if (_ev[i].Time == 1 && _ev[i].Type == 54 &&
                unchecked((ushort)_ev[i].Dat) >= 60000)
                return true;
        return false;
    }

    private EnemyDat DatFor(int id)
    {
        var d = _ed.Get(id);
        if (id == 0) d.Armor = B8(_dat0Armor);
        return d;
    }

    private CompShapes? SheetForSlot(int slot) => slot switch
    {
        -2 => _gd.Main.CoinsGems,
        -3 => _gd.Main.PowerUps,
        0 => _gd.GetNewsh(_s.sheetId0),
        1 => _gd.GetNewsh(_s.sheetId1),
        2 => _gd.GetNewsh(_s.sheetId2),
        3 => _gd.GetNewsh(_s.sheetId3),
        _ => null,
    };

    private int SheetIdAt(int slot) => slot switch
    {
        0 => _s.sheetId0, 1 => _s.sheetId1, 2 => _s.sheetId2, 3 => _s.sheetId3, _ => 0,
    };

    /// <summary>Neutral-frame parallax offsets (mainint.c:4693, phantom player).</summary>
    private void ComputeParallax()
    {
        int tempX = PlayerX;
        int tempW = (int)MathF.Floor((float)(260 - (tempX - 36)) / (260 - 36) * (24 * 3) - 1);
        _s.mapX3Ofs = tempW;
        _s.mapX3Pos = _s.mapX3Ofs % 24;
        _s.mapX3bp = 1 - _s.mapX3Ofs / 24;
        _s.mapX2Ofs = tempW * 2 / 3;
        _s.mapX2Pos = _s.mapX2Ofs % 24;
        _s.mapX2bp = 1 - _s.mapX2Ofs / 24;
        _s.mapXOfs = _s.mapX2Ofs / 2;
        _s.mapXPos = _s.mapXOfs % 24;
        _s.mapXbp = 1 - _s.mapXOfs / 24;
        if (_s.background3x1)
        {
            _s.mapX3Ofs = _s.mapXOfs;
            _s.mapX3Pos = _s.mapXPos;
            _s.mapX3bp = _s.mapXbp - 1;
        }
    }

    private int ScaleMove(int move, ref float carry)
    {
        if (ScrollMult == 1f || move == 0) return move;
        float want = move * ScrollMult + carry;
        int n = (int)MathF.Floor(want);
        carry = want - n;
        return n;
    }

    // =====================================================================
    //  One engine tick (the level_loop body, sim-relevant parts in order).
    // =====================================================================
    public void Tick(bool draw)
    {
        if (_s.finished) return;
        _s.tickCount++;
        LogTick = _s.tickCount;

        // Background wrapping (level_loop top)
        if (_s.mapYPos <= _s.bkWrap1) _s.mapYPos = _s.bkWrap1to;
        if (_s.mapY2Pos <= _s.bkWrap2) _s.mapY2Pos = _s.bkWrap2to;
        if (_s.mapY3Pos <= _s.bkWrap3) _s.mapY3Pos = _s.bkWrap3to;

        _s.oldMapX3Ofs = _s.mapX3Ofs;
        _s.oldMapXOfs = _s.mapXOfs;

        _s.enemyOnScreen = 0;

        // --- EVENTS ---
        int guard = 0;
        while (_s.eventLoc >= 1 && _s.eventLoc <= _maxEvent && _ev[_s.eventLoc - 1].Time <= _s.curLoc)
        {
            EventSystem();
            if (_s.finished) return;
            if (++guard > 20000) { _s.finished = true; return; }   // runaway loop safety
        }

        // Events run before the engine's player/parallax update. In particular,
        // SQUADRON sets background3x1 at time zero; calculating before events makes
        // that layer use the normal BG3 anchor for one frame, then snap 42 px left.
        // The viewer has a fixed phantom player, so calculate from the just-authored
        // mode before drawing the frame and never expose that hidden startup state.
        ComputeParallax();

        // JE_checkSmoothies: while smoothies are active, drawing goes to VGAScreen2
        // and each filter pass composites it into the persistent game_screen.
        bool anySmoothies = draw && ShowTerrainSmoothies &&
            (_s.smoothies[0] != 0 || _s.smoothies[1] != 0 || _s.smoothies[2] != 0 ||
             _s.smoothies[3] != 0 || _s.smoothies[4] != 0);
        _tgt = anySmoothies ? _screenB : Screen;

        // --- BACKGROUND 1 ---
        if (_s.forceEvents && _s.backMove == 0) _s.curLoc++;
        if (_s.map1YDelayMax > 1 && _s.backMove < 2)
            _s.backMove = _s.map1YDelay == 1 ? 1 : 0;

        if (draw) DrawBackground1();

        int effBack = 0;
        if (--_s.map1YDelay == 0)
        {
            _s.map1YDelay = _s.map1YDelayMax;
            effBack = ScaleMove(_s.backMove, ref _s.carry1);
            _s.curLoc += effBack;
            _s.backPos += effBack;
            while (_s.backPos > 27)
            {
                _s.backPos -= 28;
                _s.mapY--;
                _s.mapYPos -= 14;
            }
        }

        int eff3 = _s.backMove3;   // refined when background 3 draws this tick

        if (_s.starActive) UpdateAndDrawStarfield(draw);

        // iced blur, early slot (smoothies[5-1])
        if (anySmoothies && _s.smoothies[4] != 0) ApplySmoothie(SmoothieKind.IcedBlur);

        // --- BACKGROUND 2 (early positions; over==3 never blends) ---
        if (_s.background2over == 3) DrawBackground2(draw, allowBlend: false);
        if (_s.background2over == 0) DrawBackground2(draw);

        // lava (early variant) and water run before the ground bands
        if (anySmoothies && _s.smoothies[0] != 0 && _s.smoothieData[0] == 0)
            ApplySmoothie(SmoothieKind.Lava);
        if (anySmoothies && _s.smoothies[1] != 0)
            ApplySmoothie(SmoothieKind.Water);

        // --- Ground enemies ---
        int lastEnemyOnScreen = _s.enemyOnScreen;
        DrawEnemy(50, _s.mapXOfs, effBack, draw);
        DrawEnemy(100, _s.mapXOfs, effBack, draw);
        if (_s.enemyOnScreen == 0 || _s.enemyOnScreen == lastEnemyOnScreen)
            if (_s.stopBackgroundNum == 1) _s.stopBackgroundNum = 9;

        // lava, late variant (smoothie_data[0] > 0)
        if (anySmoothies && _s.smoothies[0] != 0 && _s.smoothieData[0] > 0)
            ApplySmoothie(SmoothieKind.Lava);

        if (_s.background2over == 1) DrawBackground2(draw);
        if (_s.background3over == 2) eff3 = DrawBackground3(draw);

        // --- New random enemy ---
        if (_s.enemiesActive && _rng.Next() % 100 > _s.levelEnemyFrequency && _lv.LevelEnemy.Length > 0)
        {
            int tw = _lv.LevelEnemy[_rng.Next() % (uint)_lv.LevelEnemy.Length];
            NewEnemy(0, tw, 0);
        }

        // iced blur / blur slots run after the random spawn, before the sky band
        if (anySmoothies && _s.smoothies[2] != 0) ApplySmoothie(SmoothieKind.IcedBlur);
        if (anySmoothies && _s.smoothies[3] != 0) ApplySmoothie(SmoothieKind.Blur);

        // --- Sky enemies (under bg3) ---
        if (!_s.skyEnemyOverAll)
        {
            lastEnemyOnScreen = _s.enemyOnScreen;
            DrawEnemy(25, _s.mapX2Ofs, 0, draw);
            if (_s.enemyOnScreen == lastEnemyOnScreen)
                if (_s.stopBackgroundNum == 2) _s.stopBackgroundNum = 9;
        }

        if (_s.background3over == 0) eff3 = DrawBackground3(draw);

        // --- Top enemies (under) ---
        if (!_s.topEnemyOver)
            DrawEnemy(75, _s.background3x1 ? _s.mapXOfs : _s.oldMapX3Ofs, eff3, draw);

        // --- Enemy shots ---
        UpdateEnemyShots(draw);

        if (_s.background3over == 1) eff3 = DrawBackground3(draw);

        // --- Top enemies (over) ---
        if (_s.topEnemyOver)
            DrawEnemy(75, _s.background3x1 ? _s.oldMapXOfs : _s.oldMapX3Ofs, eff3, draw);

        // --- Sky enemies (over all) ---
        if (_s.skyEnemyOverAll)
        {
            lastEnemyOnScreen = _s.enemyOnScreen;
            DrawEnemy(25, _s.mapX2Ofs, 0, draw);
            if (_s.enemyOnScreen == lastEnemyOnScreen)
                if (_s.stopBackgroundNum == 2) _s.stopBackgroundNum = 9;
        }

        UpdateRepExplosions();
        UpdateExplosions(draw);

        if (_s.background2over == 2) DrawBackground2(draw);

        if (_s.randomExplosions && _rng.Next() % 10 == 1)
            SetupExplosionLarge(false, 20, (int)(_rng.Next() % 280), (int)(_rng.Next() % 180));

        if (_s.returnActive && _s.enemyOnScreen == 0)
        {
            EventJump(65535);
            _s.returnActive = false;
        }

        // --- Level timer ---
        if (_s.levelTimer && !_s.previewGateTimerPause && _s.levelTimerCountdown > 0)
        {
            _s.levelTimerCountdown--;
            if (_s.levelTimerCountdown == 0) EventJump((ushort)_s.levelTimerJumpTo);
        }

        // --- Screen filter (also advances the fade state) ---
        if (_s.filterActive) FilterScreen(draw);

        UpdateBossBars(draw);

        // --- Map-stop release / level end ---
        PreviewQuietEnemyHold();

        if (_s.stopBackgroundNum == 9 && _s.backMove == 0 && !_s.enemyStillExploding)
        {
            _s.backMove = 1; _s.backMove2 = 2; _s.backMove3 = 3; _s.explodeMove = 2;
            _s.stopBackgroundNum = 0;
            _s.stopBackgrounds = false;
        }

        if (!_s.endLevel && _s.enemyOnScreen == 0)
        {
            if (_s.readyToEndLevel && !_s.enemyStillExploding)
            {
                _s.readyToEndLevel = false;
                _s.endLevel = true;
                _s.finished = true;
                return;
            }
            if (_s.stopBackgrounds)
            {
                _s.stopBackgrounds = false;
                _s.backMove = 1; _s.backMove2 = 2; _s.backMove3 = 3; _s.explodeMove = 2;
            }
        }
    }

    // =====================================================================
    //  Backgrounds (backgrnd.c)
    // =====================================================================
    /// <summary>Floor division (the tile window may sit at a negative flat index).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -(((-a) + b - 1) / b);

    /// <summary>
    /// One layer pass. The engine draws 12 tile columns x 8 rows; the extended view
    /// widens the range to the full authored map width plus off-screen rows above and
    /// below — same tiles, same registration, just more of them.
    ///
    /// The 12 on-screen columns follow backgrnd.c literally: mapY*Pos is a *flat* pointer
    /// into the `JE_byte *mainmap[rows][cols]` tile-pointer array (tyrian2.c:787), the
    /// band cursor steps one map width per row (`map += 14/15`) and the tile loop reads
    /// `map[0..11]` with no column bound (backgrnd.c:63-105, 149-253). A window that
    /// starts late in a row therefore runs straight on into the next row's leading
    /// columns, and levels depend on it: SQUADRON (ep4 #18) re-seats BG1/BG2 with event
    /// 77 dat=8000 -> flat index 4000, i.e. 10 columns into row 285, so its whole
    /// backdrop comes from the wrapped-around tiles. Clipping each band to its own row
    /// left only the two columns before the wrap, i.e. a blue strip pinned to the left
    /// edge instead of a full-screen starfield backdrop.
    ///
    /// The extended margins are a viewer-only invention (the engine never draws them), so
    /// they stay row-bounded: a margin tile is drawn only while it is still in the same
    /// map row as the on-screen column it extends. That shows the authored columns just
    /// outside the 288px window without spilling neighbouring rows across the margin.
    /// </summary>
    private void DrawBgLayer(int layer, int posIdx, int bp, int xPos, int backPos, bool blend)
    {
        var map = _map[layer];
        int cols = layer == 2 ? 15 : 14;
        int start = posIdx + bp - 12;

        int iMin = ExtendedDraw ? -6 : -1;
        int iMax = ExtendedDraw ? 7 : 6;
        int tMin = ExtendedDraw ? -4 : 0;
        int tMax = ExtendedDraw ? 16 : 11;

        var tgt = _tgt;
        for (int i = iMin; i <= iMax; i++)
        {
            int rowBase = start + (i + 1) * cols;
            int leftRow = FloorDiv(rowBase, cols);          // map row of on-screen column 0
            int rightRow = FloorDiv(rowBase + 11, cols);    // map row of on-screen column 11
            int y = i * 28 + backPos;
            for (int t = tMin; t <= tMax; t++)
            {
                int idx = rowBase + t;
                if (t < 0 && FloorDiv(idx, cols) != leftRow) continue;
                if (t > 11 && FloorDiv(idx, cols) != rightRow) continue;
                if ((uint)idx >= (uint)map.Length) continue;
                byte[]? data = map[idx];
                if (data == null) continue;
                int bx = xPos + t * 24;
                for (int ty = 0; ty < 28; ty++)
                {
                    int dy = y + ty + OY;
                    if ((uint)dy >= BufH) continue;
                    int src = ty * 24;
                    int dstRow = dy * BufW;
                    for (int tx = 0; tx < 24; tx++)
                    {
                        byte v = data[src + tx];
                        if (v == 0) continue;
                        int dx = bx + tx + OX;
                        if ((uint)dx >= BufW) continue;
                        if (blend)
                        {
                            byte d = tgt[dstRow + dx];
                            tgt[dstRow + dx] = (byte)((v & 0xF0) | (((d & 0x0F) + (v & 0x0F)) / 2));
                        }
                        else tgt[dstRow + dx] = v;
                    }
                }
            }
        }
    }

    private void DrawBackground1()
    {
        Array.Clear(_tgt);   // SDL_FillRect: the frame always starts blank
        if (ShowBg1)
            DrawBgLayer(0, _s.mapYPos, _s.mapXbp, _s.mapXPos, _s.backPos, blend: false);
    }

    private void DrawBackground2(bool draw, bool allowBlend = true)
    {
        if (_s.map2YDelayMax > 1 && _s.backMove2 < 2)
            _s.backMove2 = _s.map2YDelay == 1 ? 1 : 0;

        bool blend = allowBlend && !_s.background2notTransparent;   // wild detail default
        if (draw && ShowBg2)
            DrawBgLayer(1, _s.mapY2Pos, _s.mapX2bp, _s.mapX2Pos, _s.backPos2, blend);

        if (--_s.map2YDelay == 0)
        {
            _s.map2YDelay = _s.map2YDelayMax;
            int m = ScaleMove(_s.backMove2, ref _s.carry2);
            _s.backPos2 += m;
            while (_s.backPos2 > 27)
            {
                _s.backPos2 -= 28;
                _s.mapY2--;
                _s.mapY2Pos -= 14;
            }
        }
    }

    private int DrawBackground3(bool draw)
    {
        int m = ScaleMove(_s.backMove3, ref _s.carry3);
        _s.backPos3 += m;
        while (_s.backPos3 > 27)
        {
            _s.backPos3 -= 28;
            _s.mapY3--;
            _s.mapY3Pos -= 15;
        }
        if (draw && ShowBg3)
            DrawBgLayer(2, _s.mapY3Pos, _s.mapX3bp, _s.mapX3Pos, _s.backPos3, blend: false);
        return m;
    }

    // =====================================================================
    //  Smoothie filters (backgrnd.c) — composite the draw target into the
    //  persistent game_screen; lava/water read the previous frame (feedback).
    // =====================================================================
    private enum SmoothieKind { Lava, Water, IcedBlur, Blur }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int VIdx(int p) => (p / 320 + OY) * BufW + p % 320 + OX;   // vanilla-linear -> buffer

    private void ApplySmoothie(SmoothieKind kind)
    {
        byte[] src = _tgt;
        byte[] dst = Screen;
        if (!ReferenceEquals(src, dst))
        {
            CopyMargins(src, dst);   // margins bypass the filters (they cover the vanilla area)
            int filteredRows = kind is SmoothieKind.Lava or SmoothieKind.Water ? 185 : 184;
            CopyVanillaTail(src, dst, filteredRows);
        }

        switch (kind)
        {
            case SmoothieKind.Lava:
            {
                int w = 320 * 185 - 1;
                int p = 320 * 185;
                for (int y = 185 - 1; y >= 0; y--)
                {
                    for (int x = 320 - 1; x >= 0; x -= 8)
                    {
                        int waver = Math.Abs(((w >> 9) & 0x0F) - 8) - 1;
                        w -= 8;
                        for (int xi = 8 - 1; xi >= 0; xi--)
                        {
                            p--;
                            int value = 0;
                            if (p + waver >= 0)
                                value += (src[VIdx(p + waver)] & 0x0F) * 2;
                            value += dst[VIdx(p + waver + 320)] & 0x0F;
                            if (p + waver - 320 >= 0)
                                value += dst[VIdx(p + waver - 320)] & 0x0F;
                            dst[VIdx(p)] = (byte)((value / 4) | 0x70);
                        }
                    }
                }
                break;
            }
            case SmoothieKind.Water:
            {
                byte hue = (byte)(_s.smoothieData[1] << 4);
                int w = 320 * 185 - 1;
                int p = 320 * 185;
                for (int y = 185 - 1; y >= 0; y--)
                {
                    for (int x = 320 - 1; x >= 0; x -= 8)
                    {
                        int waver = Math.Abs(((w >> 10) & 0x07) - 4) - 1;
                        w -= 8;
                        for (int xi = 8 - 1; xi >= 0; xi--)
                        {
                            p--;
                            byte s = src[VIdx(p)];
                            if ((s & 0x30) == 0)
                                dst[VIdx(p)] = s;
                            else
                            {
                                int value = (s & 0x0F) + (dst[VIdx(p + waver + 320)] & 0x0F);
                                dst[VIdx(p)] = (byte)((value / 2) | hue);
                            }
                        }
                    }
                }
                break;
            }
            case SmoothieKind.IcedBlur:
            case SmoothieKind.Blur:
            {
                bool iced = kind == SmoothieKind.IcedBlur;
                for (int y = 0; y < 184; y++)
                {
                    int row = (y + OY) * BufW + OX;
                    for (int x = 0; x < 320; x++)
                    {
                        byte s = src[row + x];
                        byte d = dst[row + x];
                        int value = (s & 0x0F) + (d & 0x0F);
                        dst[row + x] = (byte)((value / 2) | (iced ? 0x80 : s & 0xF0));
                    }
                }
                break;
            }
        }
        _tgt = Screen;   // VGAScreen = game_screen; later draws land on the filtered image
    }

    /// <summary>Copy everything outside the vanilla 320x200 area from src to dst so the
    /// extended margins stay in sync when a filter switches the draw target.</summary>
    private static void CopyMargins(byte[] src, byte[] dst)
    {
        for (int y = 0; y < BufH; y++)
        {
            int row = y * BufW;
            if (y < OY || y >= OY + 200)
            {
                Array.Copy(src, row, dst, row, BufW);
                continue;
            }
            Array.Copy(src, row, dst, row, OX);
            Array.Copy(src, row + OX + 320, dst, row + OX + 320, BufW - OX - 320);
        }
    }

    /// <summary>The original filters stop at the displayed scanlines. Preserve the
    /// otherwise invisible vanilla tail so it remains useful in extended view.</summary>
    private static void CopyVanillaTail(byte[] src, byte[] dst, int firstRow)
    {
        for (int y = firstRow; y < ScreenH; y++)
        {
            int row = (y + OY) * BufW + OX;
            Array.Copy(src, row, dst, row, ScreenW);
        }
    }

    private void UpdateAndDrawStarfield(bool draw)
    {
        var tgt = _tgt;
        for (int i = _stars.Length - 1; i >= 0; i--)
        {
            ref var st = ref _stars[i];
            st.position = (ushort)(st.position + (st.speed + _s.starfieldSpeed) * Pitch);
            if (!draw || !ShowStarfield) continue;
            int pos = st.position;
            if (pos < 177 * Pitch)
            {
                int b = VIdx(pos);
                if (tgt[b] == 0) tgt[b] = st.color;
                if (st.color - 4 >= 0x90)
                {
                    byte halo = (byte)(st.color - 4);
                    if (tgt[b + 1] == 0) tgt[b + 1] = halo;
                    if (pos > 0 && tgt[b - 1] == 0) tgt[b - 1] = halo;
                    if (tgt[b + BufW] == 0) tgt[b + BufW] = halo;
                    if (pos >= Pitch && tgt[b - BufW] == 0) tgt[b - BufW] = halo;
                }
            }
        }
    }

    // =====================================================================
    //  Sprites
    // =====================================================================
    private void BlitSprite(CompShapes? sheet, int index, int x, int y, byte filter)
    {
        Sprite? spr = sheet?.Decode(index);
        if (spr == null) return;
        var tgt = _tgt;
        for (int sy = 0; sy < spr.H; sy++)
        {
            int dy = y + sy + OY;
            if ((uint)dy >= BufH) continue;
            int row = dy * BufW;
            int srow = sy * spr.W;
            for (int sx = 0; sx < spr.W; sx++)
            {
                byte v = spr.Pixels[srow + sx];
                if (v == 0) continue;
                int dx = x + sx + OX;
                if ((uint)dx >= BufW) continue;
                tgt[row + dx] = filter != 0 ? (byte)(filter | (v & 0x0F)) : v;
            }
        }
    }

    private void BlitSpriteBlend(CompShapes? sheet, int index, int x, int y)
    {
        Sprite? spr = sheet?.Decode(index);
        if (spr == null) return;
        var tgt = _tgt;
        for (int sy = 0; sy < spr.H; sy++)
        {
            int dy = y + sy + OY;
            if ((uint)dy >= BufH) continue;
            int row = dy * BufW;
            int srow = sy * spr.W;
            for (int sx = 0; sx < spr.W; sx++)
            {
                byte v = spr.Pixels[srow + sx];
                if (v == 0) continue;
                int dx = x + sx + OX;
                if ((uint)dx >= BufW) continue;
                byte d = tgt[row + dx];
                tgt[row + dx] = (byte)((v & 0xF0) | (((d & 0x0F) + (v & 0x0F)) / 2));
            }
        }
    }

    private void BlitEnemy(ref Enemy e, int tempMapXOfs, int xOfs, int yOfs, int sprOfs)
    {
        int cyc = Math.Clamp(e.enemycycle - 1, 0, 19);
        BlitSprite(SheetForSlot(e.sheetSlot), e.egr[cyc] + sprOfs,
            e.ex + xOfs + tempMapXOfs, e.ey + yOfs, e.filter);
    }

    /// <summary>The engine's per-blit visibility gates; the extended view bypasses
    /// them (they only exist to avoid drawing outside the vanilla screen).</summary>
    private void DrawEnemyBlits(ref Enemy e, int tempMapXOfs, bool gated)
    {
        if (e.size == 1)
        {
            if (!gated || e.ey > -13)
            {
                BlitEnemy(ref e, tempMapXOfs, -6, -7, 0);
                BlitEnemy(ref e, tempMapXOfs, 6, -7, 1);
            }
            if (!gated || (e.ey > -26 && e.ey < 182))
            {
                BlitEnemy(ref e, tempMapXOfs, -6, 7, 19);
                BlitEnemy(ref e, tempMapXOfs, 6, 7, 20);
            }
        }
        else if (!gated || e.ey > -13)
        {
            BlitEnemy(ref e, tempMapXOfs, 0, 0, 0);
        }
    }

    // =====================================================================
    //  Enemies (JE_drawEnemy)
    // =====================================================================
    private void DrawEnemy(int enemyOffset, int tempMapXOfs, int tempBackMove, bool draw)
    {
        int px = PlayerX - 25;   // player[0].x -= 25 wrapper
        for (int i = enemyOffset - 25; i < enemyOffset; i++)
        {
            if (_avail[i] == 1) continue;
            ref var e = ref _enemy[i];
            e.mapoffset = tempMapXOfs;

            if (e.xaccel != 0 && unchecked((uint)(e.xaccel - 89)) > _rng.Next() % 11)
            {
                if (px > e.ex) { if (e.exc < e.xaccel - 89) e.exc++; }
                else { if (e.exc >= 0 || -e.exc < e.xaccel - 89) e.exc--; }
            }
            if (e.yaccel != 0 && unchecked((uint)(e.yaccel - 89)) > _rng.Next() % 11)
            {
                if (PlayerY > e.ey) { if (e.eyc < e.yaccel - 89) e.eyc++; }
                else { if (e.eyc >= 0 || -e.eyc < e.yaccel - 89) e.eyc--; }
            }

            if (e.ex + tempMapXOfs > -29 && e.ex + tempMapXOfs < 300)
            {
                if (e.aniactive == 1)
                {
                    e.enemycycle++;
                    if (e.enemycycle == e.animax) e.aniactive = e.aniwhenfire;
                    else if (e.enemycycle > e.ani) e.enemycycle = B8(e.animin);
                }

                if (e.egr[Math.Clamp(e.enemycycle - 1, 0, 19)] == 999)
                {
                    _avail[i] = 1;
                    continue;
                }

                if (draw && CategoryVisible(e.objCat))
                    DrawEnemyBlits(ref e, tempMapXOfs, gated: !ExtendedDraw);
                e.filter = 0;
            }
            else if (ExtendedDraw && draw && CategoryVisible(e.objCat) &&
                     e.egr[Math.Clamp(e.enemycycle - 1, 0, 19)] != 999)
            {
                // outside the engine's animation window: frozen, but visible beyond
                // the screen in the extended view
                DrawEnemyBlits(ref e, tempMapXOfs, gated: false);
            }

            // cyclic acceleration
            if (e.excc != 0 && --e.exccw <= 0)
            {
                if (e.exc == e.exrev)
                {
                    e.excc = S8(-e.excc); e.exrev = -e.exrev; e.exccadd = -e.exccadd;
                }
                else
                {
                    e.exc = S8(e.exc + e.exccadd);
                    e.exccw = e.exccwmax;
                    if (e.exc == e.exrev)
                    {
                        e.excc = S8(-e.excc); e.exrev = -e.exrev; e.exccadd = -e.exccadd;
                    }
                }
            }
            if (e.eycc != 0 && --e.eyccw <= 0)
            {
                if (e.eyc == e.eyrev)
                {
                    e.eycc = S8(-e.eycc); e.eyrev = -e.eyrev; e.eyccadd = -e.eyccadd;
                }
                else
                {
                    e.eyc = S8(e.eyc + e.eyccadd);
                    e.eyccw = e.eyccwmax;
                    if (e.eyc == e.eyrev)
                    {
                        e.eycc = S8(-e.eycc); e.eyrev = -e.eyrev; e.eyccadd = -e.eyccadd;
                    }
                }
            }

            e.ey += e.fixedmovey;

            e.ex += e.exc;
            if (e.ex < -80 || e.ex > 340) { _avail[i] = 1; continue; }
            e.ey += e.eyc;
            if (e.ey < -112 || e.ey > 190) { _avail[i] = 1; continue; }

            if (e.ex <= e.xminbounce || e.ex >= e.xmaxbounce) e.exc = S8(-e.exc);
            if (e.ey <= e.yminbounce || e.ey >= e.ymaxbounce) e.eyc = S8(-e.eyc);

            if (e.scoreitem)
            {
                if (e.ex < -5) e.ex++;
                if (e.ex > 245) e.ex--;
            }

            e.ey += tempBackMove;

            if (e.ex <= -24 || e.ex >= 296) continue;

            int tempX = e.ex, tempY = e.ey;

            if (e.edamaged) continue;

            _s.enemyOnScreen++;

            if (e.iced > 0)
            {
                e.iced--;
                if (e.enemyground) e.filter = 0x09;
                continue;
            }

            bool slotFull = false;
            if (FireEnabled)
                slotFull = FireTurrets(ref e, tempX, tempY, tempMapXOfs);
            if (slotFull) continue;   // goto draw_enemy_end (skips launch)

            // Enemy launch routine
            if (e.launchfreq != 0)
            {
                if (--e.launchwait == 0)
                {
                    e.launchwait = B8(e.launchfreq);

                    if (e.launchspecial != 0 && Math.Abs(e.ey - PlayerY) > 5)
                        continue;

                    if (e.aniactive == 2) e.aniactive = 1;
                    if (e.launchtype == 0) continue;

                    int b = NewEnemy(enemyOffset == 50 ? 75 : enemyOffset - 25, e.launchtype, 0);
                    if (b > 0)
                    {
                        ref var l = ref _enemy[b - 1];
                        l.ex = tempX;
                        l.ey = tempY + DatFor(l.enemytype).StartYC;
                        if (l.size == 0) l.ey -= 7;

                        if (l.launchtype > 0 && l.launchfreq == 0)
                        {
                            if (l.launchtype > 90)
                            {
                                l.ex += (int)(_rng.Next() % (uint)((l.launchtype - 90) * 4))
                                    - (l.launchtype - 90) * 2;
                            }
                            else
                            {
                                int aimX = PlayerX - tempX - tempMapXOfs - 4;
                                if (aimX == 0) aimX = 1;
                                int aimY = PlayerY - tempY;
                                if (aimY == 0) aimY = 1;
                                int mag = Math.Max(Math.Abs(aimX), Math.Abs(aimY));
                                l.exc = S8((int)MathF.Round((float)aimX / mag * l.launchtype,
                                    MidpointRounding.AwayFromZero));
                                l.eyc = S8((int)MathF.Round((float)aimY / mag * l.launchtype,
                                    MidpointRounding.AwayFromZero));
                            }
                        }

                        uint t;
                        do { t = _rng.Next() % 8; } while (t == 3);
                        _ = _rng.Next() % 3;   // randomEnemyLaunchSounds pick

                        if (e.launchspecial == 1 && e.linknum < 100)
                            l.linknum = e.linknum;
                    }
                }
            }
        }
    }

    /// <summary>Turret fire (JE_drawEnemy shots). Returns true when out of shot slots
    /// (engine: goto draw_enemy_end, skipping the launch routine).</summary>
    private bool FireTurrets(ref Enemy e, int tempX, int tempY, int tempMapXOfs)
    {
        for (int j = 3; j > 0; j--)
        {
            if (e.freq[j - 1] == 0) continue;
            int temp3 = e.tur[j - 1];
            e.eshotwait[j - 1] = B8(e.eshotwait[j - 1] - 1);
            if (e.eshotwait[j - 1] != 0 || temp3 == 0) continue;

            e.eshotwait[j - 1] = e.freq[j - 1];
            if (_s.difficultyLevel > 2)
            {
                e.eshotwait[j - 1] = B8(e.eshotwait[j - 1] / 2 + 1);
                if (_s.difficultyLevel > 7)
                    e.eshotwait[j - 1] = B8(e.eshotwait[j - 1] / 2 + 1);
            }

            switch (temp3)
            {
                case 252: // Savara Boss DualMissile
                    if (e.ey > 20)
                    {
                        SetupExplosion(tempX - 8 + tempMapXOfs, tempY - 20 - _s.backMove * 8, -2, 6);
                        SetupExplosion(tempX + 4 + tempMapXOfs, tempY - 20 - _s.backMove * 8, -2, 6);
                    }
                    break;
                case 251: // Suck-O-Magnet (player force only)
                case 253: // ShortRange Magnets
                case 254:
                    break;
                case 255: // Magneto RePulse!!
                    if (_s.difficultyLevel != 1 && j == 3)
                        e.filter = 0x70;
                    break;
                default:
                {
                    var w = _wd.Get(temp3);
                    if (!w.Loaded) break;
                    for (int count = w.Multi; count > 0; count--)
                    {
                        int b;
                        for (b = 0; b < _shotAvail.Length; b++)
                            if (_shotAvail[b] == 1) break;
                        if (b == _shotAvail.Length)
                            return true;   // goto draw_enemy_end

                        _shotAvail[b] = 0;

                        if (w.Sound > 0)
                        {
                            uint t;
                            do { t = _rng.Next() % 8; } while (t == 3);
                        }

                        if (e.aniactive == 2) e.aniactive = 1;

                        e.eshotmultipos[j - 1] = B8(e.eshotmultipos[j - 1] + 1);
                        if (e.eshotmultipos[j - 1] > w.Max) e.eshotmultipos[j - 1] = 1;
                        int pos = e.eshotmultipos[j - 1] - 1;

                        ref var sh = ref _shot[b];
                        sh.sx = tempX + w.Bx[pos] + tempMapXOfs;
                        sh.sy = tempY + w.By[pos];
                        sh.tx = w.Tx;
                        sh.ty = w.Ty;
                        sh.duration = w.Del[pos];
                        sh.animate = 0;
                        sh.animax = w.WeapAni;
                        sh.sgr = w.Sg[pos];
                        switch (j)
                        {
                            case 1:
                                sh.syc = w.Acceleration; sh.sxc = w.AccelerationX;
                                sh.sxm = w.Sx[pos]; sh.sym = w.Sy[pos];
                                break;
                            case 3:
                                sh.sxc = -w.Acceleration; sh.syc = w.AccelerationX;
                                sh.sxm = -w.Sy[pos]; sh.sym = -w.Sx[pos];
                                break;
                            case 2:
                                sh.sxc = w.Acceleration; sh.syc = -w.Acceleration;
                                sh.sxm = w.Sy[pos]; sh.sym = -w.Sx[pos];
                                break;
                        }

                        if (w.Aim > 0)
                        {
                            int aim = w.Aim;
                            if (_s.difficultyLevel > 2) aim += _s.difficultyLevel - 2;
                            int aimX = PlayerX - tempX - tempMapXOfs - 4;
                            if (aimX == 0) aimX = 1;
                            int aimY = PlayerY - tempY;
                            if (aimY == 0) aimY = 1;
                            int mag = Math.Max(Math.Abs(aimX), Math.Abs(aimY));
                            sh.sxm = (int)MathF.Round((float)aimX / mag * aim, MidpointRounding.AwayFromZero);
                            sh.sym = (int)MathF.Round((float)aimY / mag * aim, MidpointRounding.AwayFromZero);
                        }
                    }
                    break;
                }
            }
        }
        return false;
    }

    private void UpdateEnemyShots(bool draw)
    {
        for (int z = 0; z < _shot.Length; z++)
        {
            if (_shotAvail[z] != 0) continue;
            ref var sh = ref _shot[z];
            sh.sxm += sh.sxc;
            sh.sx += sh.sxm;
            if (sh.tx != 0)
            {
                if (sh.sx > PlayerX) { if (sh.sxm > -sh.tx) sh.sxm--; }
                else { if (sh.sxm < sh.tx) sh.sxm++; }
            }
            sh.sym += sh.syc;
            sh.sy += sh.sym;
            if (sh.ty != 0)
            {
                if (sh.sy > PlayerY) { if (sh.sym > -sh.ty) sh.sym--; }
                else { if (sh.sym < sh.ty) sh.sym++; }
            }
            if (sh.duration-- == 0 || sh.sy > 190 || sh.sy <= -14 || sh.sx > 275 || sh.sx <= 0)
            {
                _shotAvail[z] = 1;
                continue;
            }
            if (sh.animax != 0 && ++sh.animate >= sh.animax)
                sh.animate = 0;
            if (draw)
            {
                if (sh.sgr >= 500)
                    BlitSprite(_gd.Main.Sheets[11], sh.sgr + sh.animate - 500, sh.sx, sh.sy, 0);
                else
                    BlitSprite(_gd.Main.Sheets[7], sh.sgr + sh.animate, sh.sx, sh.sy, 0);
            }
        }
    }

    // =====================================================================
    //  Explosions (varz.c)
    // =====================================================================
    private static readonly (int Sprite, int Ttl)[] ExplosionData =
    {
        (144, 7), (120, 12), (190, 12), (209, 12), (152, 12), (171, 12),
        (133, 7), (1, 12), (20, 12), (39, 12), (58, 12), (110, 3), (76, 7), (91, 3),
        (227, 3), (230, 3), (233, 3), (252, 3), (246, 3), (249, 3), (265, 3), (268, 3),
        (271, 3), (236, 3), (239, 3), (242, 3), (261, 3), (274, 3), (277, 3), (280, 3),
        (299, 3), (284, 3), (287, 3), (290, 3), (293, 3), (165, 8), (184, 8), (203, 8),
        (222, 8), (168, 8), (187, 8), (206, 8), (225, 10), (169, 10), (188, 10),
        (207, 20), (226, 14), (170, 14), (189, 14), (208, 14), (246, 14), (227, 14),
        (265, 14), (96, 3),
    };

    private void SetupExplosion(int x, int y, int deltaY, int type, bool fixedPosition = false)
    {
        if (y <= -16 || y >= 190) return;
        for (int i = 0; i < _expl.Length; i++)
        {
            if (_expl[i].ttl != 0) continue;
            _expl[i].x = x;
            _expl[i].y = y;
            if (type == 6) { _expl[i].y += 12; _expl[i].x += 2; }
            else if (type == 98 || type == 198) type = 6;
            if ((uint)type >= (uint)ExplosionData.Length) type = 1;
            _expl[i].sprite = ExplosionData[type].Sprite;
            _expl[i].ttl = ExplosionData[type].Ttl;
            _expl[i].fixedPosition = fixedPosition;
            _expl[i].deltaY = deltaY;
            break;
        }
    }

    private void SetupExplosionLarge(bool enemyGround, int exploNum, int x, int y)
    {
        if (y < 0) return;
        if (enemyGround)
        {
            SetupExplosion(x - 6, y - 14, 0, 2);
            SetupExplosion(x + 6, y - 14, 0, 4);
            SetupExplosion(x - 6, y, 0, 3);
            SetupExplosion(x + 6, y, 0, 5);
        }
        else
        {
            SetupExplosion(x - 6, y - 14, 0, 7);
            SetupExplosion(x + 6, y - 14, 0, 9);
            SetupExplosion(x - 6, y, 0, 8);
            SetupExplosion(x + 6, y, 0, 10);
        }
        bool big = exploNum > 10;
        if (big) exploNum -= 10;
        if (exploNum == 0) return;
        for (int i = 0; i < _repExpl.Length; i++)
        {
            if (_repExpl[i].ttl != 0) continue;
            _repExpl[i].ttl = exploNum;
            _repExpl[i].delay = 2;
            _repExpl[i].x = x;
            _repExpl[i].y = y;
            _repExpl[i].big = big;
            break;
        }
    }

    private void UpdateRepExplosions()
    {
        _s.enemyStillExploding = false;
        for (int i = 0; i < _repExpl.Length; i++)
        {
            ref var r = ref _repExpl[i];
            if (r.ttl == 0) continue;
            _s.enemyStillExploding = true;
            if (r.delay > 0) { r.delay--; continue; }
            r.y += _s.backMove2 + 1;
            int tx = r.x + (int)(_rng.Next() % 24) - 12;
            int ty = r.y + (int)(_rng.Next() % 27) - 24;
            if (r.big)
            {
                SetupExplosionLarge(false, 2, tx, ty);
                if (r.ttl != 1) _ = _rng.Next() % 5;   // sound pick parity
                r.delay = 4 + (int)(_rng.Next() % 3);
            }
            else
            {
                SetupExplosion(tx, ty, 0, 1);
                r.delay = 3;
            }
            r.ttl--;
        }
    }

    private void UpdateExplosions(bool draw)
    {
        for (int j = 0; j < _expl.Length; j++)
        {
            ref var ex = ref _expl[j];
            if (ex.ttl == 0) continue;
            if (!ex.fixedPosition)
            {
                ex.sprite++;
                ex.y += _s.explodeMove;
            }
            ex.y += ex.deltaY;
            if (ex.y > 200 - 14) { ex.ttl = 0; continue; }
            if (draw) BlitSpriteBlend(_explosionSheet, ex.sprite + 1, ex.x, ex.y);
            ex.ttl--;
        }
    }

    // =====================================================================
    //  Boss bars (draw_boss_bar)
    // =====================================================================
    private void FillRect(int x1, int y1, int x2, int y2, byte col)
    {
        for (int y = y1; y <= y2; y++)
        {
            int dy = y + OY;
            if ((uint)dy >= BufH) continue;
            int row = dy * BufW;
            for (int x = x1; x <= x2; x++)
            {
                int dx = x + OX;
                if ((uint)dx < BufW)
                    Screen[row + dx] = col;
            }
        }
    }

    private void BarX(int x1, int y1, int x2, int y2, int col)
    {
        FillRect(x1, y1, x2, y1, B8(col + 1));
        FillRect(x1, y1 + 1, x2, y2 - 1, B8(col));
        FillRect(x1, y2, x2, y2, B8(col - 1));
    }

    private void UpdateBossBars(bool draw)
    {
        Span<int> link = stackalloc int[2] { _s.bossLink0, _s.bossLink1 };
        Span<int> armor = stackalloc int[2] { _s.bossArmor0, _s.bossArmor1 };
        Span<int> color = stackalloc int[2] { _s.bossColor0, _s.bossColor1 };

        for (int bi = 0; bi < 2; bi++)
        {
            if (link[bi] == 0) continue;
            int found = 256;
            for (int e = 0; e < 100; e++)
                if (_avail[e] != 1 && _enemy[e].linknum == link[bi] && _enemy[e].armorleft < found)
                    found = _enemy[e].armorleft;
            if (found > 255 || found == 0) link[bi] = 0;
            else armor[bi] = found == 255 ? 254 : found;
        }

        int bars = (link[0] != 0 ? 1 : 0) + (link[1] != 0 ? 1 : 0);
        if (bars == 1 && link[0] == 0)
        {
            link[0] = link[1]; armor[0] = armor[1]; color[0] = color[1];
            link[1] = 0;
        }

        if (draw)
        {
            for (int bi = 0; bi < bars; bi++)
            {
                int x = bars == 2 ? (bi == 0 ? 125 : 185) : (_s.levelTimer ? 250 : 155);
                int y = _s.levelTimer ? 15 : 7;
                BarX(x - 25, y, x + 25, y + 5, 115);
                BarX(x - armor[bi] / 10, y, x + (armor[bi] + 5) / 10, y + 5, 118 + color[bi]);
            }
        }
        for (int bi = 0; bi < bars; bi++)
            if (color[bi] > 0) color[bi]--;

        _s.bossLink0 = link[0]; _s.bossLink1 = link[1];
        _s.bossArmor0 = armor[0]; _s.bossArmor1 = armor[1];
        _s.bossColor0 = color[0]; _s.bossColor1 = color[1];
    }

    // =====================================================================
    //  Screen filter (JE_filterScreen)
    // =====================================================================
    private void FilterScreen(bool draw)
    {
        int col = _s.levelFilter, bright = _s.levelBrightness;

        if (_s.filterFade)
        {
            // JE_shortint is an 8-bit signed value. Some authored fades (CORE)
            // deliberately cross -128 and depend on wrapping to +127 before they
            // reverse. Keeping this as an unbounded int makes the fade never return.
            _s.levelBrightness = S8(_s.levelBrightness + _s.levelBrightnessChg);
            if ((_s.filterFadeStart && _s.levelBrightness < -14) || _s.levelBrightness > 14)
            {
                _s.levelBrightnessChg = S8(-_s.levelBrightnessChg);
                _s.filterFadeStart = false;
                _s.levelFilter = _s.levelFilterNew;
            }
            if (!_s.filterFadeStart && _s.levelBrightness == 0)
            {
                _s.filterFade = false;
                _s.levelBrightness = -99;
            }
        }

        if (!draw || !ShowScreenFilter) return;

        if (col != -99)
        {
            int hue = (col << 4) & 0xF0;
            for (int y = 0; y < ViewH; y++)
            {
                int row = (y + OY) * BufW + ViewX + OX;
                for (int x = 0; x < ViewW; x++)
                    Screen[row + x] = (byte)(hue | (Screen[row + x] & 0x0F));
            }
        }
        if (bright != -99)
        {
            for (int y = 0; y < ViewH; y++)
            {
                int row = (y + OY) * BufW + ViewX + OX;
                for (int x = 0; x < ViewW; x++)
                {
                    byte s = Screen[row + x];
                    uint t = (uint)((s & 0x0F) + bright);
                    byte low = t >= 0x1F ? (byte)0 : t >= 0x0F ? (byte)0x0F : (byte)t;
                    Screen[row + x] = (byte)((s & 0xF0) | low);
                }
            }
        }
    }

    // =====================================================================
    //  Presentation (JE_starShowVGA): flip / spotlight special codes.
    // =====================================================================
    public void PreparePresent()
    {
        Array.Copy(Screen, PresentScreen, Screen.Length);
        int code = _s.smoothies[8] + (_s.smoothies[5] << 1);
        if (code == 1 && ShowScreenFlip)
        {
            // upside-down playfield (TIME WAR / EYESPY style)
            for (int r = 0; r < ViewH; r++)
            {
                int dst = (r + OY) * BufW + ViewX + OX;
                int src = (ViewH - 1 - r + OY) * BufW + ViewX + OX;
                Array.Copy(Screen, src, PresentScreen, dst, ViewW);
            }
        }
        else if (code == 2 && ShowSpotlight)
        {
            // spotlight around the (phantom) player; everything else darkened
            int lighty = 172 - PlayerY;
            int lightx = 281 - PlayerX;
            for (int r = 0; r < ViewH; r++)
            {
                int y = 184 - r;
                int row = (r + OY) * BufW + ViewX + OX;
                for (int c = 0; c < ViewW; c++)
                {
                    byte s = Screen[row + c];
                    int x = 264 - c;
                    if (lighty > y)
                    {
                        PresentScreen[row + c] = (byte)((s & 0xF0) | ((s >> 2) & 0x03));
                        continue;
                    }
                    int lightdist = Math.Abs(lightx - x) + lighty;
                    if (lightdist < y)
                        PresentScreen[row + c] = s;
                    else if (lightdist - y <= 5)
                        PresentScreen[row + c] = (byte)((s & 0xF0) |
                            (((s & 0x0F) + 3 * (5 - (lightdist - y))) / 4));
                    else
                        PresentScreen[row + c] = (byte)((s & 0xF0) | ((s & 0x0F) >> 2));
                }
            }
        }
    }

    // =====================================================================
    //  Enemy creation (JE_newEnemy / JE_makeEnemy / JE_createNewEventEnemy)
    // =====================================================================
    private int NewEnemy(int enemyOffset, int eDatI, int uniqueShapeTableI)
    {
        for (int i = enemyOffset; i < enemyOffset + 25 && i < 100; i++)
        {
            if (_avail[i] == 1)
            {
                _avail[i] = MakeEnemy(ref _enemy[i], eDatI, uniqueShapeTableI, enemyOffset);
                return i + 1;
            }
        }
        return 0;
    }

    /// <summary><paramref name="band"/> is the slot group the enemy is being created in
    /// (0 sky / 25 ground / 50 top / 75 ground2) — it only feeds the viewer's category
    /// tag, which must be stamped here because slots are recycled and would otherwise
    /// keep the previous occupant's category.</summary>
    private byte MakeEnemy(ref Enemy e, int eDatI, int uniqueShapeTableI, int band)
    {
        var dat = DatFor(eDatI);
        // Same classification the map view uses, so a category switched off in the layer
        // list hides the same things in both views (ObjectPlacer.Classify).
        e.objCat = (byte)ObjectPlacer.Classify(dat.Armor, dat.Value, band);

        int shapeTableI = uniqueShapeTableI > 0 ? uniqueShapeTableI : dat.ShapeBank;
        if (shapeTableI == 21) e.sheetSlot = -2;
        else if (shapeTableI == 26) e.sheetSlot = -3;
        else
        {
            for (int i = 0; i < 4; i++)
                if (SheetIdAt(i) == shapeTableI) { e.sheetSlot = i; break; }
            // otherwise keep the slot from the previous occupant (engine keeps the pointer)
        }

        e.mapoffset = 0;
        e.eshotmultipos[0] = e.eshotmultipos[1] = e.eshotmultipos[2] = 0;
        e.enemyground = (dat.ExplosionType & 1) == 0;
        e.explonum = dat.ExplosionType >> 1;
        e.launchfreq = dat.ELaunchFreq;
        e.launchwait = dat.ELaunchFreq;
        if (eDatI > 1000)
        {
            e.launchtype = dat.ELaunchType;
            e.launchspecial = 0;
        }
        else
        {
            e.launchtype = dat.ELaunchType % 1000;
            e.launchspecial = dat.ELaunchType / 1000;
        }
        e.xaccel = dat.XAccel;
        e.yaccel = dat.YAccel;
        e.xminbounce = -10000; e.xmaxbounce = 10000;
        e.yminbounce = -10000; e.ymaxbounce = 10000;
        e.tur[0] = dat.Tur0; e.tur[1] = dat.Tur1; e.tur[2] = dat.Tur2;
        e.ani = dat.Ani;
        e.animin = 1;
        switch (dat.Animate)
        {
            case 0: e.enemycycle = 1; e.aniactive = 0; e.animax = 0; e.aniwhenfire = 0; break;
            case 1: e.enemycycle = 0; e.aniactive = 1; e.animax = 0; e.aniwhenfire = 0; break;
            default: e.enemycycle = 1; e.aniactive = 2; e.animax = e.ani; e.aniwhenfire = 2; break;
        }
        if (dat.StartXC != 0)
            e.ex = dat.StartX + (int)(_rng.Next() % (uint)(Math.Abs(dat.StartXC) * 2)) - dat.StartXC + 1;
        else
            e.ex = dat.StartX + 1;
        if (dat.StartYC != 0)
            e.ey = dat.StartY + (int)(_rng.Next() % (uint)(Math.Abs(dat.StartYC) * 2)) - dat.StartYC + 1;
        else
            e.ey = dat.StartY + 1;
        e.exc = dat.XMove;
        e.eyc = dat.YMove;
        e.excc = dat.XCAccel;
        e.eycc = dat.YCAccel;
        e.exccw = Math.Abs(e.excc); e.exccwmax = e.exccw;
        e.eyccw = Math.Abs(e.eycc); e.eyccwmax = e.eyccw;
        e.exccadd = e.excc > 0 ? 1 : -1;
        e.eyccadd = e.eycc > 0 ? 1 : -1;
        e.special = false;
        e.iced = 0;
        e.exrev = dat.XRev == 0 ? 100 : dat.XRev == -99 ? 0 : dat.XRev;
        e.eyrev = dat.YRev == 0 ? 100 : dat.YRev == -99 ? 0 : dat.YRev;
        e.enemytype = eDatI;
        for (int i = 0; i < 3; i++)
        {
            e.eshotwait[i] = e.tur[i] == 252 ? (byte)1 : e.tur[i] > 0 ? (byte)20 : (byte)255;
        }
        for (int i = 0; i < 20; i++)
            e.egr[i] = dat.EGraphic != null ? dat.EGraphic[i] : (ushort)0;
        if (eDatI == 0) e.egr[0] = (ushort)_dat0Egr0;
        e.size = dat.Esize;
        e.linknum = 0;
        e.edamaged = dat.DAni < 0;
        e.enemydie = dat.EEnemyDie;
        e.freq[0] = dat.Freq0; e.freq[1] = dat.Freq1; e.freq[2] = dat.Freq2;
        e.edani = dat.DAni;
        e.edgr = dat.Dgr;
        e.edlevel = dat.DLevel;
        e.fixedmovey = 0;
        e.filter = 0x00;

        int value = dat.Value;
        if (value > 1 && value < 10000)
        {
            value = _s.difficultyLevel switch
            {
                <= 0 => (int)(dat.Value * 0.75f),
                1 or 2 => dat.Value,
                3 => (int)(dat.Value * 1.125f),
                4 => (int)(dat.Value * 1.5f),
                5 => dat.Value * 2,
                6 => (int)(dat.Value * 2.5f),
                7 or 8 => dat.Value * 4,
                _ => dat.Value * 8,
            };
            if (value > 10000) value = 10000;
        }
        e.evalue = value;

        byte avail;
        if (dat.Armor > 0)
        {
            int armor;
            if (dat.Armor != 255)
            {
                armor = _s.difficultyLevel switch
                {
                    <= 0 => (int)(dat.Armor * 0.5f + 1),
                    1 => (int)(dat.Armor * 0.75f + 1),
                    2 => dat.Armor,
                    3 => (int)(dat.Armor * 1.2f),
                    4 => (int)(dat.Armor * 1.5f),
                    5 => (int)(dat.Armor * 1.8f),
                    6 => dat.Armor * 2,
                    7 => dat.Armor * 3,
                    8 => dat.Armor * 4,
                    _ => dat.Armor * 8,
                };
                if (armor > 254) armor = 254;
            }
            else armor = 255;
            e.armorleft = B8(armor);
            avail = 0;
            e.scoreitem = false;
        }
        else
        {
            avail = 2;
            e.armorleft = 255;
            e.scoreitem = e.evalue != 0;
        }
        return avail;
    }

    private void CreateNewEventEnemy(int enemyTypeOfs, int enemyOffset, int uniqueShapeTableI)
    {
        _s.b = 0;
        for (int i = enemyOffset; i < enemyOffset + 25 && i < 100; i++)
        {
            if (_avail[i] == 1) { _s.b = i + 1; break; }
        }
        if (_s.b == 0) return;

        ref EventRec ev = ref _ev[_s.eventLoc - 1];
        int tempW = ev.Dat + enemyTypeOfs;
        ref var e = ref _enemy[_s.b - 1];
        _avail[_s.b - 1] = MakeEnemy(ref e, tempW, uniqueShapeTableI, enemyOffset);

        // T2000: -200 means one random X, rolled once and written back into the event
        if (ev.Dat2 == -200)
            ev.Dat2 = (short)(_rng.Next() % 208 + 24);

        if (ev.Dat2 != -99)
        {
            switch (enemyOffset)
            {
                case 0:
                    e.ex = ev.Dat2 - (_lv.MapX - 1) * 24;
                    e.ey -= _s.backMove2;
                    break;
                case 25:
                case 75:
                    e.ex = ev.Dat2 - (_lv.MapX - 1) * 24 - 12;
                    e.ey -= _s.backMove;
                    break;
                case 50:
                    if (_s.background3x1)
                        e.ex = ev.Dat2 - (_lv.MapX - 1) * 24 - 12;
                    else
                        e.ex = ev.Dat2 - _lv.MapX3 * 24 - 24 * 2 + 6;
                    e.ey -= _s.backMove3;
                    if (_s.background3x1b) e.ex -= 6;
                    break;
            }
            e.ey = -28;
            if (_s.background3x1b && enemyOffset == 50) e.ey += 4;
        }

        if (_s.smallEnemyAdjust && e.size == 0)
        {
            e.ex -= 10;
            e.ey -= 7;
        }

        e.ey += ev.Dat5;
        e.eyc = S8(e.eyc + ev.Dat3);
        e.linknum = ev.Dat4;
        e.fixedmovey = ev.Dat6;
    }

    private void EventJump(ushort jump)
    {
        if (jump == 65535) _s.curLoc = _s.returnLoc;
        else
        {
            _s.returnLoc = _s.curLoc + 1;
            _s.curLoc = jump;
        }
        int tw = 0;
        do { tw++; } while (tw <= _maxEvent && _ev[tw - 1].Time < _s.curLoc);
        _s.eventLoc = tw - 1;
        if (_s.eventLoc < 1) _s.eventLoc = 1;
    }

    private bool SearchFor(int plType, out int index)
    {
        index = -1;
        for (int i = 0; i < 100; i++)
            if (_avail[i] == 0 && _enemy[i].linknum == plType)
                index = i;
        return index != -1;
    }

    private bool GateEnemiesPresent(in EventRec gate)
    {
        if (gate.Dat2 == 0)
        {
            for (int link = 1; link <= 19; link++)
                if (SearchFor(link, out _)) return true;
            return false;
        }

        return SearchFor(gate.Dat2, out _) ||
               (gate.Dat3 != 0 && SearchFor(gate.Dat3, out _)) ||
               (gate.Dat4 != 0 && SearchFor(gate.Dat4, out _));
    }

    private void DefeatGateEnemies(in EventRec gate)
    {
        for (int i = 0; i < _enemy.Length; i++)
        {
            if (_avail[i] != 0) continue;
            int link = _enemy[i].linknum;
            bool match = gate.Dat2 == 0
                ? link is >= 1 and <= 19
                : link == gate.Dat2 || (gate.Dat3 != 0 && link == gate.Dat3) ||
                  (gate.Dat4 != 0 && link == gate.Dat4);
            if (match) DefeatPreviewEnemy(i);
        }
    }

    private void DefeatLink(int link)
    {
        for (int i = 0; i < _enemy.Length; i++)
            if (_avail[i] == 0 && _enemy[i].linknum == link)
                DefeatPreviewEnemy(i);
    }

    private void DefeatAllActiveEnemies()
    {
        for (int i = 0; i < _enemy.Length; i++)
            if (_avail[i] == 0)
                DefeatPreviewEnemy(i);
    }

    private void DefeatPreviewEnemy(int i)
    {
        ref Enemy e = ref _enemy[i];
        if (e.special && e.flagnum is >= 1 and <= 10)
            _s.globalFlags[e.flagnum - 1] = e.setto;
        _avail[i] = 1;
    }

    /// <summary>Find the enemy-test event that guards this authored backward jump.</summary>
    private int FindGateForLoop(int jumpIndex, ushort target)
    {
        int best = -1;
        ushort loopEnd = _ev[jumpIndex].Time;
        for (int i = 0; i < jumpIndex; i++)
        {
            ref EventRec candidate = ref _ev[i];
            if (candidate.Time < target || candidate.Time > loopEnd || candidate.Type != 70)
                continue;

            // A gate that exits beyond the rewind point is the controlling branch.
            ushort exit = unchecked((ushort)candidate.Dat);
            if (exit <= loopEnd || !GateEnemiesPresent(candidate)) continue;
            best = i;
            break;
        }
        return best;
    }

    private bool LoopContainsReadyEnd(int jumpIndex, ushort target)
    {
        for (int i = 0; i < jumpIndex; i++)
            if (_ev[i].Time >= target && _ev[i].Time <= _ev[jumpIndex].Time &&
                _ev[i].Type == 36)
                return true;
        return false;
    }

    private bool HasFutureEnd(int eventIndex)
    {
        for (int i = eventIndex + 1; i < _maxEvent; i++)
            if (_ev[i].Type is 11 or 36)
                return true;
        return false;
    }

    /// <summary>
    /// Cycles 1..PreviewLoopCycles only note when they ended; the release pass
    /// (PreviewLoopCycles + 1) turns those marks into the retained loop region — it starts
    /// where cycle 1 ended and closes on this tick, so it spans PreviewLoopCycles repeats.
    /// </summary>
    private void RecordScriptedGateCycle(
        int eventIndex, int cycle, PreviewKind kind = PreviewKind.EnemyLoop)
    {
        int slot = eventIndex * PreviewLoopCycles;
        if (cycle >= 1 && cycle <= PreviewLoopCycles)
        {
            _gateLoopTicks[slot + cycle - 1] = LogTick;
            return;
        }
        if (cycle != PreviewLoopCycles + 1) return;

        var cycleEnds = new int[PreviewLoopCycles];
        for (int n = 1; n < PreviewLoopCycles; n++) cycleEnds[n - 1] = _gateLoopTicks[slot + n];
        cycleEnds[^1] = LogTick;
        GatePreviewLog?.Add(new GatePreview(
            _gateLoopTicks[slot], LogTick, cycleEnds, kind, eventIndex));
    }

    private void PreviewQuietEnemyHold()
    {
        if (!PreviewEnemyGates || _s.enemyOnScreen == 0 || _s.previewGateTimerPause)
            return;

        bool blocksProgress = _s.stopBackgrounds || _s.stopBackgroundNum is > 0 and < 9 ||
                              _s.readyToEndLevel || _s.backMove == 0;
        const int holdTicks = (int)(20 * TicksPerSecond);
        if (!blocksProgress || _s.tickCount - _s.previewLastEventTick < holdTicks)
            return;

        int start = Math.Max(1, _s.tickCount - holdTicks);
        GatePreviewLog?.Add(new GatePreview(start, _s.tickCount, Array.Empty<int>(),
            PreviewKind.EnemyHold, EventIndex: -1));

        bool jump254 = _s.superEnemy254Jump > _s.curLoc && SearchFor(254, out _);
        DefeatAllActiveEnemies();
        if (_s.backMove == 0)
        {
            _s.backMove = 1;
            _s.backMove2 = 2;
            _s.backMove3 = 3;
            _s.explodeMove = 2;
            _s.stopBackgrounds = false;
            _s.stopBackgroundNum = 0;
        }
        _s.previewLastEventTick = _s.tickCount;
        if (jump254) EventJump((ushort)_s.superEnemy254Jump);
    }

    // =====================================================================
    //  Event system (JE_eventSystem) 窶・all sim-relevant event types.
    // =====================================================================
    private void EventSystem()
    {
        ref EventRec ev = ref _ev[_s.eventLoc - 1];
        int logIndex = _s.eventLoc - 1;
        int prevLoc = _s.curLoc;
        _s.previewLastEventTick = _s.tickCount;

        switch (ev.Type)
        {
            case 1:
                _s.starfieldSpeed = ev.Dat;
                break;

            case 2:
                _s.map1YDelay = 1; _s.map1YDelayMax = 1;
                _s.map2YDelay = 1; _s.map2YDelayMax = 1;
                _s.backMove = ev.Dat;
                _s.backMove2 = ev.Dat2;
                _s.explodeMove = _s.backMove2 > 0 ? _s.backMove2 : _s.backMove;
                _s.backMove3 = ev.Dat3;
                if (_s.backMove > 0) _s.stopBackgroundNum = 0;
                break;

            case 3:
                _s.backMove = 1;
                _s.map1YDelay = 3; _s.map1YDelayMax = 3;
                _s.backMove2 = 1;
                _s.map2YDelay = 2; _s.map2YDelayMax = 2;
                _s.backMove3 = 1;
                break;

            case 4:   // map stop (released when the armed band empties)
            case 83:
                _s.stopBackgrounds = true;
                _s.stopBackgroundNum = ev.Dat switch { 0 or 1 => 1, 2 => 2, 3 => 3, _ => _s.stopBackgroundNum };
                break;

            case 5:   // load enemy shape banks (dat<=0 frees the slot, like the engine)
                _s.sheetId0 = Math.Max(0, (int)ev.Dat);
                _s.sheetId1 = Math.Max(0, (int)ev.Dat2);
                _s.sheetId2 = Math.Max(0, (int)ev.Dat3);
                _s.sheetId3 = Math.Max(0, (int)ev.Dat4);
                break;

            case 6: CreateNewEventEnemy(0, 25, 0); break;
            case 7: CreateNewEventEnemy(0, 50, 0); break;

            case 8: _s.starActive = false; break;
            case 9: _s.starActive = true; break;

            case 10: CreateNewEventEnemy(0, 75, 0); break;

            case 11:
                _s.endLevel = true;
                _s.finished = true;
                break;

            case 12:
            {
                int band = ev.Dat6 switch { 2 => 0, 3 => 50, 4 => 75, _ => 25 };
                ev.Dat6 = 0;   // engine reuses eventdat6 for the band, then clears it
                CreateNewEventEnemy(0, band, 0);
                CreateNewEventEnemy(1, band, 0);
                if (_s.b > 0) _enemy[_s.b - 1].ex += 24;
                CreateNewEventEnemy(2, band, 0);
                if (_s.b > 0) _enemy[_s.b - 1].ey -= 28;
                CreateNewEventEnemy(3, band, 0);
                if (_s.b > 0) { _enemy[_s.b - 1].ex += 24; _enemy[_s.b - 1].ey -= 28; }
                break;
            }

            case 13: _s.enemiesActive = false; break;
            case 14: _s.enemiesActive = true; break;

            case 15: CreateNewEventEnemy(0, 0, 0); break;

            case 16: break;   // text window (not shown)

            case 17:
                CreateNewEventEnemy(0, 25, 0);
                if (_s.b > 0) _enemy[_s.b - 1].ey = 190 + ev.Dat5;
                break;

            case 18:
                CreateNewEventEnemy(0, 0, 0);
                if (_s.b > 0) _enemy[_s.b - 1].ey = 190 + ev.Dat5;
                break;

            case 19:  // Enemy Global Move
            {
                int initial = 0, max = 100;
                bool all = false;
                if (ev.Dat3 > 79 && ev.Dat3 < 90)
                    ev.Dat4 = _s.newPL[ev.Dat3 - 80];
                else
                {
                    switch (ev.Dat3)
                    {
                        case 0: initial = 0; max = 100; all = false; break;
                        case 2: initial = 0; max = 25; all = true; break;
                        case 1: initial = 25; max = 50; all = true; break;
                        case 3: initial = 50; max = 75; all = true; break;
                        case 99: initial = 0; max = 100; all = true; break;
                    }
                }
                for (int i = initial; i < max; i++)
                {
                    if (!all && _enemy[i].linknum != ev.Dat4) continue;
                    if (ev.Dat != -99) _enemy[i].exc = S8(ev.Dat);
                    if (ev.Dat2 != -99) _enemy[i].eyc = S8(ev.Dat2);
                    if (ev.Dat6 != 0) _enemy[i].fixedmovey = ev.Dat6;
                    if (ev.Dat6 == -99) _enemy[i].fixedmovey = 0;
                    if (ev.Dat5 > 0) _enemy[i].enemycycle = B8(ev.Dat5);
                }
                break;
            }

            case 20:  // Enemy Global Accel
                if (ev.Dat3 > 79 && ev.Dat3 < 90)
                    ev.Dat4 = _s.newPL[ev.Dat3 - 80];
                for (int i = 0; i < 100; i++)
                {
                    if (_avail[i] == 1 || (_enemy[i].linknum != ev.Dat4 && ev.Dat4 != 0)) continue;
                    if (ev.Dat != -99)
                    {
                        _enemy[i].excc = S8(ev.Dat);
                        _enemy[i].exccw = Math.Abs(ev.Dat);
                        _enemy[i].exccwmax = Math.Abs(ev.Dat);
                        _enemy[i].exccadd = ev.Dat > 0 ? 1 : -1;
                    }
                    if (ev.Dat2 != -99)
                    {
                        _enemy[i].eycc = S8(ev.Dat2);
                        _enemy[i].eyccw = Math.Abs(ev.Dat2);
                        _enemy[i].eyccwmax = Math.Abs(ev.Dat2);
                        _enemy[i].eyccadd = ev.Dat2 > 0 ? 1 : -1;
                    }
                    if (ev.Dat5 > 0) _enemy[i].enemycycle = B8(ev.Dat5);
                    if (ev.Dat6 > 0)
                    {
                        _enemy[i].ani = ev.Dat6;
                        _enemy[i].animin = ev.Dat5;
                        _enemy[i].animax = 0;
                        _enemy[i].aniactive = 1;
                    }
                }
                break;

            case 21: _s.background3over = 1; break;
            case 22: _s.background3over = 0; break;

            case 23:
                CreateNewEventEnemy(0, 50, 0);
                if (_s.b > 0) _enemy[_s.b - 1].ey = 180 + ev.Dat5;
                break;

            case 24:  // Enemy Global Animate
                for (int i = 0; i < 100; i++)
                {
                    if (_enemy[i].linknum != ev.Dat4) continue;
                    _enemy[i].aniactive = 1;
                    _enemy[i].aniwhenfire = 0;
                    if (ev.Dat2 > 0)
                    {
                        _enemy[i].enemycycle = B8(ev.Dat2);
                        _enemy[i].animin = _enemy[i].enemycycle;
                    }
                    else _enemy[i].enemycycle = 0;
                    if (ev.Dat > 0) _enemy[i].ani = ev.Dat;
                    if (ev.Dat3 == 1) _enemy[i].animax = _enemy[i].ani;
                    else if (ev.Dat3 == 2)
                    {
                        _enemy[i].aniactive = 2;
                        _enemy[i].animax = _enemy[i].ani;
                        _enemy[i].aniwhenfire = 2;
                    }
                }
                break;

            case 25:  // Enemy Global Damage change
                for (int i = 0; i < 100; i++)
                    if (ev.Dat4 == 0 || _enemy[i].linknum == ev.Dat4)
                        _enemy[i].armorleft = B8(ev.Dat);
                break;

            case 26: _s.smallEnemyAdjust = ev.Dat != 0; break;

            case 27:  // Enemy Global AccelRev
                if (ev.Dat3 > 79 && ev.Dat3 < 90)
                    ev.Dat4 = _s.newPL[ev.Dat3 - 80];
                for (int i = 0; i < 100; i++)
                {
                    if (ev.Dat4 != 0 && _enemy[i].linknum != ev.Dat4) continue;
                    if (ev.Dat != -99) _enemy[i].exrev = ev.Dat;
                    if (ev.Dat2 != -99) _enemy[i].eyrev = ev.Dat2;
                    if (ev.Dat3 != 0 && ev.Dat3 < 17) _enemy[i].filter = B8(ev.Dat3);
                }
                break;

            case 28: _s.topEnemyOver = false; break;
            case 29: _s.topEnemyOver = true; break;

            case 30:
                _s.map1YDelay = 1; _s.map1YDelayMax = 1;
                _s.map2YDelay = 1; _s.map2YDelayMax = 1;
                _s.backMove = ev.Dat;
                _s.backMove2 = ev.Dat2;
                _s.explodeMove = _s.backMove2;
                _s.backMove3 = ev.Dat3;
                break;

            case 31:  // Enemy Fire Override
                for (int i = 0; i < 100; i++)
                {
                    if (ev.Dat4 != 99 && _enemy[i].linknum != ev.Dat4) continue;
                    _enemy[i].freq[0] = B8(ev.Dat);
                    _enemy[i].freq[1] = B8(ev.Dat2);
                    _enemy[i].freq[2] = B8(ev.Dat3);
                    for (int k = 0; k < 3; k++) _enemy[i].eshotwait[k] = 1;
                    if (_enemy[i].launchtype > 0)
                    {
                        _enemy[i].launchfreq = ev.Dat5;
                        _enemy[i].launchwait = 1;
                    }
                }
                break;

            case 32:
                CreateNewEventEnemy(0, 50, 0);
                if (_s.b > 0) _enemy[_s.b - 1].ey = 190;
                break;

            case 33:  // Enemy From other Enemies
            {
                const int lives = 1;
                if (ev.Dat == 533 && _rng.Next() % 15 < lives)
                    ev.Dat = (short)(829 + _rng.Next() % 6);
                for (int i = 0; i < 100; i++)
                    if (_enemy[i].linknum == ev.Dat4)
                        _enemy[i].enemydie = ev.Dat;
                break;
            }

            case 34: break;  // music fade
            case 35: break;  // play song

            case 36: _s.readyToEndLevel = true; break;

            case 37: _s.levelEnemyFrequency = ev.Dat; break;

            case 38:
            {
                ushort target = unchecked((ushort)ev.Dat);
                if (PreviewEnemyGates && target < ev.Time)
                {
                    int gateEvent = FindGateForLoop(logIndex, target);
                    if (gateEvent >= 0)
                    {
                        int cycle = Math.Min(PreviewLoopCycles + 1, (int)++_gateLoopVisits[logIndex]);
                        _s.previewGateTimerPause = true;
                        RecordScriptedGateCycle(logIndex, cycle);
                        if (cycle == PreviewLoopCycles + 1)
                        {
                            _gateLoopVisits[logIndex] = ushort.MaxValue;
                            _releaseGateEvent = gateEvent;
                        }
                    }
                }

                _s.curLoc = target;
                int newLoc = 1;
                for (int t = 0; t < _maxEvent; t++)
                    if (_ev[t].Time <= _s.curLoc)
                        newLoc = t;
                _s.eventLoc = Math.Max(1, newLoc);
                break;
            }

            case 39:  // Enemy Global Linknum Change
                for (int i = 0; i < 100; i++)
                    if (_enemy[i].linknum == ev.Dat)
                        _enemy[i].linknum = ev.Dat2;
                break;

            case 40: _s.enemyContinualDamage = true; break;

            case 41:
                if (ev.Dat == 0) Array.Fill(_avail, (byte)1);
                else for (int i = 0; i <= 24; i++) _avail[i] = 1;
                break;

            case 42: _s.background3over = 2; break;
            case 43: _s.background2over = B8(ev.Dat); break;

            case 44:
                // levelFilter/levelFilterNew are JE_shortint (signed char): dat4=157
                // wraps to -99 = "no hue" — keeping it unsigned paints the whole
                // screen one colour where the game shows only a brightness fade.
                _s.filterActive = ev.Dat > 0;
                _s.filterFade = ev.Dat == 2;
                _s.levelFilter = S8(ev.Dat2);
                _s.levelBrightness = ev.Dat3;
                _s.levelFilterNew = S8(ev.Dat4);
                _s.levelBrightnessChg = ev.Dat5;
                _s.filterFadeStart = ev.Dat6 == 0;
                break;

            case 45:  // arcade-only enemy from other enemies
            {
                const int lives = 1;
                if (ev.Dat == 533 && _rng.Next() % 15 < lives)
                    ev.Dat = (short)(829 + _rng.Next() % 6);
                // twoPlayerMode/onePlayerAction are false: no enemydie changes
                break;
            }

            case 46:
                if (ev.Dat2 == 0)
                {
                    _s.difficultyLevel += ev.Dat;
                    if (_s.difficultyLevel < 1) _s.difficultyLevel = 1;
                    if (_s.difficultyLevel > 10) _s.difficultyLevel = 10;
                }
                break;

            case 47:
                for (int i = 0; i < 100; i++)
                    if (ev.Dat4 == 0 || _enemy[i].linknum == ev.Dat4)
                        _enemy[i].armorleft = B8(ev.Dat);
                break;

            case 48: _s.background2notTransparent = true; break;

            case 49:
            case 50:
            case 51:
            case 52:
            {
                int tempDat2 = ev.Dat; ev.Dat = 0;
                int tempDat = ev.Dat3; ev.Dat3 = 0;
                int tempDat3 = ev.Dat6; ev.Dat6 = 0;
                _dat0Armor = tempDat3;   // enemyDat[0] stays mutated, like the engine
                _dat0Egr0 = tempDat2;
                int band = (ev.Type - 48) switch { 1 => 25, 2 => 0, 3 => 50, _ => 75 };
                CreateNewEventEnemy(0, band, tempDat);
                ev.Dat = (short)tempDat2;
                ev.Dat3 = S8(tempDat);
                ev.Dat6 = S8(tempDat3);
                break;
            }

            case 53: _s.forceEvents = ev.Dat != 99; break;

            case 54:
            {
                ushort target = unchecked((ushort)ev.Dat);
                int gateEvent = -1;
                bool super254Gate = false;
                bool finalReadyGate = false;
                bool routeLoop = false;
                if (PreviewEnemyGates && target < ev.Time)
                {
                    gateEvent = FindGateForLoop(logIndex, target);
                    super254Gate = gateEvent < 0 && _s.superEnemy254Jump > ev.Time &&
                                       SearchFor(254, out _);
                    finalReadyGate = gateEvent < 0 && !super254Gate &&
                                     (_s.readyToEndLevel || LoopContainsReadyEnd(logIndex, target));
                    routeLoop = gateEvent < 0 && !super254Gate && !finalReadyGate &&
                                HasFutureEnd(logIndex);
                }

                if (gateEvent >= 0 || super254Gate || finalReadyGate || routeLoop)
                {
                    int cycle = Math.Min(PreviewLoopCycles + 1, (int)++_gateLoopVisits[logIndex]);
                    if (!routeLoop) _s.previewGateTimerPause = true;
                    RecordScriptedGateCycle(logIndex, cycle,
                        routeLoop ? PreviewKind.RouteLoop : PreviewKind.EnemyLoop);
                    if (cycle == PreviewLoopCycles + 1)
                    {
                        _gateLoopVisits[logIndex] = ushort.MaxValue;
                        if (super254Gate)
                        {
                            DefeatLink(254);
                            _s.previewGateTimerPause = false;
                            EventJump((ushort)_s.superEnemy254Jump);
                            break;
                        }

                        if (finalReadyGate)
                        {
                            DefeatAllActiveEnemies();
                            _s.readyToEndLevel = true;
                            _s.endLevel = true;
                            _s.finished = true;
                            _s.previewGateTimerPause = false;
                            break;
                        }

                        if (routeLoop)
                        {
                            _s.previewGateTimerPause = false;
                            break;
                        }

                        // One final rewind lands at the authored gate. On that pass,
                        // take its success branch as though the player killed the boss.
                        _releaseGateEvent = gateEvent;
                    }
                }

                EventJump(target);
                break;
            }

            case 55:
                if (ev.Dat3 > 79 && ev.Dat3 < 90)
                    ev.Dat4 = _s.newPL[ev.Dat3 - 80];
                for (int i = 0; i < 100; i++)
                {
                    if (ev.Dat4 != 0 && _enemy[i].linknum != ev.Dat4) continue;
                    if (ev.Dat != -99) _enemy[i].xaccel = ev.Dat;
                    if (ev.Dat2 != -99) _enemy[i].yaccel = ev.Dat2;
                }
                break;

            case 56:
                CreateNewEventEnemy(0, 75, 0);
                if (_s.b > 0) _enemy[_s.b - 1].ey = 190;
                break;

            case 57: _s.superEnemy254Jump = unchecked((ushort)ev.Dat); break;

            case 58:  // Set enemy launch
                for (int i = 0; i < 100; i++)
                    if (ev.Dat4 == 99 || _enemy[i].linknum == ev.Dat4)
                        _enemy[i].launchtype = unchecked((ushort)ev.Dat);
                break;

            case 59:  // Replace enemy
            case 68:
            {
                int eDatI = unchecked((ushort)ev.Dat);
                for (int i = 0; i < 100; i++)
                {
                    if (!(ev.Dat4 == 0 || _enemy[i].linknum == ev.Dat4)) continue;
                    int offset = i - i % 25;
                    int nb = NewEnemy(offset, eDatI, 0);
                    if (nb != 0)
                    {
                        _enemy[nb - 1].ex = _enemy[i].ex;
                        _enemy[nb - 1].ey = _enemy[i].ey;
                    }
                    _avail[i] = 1;
                }
                break;
            }

            case 60:  // Assign Special Enemy
                for (int i = 0; i < 100; i++)
                {
                    if (_enemy[i].linknum != ev.Dat4) continue;
                    _enemy[i].special = true;
                    _enemy[i].flagnum = ev.Dat;
                    _enemy[i].setto = ev.Dat2 == 1;
                }
                break;

            case 61:
                if (ev.Dat >= 1 && ev.Dat <= 10 && _s.globalFlags[ev.Dat - 1] == (ev.Dat2 != 0))
                    _s.eventLoc += ev.Dat3;
                break;

            case 62: break;  // sound effect

            case 63:  // skip events if not 2-player
                _s.eventLoc += ev.Dat;
                break;

            case 64:
                if (ev.Dat >= 1 && ev.Dat <= 9)
                {
                    _s.smoothies[ev.Dat - 1] = B8(ev.Dat2);
                    int t = ev.Dat == 5 ? 3 : ev.Dat;
                    _s.smoothieData[t - 1] = B8(ev.Dat3);
                }
                break;

            case 65: _s.background3x1 = ev.Dat == 0; break;

            case 66:  // skip if difficulty at/below
                if (Difficulty <= ev.Dat)
                    _s.eventLoc += ev.Dat2;
                break;

            case 67:
                _s.levelTimer = ev.Dat == 1;
                _s.levelTimerCountdown = ev.Dat3 * 100;
                _s.levelTimerJumpTo = unchecked((ushort)ev.Dat2);
                break;

            case 69: break;  // invulnerability

            case 70:
                if (PreviewEnemyGates && _releaseGateEvent == logIndex)
                {
                    DefeatGateEnemies(ev);
                    _releaseGateEvent = -1;
                    _s.previewGateTimerPause = false;
                    EventJump(unchecked((ushort)ev.Dat));
                    break;
                }
                if (ev.Dat2 == 0)
                {
                    bool found = false;
                    for (int t = 1; t <= 19; t++) found = found || SearchFor(t, out _);
                    if (!found) EventJump(unchecked((ushort)ev.Dat));
                }
                else if (!SearchFor(ev.Dat2, out _) &&
                         (ev.Dat3 == 0 || !SearchFor(ev.Dat3, out _)) &&
                         (ev.Dat4 == 0 || !SearchFor(ev.Dat4, out _)))
                {
                    EventJump(unchecked((ushort)ev.Dat));
                }
                break;

            case 71:
                if ((uint)(_s.mapYPos * 2) <= (uint)ev.Dat2)
                    EventJump(unchecked((ushort)ev.Dat));
                break;

            case 72: _s.background3x1b = ev.Dat == 1; break;
            case 73: _s.skyEnemyOverAll = ev.Dat == 1; break;

            case 74:  // Enemy Global BounceParams
                for (int i = 0; i < 100; i++)
                {
                    if (ev.Dat4 != 0 && _enemy[i].linknum != ev.Dat4) continue;
                    if (ev.Dat5 != -99) _enemy[i].xminbounce = ev.Dat5;
                    if (ev.Dat6 != -99) _enemy[i].yminbounce = ev.Dat6;
                    if (ev.Dat != -99) _enemy[i].xmaxbounce = ev.Dat;
                    if (ev.Dat2 != -99) _enemy[i].ymaxbounce = ev.Dat2;
                }
                break;

            case 75:
            {
                bool anyCandidate = false;
                for (int i = 0; i < 100; i++)
                    if (_avail[i] == 0 && _enemy[i].eyc == 0 &&
                        _enemy[i].linknum >= ev.Dat && _enemy[i].linknum <= ev.Dat2)
                        anyCandidate = true;

                int slot = ev.Dat3 - 80;
                if (slot < 0 || slot > 9) break;
                if (anyCandidate)
                {
                    int pick = 0, guard = 0;
                    while (guard++ < 100000)
                    {
                        pick = (int)(_rng.Next() % (uint)(ev.Dat2 + 1 - ev.Dat)) + ev.Dat;
                        if (SearchFor(pick, out int ei) && _enemy[ei].eyc == 0) break;
                    }
                    _s.newPL[slot] = B8(pick);
                }
                else
                {
                    _s.newPL[slot] = 255;
                    if (ev.Dat4 > 0)
                    {
                        int idx = _s.eventLoc - 1 + ev.Dat4;
                        if (idx >= 0 && idx < _ev.Length)
                            _s.curLoc = _ev[idx].Time - 1;
                        _s.eventLoc += ev.Dat4 - 1;
                    }
                }
                break;
            }

            case 76: _s.returnActive = true; break;

            case 77:
                _s.mapYPos = ev.Dat / 2;
                _s.mapY2Pos = (ev.Dat2 > 0 ? ev.Dat2 : ev.Dat) / 2;
                break;

            case 78: break;  // galaga shot freq

            case 79:
                _s.bossLink0 = ev.Dat;
                _s.bossLink1 = ev.Dat2;
                break;

            case 80: break;  // skip if 2-player: not

            case 81:
                _s.bkWrap2 = ev.Dat / 2;
                _s.bkWrap2to = ev.Dat2 / 2;
                break;

            case 82: break;  // give special weapon
            case 84: break;  // timed battle timer (not timed battle)
            case 85: break;  // timed battle enemydie

            case 99: _s.randomExplosions = ev.Dat == 1; break;

            default:
                break;
        }

        EventLog?.Add(new EventExec(LogTick, ev.Type, logIndex, _s.curLoc < prevLoc));
        _s.eventLoc++;
    }
}
