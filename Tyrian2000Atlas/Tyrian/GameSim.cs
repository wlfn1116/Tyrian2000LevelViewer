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
    /// <summary>Widescreen playfield crop width: the widescreen build widens vga_width to
    /// 356 and crops PLAYFIELD_WIDTH = 356 - HUD 57 = 299 (video.h / notes.md §Widescreen).
    /// <see cref="ViewW"/> is the vanilla 264; <see cref="PlayfieldWidth"/> picks between
    /// them per <see cref="Widescreen"/>. ViewX (PLAYFIELD_LEFT crop) stays 24 either way.</summary>
    public const int WideViewW = 299;
    /// <summary>Background/object horizontal phase in widescreen (video.h PLAYFIELD_X_SHIFT):
    /// every background row AND every enemy/shot tempMapXOfs shifts left by this, so terrain and
    /// objects stay locked together as the playfield widens (the widescreen build applies it in
    /// backgrnd.c and to every JE_drawEnemy pass). Vanilla applies no shift. notes.md §Widescreen.</summary>
    private const int PlayfieldXShift = -12;

    // Widescreen gameplay/cull bounds, mirrored from the widescreen source's video.h macros
    // (PLAYFIELD_LEFT=24, PLAYFIELD_WIDTH=299, PLAYFIELD_RIGHT=322, vga_width=356). Each is
    // used only when Widescreen is set; the vanilla path keeps its own literals unchanged, so
    // normal playback stays byte-for-byte identical. The widescreen author deliberately
    // retuned several of these (not a pure width shift) — see tyrian2.c/shots.c comments.
    private const int WsDrawGateL  = -28;  // enemy draw/animate gate, left   (vanilla -29)
    private const int WsDrawGateR  = 360;  // enemy draw/animate gate, right = PLAYFIELD_RIGHT+38 (vanilla 300)
    private const int WsEnemyCullR = 376;  // enemy despawn X                 = vga_width+20      (vanilla 340)
    private const int WsScoreParkL = 19;   // score-item park, left           = PLAYFIELD_LEFT-5  (vanilla -5)
    private const int WsScoreParkR = 305;  // score-item park, right          = PLAYFIELD_RIGHT-17 (vanilla 245)
    private const int WsOnScreenR  = 332;  // enemyOnScreen count gate, right = vga_width-24      (vanilla 296)
    private const int WsShotCullR  = 335;  // enemy-shot despawn X            = PLAYFIELD_RIGHT+13 (vanilla 275)
    // Extended render buffer: the vanilla surface plus margins so the view can zoom out
    // and show terrain/enemies before they reach the screen. Vanilla (x, y) lives at
    // buffer (x + OX, y + OY).
    public const int OX = 72, OY = 128;
    public const int BufW = 320 + OX + 72, BufH = 200 + OY + 32;  // 464 x 360
    public const double TicksPerSecond = 35.0;
    /// <summary>How many repetitions of a player-gated (boss) loop the preview keeps
    /// before it continues as though the gate was cleared. The loop body still plays
    /// once on the way in, so the retained region spans this many marked cycles.
    /// Settable per run (the viewer exposes it); a change needs a rebuild.</summary>
    public int PreviewLoopCycles = 2;
    /// <summary>How long (seconds) an enemy-gated hold — a stopped map with live enemies and
    /// no further events, which no script will ever release — is watched before the preview
    /// destroys those enemies and moves on. Also the length of the retained hold region, and
    /// the tail <see cref="SimPlayback"/> keeps when it finds the same stall without a gate.
    /// Settable per run (the viewer exposes it); a change needs a rebuild.</summary>
    public int PreviewHoldSeconds = 20;

    // --- static level data (not part of snapshots) ---
    private readonly Level _lv;
    private readonly EnemyData _ed;
    private readonly WeaponData _wd;
    private readonly GameData _gd;
    private readonly byte[]?[][] _map = new byte[]?[3][];   // [layer][row*cols] -> 672b tile
    private readonly int _maxEvent;
    private readonly CompShapes? _explosionSheet;
    /// <summary>Last authored event time that addresses each link number, so the parked-above
    /// recycler below can tell a structure the script is still staging from one it has
    /// abandoned. Index 0 is unused: dat4 0 means "no link" and appears on most events.</summary>
    private readonly int[] _lastLinkEvent = new int[256];

    // --- sim parameters (fixed per run; changing them requires a rebuild) ---
    public int Difficulty = 2;          // 0 wimp .. 10; 2 = normal
    /// <summary>The engine's galagaMode, set by a ']g' in the episode script — only
    /// ** ALE ** (ep1 #18) and SQUADRON (ep4 #18) carry one. It rewrites enemy fire
    /// wholesale (see <see cref="FireTurrets"/>) and pins the difficulty at NORMAL, so
    /// without it those two levels play with normal turret rates and are far more hostile
    /// than the real game. Defaulted from the level in the constructor; a sim parameter,
    /// so overriding it needs a rebuild.</summary>
    public bool GalagaMode;
    public float ScrollMult = 1f;       // terrain speed what-if multiplier
    public bool FireEnabled = true;     // simulate enemy turrets
    /// <summary>Set to have the run report what it throws at the player — see
    /// <see cref="LevelThreat"/>. Null (the default) costs the simulation nothing; the analysis
    /// window attaches one for the length of a measuring run and detaches it again.</summary>
    public LevelThreat? Threat;
    public bool PreviewEnemyGates = true; // keep PreviewLoopCycles boss loops, then continue as if defeated
    public int PlayerX = 100, PlayerY = 180;   // phantom player (aim/chase target)
    public uint RngSeed = 5489;
    /// <summary>True-widescreen playback, mirroring the widescreen game build: the playfield
    /// widens from 264 to 299px and the player range, parallax, spotlight and enemy/shot cull
    /// bounds all follow, exactly as the widescreen source derives them (notes.md §Widescreen).
    /// A sim parameter — it changes the simulation, so a change requires a rebuild. Off =
    /// vanilla, byte-for-byte (the one exception being <see cref="WideStarfield"/>, which is
    /// offered in both modes).</summary>
    public bool Widescreen;
    /// <summary>Widescreen-only "Extra Parallax" (OpenTyrian2000-Engaged commits edd8118 +
    /// ae13d1c): the near terrain layer pans across EXACTLY its 336px map — mapXOfs sweeps 36
    /// (far-left flush) to -1 (far-right flush), normalized over the ship's actual travel — so
    /// a strafe runs it edge to edge with 0px spilling off either side; the mid/deep layers keep
    /// the original coupled 4:2:1 ratio and intentionally over-pan/uncover their edges at
    /// far-left. Bound ground enemies ride the same offsets, so they slide much further too.
    /// Only meaningful with <see cref="Widescreen"/>; a sim parameter, so a change requires a
    /// rebuild.</summary>
    public bool ExpandedParallax;
    /// <summary>Run the widescreen build's rewritten starfield instead of vanilla's. Off
    /// restores the original 100 stars on a 16-bit linear position, which stop seven rows above
    /// the playfield bottom (at either width) and never reach the widened right edge. A sim
    /// parameter, not a draw option: the two fields hold different state and draw a different
    /// number of stars from the level's RNG at init, so switching needs a rebuild — and shifts
    /// enemy spawns, exactly as the difference does between the real builds. Unlike
    /// <see cref="ExpandedParallax"/> / <see cref="MirrorLayers"/> this is NOT gated on
    /// <see cref="Widescreen"/>: the bug it fixes is just as visible at the vanilla width. It is
    /// therefore the one setting that can make vanilla playback differ from the stock engine —
    /// leave it off for a byte-for-byte vanilla run.</summary>
    public bool WideStarfield = true;
    /// <summary>Playfield crop width in effect: <see cref="WideViewW"/> (299) in widescreen,
    /// else vanilla <see cref="ViewW"/> (264). Drives the display crop, screen filter, flip
    /// and spotlight geometry.</summary>
    public int PlayfieldWidth => Widescreen ? WideViewW : ViewW;
    /// <summary>The widescreen build's surface width (video.h vga_width). The playfield's
    /// right edge (PLAYFIELD_RIGHT = 322) lives past the vanilla 320, so anything the engine
    /// walks per scanline has to run this far.</summary>
    private const int VgaWidth = 356;
    /// <summary>Width of the engine surface the frame is composed on: <see cref="VgaWidth"/>
    /// in widescreen, else the vanilla <see cref="ScreenW"/>. Everything the engine derives
    /// from surface->w / surface->pitch follows it — the smoothie filters, the mirrored-layer
    /// edge strip and the starfield's spread.</summary>
    public int SurfaceWidth => Widescreen ? VgaWidth : ScreenW;

    // --- view options (draw-only; safe to change without a rebuild + redraw) ---
    public bool ExtendedDraw;           // render beyond the vanilla screen bounds
    public bool ShowScreenFilter = true;   // event-44 hue/brightness filter
    public bool ShowTerrainSmoothies = true; // lava/water/ice/blur feedback filters
    public bool ShowSpotlight = true;       // JE_starShowVGA light cone
    public bool ShowScreenFlip = true;      // JE_starShowVGA vertical flip
    public bool ShowBossBars = true;        // draw_boss_bar armor readouts

    /// <summary>Widescreen-only "Mirrored Layers" (commit 1f7ba83): background columns panned
    /// past a layer's side edge re-read the same row's edge columns in reflected order and draw
    /// horizontally FLIPPED, so the layer continues past its edge as a seamless mirror image
    /// (mirrored-repeat) instead of wrapping into the adjacent map row. Independent of
    /// <see cref="ExpandedParallax"/> — the stock widescreen span already uncovers ~12px of
    /// layer 3 at far-left. Draw-only (a redraw suffices; no rebuild). App gates it on
    /// <see cref="Widescreen"/>.</summary>
    public bool MirrorLayers;

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
            bool onScreen = Widescreen ? (sx > WsDrawGateL && sx < WsDrawGateR)
                                       : (sx > -29 && sx < 300);
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

    /// <summary>
    /// Which slots hold a live enemy right now, drawn this frame or not. Unlike
    /// <see cref="CollectEnemies"/> this says nothing about what is visible — it is the raw
    /// occupancy, which is what a viewer needs to follow one particular spawn across the
    /// frames after it, before any of it has reached the playfield.
    /// </summary>
    public void CollectLiveSlots(HashSet<int> into)
    {
        into.Clear();
        for (int i = 0; i < _avail.Length; i++)
            if (_avail[i] == 0) into.Add(i);
    }

    /// <summary>
    /// Hand the frame just simulated to the attached <see cref="Threat"/>: how much enemy fire
    /// is in the air, how many enemies are on screen, and how much destructible armour is
    /// standing in front of the player. Called by the measuring driver after each completed
    /// tick; a no-op when nothing is measuring.
    /// </summary>
    /// <summary>Armour at which a destructible enemy counts half as much as an indestructible
    /// one in the presence tally. Roughly what a mid-campaign ship clears in the moment an
    /// enemy spends in front of it: below this the thing is gone before it matters, well above
    /// it the thing is effectively scenery that happens to be shootable.</summary>
    private const float Stickiness = 25f;

    public void SampleThreat()
    {
        var t = Threat;
        if (t == null) return;

        int bullets = 0;
        for (int z = 0; z < _shotAvail.Length; z++)
            if (_shotAvail[z] == 0) bullets++;

        int armor = 0, hulks = 0;
        float presence = 0;
        for (int i = 0; i < _enemy.Length; i++)
        {
            if (_avail[i] != 0) continue;               // free, or an already-dropped pickup
            ref var e = ref _enemy[i];
            // armorleft 0 is a pickup or pure decoration -- deliberately not counted, which is
            // why this does not simply reuse the engine's enemyOnScreen: that tally includes
            // every coin and powerup on screen, and a level raining money is not a hard level.
            if (e.armorleft == 0) continue;
            int sx = e.ex + e.mapoffset;
            bool onScreen = Widescreen ? (sx > WsDrawGateL && sx < WsDrawGateR)
                                       : (sx > -29 && sx < 300);
            if (!onScreen) continue;

            // Everything here is a contact hazard: JE_playerCollide (mainint.c:7784) tests every
            // live slot and charges the player damageRate on touch, and the enemyDat entries the
            // levels are built from are all on the side of that test that collides. Boss plating,
            // gauntlet walls and a passing fighter alike -- flying into any of them hurts.
            //
            // What separates them is whether shooting makes them go away. 255 is the engine's
            // indestructible marker, and those never stop being in the way however good the
            // player is; a six-armour drone is gone the instant it is looked at. So presence is
            // weighted by how long the thing survives being shot at rather than counted flat --
            // that is the one correction for there being no player here to clear the screen.
            if (e.armorleft == 255)
            {
                presence += 1f;
                // ... but only counts as a *wall* if its enemyDat entry was authored
                // indestructible. Levels routinely make an ordinary enemy invulnerable for a
                // while with event 25 -- ASSASSIN does it to its boss links at t=1900 and undoes
                // it at t=2200 -- and taking the live value at face value turns that entrance
                // into seventeen indestructible walls, outscoring a real gauntlet. Something
                // that will be shootable again in ten seconds is not scenery.
                if (DatFor(e.enemytype).Armor == 255) hulks++;
            }
            else { armor += e.armorleft; presence += e.armorleft / (e.armorleft + Stickiness); }
        }

        t.OnTick(bullets, presence, armor, hulks, _s.difficultyLevel);
    }

    private readonly List<EnemyView> _pickScratch = new();

    /// <summary>
    /// Slot of the front-most enemy whose sprite cell contains the buffer-space point, or -1.
    /// Uses <see cref="CollectEnemies"/>'s own footprints and visibility rules, so what the
    /// viewer boxes on hover is exactly what a click hits. Score items (already-dropped
    /// pickups) are skipped, matching the engine's shot test — it only collides with slots in
    /// the live state.
    /// </summary>
    public int PickEnemyAt(int bufX, int bufY, int categoryMask = ~0)
    {
        CollectEnemies(_pickScratch, categoryMask);
        for (int i = _pickScratch.Count - 1; i >= 0; i--)   // front-most band last
        {
            var e = _pickScratch[i];
            if (_avail[e.Slot] != 0) continue;
            if (Math.Abs(bufX - e.CenterX) <= e.HalfW && Math.Abs(bufY - e.CenterY) <= e.HalfH)
                return e.Slot;
        }
        return -1;
    }

    /// <summary>Damage that outlives any armour value the data can hold (the engine's cap is
    /// 255), so one hit always reaches the kill branch.</summary>
    public const int InstantKillDamage = 30000;

    /// <summary>Ticks a sky slot may stay frozen above the top edge before playback reclaims
    /// it — tyrian2.c's MAP_STOP_STALL_LIMIT (~6 s), the same dwell its map-stop watchdog
    /// waits before culling an unreachable parked enemy.</summary>
    private const int ParkedAboveLimit = 210;

    /// <summary>Palette band the damage flash paints with. Tyrian uses the firing weapon's
    /// `shipblastfilter`; with no player weapon here, the front-weapon slot (1, the
    /// Pulse-Cannon every ship starts with) stands in for it. Falls back to the engine's own
    /// red hit band if that entry carries no tint.</summary>
    public byte HitFilter
    {
        get { byte f = _wd.Get(1).ShipBlastFilter; return f != 0 ? f : (byte)0x70; }
    }

    /// <summary>
    /// Hit the enemy in <paramref name="slot"/> for <paramref name="damage"/>, running the
    /// engine's own shot-collision outcome (tyrian2.c draw_player_shot): every segment sharing
    /// the hit enemy's link number dies with it — which is what takes a multi-part boss apart in
    /// one go — `enemydie` successors spawn, the damaged-state art swaps in once armour crosses
    /// `edlevel`, and each corpse gets the explosion its enemyDat asks for. There is no player
    /// here, so the original's score / cash / cube payout, sound queue and the shot's own
    /// ice+filter payload are dropped; everything that shows on screen is kept.
    /// <paramref name="explosions"/> off suppresses the débris only — what dies still dies.
    /// Returns false if the slot held no live enemy.
    /// </summary>
    public bool DamageEnemy(int slot, int damage, bool explosions)
    {
        if ((uint)slot >= (uint)_enemy.Length || _avail[slot] != 0) return false;

        int armorleft = _enemy[slot].armorleft;
        int link = _enemy[slot].linknum;
        if (link == 0) link = 255;

        if (armorleft < 255)   // 255 = invulnerable: no bar flash, no tint, no armour lost
        {
            if (link == _s.bossLink0) _s.bossColor0 = 6;
            if (link == _s.bossLink1) _s.bossColor1 = 6;

            // Tyrian's damage flash: a hit repaints the enemy in the firing weapon's blast
            // band for exactly one drawn frame (JE_drawEnemy clears `filter` right after
            // blitting it), and every linked segment flashes with it. The engine restricts
            // that to `enemyground` sprites — where a shot's blast physically lands — but a
            // click has no blast, and feedback that only shows on some enemies is no feedback,
            // so the tint goes on whatever was hit.
            byte tint = HitFilter;
            _enemy[slot].filter = tint;
            for (int i = 0; i < _enemy.Length; i++)
                if (_avail[i] != 1 && _enemy[i].linknum == link)
                    _enemy[i].filter = tint;
        }

        if (armorleft > damage) DamageEnemySurvived(slot, damage, link, armorleft, explosions);
        else KillEnemyGroup(slot, link, explosions);
        return true;
    }

    /// <summary>The hit was survivable: take the armour off, and if that crosses the enemy's
    /// `edlevel` threshold, flip it (and its linked siblings) into their damaged state.</summary>
    private void DamageEnemySurvived(int slot, int damage, int link, int armorleft, bool explosions)
    {
        ref Enemy hit = ref _enemy[slot];
        // Outside the armour test, as in tyrian2.c:2930: hitting indestructible plating still
        // clicks, it just does not take anything off.
        Queue(5, 3);   // S_ENEMY_HIT — it took the hit and lived

        if (hit.armorleft != 255)
        {
            hit.armorleft = B8(hit.armorleft - damage);
            // The engine sparks at the shot; with no shot, the enemy taking it is the spot.
            if (explosions) SetupExplosion(hit.ex + hit.mapoffset, hit.ey, 0, 0);
        }

        // engine: (armorleft - damage <= edlevel) && ((!edamaged) ^ (edani < 0))
        if (armorleft - damage > hit.edlevel || !(!hit.edamaged ^ (hit.edani < 0))) return;

        for (int i = 0; i < _enemy.Length; i++)
        {
            if (_avail[i] == 1) continue;
            int ln = _enemy[i].linknum;
            if (i != slot && !(link != 255 &&
                    ((_enemy[i].edlevel > 0 && ln == link) ||
                     (_s.enemyContinualDamage && link - 100 == ln) ||
                     (ln > 40 && ln / 20 == link / 20 && ln <= link))))
                continue;

            ref Enemy e = ref _enemy[i];
            e.enemycycle = 1;
            e.edamaged = !e.edamaged;
            if (e.edani != 0)
            {
                e.ani = Math.Abs(e.edani);
                e.aniactive = 1; e.animax = 0; e.animin = e.edgr;
                e.enemycycle = B8(e.animin - 1);
            }
            else if (e.edgr > 0)
            {
                e.egr[0] = (ushort)e.edgr;
                e.ani = 1; e.aniactive = 0; e.animax = 0; e.animin = 1;
            }
            else _avail[i] = 1;
            e.aniwhenfire = 0;
            if (e.armorleft > (byte)e.edlevel) e.armorleft = (byte)e.edlevel;

            if (!explosions) continue;
            int tx = e.ex + e.mapoffset;
            if (DatFor(e.enemytype).Esize != 1) SetupExplosion(tx, e.ey - 6, 0, 1);
            else SetupExplosionLarge(e.enemyground, e.explonum / 2, tx, e.ey);
        }
    }

    /// <summary>The armour ran out: destroy the whole linked formation the hit belonged to.</summary>
    private void KillEnemyGroup(int slot, int link, bool explosions)
    {
        if (link == 254 && _s.superEnemy254Jump > 0) EventJump((ushort)_s.superEnemy254Jump);

        for (int i = 0; i < _enemy.Length; i++)
        {
            if (_avail[i] == 1) continue;
            int ln = _enemy[i].linknum;
            if (i != slot && link != 254 && !(link != 255 &&
                    (link == ln || link - 100 == ln ||
                     (ln > 40 && ln / 20 == link / 20 && ln <= link))))
                continue;

            ref Enemy e = ref _enemy[i];
            int sx = e.ex + e.mapoffset;
            if (e.special && e.flagnum is >= 1 and <= 10)
                _s.globalFlags[e.flagnum - 1] = e.setto;

            // Whatever this enemy leaves behind — a powerup, a wreck, the next boss stage.
            if (e.enemydie > 0)
            {
                int offset = DatFor(e.enemydie).Value > 30000 ? 0 : i - i % 25;
                int made = NewEnemy(offset, e.enemydie, 0);
                if (made != 0)
                {
                    ref Enemy spawn = ref _enemy[made - 1];
                    spawn.scoreitem = spawn.evalue != 0;
                    spawn.ex = e.ex;
                    spawn.ey = e.ey;
                }
            }

            if (e.edlevel == -1 && link == ln)   // becomes a collectable rather than dying
            {
                e.edlevel = 0;
                _avail[i] = 2;
                e.egr[0] = (ushort)e.edgr;
                e.ani = 1; e.aniactive = 0; e.animax = 0; e.animin = 1;
                e.edamaged = true; e.enemycycle = 1;
            }
            else _avail[i] = 1;

            // A big enemy goes with a low boom, anything else with the short one.
            if (DatFor(e.enemytype).Esize == 1) Queue(6, 9); else Queue(6, 8);

            if (!explosions) continue;
            if (DatFor(e.enemytype).Esize == 1)
                SetupExplosionLarge(e.enemyground, e.explonum, sx, e.ey);
            else
                SetupExplosion(sx, e.ey, 0, 1);
        }
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
            // Layer 2 rides layer 1's X phase while the water smoothie welds them (see
            // Bg2WaterSync); the blend variant never does, and only background2over == 3
            // draws layer 2 unblended without background2notTransparent.
            bool bg2Blend = !_s.background2notTransparent && _s.background2over != 3;
            bool bg2Sync = Bg2WaterSync(bg2Blend);
            (int posIdx, int bp, int xPos, int backPos) = layer switch
            {
                0 => (_s.mapYPos, _s.mapXbp, _s.mapXPos, _s.backPos),
                1 => (_s.mapY2Pos, bg2Sync ? _s.mapXbp : _s.mapX2bp,
                      bg2Sync ? _s.mapXPos : _s.mapX2Pos, _s.backPos2),
                _ => (_s.mapY3Pos, _s.mapX3bp, _s.mapX3Pos, _s.backPos3),
            };
            int cols = layer == 2 ? 15 : 14;
            var map = _map[layer];

            // Mirror DrawBgLayer's widescreen shift / mirror / suppression exactly, so hover
            // reports the tile actually drawn under the cursor.
            int xshift = Widescreen ? PlayfieldXShift : 0;
            int start = posIdx + bp - (Widescreen ? 14 : 12);
            int col0 = cols == 15 ? bp : bp - 1;
            bool mirror = MirrorLayers && Widescreen;
            if (mirror) { if (start - col0 < 0) { start = 0; col0 = 0; } }
            else if (ExpandedParallax && start < 0) start = 0;
            int i = FloorDiv(bufY - OY - backPos, 28);
            int t = FloorDiv(bufX - OX - xPos - xshift, 24);
            int rowBase = start + (i + 1) * cols;
            int idx = rowBase + t;
            if (mirror)
            {
                int c = col0 + t;
                if (c < 0 || c >= cols)   // mirrored region: report the reflected source tile
                    idx = rowBase - col0 + (c < 0 ? -1 - c : 2 * cols - 1 - c);
            }
            // Mirror off: expanded parallax's phantom-copy tiles are not drawn -> not pickable.
            else if (ExpandedParallax && bp <= 0 &&
                FloorDiv(idx, cols) != FloorDiv(rowBase + (Widescreen ? 13 : 11), cols)) continue;
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
        public int parkedTicks;       // consecutive ticks frozen above the top edge (sky bank)
    }

    internal struct Shot
    {
        public int sx, sy, sxm, sym, sxc, syc;
        public byte tx, ty;
        public int sgr, duration, animate, animax;
    }

    internal struct Expl { public int ttl, x, y, sprite, deltaY; public bool fixedPosition; }
    internal struct RepExpl { public int delay, ttl, x, y; public bool big; }
    /// <summary><see cref="position"/> is the vanilla star (one JE_word linear offset that
    /// relies on 16-bit overflow to wrap); <see cref="x"/>/<see cref="y"/> are the widescreen
    /// build's, where only the row advances. Each path uses its own fields.</summary>
    internal struct Star { public ushort position; public int x; public float y; public int speed; public byte color; }

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
        /// <summary>Last tick on which a standoff was actually standing. Observation only —
        /// nothing in the simulation reads it. See <see cref="PendingHoldStartTick"/>.</summary>
        public int previewHoldSeenTick;
        public Bool10 globalFlags;
        public Byte10 newPL;
        public Byte9 smoothies, smoothieData;
        public bool levelTimer;
        public int levelTimerCountdown, levelTimerJumpTo;
        public bool previewGateTimerPause;
        public bool randomExplosions, readyToEndLevel, endLevel, finished;
        public int difficultyLevel;
        public int galagaShotFreq;                     // galagaMode fire chance, in 400
        public int starfieldSpeed;
        /// <summary>Widescreen starfield only: rotates the above-screen respawn height so
        /// consecutive recycles stagger across the spawn band. RNG-free by design — the
        /// per-tick starfield must never touch the gameplay stream.</summary>
        public int starSpawnPhase;
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

        void MarkLink(int link, int time)
        {
            if (link > 0 && link < 256) _lastLinkEvent[link] = Math.Max(_lastLinkEvent[link], time);
        }
        foreach (var ev in lv.Events)
        {
            // Only events that command enemies already on the field. A spawn's dat4 merely
            // stamps a number on the new one, and levels recycle the low numbers all the way
            // through -- counting those would let a stream of fighters on link 3 protect
            // something the script forgot above the top edge hours earlier.
            if (ObjectPlacer.IsSpawn(ev.Type, out _, out _)) continue;
            MarkLink(ev.Dat4, ev.Time);
            // Event 39 names its links in dat/dat2 instead: the group it renames is still being
            // handled at that moment, and so is the number it renames them to.
            if (ev.Type == 39) { MarkLink(ev.Dat, ev.Time); MarkLink(ev.Dat2, ev.Time); }
        }

        // The ']g' that JE_loadMap reads out of the episode script belongs to the level, not
        // to the caller, so resolve it here — the viewer, --simshot and --simtest all get it
        // without plumbing a flag through.
        foreach (var item in ep.Levels)
            if (item.FileNum == lv.FileNum) { GalagaMode = item.GalagaMode; break; }
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
        if (PreviewLoopCycles < 1) PreviewLoopCycles = 1;   // guard array sizing below
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
        // JE_loadMap pins a Galaga level at NORMAL whatever the player picked (tyrian2.c:2263),
        // so the difficulty parameter genuinely has no effect on those two levels.
        _s.difficultyLevel = GalagaMode ? 2 : Difficulty;
        _s.galagaShotFreq = 0;
        // keeps map from scrolling past the top
        _s.bkWrap1 = _s.bkWrap1to = 1 * 14;
        _s.bkWrap2 = _s.bkWrap2to = 1 * 14;
        _s.bkWrap3 = _s.bkWrap3to = 1 * 15;

        // initialize_starfield. Both models draw from the gameplay RNG, and the widescreen
        // build's larger field consumes more of it — exactly as the real build does, so a
        // widescreen run's stream legitimately differs from a vanilla one.
        bool wideStars = WideStarfield;
        int starCount = wideStars ? WideStarCount : VanillaStarCount;
        if (_stars.Length != starCount) _stars = new Star[starCount];
        for (int i = _stars.Length - 1; i >= 0; i--)
        {
            if (wideStars)
            {
                _stars[i].x = (int)(_rng.Next() % (uint)SurfaceWidth);
                _stars[i].y = _rng.Next() % StarfieldWrap;
            }
            else
                _stars[i].position = (ushort)(_rng.Next() % 320 + _rng.Next() % 200 * Pitch);
            _stars[i].speed = (int)(_rng.Next() % 3) + 2;
            _stars[i].color = (byte)(_rng.Next() % 16 + StarfieldHue);
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

    /// <summary>The shape bank a sheet slot is holding. The two negative slots are the banks
    /// MakeEnemy intercepts before the four event-5 slots — reporting them as bank 0 would
    /// send anything that reads this back to a sheet the sprite is not in.</summary>
    private int SheetIdAt(int slot) => slot switch
    {
        0 => _s.sheetId0, 1 => _s.sheetId1, 2 => _s.sheetId2, 3 => _s.sheetId3,
        -2 => 21,   // coins / gems
        -3 => 26,   // powerups
        _ => 0,
    };

    /// <summary>Neutral-frame parallax offsets (mainint.c:4693, phantom player). In
    /// widescreen the widescreen build's rewritten formula (mainint.c
    /// JE_mainGamePlayerFunctions) applies instead: a clamped 0..1 ramp over the widened
    /// player range, with layer 2 pulled back 17px. notes.md §Widescreen.</summary>
    private void ComputeParallax()
    {
        int tempX = PlayerX;
        int tempW;
        if (Widescreen)
        {
            // w_f is the shared float driver for all three layers: mapX3Ofs = w_f,
            // mapX2Ofs = (w_f-17)*2/3, mapXOfs = mapX2Ofs/2 (the original coupled 4:2:1 ratio).
            float wf;
            if (ExpandedParallax)
            {
                // Extra Parallax (commit ae13d1c): pan the NEAR layer across EXACTLY its 336px
                // map -- mapXOfs sweeps 36 (far-left: map plane-px 0 at the window's left edge)
                // down by the slack (336 - 299 = 37) to -1 (far-right: last map px at the right
                // edge) -- normalized over the ship's ACTUAL travel [SHIP_LEFT_MARGIN,
                // PLAYFIELD_WIDTH - SHIP_RIGHT_MARGIN] = [29, 303] so BOTH walls are reached.
                // w_f is back-derived (3*near + 17) so the mid/deep layers keep the coupled
                // ratio and still over-pan/uncover their edges at far-left (DrawBgLayer's base
                // clamp guards the resulting out-of-range read).
                const float shipLeft = 29f, shipRight = WideViewW + 4;   // SHIP_LEFT/RIGHT_MARGIN 29/-4
                const float nearFlushLeft = ViewX - PlayfieldXShift;     // 24 - (-12) = 36
                const float nearSlack = 14 * 24 - WideViewW;             // 336 - 299 = 37
                float uu = Math.Clamp((tempX - shipLeft) / (shipRight - shipLeft), 0f, 1f);
                wf = 3f * (nearFlushLeft - nearSlack * uu) + 17f;        // 125 (far-left) .. 14 (far-right)
            }
            else
            {
                // Stock widescreen amplitude and normalization. (The build's far-left bg2
                // sub-pixel snap only touches its smooth-motion float mirrors; the viewer
                // renders the integer offsets directly, which is already the crisp result.)
                float u = Math.Clamp((tempX - 40f) / (WideViewW + 64 - 40), 0f, 1f);
                wf = (1f - u) * (24 * 3);
            }
            tempW = (int)MathF.Floor(wf);
            _s.mapX3Ofs = tempW;
            _s.mapX3Pos = _s.mapX3Ofs % 24;
            _s.mapX3bp = 1 - _s.mapX3Ofs / 24;
            _s.mapX2Ofs = ((tempW - 17) * 2) / 3;
        }
        else
        {
            tempW = (int)MathF.Floor((float)(260 - (tempX - 36)) / (260 - 36) * (24 * 3) - 1);
            _s.mapX3Ofs = tempW;
            _s.mapX3Pos = _s.mapX3Ofs % 24;
            _s.mapX3bp = 1 - _s.mapX3Ofs / 24;
            _s.mapX2Ofs = tempW * 2 / 3;
        }
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

        // Layer 2 (bg2 overlay) right-edge coverage guard (commit ae13d1c). Its 14-tile (336px)
        // strip is 1px too short of the widescreen playfield's right edge (col 322) once the pan
        // pushes mapX2Ofs to its far-right -2 (strip at x=-14 ends on col 321); the near layer
        // bottoms out at -1, so clamp layer 2 to that same floor. Applied AFTER mapXOfs is
        // derived from the unclamped value (engine order), so only layer 2 itself moves. Not
        // gated on ExpandedParallax -- the gap exists in plain widescreen too; vanilla mapX2Ofs
        // never drops below 0, so the Widescreen gate keeps normal mode byte-identical.
        if (Widescreen && _s.mapX2Ofs < -1)
        {
            _s.mapX2Ofs = -1;
            _s.mapX2Pos = _s.mapX2Ofs % 24;
            _s.mapX2bp = 1 - _s.mapX2Ofs / 24;
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
    /// <summary>
    /// The engine's <c>soundQueue[8]</c> — one slot per mixer channel, filled during a tick
    /// and drained after it, so the last write in a tick wins. Channel 3 is the announcer
    /// channel and plays at full volume. Deliberately outside the snapshot: it is a single
    /// tick's worth of intent, and re-simulating for a seek must not replay old noise.
    /// </summary>
    public readonly byte[] SoundQueue = new byte[8];

    /// <summary>varz.c's randomEnemyLaunchSounds: one of these three plays when an enemy launches another.</summary>
    private static readonly byte[] RandomEnemyLaunchSounds = { 13, 6, 26 };

    /// <summary>Set by event 35 during a tick: the 1-based song to switch to. 0 = no change.</summary>
    public int SongChange { get; private set; }

    /// <summary>Set by event 34 during a tick: start fading the music out.</summary>
    public bool MusicFade { get; private set; }

    /// <summary>Queues a 1-based sound number on one of the eight channels (JE_playSampleNum's queue).</summary>
    private void Queue(int channel, int sound)
    {
        if (sound > 0 && sound <= 40) SoundQueue[channel & 7] = (byte)sound;
    }

    public void Tick(bool draw)
    {
        if (_s.finished) return;
        Array.Clear(SoundQueue);
        SongChange = 0;
        MusicFade = false;
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
        {
            // Widescreen spreads them across the widened playfield (PLAYFIELD_LEFT + rand %
            // PLAYFIELD_WIDTH) and the full 184px height (vanilla stopped at 180). Same two
            // RNG draws either way, so the timeline stays deterministic. tyrian2.c:3513.
            if (Widescreen)
                SetupExplosionLarge(false, 20, ViewX + (int)(_rng.Next() % WideViewW), (int)(_rng.Next() % 184));
            else
                SetupExplosionLarge(false, 20, (int)(_rng.Next() % 280), (int)(_rng.Next() % 180));
        }

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
        // Widescreen (backgrnd.c): the band cursor starts one BG_TILE_COUNT (14) back instead of
        // 12, draws 14 on-screen columns (lastCol 13) instead of 12, and every row blits shifted
        // by PLAYFIELD_X_SHIFT -- so terrain rides the same phase as the objects (see DrawEnemy).
        int start = posIdx + bp - (Widescreen ? 14 : 12);
        // col0 = map-column index of the strip's first tile within its row: the mapY*Pos cursors
        // carry a -1 bias, so the 14-wide layers sit at bp-1; layer 3's 15-wide stride absorbs
        // the bias, leaving bp (commit 1f7ba83 bg_mirror_setup call sites).
        int col0 = cols == 15 ? bp : bp - 1;
        // Mirror Layers (commit 1f7ba83 bg_mirror_tile): columns outside [0, cols) re-read the
        // same row's edge columns in reflected order and draw horizontally flipped, so a layer
        // panned past its side edge continues as a seamless mirror image instead of wrapping
        // into the adjacent map row. If the first row itself starts before the map (level-end
        // re-seat), fall back to the base clamp with mirroring inert (col0 = 0).
        bool mirror = MirrorLayers && Widescreen;
        if (mirror)
        {
            if (start - col0 < 0) { start = 0; col0 = 0; }
        }
        // Mirror off: Extra Parallax keeps the old base clamp (commit edd8118 bg_clamp_map) so
        // the top-of-scroll over-pan repeats row 0 rather than reading out of bounds.
        else if (ExpandedParallax && start < 0) start = 0;
        int lastCol = Widescreen ? 13 : 11;          // rightmost on-screen tile column
        int xshift = Widescreen ? PlayfieldXShift : 0;
        // Mirror off + expanded parallax: the flat map layout would fill the over-panned strip
        // (the columns panned past the layer's left edge) with the PREVIOUS row's right columns
        // -- a phantom second copy of the map on the left. Skip exactly those, so the uncovered
        // edge reads black instead (user request; the build's notes call black the intent). The
        // test is the layer column, the same `c < 0` the mirror branch keys on, NOT the tile's
        // map row: a strip seated mid-row by event 77 (SQUADRON's backdrop, flat index 4000)
        // straddles two map rows at every pan, and comparing rows blacked out the whole authored
        // left-hand wrap along with the over-pan.
        bool suppressWrap = !mirror && ExpandedParallax;

        int iMin = ExtendedDraw ? -6 : -1;
        int iMax = ExtendedDraw ? 7 : 6;
        int tMin = ExtendedDraw ? -4 : 0;
        // Mirrored Layers right-edge strip (commit b529895 bg_edge_px). A row is exactly
        // (lastCol+1)*24 = 336px -- the width of the near map -- so at the far-right pan extreme
        // it ends flush with PLAYFIELD_RIGHT and nothing covers the columns past it. The lava and
        // water smoothies SAMPLE up to 7px to the right of the pixel they write, so at the
        // screen's right edge they read that black fill; their per-scanline waver is a triangle
        // wave, which turns the miss into the sawtooth "black triangles" seen on EP1 ASSASSIN /
        // EP4 LAVA RUN, and the feedback through the row above/below bleeds it further left over
        // frames. Append up to one more tile column, clipped to the surface width so the strip
        // can never run past it; the mirror branch below already resolves that out-of-row column
        // as a flipped edge column, so it is simply more of the layer. Inert with mirroring off
        // (out-of-row columns have no defined content there -- they wrap into the next map row)
        // and in extended view, which already draws whole columns well past the surface.
        int edgePx = 0;
        if (mirror && !ExtendedDraw)
        {
            int room = SurfaceWidth - (xPos + xshift) - (lastCol + 1) * 24;
            edgePx = room <= 0 ? 0 : Math.Min(room, 24);
        }
        int tMax = ExtendedDraw ? 16 : lastCol + (edgePx > 0 ? 1 : 0);

        var tgt = _tgt;
        for (int i = iMin; i <= iMax; i++)
        {
            int rowBase = start + (i + 1) * cols;
            int leftRow = FloorDiv(rowBase, cols);              // map row of on-screen column 0
            int rightRow = FloorDiv(rowBase + lastCol, cols);  // map row of the rightmost on-screen column
            int y = i * 28 + backPos;
            for (int t = tMin; t <= tMax; t++)
            {
                int idx = rowBase + t;
                bool flip = false;
                if (mirror)
                {
                    int c = col0 + t;
                    if (c < 0 || c >= cols)
                    {
                        // Reflected re-read from inside the same row, drawn flipped: per-tile
                        // reflection + pixel flip compose to the exact plane-pixel mirror
                        // p -> -1-p about the map edge (bg_mirror_tile).
                        flip = true;
                        idx = rowBase - col0 + (c < 0 ? -1 - c : 2 * cols - 1 - c);
                    }
                }
                if (!flip)
                {
                    if (t < 0 && FloorDiv(idx, cols) != leftRow) continue;
                    if (t > lastCol && FloorDiv(idx, cols) != rightRow) continue;
                    if (suppressWrap && col0 + t < 0) continue;
                }
                if ((uint)idx >= (uint)map.Length) continue;
                byte[]? data = map[idx];
                if (data == null) continue;
                int bx = xPos + xshift + t * 24;
                int tileW = edgePx > 0 && t > lastCol ? edgePx : 24;   // clipped edge strip
                for (int ty = 0; ty < 28; ty++)
                {
                    int dy = y + ty + OY;
                    if ((uint)dy >= BufH) continue;
                    int src = ty * 24;
                    int dstRow = dy * BufW;
                    for (int tx = 0; tx < tileW; tx++)
                    {
                        byte v = data[src + (flip ? 23 - tx : tx)];
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

    /// <summary>backgrnd.c draw_background_2: "the water effect combines background 1 and 2 by
    /// synchronizing the x coordinate" — while the water smoothie runs, layer 2 pans on layer
    /// 1's X phase (mapXPos / mapXbpPos) instead of its own. Only the plain variant does this;
    /// draw_background_2_blend always uses layer 2's own, so the weld follows the same condition
    /// the blend does. CORE is the level that shows it: without the weld its overlay drifts
    /// against the terrain it is supposed to ripple with.</summary>
    private bool Bg2WaterSync(bool blend) => !blend && _s.smoothies[1] != 0;

    private void DrawBackground2(bool draw, bool allowBlend = true)
    {
        if (_s.map2YDelayMax > 1 && _s.backMove2 < 2)
            _s.backMove2 = _s.map2YDelay == 1 ? 1 : 0;

        bool blend = allowBlend && !_s.background2notTransparent;   // wild detail default
        bool sync = Bg2WaterSync(blend);
        if (draw && ShowBg2)
            DrawBgLayer(1, _s.mapY2Pos, sync ? _s.mapXbp : _s.mapX2bp,
                sync ? _s.mapXPos : _s.mapX2Pos, _s.backPos2, blend);

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

    /// <summary>Surface-linear index -> buffer, for a scanline stride of <paramref name="w"/>
    /// (the engine's surface->pitch). Identical to <see cref="VIdx"/> at the vanilla 320.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SIdx(int p, int w) => (p / w + OY) * BufW + p % w + OX;

    private void ApplySmoothie(SmoothieKind kind)
    {
        byte[] src = _tgt;
        byte[] dst = Screen;
        // The filters walk vga_width per scanline (backgrnd.c), which is 356 in widescreen --
        // 36 px past the vanilla 320, and the playfield's right edge (PLAYFIELD_RIGHT = 322)
        // sits inside that extra span. Running them at a fixed 320 left the last 3 displayed
        // columns unfiltered: raw terrain beside the smoothed image.
        int W = SurfaceWidth;
        if (!ReferenceEquals(src, dst))
        {
            CopyMargins(src, dst, W);   // margins bypass the filters (they cover the surface)
            int filteredRows = kind is SmoothieKind.Lava or SmoothieKind.Water ? 185 : 184;
            CopySurfaceTail(src, dst, filteredRows, W);
        }

        switch (kind)
        {
            case SmoothieKind.Lava:
            {
                int w = W * 185 - 1;
                int p = W * 185;
                for (int y = 185 - 1; y >= 0; y--)
                {
                    // waver steps once per group of 8 and the row's last group is short when
                    // the width is not a multiple of 8 (356 = 44*8 + 4), so w outruns p by
                    // those 4 px per row -- the engine's own drift; reproduce it literally.
                    for (int x = W; x > 0; )
                    {
                        int waver = Math.Abs(((w >> 9) & 0x0F) - 8) - 1;
                        w -= 8;
                        int count = Math.Min(8, x);
                        x -= count;
                        for (int xi = 0; xi < count; xi++)
                        {
                            p--;
                            int value = 0;
                            if (p + waver >= 0)
                                value += (src[SIdx(p + waver, W)] & 0x0F) * 2;
                            value += dst[SIdx(p + waver + W, W)] & 0x0F;
                            if (p + waver - W >= 0)
                                value += dst[SIdx(p + waver - W, W)] & 0x0F;
                            dst[SIdx(p, W)] = (byte)((value / 4) | 0x70);
                        }
                    }
                }
                break;
            }
            case SmoothieKind.Water:
            {
                byte hue = (byte)(_s.smoothieData[1] << 4);
                int w = W * 185 - 1;
                int p = W * 185;
                for (int y = 185 - 1; y >= 0; y--)
                {
                    for (int x = W; x > 0; )
                    {
                        int waver = Math.Abs(((w >> 10) & 0x07) - 4) - 1;
                        w -= 8;
                        int count = Math.Min(8, x);
                        x -= count;
                        for (int xi = 0; xi < count; xi++)
                        {
                            p--;
                            byte s = src[SIdx(p, W)];
                            if ((s & 0x30) == 0)
                                dst[SIdx(p, W)] = s;
                            else
                            {
                                int value = (s & 0x0F) + (dst[SIdx(p + waver + W, W)] & 0x0F);
                                dst[SIdx(p, W)] = (byte)((value / 2) | hue);
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
                    for (int x = 0; x < W; x++)
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

    /// <summary>Copy everything outside the <paramref name="surfaceW"/> x 200 engine surface
    /// from src to dst so the extended margins stay in sync when a filter switches the draw
    /// target. The surface itself is left alone: lava and water read dst back as their previous
    /// frame, so overwriting it here would break the feedback.</summary>
    private static void CopyMargins(byte[] src, byte[] dst, int surfaceW)
    {
        for (int y = 0; y < BufH; y++)
        {
            int row = y * BufW;
            if (y < OY || y >= OY + ScreenH)
            {
                Array.Copy(src, row, dst, row, BufW);
                continue;
            }
            Array.Copy(src, row, dst, row, OX);
            Array.Copy(src, row + OX + surfaceW, dst, row + OX + surfaceW, BufW - OX - surfaceW);
        }
    }

    /// <summary>The original filters stop at the displayed scanlines. Preserve the
    /// otherwise invisible tail below them so it remains useful in extended view.</summary>
    private static void CopySurfaceTail(byte[] src, byte[] dst, int firstRow, int surfaceW)
    {
        for (int y = firstRow; y < ScreenH; y++)
        {
            int row = (y + OY) * BufW + OX;
            Array.Copy(src, row, dst, row, surfaceW);
        }
    }

    // =====================================================================
    //  Starfield (backgrnd.c). Vanilla carries each star as a single JE_word linear offset
    //  that relies on 16-bit overflow to wrap, and draws it only while it is above row 177 —
    //  seven rows short of the 184-row playfield, so the bottom of the screen has no stars,
    //  and the wrap lands mid-row so a star jumps sideways as it recycles. The widescreen
    //  build rewrote it as (x, float y) points: x never moves, the field spans the full surface
    //  width and all 184 displayed rows, and a recycled star respawns just above the top edge
    //  instead of popping in at row 0. Which model runs is WideStarfield's call, independent of
    //  Widescreen — the dead stripe at the bottom is the same bug at either width.
    // =====================================================================
    private const int VanillaStarCount = 100, WideStarCount = 330;
    private const int StarfieldHue = 0x90;
    private const int StarfieldWrap = 184;      // rows; a star recycles once it drifts past this
    private const int StarfieldVisible = 184;   // rows; stars draw above this (the playfield bottom)
    private const int StarfieldSpawnMin = 4, StarfieldSpawnSpread = 32;

    private void UpdateAndDrawStarfield(bool draw)
    {
        if (WideStarfield) { UpdateAndDrawStarfieldWide(draw); return; }

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
                if (st.color - 4 >= StarfieldHue)
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

    private void UpdateAndDrawStarfieldWide(bool draw)
    {
        int w = SurfaceWidth;
        for (int i = _stars.Length - 1; i >= 0; i--)
        {
            ref var st = ref _stars[i];
            st.y += st.speed + _s.starfieldSpeed;   // only the row moves; x is fixed for life
            if (st.y >= StarfieldWrap)
            {
                // Respawn a little ABOVE the top edge so the star drifts into view instead of
                // popping in at row 0 and holding there for the wrap tick.
                st.y = -(StarfieldSpawnMin + _s.starSpawnPhase % StarfieldSpawnSpread);
                _s.starSpawnPhase += 13;   // step coprime with the spread -> even coverage
            }
            if (draw && ShowStarfield) DrawStar(st.x, (int)(st.y + 0.5f), st.color, w);
        }
    }

    /// <summary>One star: centre pixel plus a dimmer 4-neighbour halo, each written only where
    /// the screen is still black. Bounds are checked per axis so a halo pixel cannot wrap into
    /// the neighbouring row.</summary>
    private void DrawStar(int x, int y, byte color, int surfaceW)
    {
        if (x < 0 || x >= surfaceW || y < 0 || y >= StarfieldVisible) return;

        var tgt = _tgt;
        int pos = (y + OY) * BufW + x + OX;
        if (tgt[pos] == 0) tgt[pos] = color;

        if (color - 4 >= StarfieldHue)
        {
            byte halo = (byte)(color - 4);
            if (x + 1 < surfaceW && tgt[pos + 1] == 0) tgt[pos + 1] = halo;
            if (x - 1 >= 0 && tgt[pos - 1] == 0) tgt[pos - 1] = halo;
            if (y + 1 < ScreenH && tgt[pos + BufW] == 0) tgt[pos + BufW] = halo;
            if (y - 1 >= 0 && tgt[pos - BufW] == 0) tgt[pos - BufW] = halo;
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
        // Widescreen shifts every pass's tempMapXOfs by PLAYFIELD_X_SHIFT (tyrian2.c sets it on
        // the tempMapXOfs global before each JE_drawEnemy). This flows to the enemy blit position,
        // e.mapoffset (hover/on-screen), the draw/cull gates, aim, and shot creation (sh.sx), so
        // objects ride the same shifted playfield as the background. notes.md §Widescreen.
        if (Widescreen) tempMapXOfs += PlayfieldXShift;
        bool skyBank = enemyOffset == 25;   // slots 0..24, the batch with no tempBackMove channel
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

            if (e.ex + tempMapXOfs > (Widescreen ? WsDrawGateL : -29) &&
                e.ex + tempMapXOfs < (Widescreen ? WsDrawGateR : 300))
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
            if (e.ex < -80 || e.ex > (Widescreen ? WsEnemyCullR : 340)) { _avail[i] = 1; continue; }
            e.ey += e.eyc;
            if (e.ey < -112 || e.ey > 190) { _avail[i] = 1; continue; }

            // The sky bank is the one batch drawn with tempBackMove == 0 (tyrian2.c 2757/3324),
            // so a sky enemy with no vertical motion of its own is frozen where it spawned. One
            // frozen above the top edge is never drawn and never leaves the [-112, 190]
            // keep-alive band, so it holds its slot — and its share of enemyOnScreen — for the
            // rest of the level. The game does clear those: the ones in reach die to the
            // player's ascending fire, and the map-stop watchdog culls the rest
            // (enemy_stuck_above_screen, notes.md §Map-stop softlock watchdog). Playback has
            // neither, and 25 sky slots is exactly what GYGES's boss needs — its eight parked
            // links (map 269, every one eyc 0 / fixedmovey 0) cost the boss seven of its 24
            // tiles, and one parked slot alone stalls SURFACE and MACES on a sky-bank map stop.
            // Cull on the watchdog's own 210-tick dwell, so an enemy a later event starts
            // moving is never taken.
            //
            // Except while the script still has the link on its list. Parked is not the same as
            // stuck: TIME WAR hangs a 30-part machine over the top edge at t=120 and then walks
            // it down ten pixels per wave, so two thirds of it stands frozen above -26 for
            // eighteen hundred ticks at a time and the plain dwell reclaimed it long before the
            // level brought it back into view. The exemption lapses the moment a map stop is
            // actually waiting on the thing, which is the only case that can hang playback and
            // the one the engine's own watchdog covers (tyrian2.c:3921).
            bool scriptOwns = (uint)e.linknum < 256 && e.linknum != 0 &&
                              _s.curLoc <= _lastLinkEvent[e.linknum] &&
                              !(_s.stopBackgrounds && !_s.forceEvents);
            if (skyBank && !scriptOwns && e.ey <= (e.size == 1 ? -26 : -13) &&
                e.eyc <= 0 && e.eycc == 0 && e.fixedmovey <= 0 && e.yaccel == 0)
            {
                if (++e.parkedTicks >= ParkedAboveLimit) { _avail[i] = 1; continue; }
            }
            else e.parkedTicks = 0;

            if (e.ex <= e.xminbounce || e.ex >= e.xmaxbounce) e.exc = S8(-e.exc);
            if (e.ey <= e.yminbounce || e.ey >= e.ymaxbounce) e.eyc = S8(-e.eyc);

            if (e.scoreitem)
            {
                if (e.ex < (Widescreen ? WsScoreParkL : -5)) e.ex++;
                if (e.ex > (Widescreen ? WsScoreParkR : 245)) e.ex--;
            }

            e.ey += tempBackMove;

            if (e.ex <= -24 || e.ex >= (Widescreen ? WsOnScreenR : 296)) continue;

            int tempX = e.ex, tempY = e.ey;

            if (e.edamaged) continue;

            _s.enemyOnScreen++;

            if (e.iced > 0)
            {
                e.iced--;
                if (e.enemyground) e.filter = 0x09;
                continue;
            }

            bool endEnemy = false;
            if (FireEnabled)
                endEnemy = FireTurrets(ref e, tempX, tempY, tempMapXOfs);
            if (endEnemy) continue;   // goto draw_enemy_end (skips launch)

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
                                Threat?.OnAimedLaunch();
                            }
                        }

                        uint t;
                        do { t = _rng.Next() % 8; } while (t == 3);
                        Queue((int)t, RandomEnemyLaunchSounds[_rng.Next() % 3]);

                        if (e.launchspecial == 1 && e.linknum < 100)
                            l.linknum = e.linknum;
                    }
                }
            }
        }
    }

    /// <summary>Turret fire (JE_drawEnemy shots). Returns true when the engine takes its
    /// goto draw_enemy_end — out of shot slots, or a suppressed Galaga shot — which abandons
    /// the remaining turrets and the launch routine for this enemy this tick.</summary>
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

            // Galaga levels gut enemy fire (tyrian2.c:1374): an enemy still sitting in
            // formation (eyc == 0) never shoots, and a diving one only gets a
            // galagaShotFreq-in-400 chance. galagaShotFreq starts at 0 and only event 78
            // raises it, so ** ALE ** never fires a shot and SQUADRON only reaches 3/400
            // in its final loop. mt_rand runs only when the first test fails, as in C.
            if (GalagaMode && (e.eyc == 0 || _rng.Next() % 400 >= (uint)_s.galagaShotFreq))
                return true;   // goto draw_enemy_end

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
                        {
                            Threat?.OnShotBlocked();
                            return true;   // goto draw_enemy_end
                        }

                        _shotAvail[b] = 0;

                        if (w.Sound > 0)
                        {
                            uint t;
                            do { t = _rng.Next() % 8; } while (t == 3);
                            Queue((int)t, w.Sound);
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

                        Threat?.OnShotCreated(w.Attack[pos], w.Aim, w.Tx, w.Ty);
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
            if (sh.duration-- == 0 || sh.sy > 190 || sh.sy <= -14 ||
                sh.sx > (Widescreen ? WsShotCullR : 275) || sh.sx <= 0)
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
                // The last blast of a cascade, and one in five before it, is the big one.
                if (r.ttl == 1) Queue(7, 11);
                else if (_rng.Next() % 5 == 1) Queue(7, 11);
                else Queue(6, 9);
                r.delay = 4 + (int)(_rng.Next() % 3);
            }
            else
            {
                SetupExplosion(tx, ty, 0, 1);
                Queue(5, 4);
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

        if (draw && ShowBossBars)
        {
            // Widescreen re-centres the bars in the wider playfield (the widescreen build centres
            // its boss bars on PLAYFIELD_WIDTH/2), so a centred bar stays centred rather than
            // sitting left of centre. The bars are HUD overlays, so they take the centring delta,
            // not the terrain's PLAYFIELD_X_SHIFT.
            int wsBarShift = Widescreen ? (WideViewW - ViewW) / 2 : 0;   // 17
            for (int bi = 0; bi < bars; bi++)
            {
                int x = (bars == 2 ? (bi == 0 ? 125 : 185) : (_s.levelTimer ? 250 : 155)) + wsBarShift;
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

        int pw = PlayfieldWidth;   // 299 widescreen / 264 vanilla
        if (col != -99)
        {
            int hue = (col << 4) & 0xF0;
            for (int y = 0; y < ViewH; y++)
            {
                int row = (y + OY) * BufW + ViewX + OX;
                for (int x = 0; x < pw; x++)
                    Screen[row + x] = (byte)(hue | (Screen[row + x] & 0x0F));
            }
        }
        if (bright != -99)
        {
            for (int y = 0; y < ViewH; y++)
            {
                int row = (y + OY) * BufW + ViewX + OX;
                for (int x = 0; x < pw; x++)
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
    /// <summary>True when this frame is presented vertically flipped (JE_starShowVGA code 1).</summary>
    public bool ScreenFlipped => ShowScreenFlip && _s.smoothies[8] + (_s.smoothies[5] << 1) == 1;

    public void PreparePresent()
    {
        Array.Copy(Screen, PresentScreen, Screen.Length);
        int pw = PlayfieldWidth;   // 299 widescreen / 264 vanilla
        int code = _s.smoothies[8] + (_s.smoothies[5] << 1);
        if (code == 1 && ShowScreenFlip)
        {
            // upside-down playfield (TIME WAR / EYESPY style)
            for (int r = 0; r < ViewH; r++)
            {
                int dst = (r + OY) * BufW + ViewX + OX;
                int src = (ViewH - 1 - r + OY) * BufW + ViewX + OX;
                Array.Copy(Screen, src, PresentScreen, dst, pw);
            }
        }
        else if (code == 2 && ShowSpotlight)
        {
            // spotlight around the (phantom) player; everything else darkened. lightx/x
            // widen with the playfield: 281 = 264+17, 316 = 299+17 (widescreen
            // composite_playfield: PLAYFIELD_WIDTH - PLAYFIELD_X_SHIFT + 5). notes.md §Widescreen.
            int lighty = 172 - PlayerY;
            int lightx = pw + 17 - PlayerX;
            for (int r = 0; r < ViewH; r++)
            {
                int y = 184 - r;
                int row = (r + OY) * BufW + ViewX + OX;
                for (int c = 0; c < pw; c++)
                {
                    byte s = Screen[row + c];
                    int x = pw - c;
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
        e.parkedTicks = 0;
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
            // Widescreen starts the background map cursor 2 tiles earlier (DrawBgLayer's
            // -tile_count), so event-spawned enemies compensate by 2 tiles -- (mapX-3) vs the
            // vanilla (mapX-1), and case 50's else form drops the -24*2 -- to stay locked to the
            // terrain column they are authored onto. tyrian2.c JE_createNewEventEnemy.
            int mapXAdj = Widescreen ? 3 : 1;
            switch (enemyOffset)
            {
                case 0:
                    e.ex = ev.Dat2 - (_lv.MapX - mapXAdj) * 24;
                    e.ey -= _s.backMove2;
                    break;
                case 25:
                case 75:
                    e.ex = ev.Dat2 - (_lv.MapX - mapXAdj) * 24 - 12;
                    e.ey -= _s.backMove;
                    break;
                case 50:
                    if (_s.background3x1)
                        e.ex = ev.Dat2 - (_lv.MapX - mapXAdj) * 24 - 12;
                    else
                        e.ex = ev.Dat2 - _lv.MapX3 * 24 + 6 - (Widescreen ? 0 : 24 * 2);
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

    /// <summary>The map cannot advance on its own from here: it is stopped, or is waiting
    /// on the level-end condition. With live enemies aboard and no script left to run, only
    /// the player clears it.</summary>
    private bool ProgressBlocked =>
        PreviewEnemyGates && _s.enemyOnScreen != 0 && !_s.previewGateTimerPause &&
        (_s.stopBackgrounds || _s.stopBackgroundNum is > 0 and < 9 ||
         _s.readyToEndLevel || _s.backMove == 0);

    /// <summary>Tick an enemy-gated hold has been standing since, or -1 if nothing is held
    /// right now. A hold is only logged as a gate when it is released, so a run that stops
    /// mid-standoff — the tick cap landed inside it, or <see cref="PreviewHoldSeconds"/> is
    /// longer than the run had left — leaves no record of the very thing that ended it.
    /// <see cref="SimPlayback"/> reads this afterwards to hatch it anyway.
    ///
    /// Deliberately NOT <see cref="ProgressBlocked"/> sampled on the final tick. One of that
    /// property's disjuncts is <c>backMove == 0</c>, and in any slow-scrolling section
    /// (map1YDelayMax &gt; 1) backMove is rewritten to 0 on two ticks in three before the map
    /// even moves, so the answer depended on which phase of that the tick cap happened to land
    /// on — and a standoff plainly on screen reported nothing for a one-second nudge of the
    /// hold slider. What matters is that a standoff was standing recently, not on that
    /// particular tick.</summary>
    public int PendingHoldStartTick =>
        _s.previewHoldSeenTick > 0 && _s.tickCount - _s.previewHoldSeenTick <= (int)TicksPerSecond
            ? Math.Max(1, _s.previewLastEventTick)
            : -1;

    private void PreviewQuietEnemyHold()
    {
        bool blocked = ProgressBlocked;
        if (blocked) _s.previewHoldSeenTick = _s.tickCount;

        int holdTicks = (int)(Math.Max(1, PreviewHoldSeconds) * TicksPerSecond);
        if (!blocked || _s.tickCount - _s.previewLastEventTick < holdTicks)
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

            // The window's text is not drawn, but it speaks: sndmast.c's windowTextSamples
            // pairs each of the nine slots with an announcer line ("Large enemy approaching").
            case 16:
                if (ev.Dat is >= 1 and <= 9) Queue(3, Audio.SoundBank.WindowTextSamples[ev.Dat - 1]);
                break;

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
                        // Galaga scales the armor by difficultyLevel / 2, integer-divided
                        // (tyrian2.c:6642) — 1x at the NORMAL that JE_loadMap pins it to.
                        _enemy[i].armorleft = B8(GalagaMode
                            ? ev.Dat * (_s.difficultyLevel / 2) : ev.Dat);
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

            case 34: MusicFade = true; break;          // start music fade
            case 35: SongChange = ev.Dat; break;       // play song (1-based)

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

            case 62: Queue(3, ev.Dat); break;  // play sound effect

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
                    // Widescreen: the right bounce bound was authored for the 320px field; shift
                    // it out by the widescreen extension (vga_width - LEGACY_WIDTH = 36) so
                    // sweeping enemies cover the full widened playfield. tyrian2.c case 74.
                    if (ev.Dat != -99) _enemy[i].xmaxbounce = ev.Dat + (Widescreen ? 36 : 0);
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

            case 78:  // raise the galagaMode fire chance by 1/400, capped at 10
                if (_s.galagaShotFreq < 10) _s.galagaShotFreq++;
                break;

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
