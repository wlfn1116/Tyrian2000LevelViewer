namespace T2A.Tyrian;

/// <summary>
/// How hard a level is to survive, measured by running it rather than by reading its event
/// list. <see cref="LevelStats"/> answers "what is in this level"; this answers "what does it
/// throw at you, and how much of it can you not simply sidestep".
///
/// The readings come from the same <see cref="GameSim"/> the atlas plays back, at a chosen
/// difficulty, so every engine rule that scales with difficulty is already applied by the time
/// the numbers are taken: armour multipliers, the halved (and re-halved) turret reload above
/// Normal, and the aim bonus that makes tracked shots faster (tyrian2.c:1355, 1512, 5954).
///
/// There is no player firing back, so nothing dies early and every authored turret gets to
/// say its whole piece. That is the point: the reading is of what the level *offers*, on the
/// same terms for every level, not of one person's run through it.
/// </summary>
public sealed class LevelThreat
{
    /// <summary>Profile resolution, matching <see cref="LevelStats.Buckets"/> so the two sets
    /// of strips in the analysis window line up tick-for-tick.</summary>
    public const int Buckets = LevelStats.Buckets;

    /// <summary>The window the "sustained" figures average over. Ten seconds is about as long
    /// as a bad patch has to last before it stops being a spike you can tank through and
    /// starts being a stretch you have to actually play.</summary>
    public const int WindowTicks = 350;

    // ---- How much worse than a plain shot each kind of fire is to get out of the way of. ----
    // A fixed-direction shot is aimed where the turret points, so it only ever threatens you if
    // you are already standing there. An aimed shot is drawn along the line to where you are
    // when it leaves the barrel: you have to move, every time. A homing shot (weapon tx/ty)
    // re-steers toward you every tick for its whole life, so moving is not enough on its own --
    // it has to be outrun or outlasted. These three ratios are the core of the whole model.
    // The ratios are read off hit probability rather than picked by feel. A bullet fired on a
    // fixed heading only threatens the strip of screen it was already pointed down: with a ship
    // about 24px wide in a 264px playfield, one crossing at random has roughly a one in ten
    // chance of mattering. An aimed one starts on the line to where you are standing, so it hits
    // unless you move -- several times likelier.
    //
    // A homing shot sits just below an aimed one rather than above it, which is not the obvious
    // ordering. Tyrian's tracking is the weapon's tx/ty: the shot's velocity is nudged one step
    // per tick and clamped to +/-tx (tyrian2.c:1963-1975), so these are the slow shots that
    // follow you around, not fast ones that cannot miss. They have to be outrun or outlasted
    // rather than sidestepped, which is a different problem and, level for level, a smaller one.
    private const double WPlain = 1.0;
    private const double WAimed = 6.0;
    private const double WHoming = 5.0;

    /// <summary>The difficulty asked for.</summary>
    public int Difficulty { get; private set; }
    /// <summary>The range the level actually ran at. A level can move the engine's difficulty
    /// itself, for the rest of the level, with event 46 (tyrian2.c:6831) -- BOTANY B opens with
    /// one that adds a step, so picking Normal there gets you Hard's turret reload and aim. It
    /// is clamped to Easy at the bottom, so the shift is invisible at Wimp and brutal above it,
    /// which is exactly why such a level's standing jumps as soon as you leave the easy end.</summary>
    public int RunAtLow { get; private set; } = -1;
    public int RunAtHigh { get; private set; } = -1;
    /// <summary>The level moves the engine's difficulty away from the one selected.</summary>
    public bool ShiftsDifficulty => RunAtLow >= 0 && (RunAtLow != Difficulty || RunAtHigh != Difficulty);
    public int Ticks { get; private set; }
    public bool EndedNaturally { get; internal set; }

    // ---- Totals over the run ----
    public long ShotsPlain { get; private set; }
    public long ShotsAimed { get; private set; }
    public long ShotsHoming { get; private set; }
    /// <summary>Shots the engine wanted to fire but had no free slot for. The pool is 60 wide;
    /// a level that keeps hitting it is not merely busy, it is saturated.</summary>
    public long ShotsBlocked { get; private set; }
    /// <summary>Enemies launched straight at you (enemyDat launchtype 1..90 with no relaunch,
    /// tyrian2.c:1633) -- missiles rather than bullets, but the same problem for the player.</summary>
    public long AimedLaunches { get; private set; }

    public long Shots => ShotsPlain + ShotsAimed + ShotsHoming;
    /// <summary>Share of fire that tracks you. The single most legible number here: it is what
    /// separates a level you can pick a lane through from one you have to keep moving in.</summary>
    public double TrackedShare => Shots > 0 ? (ShotsAimed + ShotsHoming) / (double)Shots : 0;

    // ---- Per-tick series, kept only while measuring, then reduced to the figures below. The
    //      three kinds of fire stay apart all the way to Reduce() so the weighting above is
    //      applied in exactly one place -- and so "tracked fire" can be reported on its own.
    private readonly List<float> _plain = new(8192);
    private readonly List<float> _aimed = new(8192);
    private readonly List<float> _homing = new(8192);
    private readonly List<float> _bullets = new(8192);    // enemy shots alive
    private readonly List<float> _enemies = new(8192);    // enemies on screen (engine's count)
    private readonly List<float> _armor = new(8192);      // destructible armour on screen
    private readonly List<float> _hulks = new(8192);      // indestructible bodies on screen

    // ---- Reduced readings ----
    /// <summary>Mean weighted fire per 1000 ticks over the whole level.</summary>
    public double FireRate { get; private set; }
    /// <summary>The same, over the worst <see cref="WindowTicks"/>-tick stretch.</summary>
    public double PeakFireRate { get; private set; }
    /// <summary>The part of <see cref="FireRate"/> contributed by fire that follows you.</summary>
    public double TrackedFireRate { get; private set; }
    /// <summary>The three kinds of fire on their own, per 1000 ticks, before the evasion
    /// weighting -- i.e. how much of each actually arrives, which is what the ranking's
    /// tooltip quotes so the composite never has to be taken on trust.</summary>
    public double PlainRate { get; private set; }
    public double AimedRate { get; private set; }
    public double HomingRate { get; private set; }
    public double BulletDensity { get; private set; }
    public double PeakBulletDensity { get; private set; }
    /// <summary>Things in the way, on screen, weighted by how long they survive being shot at:
    /// an indestructible wall counts a whole one, a six-armour drone about a fifth. Pickups and
    /// decoration are excluded, so this is not the engine's raw enemyOnScreen.</summary>
    public double EnemyDensity { get; private set; }
    public double PeakEnemyDensity { get; private set; }
    public double ArmorDensity { get; private set; }
    /// <summary>Indestructible bodies standing in the playfield -- the walls of a gauntlet, a
    /// minefield, boss plating. Shooting does nothing about these; they only ever get flown
    /// around, which is why they are counted apart from armour.</summary>
    public double HulkDensity { get; private set; }
    public double PeakHulkDensity { get; private set; }
    /// <summary>Fraction of ticks in which the shot pool refused a shot.</summary>
    public double Saturation { get; private set; }

    /// <summary>The headline number. See <see cref="Score"/> for what goes into it.</summary>
    public double Difficulty01 { get; private set; }

    public readonly float[] PressureProfile = new float[Buckets];
    public readonly float[] BulletProfile = new float[Buckets];
    public float PeakPressure { get; private set; }
    public float PeakBullets { get; private set; }

    // =====================================================================
    //  Collection -- called by GameSim while it runs
    // =====================================================================

    /// <param name="damage">The volley slot's own attack value; 0 for a weapon with no table.</param>
    /// <param name="aim">weapons[].aim -- non-zero means the shot is laid along the line to
    /// the player at the moment it is created.</param>
    /// <param name="tx">weapons[].tx / ty -- non-zero means the shot keeps steering toward the
    /// player for its whole life.</param>
    internal void OnShotCreated(int damage, int aim, int tx, int ty)
    {
        // Damage matters, but far less than being hit at all: shields soak the difference and
        // a 1-damage bullet you cannot dodge outranks a 10-damage one you can. Half the weight
        // is flat, half scales with the hit, so a heavy shot counts about twice a light one.
        float hit = (float)(0.5 + 0.5 * Math.Clamp(damage, 1, 20) / 5.0);

        if (tx != 0 || ty != 0) { ShotsHoming++; _tickHoming += hit; }
        else if (aim > 0) { ShotsAimed++; _tickAimed += hit; }
        else { ShotsPlain++; _tickPlain += hit; }
    }

    internal void OnShotBlocked() => _tickBlocked = true;

    internal void OnAimedLaunch()
    {
        AimedLaunches++;
        // A missile launched at you is a homing shot that also has to be shot down.
        _tickHoming += 1f;
    }

    private float _tickPlain, _tickAimed, _tickHoming;
    private bool _tickBlocked;
    private long _blockedTicks;

    internal void OnTick(int bullets, float presence, int armor, int hulks, int running)
    {
        RunAtLow = RunAtLow < 0 ? running : Math.Min(RunAtLow, running);
        RunAtHigh = Math.Max(RunAtHigh, running);
        _plain.Add(_tickPlain);
        _aimed.Add(_tickAimed);
        _homing.Add(_tickHoming);
        _bullets.Add(bullets);
        _enemies.Add(presence);
        _armor.Add(armor);
        _hulks.Add(hulks);
        if (_tickBlocked) _blockedTicks++;
        _tickPlain = _tickAimed = _tickHoming = 0;
        _tickBlocked = false;
    }

    // =====================================================================
    //  Measurement
    // =====================================================================

    /// <summary>
    /// Run <paramref name="lv"/> at <paramref name="difficulty"/> and read the result. The sim
    /// is driven straight rather than through <see cref="SimPlayback"/>: this needs one forward
    /// pass and no keyframes, and a level that never ends on its own is simply cut at
    /// <paramref name="maxTicks"/> -- a boss gate that loops forever contributes its loop at the
    /// same rate however long it is left running, so the rates are unaffected by where it stops.
    /// (<see cref="Score"/> still declines to credit such a level for its length, since the tick
    /// count there is the length of the measurement, not of the level.)
    ///
    /// The simulation is left on its defaults -- vanilla playfield, no Engaged mode, one boss-gate
    /// cycle -- rather than following the atlas's playback settings. A difficulty ranking has to
    /// mean the same thing whatever the person looking at it happens to be watching in.
    /// </summary>
    public static LevelThreat Measure(GameData gd, EpisodeInfo ep, Level lv, int difficulty,
        int maxTicks = 60 * 35 * 5)
    {
        var sim = new GameSim(gd, ep, lv, gd.GetShapeTable(lv.ShapeChar))
        {
            Difficulty = Math.Clamp(difficulty, 0, 10),
        };
        var t = new LevelThreat { Difficulty = sim.Difficulty };
        sim.Threat = t;
        sim.Reset();

        int ticks = 0;
        while (ticks < maxTicks && !sim.Finished)
        {
            sim.Tick(draw: false);
            if (sim.Finished) break;      // that tick did not complete a frame
            ticks++;
            sim.SampleThreat();
        }
        sim.Threat = null;
        t.EndedNaturally = sim.Finished;
        t.Reduce();
        return t;
    }

    private void Reduce()
    {
        Ticks = Math.Max(1, _plain.Count);
        Saturation = _blockedTicks / (double)Ticks;

        // The one place the three kinds of fire are put on a common scale.
        var pressure = new List<float>(Ticks);
        for (int i = 0; i < _plain.Count; i++)
            pressure.Add((float)(WPlain * _plain[i] + WAimed * _aimed[i] + WHoming * _homing[i]));

        FireRate = Mean(pressure) * 1000;
        PeakFireRate = WindowMax(pressure) * 1000;
        TrackedFireRate = (WAimed * Mean(_aimed) + WHoming * Mean(_homing)) * 1000;
        PlainRate = Mean(_plain) * 1000;
        AimedRate = Mean(_aimed) * 1000;
        HomingRate = Mean(_homing) * 1000;
        BulletDensity = Mean(_bullets);
        PeakBulletDensity = WindowMax(_bullets);
        EnemyDensity = Mean(_enemies);
        PeakEnemyDensity = WindowMax(_enemies);
        ArmorDensity = Mean(_armor);
        HulkDensity = Mean(_hulks);
        PeakHulkDensity = WindowMax(_hulks);

        Bucketize(pressure, PressureProfile);
        Bucketize(_bullets, BulletProfile);
        foreach (float v in PressureProfile) PeakPressure = Math.Max(PeakPressure, v);
        foreach (float v in BulletProfile) PeakBullets = Math.Max(PeakBullets, v);

        Difficulty01 = Score();

        _plain.Clear(); _aimed.Clear(); _homing.Clear();
        _bullets.Clear(); _enemies.Clear(); _armor.Clear(); _hulks.Clear();
        _plain.TrimExcess(); _aimed.TrimExcess(); _homing.TrimExcess();
        _bullets.TrimExcess(); _enemies.TrimExcess(); _armor.TrimExcess(); _hulks.TrimExcess();
    }

    /// <summary>
    /// The composite, on a scale where an ordinary campaign level lands near 1.
    ///
    /// Each part is first divided by what that reading is worth in an ordinary level, so every
    /// term arrives as "how many times the usual", and then compressed. The compression is the
    /// important half: TIME WAR's boss fires eight-shot volleys every second tick and pins the
    /// engine's 60-bullet pool wide open, which is four times the fire of anything else in the
    /// game but nothing like four times as hard -- past a certain point more bullets stop
    /// making a difference, because you were already going to be hit. Straight addition of raw
    /// rates lets that one boss outscore the rest of the campaign put together.
    ///
    /// The references are fixed constants rather than the browsed set's own averages, so a
    /// level's score means the same thing whichever episodes happen to be listed beside it.
    /// </summary>
    private double Score()
    {
        // Fire: what has to be dodged. Split between the level's average and its worst
        // sustained stretch, because a level is remembered for its worst ten seconds -- but a
        // level that is never quiet is worse still.
        double fire = 0.45 * FireRate + 0.55 * PeakFireRate;
        // Space denied by fire already in the air, whoever fired it and wherever it was aimed.
        double space = 0.60 * PeakBulletDensity + 0.40 * BulletDensity;
        // Things that can be run into -- and every one of them charges the player on contact.
        // Armour is the destructible half, and also a measure of how long a threat stays a threat
        // once you have started shooting at it; hulks are the half that shooting never removes.
        // Hulks lean on the peak rather than the average because a wall section is a discrete
        // stretch to be threaded, not a background level of clutter.
        double bodies = 0.5 * PeakEnemyDensity + 0.5 * EnemyDensity;
        double hulks = 0.7 * PeakHulkDensity + 0.3 * HulkDensity;

        double t = FireWeight * Term(fire, RefFire)
                 + SpaceWeight * Term(space, RefSpace)
                 + BodyWeight * Term(bodies, RefBodies)
                 + ArmorWeight * Term(ArmorDensity, RefArmor)
                 + HulkWeight * Term(hulks, RefHulks);

        // Endurance. All of the above are rates, and rates say nothing about how long you have
        // to keep it up -- but a level is played on one life, so twice the length at the same
        // intensity really is worse. Deliberately a very flat curve: it separates a three-minute
        // level from a ninety-second one by about a tenth, and never decides the ranking.
        //
        // A level that never ended on its own is left at the reference length rather than
        // credited for the run: those are the ones sitting in a boss gate that no one is there
        // to shoot their way out of, and the tick count says how long the measurement ran, not
        // how long the level is.
        double length = EndedNaturally ? Math.Clamp(Ticks, 1200, 9000) : RefTicks;
        double endurance = Math.Pow(length / RefTicks, EnduranceExp);

        return ScoreScale * t * endurance;
    }

    /// <summary>One term: how many times the ordinary level's reading this is, compressed.
    /// The exponent is what keeps a level that is ten times as loud from scoring ten times as
    /// hard, while leaving the ordering within the pack untouched.</summary>
    private static double Term(double value, double reference)
        => Math.Pow(Math.Max(0, value) / reference, Compression);

    private const double Compression = 0.55;

    // Reference readings: what an ordinary campaign level reads at Normal, so every term arrives
    // near 1 and the weights below can be read directly as "how much of the total this is worth".
    // Most are the measured median across all 70 levels; the hulk figure is not, because more
    // than half the campaign has no indestructible scenery at all and a median of nearly zero
    // would turn every wall into a hundredfold outlier. It is set instead to what a level that
    // does have walls typically shows.
    private const double RefFire = 522.0;    // weighted fire per 1000 ticks
    private const double RefSpace = 6.8;     // bullets in the air
    private const double RefBodies = 11.8;   // presence-weighted bodies on screen
    private const double RefArmor = 673.0;   // destructible armour on screen
    private const double RefHulks = 4.0;     // indestructible bodies on screen
    private const double RefTicks = 6000.0;  // an ordinary level's length
    private const double EnduranceExp = 0.18;

    // Weights sum to 1, so an ordinary level lands on 1.00 and the number is readable as a
    // multiple of "ordinary" rather than as an arbitrary index. Split roughly half and half
    // between what the level shoots at you and what it puts in your way: a gauntlet of
    // indestructible walls and high-armour hulls is not an easy level merely because it is a
    // quiet one, and a swarm of paper drones is not a hard one merely because there are many.
    private const double FireWeight = 0.46;
    private const double SpaceWeight = 0.10;
    private const double BodyWeight = 0.15;
    private const double ArmorWeight = 0.14;
    private const double HulkWeight = 0.15;
    private const double ScoreScale = 1.0;

    // =====================================================================

    private static double Mean(List<float> v)
    {
        if (v.Count == 0) return 0;
        double s = 0;
        foreach (float x in v) s += x;
        return s / v.Count;
    }

    /// <summary>The highest mean over any <see cref="WindowTicks"/>-tick window. A level shorter
    /// than the window is simply its own mean.</summary>
    private static double WindowMax(List<float> v)
    {
        if (v.Count == 0) return 0;
        int w = Math.Min(WindowTicks, v.Count);
        double sum = 0;
        for (int i = 0; i < w; i++) sum += v[i];
        double best = sum;
        for (int i = w; i < v.Count; i++)
        {
            sum += v[i] - v[i - w];
            if (sum > best) best = sum;
        }
        return best / w;
    }

    private static void Bucketize(List<float> v, float[] into)
    {
        Array.Clear(into);
        if (v.Count == 0) return;
        var n = new int[into.Length];
        for (int i = 0; i < v.Count; i++)
        {
            int b = Math.Clamp(i * into.Length / v.Count, 0, into.Length - 1);
            into[b] += v[i];
            n[b]++;
        }
        for (int b = 0; b < into.Length; b++)
            if (n[b] > 0) into[b] /= n[b];
    }
}
