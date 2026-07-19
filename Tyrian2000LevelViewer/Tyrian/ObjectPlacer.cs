namespace T2LV.Tyrian;

// Object layer categories. Enemies (anything with armour) are split by the render band
// they're locked to, since that's what actually distinguishes them in-game; the rest are
// the non-shootable pickups and pure decoration.
public enum ObjCategory { EnemyAir, EnemyGround, EnemyForeground, Powerup, Money, Datacube, Decor }

public struct PlacedObject
{
    public float X, Y;          // canvas coords (top-left of sprite cell)
    public int EnemyId;
    public ObjCategory Cat;
    public int Band;            // 0 sky / 25 ground / 50 top / 75 ground2
    public ushort Time;
    public bool ApproxX;
    public int PathDistance;     // >= 0 when placed on an unrolled LevelTimeline
    public int UniformPathDistance; // same event on its carrier layer's 1:1 track
    public int UniformLayer;      // -1 screen-relative, otherwise background layer 0..2
    public int GameSourceY;       // carrier's in-game source Y at the event
    public int UniformSourceY;    // carrier's source Y at the event, for alignment checks
    public float ScreenY;        // spawn Y relative to the top of the playfield
    // Sprite resolution
    public CompShapes? Sheet;
    public int SpriteIndex;     // egraphic[0] (1-based into sheet)
    public int Esize;           // 0 single, 1 = 2x2
}

/// <summary>
/// Walks a level's event list, simulating event-5 shape-bank loads and the
/// background scroll-speed events, and produces the set of objects
/// (enemies / buildings / pickups) with resolved sprites and canvas positions.
/// See tyrian2.c:JE_eventSystem / JE_createNewEventEnemy and backgrnd.c.
/// </summary>
public static class ObjectPlacer
{
    // Canvas Y of the top-of-screen (screen y=0) at level start (curLoc=0):
    // bottom-aligned bg1 puts row r at canvas y = (CanvasH - Bg1Rows*28) + r*28.
    // At start the engine shows map row mapY=292 at screen y=0 (backgrnd.c:157-164,
    // tyrian2.c:815), so YBase = 8400 + 292*28 = 16576. bg2/bg3 also start with their
    // top-of-screen row (592) at the same canvas y, so this base is shared by all layers.
    public const float YBase = 16576f;

    // Screen x=0 maps to canvas x = (leftmost on-screen column)*24. With the neutral
    // (zero player-offset) parallax frame, mapXbpPos = 1 (mainint.c:4857), so bg1/bg2
    // show column 2 at screen x=0  ->  +48. bg3 shows column 3 (mapX3bpPos = 1) -> +72,
    // unless background3x1 locks it to bg1's column 2 -> +48 (mainint.c:4859-4864).
    private const float RegMain = 48f;   // bg1 / bg2 aligned
    private const float RegBg3  = 72f;   // bg3 (clouds) aligned, independent scroll

    /// <summary>
    /// How far each background layer actually scrolls over the level, and where along
    /// the map each layer is introduced. A layer that only starts moving mid-level
    /// (BRAINIAC's escape BG2) is anchored at the BG1 position reached at that moment,
    /// so its content sits where the game first shows it instead of at the level start.
    /// </summary>
    public sealed class LayerScroll
    {
        public readonly double[] Anchor = new double[3];  // canvas offset (px up from the shared base)
        public readonly double[] Seen = new double[3];    // total scrolled distance (px)
        public readonly bool[] Late = new bool[3];        // first motion after the level start
    }

    public static List<PlacedObject> Place(GameData gd, EpisodeInfo ep, Level lv, EnemyData ed,
        LevelTimeline? timeline = null, LayerScroll? scrollOut = null)
    {
        if (timeline?.IsUnrolled == true)
            return PlaceTimeline(gd, lv, ed, timeline);

        var result = new List<PlacedObject>();
        var banks = new int[4];
        bool smallEnemyAdjust = false;
        int spawnSerial = 0;

        // --- Background scroll state, integrated over event time so each object can
        // be placed in the coordinate frame of the layer it is locked to. ---
        // bg1 advances in lockstep with curLoc (tyrian2.c:1292-1294) except while
        // stopped with forced events, where authored times keep counting but the map
        // does not move. bg2 advances backMove2 every map2YDelayMax frames and bg3
        // backMove3 every frame, while curLoc advances backMove/map1YDelayMax per frame.
        int backMove = 1, backMove2 = 2, backMove3 = 3;
        int map1YDelayMax = 1, map2YDelayMax = 1;
        bool bg3x1 = false;
        bool bg3x1b = false;
        double cumBg1 = 0, cumBg2 = 0, cumBg3 = 0;
        var started = new bool[3];
        var anchor = new double[3];
        int lastTime = 0;
        double Rate1() => backMove > 0 ? 1 : 0;
        double Rate2() => (double)backMove2 * map1YDelayMax / (Math.Max(1, backMove) * map2YDelayMax);
        double Rate3() => (double)backMove3 * map1YDelayMax / Math.Max(1, backMove);

        IEnumerable<EventRec> route = timeline is { Occurrences.Count: > 0 }
            ? timeline.Occurrences.Select(o => o.Event)
            : lv.Events;
        bool endedWalk = false;
        foreach (var e in route)
        {
            // Advance the scroll accumulators up to this event's time at the current rates.
            // The maps stop when the level ends; authored events past the first end
            // marker (trailing filler times) must not keep scrolling the accumulators.
            int dt = e.Time - lastTime;
            if (dt > 0 && !endedWalk)
            {
                double r1 = Rate1(), r2 = Rate2(), r3 = Rate3();
                // A layer's anchor is the BG1 position when it first starts to move.
                if (!started[0] && r1 > 0) { started[0] = true; anchor[0] = cumBg1; }
                if (!started[1] && r2 > 0) { started[1] = true; anchor[1] = cumBg1; }
                if (!started[2] && r3 > 0) { started[2] = true; anchor[2] = cumBg1; }
                cumBg1 += r1 * dt; cumBg2 += r2 * dt; cumBg3 += r3 * dt;
                lastTime = e.Time;
            }
            if (e.Type == 11) endedWalk = true;

            switch (e.Type)
            {
                case 2:  // set scroll speeds (map delays reset to 1)
                case 30: // same layer speeds + delay reset (tyrian2.c:6624)
                    backMove = e.Dat; backMove2 = e.Dat2; backMove3 = e.Dat3;
                    map1YDelayMax = 1; map2YDelayMax = 1; continue;
                case 3:  // slow-scroll preset
                    backMove = 1; backMove2 = 1; backMove3 = 1;
                    map1YDelayMax = 3; map2YDelayMax = 2; continue;
                case 65: // background 3 locked to background 1 (1x) when dat==0
                    bg3x1 = e.Dat == 0; continue;
                case 72:
                    bg3x1b = e.Dat == 1; continue;
                case 26:
                    smallEnemyAdjust = e.Dat != 0; continue;
                case 5:  // load enemy shape banks
                    int b0 = Math.Max(0, (int)e.Dat), b1 = Math.Max(0, (int)e.Dat2);
                    int b2 = Math.Max(0, (int)e.Dat3), b3 = Math.Max(0, (int)e.Dat4);
                    if (b0 != 0) banks[0] = b0;
                    if (b1 != 0) banks[1] = b1;
                    if (b2 != 0) banks[2] = b2;
                    if (b3 != 0) banks[3] = b3;
                    continue;
            }

            if (!IsSpawn(e.Type, out int band, out int baseEy)) continue;
            int randomKey = SpawnRandomKey(e, spawnSerial++);

            // The object scrolls with bg1 (ground/sky) or bg3 (top band). During a map
            // stop with forced events, ground spawns pile onto the frozen screen — in
            // the game they all appear over the same held terrain (a boss arena).
            // When bg3 is frozen (backMove3==0, e.g. CAMANIS) a top object doesn't move with
            // bg3, so fall back to the bg1 position instead of piling all of them onto one row.
            double TopScroll() => backMove3 > 0 ? anchor[2] + cumBg3 : cumBg1;
            double scrollPos = band == 50 ? TopScroll() : cumBg1;

            // event 12 = 4x4 cluster: band chosen by Dat6, spawns a 2x2 group of four
            // *consecutive* enemy types (dat+0..dat+3), each its own 2x2 metasprite, so
            // together they form one big 48x56 object. tyrian2.c:4532.
            if (e.Type == 12)
            {
                band = e.Dat6 switch { 2 => 0, 3 => 50, 4 => 75, _ => 25 };
                scrollPos = band == 50 ? TopScroll() : cumBg1;
                int k = 0;
                for (int gy = 0; gy < 2; gy++)
                    for (int gx = 0; gx < 2; gx++)
                        AddObject(result, gd, lv, ed, banks, e, band, baseEy, scrollPos,
                            bg3x1, bg3x1b, smallEnemyAdjust, gx * 24, -gy * 28, k++, randomKey,
                            bg2ScrollPos: anchor[1] + cumBg2, rawMove2: backMove2);
                continue;
            }

            AddObject(result, gd, lv, ed, banks, e, band, baseEy, scrollPos,
                bg3x1, bg3x1b, smallEnemyAdjust, 0, 0, 0, randomKey,
                bg2ScrollPos: anchor[1] + cumBg2, rawMove2: backMove2);
        }

        if (scrollOut != null)
        {
            for (int k = 0; k < 3; k++)
            {
                scrollOut.Anchor[k] = anchor[k];
                scrollOut.Late[k] = anchor[k] > 0;
                scrollOut.Seen[k] = k switch { 0 => cumBg1, 1 => cumBg2, _ => cumBg3 };
            }
        }
        return result;
    }

    private static List<PlacedObject> PlaceTimeline(GameData gd, Level lv, EnemyData ed, LevelTimeline timeline)
    {
        var result = new List<PlacedObject>();
        var banks = new int[4];
        bool bg3x1 = false;
        bool bg3x1b = false;
        bool smallEnemyAdjust = false;
        int spawnSerial = 0;

        foreach (var occurrence in timeline.Occurrences)
        {
            EventRec e = occurrence.Event;
            switch (e.Type)
            {
                case 5:
                    int b0 = Math.Max(0, (int)e.Dat), b1 = Math.Max(0, (int)e.Dat2);
                    int b2 = Math.Max(0, (int)e.Dat3), b3 = Math.Max(0, (int)e.Dat4);
                    if (b0 != 0) banks[0] = b0;
                    if (b1 != 0) banks[1] = b1;
                    if (b2 != 0) banks[2] = b2;
                    if (b3 != 0) banks[3] = b3;
                    continue;
                case 65:
                    bg3x1 = e.Dat == 0;
                    continue;
                case 72:
                    bg3x1b = e.Dat == 1;
                    continue;
                case 26:
                    smallEnemyAdjust = e.Dat != 0;
                    continue;
            }

            if (!IsSpawn(e.Type, out int band, out int baseEy)) continue;
            int randomKey = SpawnRandomKey(e, spawnSerial++);

            if (e.Type == 12)
            {
                band = e.Dat6 switch { 2 => 0, 3 => 50, 4 => 75, _ => 25 };
                int k = 0;
                for (int gy = 0; gy < 2; gy++)
                    for (int gx = 0; gx < 2; gx++)
                        AddObject(result, gd, lv, ed, banks, e, band, baseEy, 0,
                            bg3x1, bg3x1b, smallEnemyAdjust,
                            gx * 24, -gy * 28, k++, randomKey, occurrence: occurrence);
                continue;
            }

            AddObject(result, gd, lv, ed, banks, e, band, baseEy, 0,
                bg3x1, bg3x1b, smallEnemyAdjust, 0, 0, 0, randomKey, occurrence: occurrence);
        }
        return result;
    }

    private static (int Distance, int Layer, int GameSourceY, int UniformSourceY) UniformTrack(
        EventOccurrence occurrence, EventRec e, int band,
        EnemyDat dat, bool inline)
    {
        // Ground banks always receive layer 1's scroll and the Top bank receives
        // layer 3's. If that layer is stopped, though, successive enemies still
        // spawn at the top/bottom of the live screen; putting them on the stopped
        // layer coordinate would collapse every occurrence onto one canvas row.
        if (band is 25 or 75)
            return occurrence.Move1 > 0
                ? (occurrence.UniformDistance1, 0, occurrence.SourceY1, occurrence.UniformSourceY1)
                : (occurrence.PathDistance, -1, 0, 0);
        if (band == 50)
            return occurrence.Move3 > 0
                ? (occurrence.UniformDistance3, 2, occurrence.SourceY3, occurrence.UniformSourceY3)
                : (occurrence.PathDistance, -1, 0, 0);

        // Sky-bank enemies have tempBackMove == 0 in JE_drawEnemy. They are
        // screen-relative unless the exact skyGlue test attaches authored scenery
        // to layer 2. Inline events zero dat3/dat6 before spawning; event 12 uses
        // dat6 only as its bank selector and also clears it before spawning.
        if (inline)
            return occurrence.Move1 > 0
                ? (occurrence.UniformDistance1, -1, 0, 0)
                : (occurrence.PathDistance, -1, 0, 0);
        int fixedMove = e.Type == 12 ? 0 : e.Dat6;
        int eyc = dat.YMove + e.Dat3;
        bool ridesLayer2 = dat.YAccel == 0 &&
            occurrence.Move2 > 0 &&
            fixedMove + (dat.YCAccel != 0 ? 0 : eyc) == occurrence.Move2;
        return ridesLayer2
            ? (occurrence.UniformDistance2, 1, occurrence.SourceY2, occurrence.UniformSourceY2)
            // A free sky enemy is not glued to a texture, but its authored spawn
            // time still advances with curLoc/BG1. In the 1:1 texture view, scale
            // that time onto BG1's physical track instead of leaving it on the
            // fastest-layer axis used by the stretched gameplay view.
            : occurrence.Move1 > 0
                ? (occurrence.UniformDistance1, -1, 0, 0)
                : (occurrence.PathDistance, -1, 0, 0);
    }

    private static void AddObject(List<PlacedObject> result, GameData gd, Level lv, EnemyData ed,
        int[] banks, EventRec e, int band, int baseEy, double scrollPos,
        bool bg3x1, bool bg3x1b, bool smallEnemyAdjust, int dx, int dy,
        int enemyTypeOfs, int randomKey, int pathDistance = -1, EventOccurrence? occurrence = null,
        double bg2ScrollPos = 0, int rawMove2 = 0)
    {
        bool inline = e.Type is 49 or 50 or 51 or 52;
        int enemyId = inline ? 0 : e.Dat + enemyTypeOfs;
        EnemyDat dat = ed.Get(enemyId);

        // Raw-grid placement of a sky-bank object glued to BG2 (the skyGlue test in
        // UniformTrack): it rides layer 2, so anchor it on BG2's scroll axis instead
        // of the BG1/time axis or it drifts off its platform as BG2 outruns BG1.
        if (occurrence == null && band == 0 && !inline && rawMove2 > 0 && dat.YAccel == 0)
        {
            int fixedMove = e.Type == 12 ? 0 : e.Dat6;
            int eyc = dat.YMove + e.Dat3;
            if (fixedMove + (dat.YCAccel != 0 ? 0 : eyc) == rawMove2)
                scrollPos = bg2ScrollPos;
        }

        int shapeBank;
        int spriteIndex;
        int esize;
        byte armor;
        short value;

        if (inline)
        {
            shapeBank = Math.Max(0, (int)e.Dat3);
            spriteIndex = e.Dat;                 // graphic override
            esize = dat.Loaded ? dat.Esize : 0;
            armor = (byte)Math.Max(0, (int)e.Dat6);
            value = dat.Value;
        }
        else
        {
            shapeBank = dat.ShapeBank;
            spriteIndex = dat.EGraphic != null && dat.EGraphic.Length > 0 ? dat.EGraphic[0] : 0;
            esize = dat.Esize;
            armor = dat.Armor;
            value = dat.Value;
        }

        int uniformPathDistance = pathDistance;
        int uniformLayer = -1;
        int gameSourceY = 0;
        int uniformSourceY = 0;
        if (occurrence is EventOccurrence timelineOccurrence)
        {
            pathDistance = timelineOccurrence.PathDistance;
            var track = UniformTrack(timelineOccurrence, e, band, dat, inline);
            uniformPathDistance = track.Distance;
            uniformLayer = track.Layer;
            gameSourceY = track.GameSourceY;
            uniformSourceY = track.UniformSourceY;
            scrollPos = pathDistance;
        }

        var cat = Classify(armor, value, band);

        // Horizontal registration: top band over bg3 is offset one column unless bg3x1.
        float reg = (band == 50 && !bg3x1) ? RegBg3 : RegMain;

        // eventdat2 == -99 keeps the enemyDat default position (startx/starty);
        // -200 selects a random event X in the authored 24..231 range.
        bool isDefault = e.Dat2 == -99;
        bool isRandom = e.Dat2 == -200;
        float ex, ey;
        bool bottomSpawn = e.Type is 17 or 18 or 23 or 32 or 56;
        if (isDefault)
        {
            // JE_makeEnemy initializes both coordinates with +1. A non-zero
            // startxc/startyc randomizes around this point; use the authored center
            // in the static view and flag the horizontal coordinate as approximate.
            int xRange = Math.Abs((int)dat.StartXC);
            int yRange = Math.Abs((int)dat.StartYC);
            ex = dat.Loaded
                ? dat.StartX + 1 + (xRange == 0 ? 0
                    : StableRange(randomKey, enemyTypeOfs * 2, xRange * 2) - xRange)
                : Render.LevelRenderer.CanvasW * 0.5f - reg;
            ey = bottomSpawn
                ? baseEy + (e.Type is 32 or 56 ? 0 : e.Dat5)
                : (dat.Loaded
                    ? dat.StartY + 1 + (yRange == 0 ? 0
                        : StableRange(randomKey, enemyTypeOfs * 2 + 1, yRange * 2) - yRange)
                    : baseEy) + e.Dat5;
        }
        else if (isRandom)
        {
            // T2000 replaces -200 with one random event X in the inclusive 24..231
            // range. A deterministic sample keeps the static view repeatable while
            // preserving the same authored distribution (and one shared X for event 12).
            int eventX = 24 + StableRange(randomKey, 100, 208);
            ex = band switch
            {
                0  => eventX - (lv.MapX - 1) * 24,
                50 => bg3x1 ? eventX - (lv.MapX - 1) * 24 - 12
                            : eventX - lv.MapX3 * 24 - 24 * 2 + 6,
                _  => eventX - (lv.MapX - 1) * 24 - 12,
            };
            ey = baseEy + (e.Type is 32 or 56 ? 0 : e.Dat5);
        }
        else
        {
            // engine screen ex per band (mapXOfs==0 in the neutral frame).
            ex = band switch
            {
                0  => e.Dat2 - (lv.MapX - 1) * 24,                         // sky
                50 => bg3x1 ? e.Dat2 - (lv.MapX - 1) * 24 - 12             // top, locked to bg1
                            : e.Dat2 - lv.MapX3 * 24 - 24 * 2 + 6,         // top, over bg3
                _  => e.Dat2 - (lv.MapX - 1) * 24 - 12,                    // ground / ground2
            };
            ey = baseEy + (e.Type is 32 or 56 ? 0 : e.Dat5);
        }

        if (band == 50 && bg3x1b && !isDefault)
        {
            ex -= 6;
            // JE_createNewEventEnemy raises ordinary top-edge foreground spawns
            // from -28 to -24 in this layout. Bottom spawns subsequently replace Y.
            if (!isDefault && !bottomSpawn) ey += 4;
        }
        if (smallEnemyAdjust && esize == 0)
        {
            ex -= 10;
            if (!bottomSpawn) ey -= 7;
        }
        ex += dx;
        ey += dy;
        bool approxX = isRandom || (isDefault && dat.StartXC != 0);
        float x = ex + reg;                          // screen -> canvas
        float y = (float)(YBase - scrollPos) + ey;

        CompShapes? sheet = (spriteIndex == 999 || spriteIndex == 0) ? null
            : gd.ResolveBankSheet(shapeBank, banks);

        result.Add(new PlacedObject
        {
            X = x, Y = y, EnemyId = enemyId == 0 && inline ? e.Dat : enemyId,
            Cat = cat, Band = band, Time = e.Time, ApproxX = approxX,
            PathDistance = pathDistance, UniformPathDistance = uniformPathDistance,
            UniformLayer = uniformLayer, GameSourceY = gameSourceY,
            UniformSourceY = uniformSourceY, ScreenY = ey,
            Sheet = sheet, SpriteIndex = spriteIndex, Esize = esize,
        });
    }

    private static int SpawnRandomKey(EventRec e, int serial)
    {
        unchecked
        {
            return serial * 1_000_003 ^ e.Time * 65_537 ^ e.Type * 8_191 ^ e.Dat * 131;
        }
    }

    private static int StableRange(int key, int stream, int exclusiveMax)
    {
        if (exclusiveMax <= 1) return 0;
        unchecked
        {
            uint x = (uint)key + (uint)stream * 0x9E3779B9u + 0x85EBCA6Bu;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (int)(x % (uint)exclusiveMax);
        }
    }

    internal static ObjCategory Classify(byte armor, short value, int band)
    {
        // No armour => not a shootable enemy: pickups (by score value) or pure decoration.
        if (armor == 0)
        {
            if (value == 0) return ObjCategory.Decor;
            if (value == 1) return ObjCategory.Datacube;
            if (value < 10000) return ObjCategory.Money;
            return ObjCategory.Powerup;
        }
        // Shootable enemy: split by the layer it's locked to (sky / ground / foreground).
        return band switch
        {
            0  => ObjCategory.EnemyAir,
            50 => ObjCategory.EnemyForeground,
            _  => ObjCategory.EnemyGround,   // 25, 75
        };
    }

    // band + base on-screen ey for each spawn event type
    private static bool IsSpawn(byte type, out int band, out int baseEy)
    {
        band = 0; baseEy = -28;
        switch (type)
        {
            case 15: band = 0; baseEy = -28; return true;
            case 6:  band = 25; baseEy = -28; return true;
            case 7:  band = 50; baseEy = -28; return true;
            case 10: band = 75; baseEy = -28; return true;
            case 12: band = 25; baseEy = -28; return true;
            case 18: band = 0; baseEy = 190; return true;
            case 17: band = 25; baseEy = 190; return true;
            case 23: band = 50; baseEy = 180; return true;
            case 32: band = 50; baseEy = 190; return true;
            case 56: band = 75; baseEy = 190; return true;
            case 49: band = 25; baseEy = -28; return true;
            case 50: band = 0; baseEy = -28; return true;
            case 51: band = 50; baseEy = -28; return true;
            case 52: band = 75; baseEy = -28; return true;
            default: return false;
        }
    }

    public static uint CategoryColor(ObjCategory c) => c switch
    {
        ObjCategory.EnemyAir        => Render.Gfx.Rgba(255, 90, 90),    // red
        ObjCategory.EnemyGround     => Render.Gfx.Rgba(255, 150, 60),   // orange
        ObjCategory.EnemyForeground => Render.Gfx.Rgba(255, 90, 200),   // pink
        ObjCategory.Powerup         => Render.Gfx.Rgba(90, 180, 255),   // blue
        ObjCategory.Money           => Render.Gfx.Rgba(255, 215, 60),   // gold
        ObjCategory.Datacube        => Render.Gfx.Rgba(180, 120, 255),  // violet
        _                           => Render.Gfx.Rgba(150, 150, 150),  // decor grey
    };

    public static string CategoryName(ObjCategory c) => c switch
    {
        ObjCategory.EnemyAir        => "Air enemies",
        ObjCategory.EnemyGround     => "Ground enemies",
        ObjCategory.EnemyForeground => "Foreground enemies",
        ObjCategory.Powerup         => "Powerups",
        ObjCategory.Money           => "Money / gems",
        ObjCategory.Datacube        => "Datacubes",
        _                           => "Scenery / decor",
    };
}
