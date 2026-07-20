namespace T2LV.Tyrian;

/// <summary>
/// What a level is made of, read straight off its event list rather than by running it.
/// Every spawn event names an enemyDat entry, and that entry carries the armour it takes to
/// kill and the turrets it shoots back with, so the shape of a level -- where it is quiet,
/// where it piles on -- falls out of the data without a simulation.
///
/// The numbers are a reading of the authored content, not of a playthrough: an enemy that
/// scrolls past untouched still counts. Treat <see cref="Difficulty"/> as a way to sort
/// levels against each other, not as an absolute.
/// </summary>
public sealed class LevelStats
{
    /// <summary>Buckets along the level's event clock. Enough to show structure, few enough
    /// to stay legible as a bar strip a few hundred pixels wide.</summary>
    public const int Buckets = 120;

    public int FileNum;
    public string Name = "";
    public int Duration;              // the last event's time, in event-clock units
    public int SpawnCount;
    /// <summary>Armour that can actually be shot off. Armour 255 is the engine's invulnerable
    /// marker (tyrian2.c:2935) -- scenery and boss plating -- and counting it would rank a
    /// level full of indestructible rock above a level full of real fights.</summary>
    public int TotalArmor;
    public int Invulnerable;          // spawns that can never be destroyed
    public int BossParts;             // spawns belonging to a boss-bar link group
    public double FirePressure;       // expected enemy damage per tick, summed over spawns

    public readonly int[] ByCategory = new int[7];
    public readonly float[] ArmorProfile = new float[Buckets];
    public readonly float[] SpawnProfile = new float[Buckets];
    public readonly float[] FireProfile = new float[Buckets];
    public readonly List<(int Id, int Count, int Armor)> TopEnemies = new();

    public float PeakArmor { get; private set; }
    public float PeakSpawn { get; private set; }
    public float PeakFire { get; private set; }

    /// <summary>
    /// A composite of the three pressures per unit of level time, scaled so a typical level
    /// lands near 1. It is a heuristic ranking key -- the components are shown beside it so
    /// the number never has to be taken on trust.
    /// </summary>
    public double Difficulty { get; private set; }

    public double ArmorRate => Duration > 0 ? TotalArmor / (double)Duration : 0;
    public double SpawnRate => Duration > 0 ? SpawnCount / (double)Duration : 0;
    private double FireRate => Duration > 0 ? FirePressure / Duration : 0;
    /// <summary>Fire per 1000 ticks. The per-tick figure is a thousandth of a damage point
    /// and reads as zero at any sane precision, so the displayed unit is scaled up.</summary>
    public double FirePer1000 => FireRate * 1000;

    public static LevelStats Build(Level lv, EnemyData ed, WeaponData? wd, string name)
    {
        var st = new LevelStats { FileNum = lv.FileNum, Name = name };
        foreach (var ev in lv.Events) if (ev.Time > st.Duration) st.Duration = ev.Time;
        if (st.Duration <= 0) st.Duration = 1;

        var bossLinks = new HashSet<int>();
        foreach (var ev in lv.Events)
            if (ev.Type == 79)
            {
                if (ev.Dat > 0) bossLinks.Add(ev.Dat);
                if (ev.Dat2 > 0) bossLinks.Add(ev.Dat2);
            }

        var counts = new Dictionary<int, (int Count, int Armor)>();

        void Count(EventRec ev, in ObjectPlacer.SpawnInfo info, int band)
        {
            if (info.Sprite <= 0 && info.Armor == 0) return;
            // Only a real entry has turrets; the scratch one events 49-52 use has none.
            var dat = ed.Get(info.EnemyId);
            double fire = info.EnemyId != 0 && dat.Loaded ? FireOf(dat, wd) : 0;

            int bucket = Math.Clamp(ev.Time * Buckets / Math.Max(1, st.Duration), 0, Buckets - 1);
            int armor = info.Armor == 255 ? 0 : info.Armor;
            st.SpawnCount++;
            st.TotalArmor += armor;
            if (info.Armor == 255) st.Invulnerable++;
            st.FirePressure += fire;
            if (ev.Dat4 != 0 && bossLinks.Contains(ev.Dat4)) st.BossParts++;
            st.ByCategory[(int)ObjectPlacer.Classify((byte)Math.Clamp(info.Armor, 0, 255), info.Value, band)]++;
            st.SpawnProfile[bucket] += 1;
            st.ArmorProfile[bucket] += armor;
            st.FireProfile[bucket] += (float)fire;

            counts.TryGetValue(info.EnemyId, out var c);
            counts[info.EnemyId] = (c.Count + 1, info.Armor);
        }

        foreach (var ev in lv.Events)
        {
            if (ev.Type == 12)
            {
                for (int k = 0; k < 4; k++) Count(ev, ObjectPlacer.Describe(ev.Dat + k, ed), 25);
                continue;
            }
            if (!ObjectPlacer.IsSpawn(ev.Type, out int band, out _)) continue;
            Count(ev, ObjectPlacer.ResolveSpawn(ev, ed), band);
        }

        for (int i = 0; i < Buckets; i++)
        {
            st.PeakArmor = Math.Max(st.PeakArmor, st.ArmorProfile[i]);
            st.PeakSpawn = Math.Max(st.PeakSpawn, st.SpawnProfile[i]);
            st.PeakFire = Math.Max(st.PeakFire, st.FireProfile[i]);
        }

        st.TopEnemies.AddRange(counts
            .Select(kv => (Id: kv.Key, kv.Value.Count, kv.Value.Armor))
            .OrderByDescending(t => t.Count).ThenByDescending(t => t.Armor).Take(12));

        // Scale factors picked so an ordinary campaign level lands near 1.0 and the hardest
        // sit near 3; they are presentation, and the three rates below are the real content.
        st.Difficulty = st.ArmorRate * 0.9 + st.FireRate * 260 + st.SpawnRate * 24;
        return st;
    }

    /// <summary>
    /// Expected damage per tick from one spawn of this entry: each armed turret fires every
    /// <c>freq</c> ticks for the summed damage of its weapon's volley. The specials 251..255
    /// are magnets and tints, not shots, so they contribute nothing.
    /// </summary>
    private static double FireOf(in EnemyDat dat, WeaponData? wd)
    {
        double total = 0;
        ReadOnlySpan<(byte Tur, byte Freq)> turrets = stackalloc (byte, byte)[]
            { (dat.Tur0, dat.Freq0), (dat.Tur1, dat.Freq1), (dat.Tur2, dat.Freq2) };
        foreach (var (tur, freq) in turrets)
        {
            if (tur == 0 || tur >= 251 || freq == 0) continue;
            int damage = 1;
            var w = wd?.Get(tur) ?? default;
            if (w.Loaded && w.Attack != null)
            {
                damage = 0;
                for (int k = 0; k < Math.Clamp((int)w.Max, 1, 8); k++) damage += w.Attack[k];
                if (damage == 0) damage = 1;
            }
            total += damage / (double)freq;
        }
        return total;
    }
}
