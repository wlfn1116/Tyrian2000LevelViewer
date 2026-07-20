namespace T2LV.Tyrian;

/// <summary>
/// The post-load pass the widescreen build runs over the item block before the game ever
/// sees it (episodes.c:597-646). The tables on disk are only half the story: several entries
/// are rewritten in code, and one of those rewrites puts back content Tyrian 2000 cut.
///
/// This is applied to <see cref="ItemData.Weapons"/> only -- the shop's view of the weapon
/// table. The copy the simulation reads (<see cref="GameData.GetWeapons"/>) is deliberately
/// left as it is on disk: some of these patches touch low weapon ids that an enemy turret
/// could name, and playback is validated against the unpatched behaviour.
///
/// Not reproduced: the Zica Laser sx/bx reshaping (episodes.c:136-210) and the superspark sg
/// tagging (212-295), because nothing here renders a bolt in flight; and the placeholder-icon
/// fill (631-644), which is an endless-mode nicety -- a browser of the shipped data should
/// show a blank item as blank.
/// </summary>
public static class ForkRestoration
{
    /// <summary>Six scratch weapon slots in the 819..999 gap the data files never use.</summary>
    public const int ChargeLaserWeaponBase = 900;

    public static void Apply(WeaponData wd, ItemData it, int episodeNum)
    {
        it.ChargeLaserSlot = AddChargeLaserCannon(wd, it);
        ApplyEpisodeDiffs(wd, episodeNum);
        LabelAmmoSidekicks(it);
        FixWobbley(it);
    }

    /// <summary>
    /// Re-add the Charge-Laser Cannon, a five-stage charge sidekick that exists in the DOS
    /// Tyrian level files but was cut from Tyrian 2000 -- its slot was reused for the
    /// Mint-O-Ship. The values are verbatim from the DOS data (episodes.c:65-132); the six
    /// stages go into the unused weapon gap and the sidekick takes the first free "None" slot.
    /// Returns that slot, or 0 if the episode has none.
    /// </summary>
    private static int AddChargeLaserCannon(WeaponData wd, ItemData it)
    {
        int slot = 0;
        for (int i = 1; i < it.Options.Length; i++)
            if (it.Options[i]?.Name.StartsWith("None", StringComparison.Ordinal) == true) { slot = i; break; }
        if (slot == 0) return 0;

        // One straight-up bolt per stage, differing only in rate, damage and sprite: the rate
        // SLOWS and the damage RISES as it charges. Everything else is zero.
        ReadOnlySpan<(byte Repeat, byte Attack, ushort Sprite)> stages = stackalloc (byte, byte, ushort)[]
        {
            (4, 2, 260), (6, 3, 261), (8, 5, 262), (10, 10, 263), (12, 20, 264), (14, 40, 265),
        };
        for (int k = 0; k < stages.Length; k++)
        {
            int id = ChargeLaserWeaponBase + k;
            if (id >= wd.Weapons.Length) break;
            var w = new WeaponDat
            {
                Attack = new byte[8], Del = new byte[8],
                Sx = new sbyte[8], Sy = new sbyte[8],
                Bx = new sbyte[8], By = new sbyte[8],
                Sg = new ushort[8], Loaded = true,
                ShotRepeat = stages[k].Repeat,
                Multi = 1,
                Max = 1,
                Sound = 6,
                Trail = 255,
                ShipBlastFilter = 208,
            };
            w.Attack[0] = stages[k].Attack;
            w.Del[0] = 255;
            w.Sy[0] = 11;                  // travels straight up at speed 11
            w.Sg[0] = stages[k].Sprite;
            wd.Weapons[id] = w;
        }

        var o = it.Options[slot] = new OptionItem
        {
            Name = "Charge-Laser Cannon",
            Pwr = 5,                       // five charge stages -- its defining trait
            ItemGraphic = 193,
            Cost = 30000,
            Tr = 0,                        // side pod, so the body draws from the ship sheet
            Option = 1,                    // always animating
            OpSpd = 3,
            Ani = 12,
            WPort = 4,                     // fires through the power-drain port
            WpNum = ChargeLaserWeaponBase,
            Ammo = 0,                      // a charge weapon, not an ammo weapon
            Stop = true,
            IconGr = 6,
        };
        // Four base frames, three ticks each. They are spaced 19 apart because the drawn
        // sprite is gr[frame] + charge, so charging walks into the neighbouring sprites.
        ReadOnlySpan<ushort> frames = stackalloc ushort[] { 87, 87, 87, 106, 106, 106, 125, 125, 125, 144, 144, 144 };
        for (int k = 0; k < frames.Length; k++) o.Gr[k] = frames[k];
        return slot;
    }

    /// <summary>
    /// The weapons whose data genuinely differs between the episode 1-3 and 4-5 copies of the
    /// item block (episodes.c:301-387). The fork's default is Auto, which means "whatever this
    /// episode shipped with" -- so this is exactly why the browsers keep an episode picker.
    /// The five sound-only diffs are omitted; nothing here plays sound.
    /// </summary>
    private static void ApplyEpisodeDiffs(WeaponData wd, int episodeNum)
    {
        bool ep45 = episodeNum > 3;

        // Xega Ball (720): a six-bolt spread in 1-3, one heavier bolt in 4-5.
        if (Weapon(wd, 720, out int xega))
        {
            ref var x = ref wd.Weapons[xega];
            if (ep45)
            {
                x.Multi = 1; x.Max = 1;
                x.Attack[0] = 8; x.Del[0] = 255;
                x.Sx[0] = 0; x.Sy[0] = 10; x.Bx[0] = -20; x.By[0] = -15;
                x.Sg[0] = 60022;
            }
            else
            {
                ReadOnlySpan<sbyte> sx = stackalloc sbyte[] { 0, 0, -8, 8, -10, 10 };
                ReadOnlySpan<sbyte> sy = stackalloc sbyte[] { 10, 10, 8, 8, 0, 0 };
                ReadOnlySpan<sbyte> bx = stackalloc sbyte[] { -20, -20, -30, -10, -40, 0 };
                ReadOnlySpan<sbyte> by = stackalloc sbyte[] { -15, -15, -10, -10, -10, -10 };
                x.Multi = 2; x.Max = 6;
                for (int i = 0; i < 6; i++)
                {
                    x.Attack[i] = 4; x.Del[i] = 255;
                    x.Sx[i] = sx[i]; x.Sy[i] = sy[i]; x.Bx[i] = bx[i]; x.By[i] = by[i];
                    x.Sg[i] = 60022;
                }
            }
        }

        // MicroSol option 5 (23): eight weak accelerating bolts in 1-3, two in 4-5.
        if (Weapon(wd, 23, out int micro))
        {
            ref var m = ref wd.Weapons[micro];
            if (ep45)
            {
                m.Drain = 40; m.Multi = 2; m.Max = 2; m.Acceleration = 0;
                for (int i = 0; i < 2; i++)
                {
                    m.Attack[i] = 1; m.Del[i] = 255;
                    m.Sx[i] = -14; m.Sy[i] = 0; m.By[i] = 0; m.Sg[i] = 99;
                }
                m.Bx[0] = -8; m.Bx[1] = 8;
            }
            else
            {
                ReadOnlySpan<sbyte> sx = stackalloc sbyte[] { 1, -1, 2, -2, 3, -3, 4, -4 };
                ReadOnlySpan<sbyte> sy = stackalloc sbyte[] { 3, 3, 2, 2, 1, 1, 0, 0 };
                m.Drain = 160; m.Multi = 8; m.Max = 8; m.Acceleration = 1;
                for (int i = 0; i < 8; i++)
                {
                    m.Attack[i] = 3; m.Del[i] = 255;
                    m.Sx[i] = sx[i]; m.Sy[i] = sy[i]; m.Bx[i] = 0; m.By[i] = 0; m.Sg[i] = 73;
                }
            }
        }

        // Flare / Super Bomb (622): the first four blast frames use a different sprite.
        if (Weapon(wd, 622, out int flare))
            for (int i = 0; i < 4; i++) wd.Weapons[flare].Sg[i] = (ushort)(ep45 ? 21 : 20);

        // Beno Wallop Beam (736) fires a second bolt in 4-5 only.
        if (Weapon(wd, 736, out int wallop))
        {
            ref var w = ref wd.Weapons[wallop];
            if (ep45)
            {
                w.Multi = 2; w.Max = 2;
                w.Attack[1] = 10; w.Del[1] = 255;
                w.Sx[1] = 0; w.Sy[1] = 10; w.Bx[1] = 0; w.By[1] = -2;
                w.Sg[1] = 7029;
            }
            else { w.Multi = 1; w.Max = 1; }   // slot 1 goes back to being unused padding
        }
    }

    private static bool Weapon(WeaponData wd, int id, out int index)
    {
        index = id;
        return id >= 0 && id < wd.Weapons.Length && wd.Weapons[id].Loaded;
    }

    /// <summary>
    /// Spell out the magazine size in the name of any ammo sidekick that lacks it, aligned to
    /// column 15 the way the entries that already carry one are (episodes.c:396-424).
    /// </summary>
    private static void LabelAmmoSidekicks(ItemData it)
    {
        foreach (var o in it.Options)
        {
            if (o == null || o.Ammo == 0) continue;
            if (o.Name.StartsWith("None", StringComparison.Ordinal) || o.Name.Contains("Ammo")) continue;

            string label = $"Ammo {o.Ammo}";
            int col = o.Name.Length < 15 ? 15 : o.Name.Length + 1;
            if (col + label.Length > 30) col = Math.Max(0, 30 - label.Length);
            o.Name = o.Name.PadRight(col) + label;
        }
    }

    /// <summary>Wobbley's first frame ships as a stray neighbouring pod sprite, so its loop
    /// flashes the wrong graphic once a cycle; snap it to the rest frame (episodes.c:624-629).</summary>
    private static void FixWobbley(ItemData it)
    {
        foreach (var o in it.Options)
            if (o != null && o.Name.StartsWith("Wobbley", StringComparison.Ordinal) && o.Gr[0] == 166)
                o.Gr[0] = o.Gr[1];
    }

}
