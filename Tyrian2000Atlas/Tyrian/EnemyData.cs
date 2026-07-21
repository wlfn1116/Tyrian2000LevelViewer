namespace T2A.Tyrian;

public struct EnemyDat
{
    public byte Ani;
    public byte Esize;          // 0 = single sprite, 1 = 2x2 metasprite
    public ushort[] EGraphic;   // [20] frame -> sprite number (frame 0 = resting)
    public byte ExplosionType;  // bit0 clear => ground object
    public byte ShapeBank;      // 21=coins/gems, 26=powerups, else event-5 newsh bank
    public byte Armor;
    public short Value;         // score/cash; 1=datacube, <10000 money, >=10000 powerup code
    public sbyte XMove, YMove;  // per-frame velocity
    public sbyte XAccel, YAccel, XCAccel, YCAccel;
    public short StartX, StartY;
    public sbyte StartXC, StartYC; // random half-range around the default start
    public ushort ELaunchType;
    public ushort EEnemyDie;
    public bool Loaded;

    // Simulation fields (JE_makeEnemy / JE_drawEnemy)
    public byte Tur0, Tur1, Tur2;      // turret weapon ids (251..255 = specials)
    public byte Freq0, Freq1, Freq2;   // fire frequencies
    public byte Animate;               // 0 static, 1 loop, 2 fire-triggered
    public sbyte XRev, YRev;           // cyclic accel reversal points
    public ushort Dgr;                 // damaged graphic
    public sbyte DLevel, DAni;         // damaged threshold / animation
    public byte ELaunchFreq;

    public bool IsGround => (ExplosionType & 1) == 0;
}

/// <summary>
/// enemyDat[] — the unified table of every spawnable object (enemies, buildings,
/// money, gems, powerups, datacubes). Loaded from tyrian.hdt (episodes 1-3) or
/// embedded at the end of tyrian{N}.lvl (episodes 4-5). See episodes.c:JE_loadItemDat.
/// </summary>
public sealed class EnemyData
{
    // Bank bounds and per-entry sizes (episodes.c / lvlmast.h).
    const int WEAP_END1 = 818, WEAP_START2 = 1000, WEAP_NUM = 1818;
    const int ENEMY_END1 = 850, ENEMY_START2 = 1001, ENEMY_NUM = 1850;
    const int PORT_NUM = 60, SPECIAL_NUM = 54, POWER_NUM = 6, SHIP_NUM = 18, OPTION_NUM = 37, SHIELD_NUM = 11;

    const int WeaponSize = 80, PortSize = 82, SpecialSize = 37, PowerSize = 37, ShipSize = 41, OptionSize = 86, ShieldSize = 37;
    const int EnemySize = 77;

    public readonly EnemyDat[] Enemies = new EnemyDat[ENEMY_NUM + 1]; // 0..1850

    /// <summary>Where the enemy table actually started. The item tables that precede it are
    /// sized exactly, so this doubles as the anchor <see cref="ItemData"/> counts backwards from —
    /// which is sturdier than counting forwards past 1638 weapon records.</summary>
    public int EnemyOffset { get; private set; }

    public static EnemyData Load(string dataDir, EpisodeInfo ep)
    {
        var ed = new EnemyData();
        var (raw, start) = LocateBlock(dataDir, ep);
        ed.EnemyOffset = ResolveEnemyOffset(raw, start + ed.PreEnemyOffset());
        ed.ParseAt(raw, ed.EnemyOffset);
        return ed;
    }

    /// <summary>Sanity-check the deterministic offset; only fall back to a scan if it looks wrong.</summary>
    public static int ResolveEnemyOffset(byte[] raw, int estimate)
        => LooksLikeEnemyTable(raw, estimate) ? estimate : DetectEnemyOffset(raw, estimate);

    public int PreEnemyOffset()
    {
        int weapCount = (WEAP_END1 + 1) + (WEAP_NUM - WEAP_START2 + 1);
        return 14 + weapCount * WeaponSize + (PORT_NUM + 1) * PortSize + (SPECIAL_NUM + 1) * SpecialSize
            + (POWER_NUM + 1) * PowerSize + (SHIP_NUM + 1) * ShipSize + (OPTION_NUM + 1) * OptionSize
            + (SHIELD_NUM + 1) * ShieldSize;
    }

    public void ParseAt(byte[] raw, int enemiesOffset)
    {
        Array.Clear(Enemies);
        var r = new ByteReader(raw, enemiesOffset);
        ReadBank(r, this, 0, ENEMY_END1);
        ReadBank(r, this, ENEMY_START2, ENEMY_NUM);
    }

    /// <summary>
    /// The computed pre-enemy size can be off by a record or two (the .hdt and the
    /// .lvl-embedded item blocks differ slightly). Scan near the estimate for the
    /// record-aligned start of the enemy table: a 600-record window where every
    /// record has a plausible esize/shapebank/egraphic only aligns at the true table.
    /// </summary>
    private static int ScoreLite(byte[] raw, int off, int window)
    {
        int good = 0;
        for (int i = 0; i < window; i++)
        {
            int rec = off + i * EnemySize;
            if (rec + EnemySize > raw.Length) break;
            int esize = raw[rec + 20];
            int bank = raw[rec + 63];
            int egr0 = raw[rec + 21] | (raw[rec + 22] << 8);
            if (esize <= 1 && bank >= 0 && bank <= 36 && egr0 < 2000) good++;
        }
        return good;
    }

    private static bool LooksLikeEnemyTable(byte[] raw, int off)
    {
        if (off < 0 || off + 200 * EnemySize > raw.Length) return false;
        return ScoreLite(raw, off, 200) >= 190;
    }

    private static int DetectEnemyOffset(byte[] raw, int approx)
    {
        for (int delta = -3000; delta <= 3000; delta++)
        {
            int o = approx + delta;
            if (o < 0 || o + 600 * EnemySize > raw.Length) continue;
            if (ScoreLite(raw, o, 600) >= 580) return o;
        }
        return approx;
    }

    public static (byte[] raw, int blockStart) LocateBlock(string dataDir, EpisodeInfo ep)
    {
        if (ep.Number <= 3)
        {
            var raw = File.ReadAllBytes(Path.Combine(dataDir, "tyrian.hdt"));
            int start = raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24);
            return (raw, start);
        }
        return (ep.Container.Raw, ep.Container.LvlPos[ep.Container.LvlNum - 1]);
    }

    private static void ReadBank(ByteReader r, EnemyData ed, int lo, int hi)
    {
        for (int i = lo; i <= hi; i++)
        {
            if (r.Pos + EnemySize > r.Length) return; // safety
            var e = new EnemyDat { EGraphic = new ushort[20], Loaded = true };
            e.Ani = r.U8();
            e.Tur0 = r.U8(); e.Tur1 = r.U8(); e.Tur2 = r.U8();
            e.Freq0 = r.U8(); e.Freq1 = r.U8(); e.Freq2 = r.U8();
            e.XMove = r.S8();
            e.YMove = r.S8();
            e.XAccel = r.S8();
            e.YAccel = r.S8();
            e.XCAccel = r.S8();
            e.YCAccel = r.S8();
            e.StartX = r.S16();    // startx
            e.StartY = r.S16();    // starty
            e.StartXC = r.S8();
            e.StartYC = r.S8();
            e.Armor = r.U8();
            e.Esize = r.U8();
            for (int g = 0; g < 20; g++) e.EGraphic[g] = r.U16();
            e.ExplosionType = r.U8();
            e.Animate = r.U8();
            e.ShapeBank = r.U8();
            e.XRev = r.S8();
            e.YRev = r.S8();
            e.Dgr = r.U16();
            e.DLevel = r.S8();
            e.DAni = r.S8();
            e.ELaunchFreq = r.U8();
            e.ELaunchType = r.U16();
            e.Value = r.S16();
            e.EEnemyDie = r.U16();
            ed.Enemies[i] = e;
        }
    }

    public EnemyDat Get(int index)
    {
        if (index < 0 || index >= Enemies.Length) return default;
        return Enemies[index];
    }
}
