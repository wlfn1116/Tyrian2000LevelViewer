namespace T2LV.Tyrian;

/// <summary>A weapon port: what a front or rear gun slot fires at each power level.</summary>
public sealed class PortItem
{
    public string Name = "";
    public byte OpNum;                          // fire modes, 1 or 2
    public readonly ushort[,] Op = new ushort[2, 11];   // [mode, power-1] -> weapons[] index
    public ushort Cost;
    public ushort ItemGraphic;                  // newsh1.shp, 2x2, 1-based
    public ushort PowerUse;
}

/// <summary>A special weapon -- the thing the ship fires when you hold the second button.</summary>
public sealed class SpecialItem
{
    public string Name = "";
    public ushort ItemGraphic;                  // the powerup sheet, 2x2, 1-based
    public byte Pwr;
    public byte SType;                          // effect dispatcher; 1..18 are handled
    public ushort Wpn;                          // weapon fired, or a sidekick id for swap types
}

/// <summary>A generator: how much shot power the ship has and how fast it comes back.</summary>
public sealed class PowerItem
{
    public string Name = "";
    public ushort ItemGraphic;
    public byte Power;
    public sbyte Speed;
    public ushort Cost;
}

/// <summary>A ship hull.</summary>
public sealed class ShipItem
{
    public string Name = "";
    /// <summary>Ship sheet, 2x2, 1-based; over 500 it means the Tyrian 2000 sheet at -500.
    /// 1 is a sentinel (the Nort Ship, drawn as two halves), not an index.</summary>
    public ushort ShipGraphic;
    public ushort ItemGraphic;                  // stored but never drawn -- ships use ShipGraphic
    public byte Ani;
    public sbyte Spd;
    public byte Dmg;
    public ushort Cost;
    public byte BigShipGraphic;                 // OPTION_SHAPES, 1-based: the shop illustration
}

/// <summary>A sidekick ("option"): the pods that fly alongside the ship.</summary>
public sealed class OptionItem
{
    public string Name = "";
    public byte Pwr;                            // charge stages
    public ushort ItemGraphic;                  // newsh1.shp, 2x2, 1-based
    public ushort Cost;
    /// <summary>Mount style: 0 side pod, 1 trailing, 2 front, 3 trailing-single, 4 orbit.
    /// Also picks the body sheet -- 1 and 2 draw 2x2 out of the powerup sheet, the rest
    /// draw a single 12x14 sprite out of the ship sheet.</summary>
    public byte Tr;
    public byte Option;                         // 0 not drawn, 1 always animating, 2 while firing
    public sbyte OpSpd;
    public byte Ani;                            // frames used in Gr
    public readonly ushort[] Gr = new ushort[20];
    public byte WPort;
    public ushort WpNum;
    public byte Ammo;
    public bool Stop;
    public byte IconGr;                         // OPTION_SHAPES, 1-based: the HUD icon

    public bool DrawsFromPowerupSheet => Tr == 1 || Tr == 2;
}

/// <summary>A shield.</summary>
public sealed class ShieldItem
{
    public string Name = "";
    public byte TPwr;                           // capacity
    public byte MPwr;                           // recharge rate
    public ushort ItemGraphic;
    public ushort Cost;
}

/// <summary>
/// The shop's half of the item block -- everything between the weapon table and enemyDat.
/// <see cref="EnemyData"/> already parses the two ends of that block; this fills in the middle:
/// ports, specials, generators, ships, sidekicks and shields, with their names and shop icons.
/// See episodes.c:JE_loadItemDat.
///
/// Offsets are counted BACKWARDS from the resolved enemy table rather than forwards from the
/// file header. The tables are fixed-size, but the .hdt and the .lvl-embedded copies disagree
/// by a record or two at the front, and the enemy table is the one landmark already verified
/// by content.
/// </summary>
public sealed class ItemData
{
    // lvlmast.h:26-41 -- these are the counts, one past each *_NUM.
    public const int PortCount = 61, SpecialCount = 55, PowerCount = 7,
                     ShipCount = 19, OptionCount = 38, ShieldCount = 12;

    private const int PortSize = 82, SpecialSize = 37, PowerSize = 37,
                      ShipSize = 41, OptionSize = 86, ShieldSize = 37;

    public readonly PortItem[] Ports = new PortItem[PortCount];
    public readonly SpecialItem[] Specials = new SpecialItem[SpecialCount];
    public readonly PowerItem[] Powers = new PowerItem[PowerCount];
    public readonly ShipItem[] Ships = new ShipItem[ShipCount];
    public readonly OptionItem[] Options = new OptionItem[OptionCount];
    public readonly ShieldItem[] Shields = new ShieldItem[ShieldCount];

    /// <summary>
    /// The shop's own view of the weapon table: the same records the simulation reads, plus
    /// the widescreen build's in-code rewrites (see <see cref="ForkRestoration"/>). Kept
    /// separate from <see cref="GameData.GetWeapons"/> so patching a weapon a shop sells can
    /// never move an enemy's shot.
    /// </summary>
    public WeaponData Weapons { get; private set; } = new();

    /// <summary>The sidekick slot the restored Charge-Laser Cannon took, or 0 if the episode
    /// had no free one.</summary>
    public int ChargeLaserSlot { get; internal set; }

    /// <summary>False when the block could not be located -- the browser says so instead of
    /// showing a table of noise.</summary>
    public bool Loaded { get; private set; }

    public static ItemData Load(string dataDir, EpisodeInfo ep)
    {
        var it = new ItemData();
        var (raw, start) = EnemyData.LocateBlock(dataDir, ep);

        int enemies = EnemyData.ResolveEnemyOffset(raw, start + new EnemyData().PreEnemyOffset());
        int shields = enemies - ShieldCount * ShieldSize;
        int options = shields - OptionCount * OptionSize;
        int ships = options - ShipCount * ShipSize;
        int powers = ships - PowerCount * PowerSize;
        int specials = powers - SpecialCount * SpecialSize;
        int ports = specials - PortCount * PortSize;
        if (ports < 0 || enemies > raw.Length) return it;

        try
        {
            it.ReadPorts(new ByteReader(raw, ports));
            it.ReadSpecials(new ByteReader(raw, specials));
            it.ReadPowers(new ByteReader(raw, powers));
            it.ReadShips(new ByteReader(raw, ships));
            it.ReadOptions(new ByteReader(raw, options));
            it.ReadShields(new ByteReader(raw, shields));
            it.Weapons = WeaponData.Load(dataDir, ep);
            ForkRestoration.Apply(it.Weapons, it, ep.Number);
            it.Loaded = true;
        }
        catch (ArgumentException) { /* a truncated block leaves Loaded false */ }
        catch (IndexOutOfRangeException) { }
        return it;
    }

    private void ReadPorts(ByteReader r)
    {
        for (int i = 0; i < PortCount; i++)
        {
            var p = new PortItem { Name = r.PascalName() };
            p.OpNum = r.U8();
            for (int m = 0; m < 2; m++)
                for (int k = 0; k < 11; k++) p.Op[m, k] = r.U16();
            p.Cost = r.U16();
            p.ItemGraphic = r.U16();
            p.PowerUse = r.U16();
            Ports[i] = p;
        }
    }

    private void ReadSpecials(ByteReader r)
    {
        for (int i = 0; i < SpecialCount; i++)
            Specials[i] = new SpecialItem
            {
                Name = r.PascalName(),
                ItemGraphic = r.U16(),
                Pwr = r.U8(),
                SType = r.U8(),
                Wpn = r.U16(),
            };
    }

    private void ReadPowers(ByteReader r)
    {
        for (int i = 0; i < PowerCount; i++)
            Powers[i] = new PowerItem
            {
                Name = r.PascalName(),
                ItemGraphic = r.U16(),
                Power = r.U8(),
                Speed = r.S8(),
                Cost = r.U16(),
            };
    }

    private void ReadShips(ByteReader r)
    {
        for (int i = 0; i < ShipCount; i++)
            Ships[i] = new ShipItem
            {
                Name = r.PascalName(),
                ShipGraphic = r.U16(),
                ItemGraphic = r.U16(),
                Ani = r.U8(),
                Spd = r.S8(),
                Dmg = r.U8(),
                Cost = r.U16(),
                BigShipGraphic = r.U8(),
            };
    }

    private void ReadOptions(ByteReader r)
    {
        for (int i = 0; i < OptionCount; i++)
        {
            var o = new OptionItem { Name = r.PascalName() };
            o.Pwr = r.U8();
            o.ItemGraphic = r.U16();
            o.Cost = r.U16();
            o.Tr = r.U8();
            o.Option = r.U8();
            o.OpSpd = r.S8();
            o.Ani = r.U8();
            for (int k = 0; k < 20; k++) o.Gr[k] = r.U16();
            o.WPort = r.U8();
            o.WpNum = r.U16();
            o.Ammo = r.U8();
            o.Stop = r.U8() != 0;
            o.IconGr = r.U8();
            Options[i] = o;
        }
    }

    private void ReadShields(ByteReader r)
    {
        for (int i = 0; i < ShieldCount; i++)
            Shields[i] = new ShieldItem
            {
                Name = r.PascalName(),
                TPwr = r.U8(),
                MPwr = r.U8(),
                ItemGraphic = r.U16(),
                Cost = r.U16(),
            };
    }
}
