namespace T2LV.Tyrian;

public struct WeaponDat
{
    public byte Multi;
    public ushort WeapAni;
    public byte Max;
    public byte Tx, Ty, Aim;
    public byte[] Attack;    // [8]
    public byte[] Del;       // [8]
    public sbyte[] Sx, Sy;   // [8]
    public sbyte[] Bx, By;   // [8]
    public ushort[] Sg;      // [8]
    public sbyte Acceleration, AccelerationX;
    public byte Sound;
    /// <summary>Palette band a hit from this weapon recolours the enemy with for one frame —
    /// Tyrian's damage flash (tyrian2.c writes it to enemy.filter, blit_sprite2_filter ORs it
    /// over the sprite's value nibble).</summary>
    public byte ShipBlastFilter;
    public bool Loaded;
}

/// <summary>
/// weapons[] — the enemy/player weapon table from the same item block as enemyDat
/// (tyrian.hdt for episodes 1-3, embedded in tyrian{4,5}.lvl). Only the fields the
/// enemy-fire simulation needs are kept. See episodes.c:JE_loadItemDat.
/// </summary>
public sealed class WeaponData
{
    public const int WEAP_END1 = 818, WEAP_START2 = 1000, WEAP_NUM = 1818;
    private const int WeaponSize = 80;

    public readonly WeaponDat[] Weapons = new WeaponDat[WEAP_NUM + 1];

    public static WeaponData Load(string dataDir, EpisodeInfo ep)
    {
        var wd = new WeaponData();
        var (raw, start) = EnemyData.LocateBlock(dataDir, ep);
        int off = start + 14;   // itemNum[7] header
        var r = new ByteReader(raw, off);
        ReadBank(r, wd, 0, WEAP_END1);
        ReadBank(r, wd, WEAP_START2, WEAP_NUM);
        return wd;
    }

    private static void ReadBank(ByteReader r, WeaponData wd, int lo, int hi)
    {
        for (int i = lo; i <= hi; i++)
        {
            if (r.Pos + WeaponSize > r.Length) return;
            var w = new WeaponDat
            {
                Attack = new byte[8], Del = new byte[8],
                Sx = new sbyte[8], Sy = new sbyte[8],
                Bx = new sbyte[8], By = new sbyte[8],
                Sg = new ushort[8], Loaded = true,
            };
            r.Pos += 2;                    // drain
            r.Pos += 1;                    // shotrepeat
            w.Multi = r.U8();
            w.WeapAni = r.U16();
            w.Max = r.U8();
            w.Tx = r.U8();
            w.Ty = r.U8();
            w.Aim = r.U8();
            for (int k = 0; k < 8; k++) w.Attack[k] = r.U8();
            for (int k = 0; k < 8; k++) w.Del[k] = r.U8();
            for (int k = 0; k < 8; k++) w.Sx[k] = r.S8();
            for (int k = 0; k < 8; k++) w.Sy[k] = r.S8();
            for (int k = 0; k < 8; k++) w.Bx[k] = r.S8();
            for (int k = 0; k < 8; k++) w.By[k] = r.S8();
            for (int k = 0; k < 8; k++) w.Sg[k] = r.U16();
            w.Acceleration = r.S8();
            w.AccelerationX = r.S8();
            r.Pos += 1;                    // circlesize
            w.Sound = r.U8();
            r.Pos += 1;                    // trail
            w.ShipBlastFilter = r.U8();
            wd.Weapons[i] = w;
        }
    }

    public WeaponDat Get(int index)
        => index >= 0 && index < Weapons.Length ? Weapons[index] : default;
}
