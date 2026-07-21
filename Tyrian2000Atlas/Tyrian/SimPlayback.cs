namespace T2A.Tyrian;

/// <summary>
/// Drives a GameSim along a precomputed timeline: runs the whole level once headless to
/// learn its duration (snapshotting keyframes on the way), then serves random access to
/// any tick for play/rewind/fast-forward/scrubbing by restoring the nearest keyframe and
/// re-simulating forward. Deterministic by construction.
/// </summary>
public sealed class SimPlayback
{
    public enum HoldLoopKind
    {
        None,
        ScriptedLoop,
        EnemyHold,
        RouteLoop,
    }

    public sealed record LoopRegion(
        int StartTick, int EndTick, int[] CycleEnds, HoldLoopKind Kind, int EventIndex);

    public const int KeyInterval = 120;          // snapshot every ~3.4s of game time
    private const int WarmupDraw = 6;            // trailing drawn ticks so feedback filters settle

    public readonly GameSim Sim;
    public int Duration { get; private set; }    // last playable tick (0-based position max)
    public bool EndedNaturally { get; private set; }
    public int CurrentTick { get; private set; } = -1;

    private readonly List<LoopRegion> _loopRegions = new();
    /// <summary>Finite previews of every player-gated section along the full route.</summary>
    public IReadOnlyList<LoopRegion> LoopRegions => _loopRegions;
    public int LoopStartTick => _loopRegions.Count == 0 ? -1 : _loopRegions[0].StartTick;
    public bool LoopDetected => _loopRegions.Count > 0;
    public HoldLoopKind LoopClassification => _loopRegions.Count == 0
        ? HoldLoopKind.None : _loopRegions[0].Kind;
    public int[] LoopCycleEnds => _loopRegions.Count == 0
        ? Array.Empty<int>() : _loopRegions[0].CycleEnds;

    public string LoopSummary => _loopRegions.Count switch
    {
        0 => "capped",
        1 when _loopRegions[0].Kind == HoldLoopKind.ScriptedLoop =>
            $"enemy-gated loop, {_loopRegions[0].CycleEnds.Length} cycles kept",
        1 when _loopRegions[0].Kind == HoldLoopKind.EnemyHold =>
            $"enemy-gated hold, {RegionSeconds(_loopRegions[0])} s preview",
        1 => $"conditional route loop, {_loopRegions[0].CycleEnds.Length} cycles kept",
        _ => $"{_loopRegions.Count} loop/hold sections previewed",
    };

    /// <summary>How long a retained region runs, in whole seconds — an enemy hold has no
    /// cycle count to report, so its span is what there is to say about it.</summary>
    public static int RegionSeconds(LoopRegion r) =>
        (int)Math.Round((r.EndTick - r.StartTick) / GameSim.TicksPerSecond);

    /// <summary>
    /// Called after each tick that was stepped live and forward, so the caller can drain
    /// <see cref="GameSim.SoundQueue"/> and act on a music change. Deliberately not fired
    /// while seeking: a scrub restores a keyframe and re-runs up to 120 ticks as fast as it
    /// can, and every one of those would otherwise shout.
    /// </summary>
    public Action? OnLiveTick;

    /// <summary>Executed events (tick, type, index, backward-jump), for timeline markers.</summary>
    public List<GameSim.EventExec> Events = new();
    /// <summary>Per-tick enemies-on-screen count (clamped to 255), for the density strip.</summary>
    public byte[] Density = Array.Empty<byte>();
    /// <summary>Busiest tick of the run, branch included. The density strip scales against
    /// this rather than a fixed ceiling: levels run anywhere from a handful of enemies to
    /// fifty-odd during a boss, and a constant divisor left every busy level a flat red wall
    /// with its shape — the lulls, the waves, where the boss actually starts — clipped off.</summary>
    public int DensityScale => Math.Max(8, Math.Max(_densityPeak, _branchPeak));
    private int _densityPeak, _branchPeak;

    private readonly List<GameSim.Snapshot> _keys = new();
    public long PrecomputeMs { get; private set; }

    private readonly int _maxTicks;

    public SimPlayback(GameSim sim, int maxTicks)
    {
        Sim = sim;
        _maxTicks = Math.Max(1, maxTicks);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        sim.Reset();
        sim.EventLog = Events;
        var gatePreviews = new List<GameSim.GatePreview>();
        sim.GatePreviewLog = gatePreviews;

        var density = new List<byte>(8192);
        _keys.Add(sim.TakeSnapshot());               // state at start of tick 1
        int ticks = 0;
        while (ticks < maxTicks && !sim.Finished)
        {
            sim.Tick(draw: false);
            if (sim.Finished) break;                 // that tick didn't complete a frame
            ticks++;
            density.Add((byte)Math.Clamp(sim.EnemyOnScreen, 0, 255));
            if (ticks % KeyInterval == 0)
                _keys.Add(sim.TakeSnapshot());
        }
        sim.EventLog = null;
        sim.GatePreviewLog = null;
        Duration = Math.Max(1, ticks);
        EndedNaturally = sim.Finished;
        Density = density.ToArray();
        foreach (byte d in Density) if (d > _densityPeak) _densityPeak = d;

        foreach (var gate in gatePreviews.OrderBy(g => g.StartTick))
        {
            int start = Math.Clamp(gate.StartTick, 1, Duration);
            int end = Math.Clamp(gate.EndTick, start, Duration);
            int[] cycleEnds = gate.CycleEnds.Select(t => Math.Clamp(t, start, end)).ToArray();
            _loopRegions.Add(new LoopRegion(
                start, end, cycleEnds,
                gate.Kind switch
                {
                    GameSim.PreviewKind.EnemyLoop => HoldLoopKind.ScriptedLoop,
                    GameSim.PreviewKind.EnemyHold => HoldLoopKind.EnemyHold,
                    _ => HoldLoopKind.RouteLoop,
                },
                gate.EventIndex));
        }

        if (!EndedNaturally)
        {
            DetectHoldingLoop();
            NoteStandingHold(sim);
        }

        PrecomputeMs = sw.ElapsedMilliseconds;
        SeekTo(1);
    }

    /// <summary>
    /// A level that never ends without a player is usually replaying a scripted attack
    /// loop while it waits for the boss (or linked enemies) to die. Only accept a loop
    /// whose event still recurs near the precompute cap, then retain
    /// Sim.PreviewLoopCycles cycles (matching the scripted-gate preview).
    /// </summary>
    private void DetectHoldingLoop()
    {
        const int minCycleTicks = 5;

        (List<int> Ticks, bool Backward, int Cycle)? FindRecurring(bool backwardOnly)
        {
            (List<int> Ticks, bool Backward, int Cycle)? best = null;
            foreach (var group in Events
                         .Where(e => !backwardOnly || e.Backward)
                         .GroupBy(e => e.Index))
            {
                var ticks = group.Select(e => e.Tick).Distinct().Order().ToList();
                if (ticks.Count < 4) continue;

                var recentIntervals = new List<int>();
                int first = Math.Max(1, ticks.Count - 8);
                for (int i = first; i < ticks.Count; i++)
                {
                    int interval = ticks[i] - ticks[i - 1];
                    if (interval > 0) recentIntervals.Add(interval);
                }
                if (recentIntervals.Count < 3) continue;
                recentIntervals.Sort();
                int cycle = recentIntervals[recentIntervals.Count / 2];
                if (cycle < minCycleTicks) continue;

                // A loop from earlier in the level is not the reason the sim hit its
                // cap. Its latest recurrence must be close to the capped frame.
                int lateWindow = Math.Max(cycle * 2, (int)(5 * GameSim.TicksPerSecond));
                if (Duration - ticks[^1] > lateWindow) continue;

                bool hasBackward = group.Any(e => e.Backward);
                if (best == null ||
                    (hasBackward && !best.Value.Backward) ||
                    (hasBackward == best.Value.Backward && ticks[^1] > best.Value.Ticks[^1]) ||
                    (hasBackward == best.Value.Backward && ticks[^1] == best.Value.Ticks[^1] &&
                     cycle > best.Value.Cycle))
                    best = (ticks, hasBackward, cycle);
            }
            return best;
        }

        // A backward event jump is the strongest signal. Event 76 returns outside
        // JE_eventSystem, so SQUADRON-style loops need the repeated-event fallback.
        var recurring = FindRecurring(backwardOnly: true) ?? FindRecurring(backwardOnly: false);
        if (recurring != null)
        {
            var ticks = recurring.Value.Ticks;
            int cycle = recurring.Value.Cycle;
            int tolerance = Math.Max(2, cycle / 10);

            // Walk back to the first stable recurrence, keeping the authored lead-in.
            int keep = Sim.PreviewLoopCycles;
            int start = ticks.Count - 1 - keep;
            while (start > 0 && Math.Abs((ticks[start] - ticks[start - 1]) - cycle) <= tolerance)
                start--;

            if (start + keep < ticks.Count)
            {
                var cycleEnds = new int[keep];
                for (int n = 0; n < keep; n++) cycleEnds[n] = ticks[start + 1 + n];
                _loopRegions.Add(new LoopRegion(ticks[start], cycleEnds[^1], cycleEnds,
                    HoldLoopKind.ScriptedLoop, EventIndex: -1));
                Trim(Math.Min(Duration, cycleEnds[^1]));
                return;
            }
        }

        // No event loop: the map is stationary while a live boss/enemy holds it. Keep
        // enough of that state to inspect and seek without fabricating a loop count.
        int lastEv = 1;
        foreach (var e in Events) lastEv = Math.Max(lastEv, e.Tick);
        // Releasing a hold is not an event: GameSim.PreviewQuietEnemyHold logs its gate and
        // moves the map on without writing to the event list. Measuring dead air from the last
        // EVENT therefore re-reports the FIRST standoff of a level that stood through several,
        // and the Trim below then cut the run back to it -- taking every later hold with it.
        // Dead air is time since the last thing that happened, of either kind.
        foreach (var r in _loopRegions) lastEv = Math.Max(lastEv, r.EndTick);
        int holdTicks = (int)(Math.Max(1, Sim.PreviewHoldSeconds) * GameSim.TicksPerSecond);
        // Thirty seconds of dead air is the "this run is stuck" test, and stays fixed: the
        // hold setting says how much of a standoff to keep, not what counts as one. Scaling
        // this too would make the section vanish from the timeline as the slider went up.
        if (Duration - lastEv > (int)(30 * GameSim.TicksPerSecond))
        {
            int end = Math.Min(Duration, lastEv + holdTicks);
            _loopRegions.Add(new LoopRegion(lastEv, end, Array.Empty<int>(),
                HoldLoopKind.EnemyHold, EventIndex: -1));
            Trim(end);
        }
    }

    /// <summary>
    /// A hold is logged as a gate when the preview releases it, so a run that stops while
    /// one is still standing — the tick cap landed inside the standoff, or the hold is set
    /// longer than the run had left to give it — records nothing, and the section that
    /// actually ended the run would show as bare timeline. Hatch what was watched of it.
    /// Whatever the detector above already found takes precedence.
    /// </summary>
    private void NoteStandingHold(GameSim sim)
    {
        if (_loopRegions.Any(r => r.EndTick >= Duration - 1)) return;

        int start = sim.PendingHoldStartTick;
        // Two seconds of standoff before it is worth drawing: backMove parks at 0 during
        // ordinary map-stop events too, and a cap landing on one of those is not a hold.
        if (start > 0 && Duration - start >= (int)(2 * GameSim.TicksPerSecond))
            _loopRegions.Add(new LoopRegion(start, Duration, Array.Empty<int>(),
                HoldLoopKind.EnemyHold, EventIndex: -1));
    }

    private void Trim(int newDuration)
    {
        Duration = Math.Max(1, newDuration);
        if (Density.Length > Duration) Density = Density[..Duration];
        Events.RemoveAll(e => e.Tick > Duration);
        int lastKey = Duration / KeyInterval;
        if (_keys.Count > lastKey + 1)
            _keys.RemoveRange(lastKey + 1, _keys.Count - lastKey - 1);

        // Regions already added were clamped against the duration the run had BEFORE this
        // trim. Anything now past the end is unreachable, and the timeline does not skip it --
        // it clamps both ends of the hatch to the last tick and draws a hairline at the
        // right-hand edge (App.DrawTimelineBar), which is how a hold that really was found
        // came to look like one that had gone. Clip what straddles the new end, drop the rest.
        for (int i = _loopRegions.Count - 1; i >= 0; i--)
        {
            var r = _loopRegions[i];
            if (r.StartTick >= Duration) { _loopRegions.RemoveAt(i); continue; }
            if (r.EndTick <= Duration) continue;
            _loopRegions[i] = r with
            {
                EndTick = Duration,
                CycleEnds = r.CycleEnds.Where(t => t <= Duration).ToArray(),
            };
        }
    }

    /// <summary>Position on frame <paramref name="tick"/> (1..<see cref="DisplayEnd"/>) and
    /// draw it. Inside the live branch the branch's own keyframes are used, so scrubbing a
    /// diverged run is as exact as scrubbing the prediction.</summary>
    public void SeekTo(int tick)
    {
        tick = Math.Clamp(tick, 1, Math.Max(DisplayEnd, CurrentTick));
        if (tick == CurrentTick) return;

        // Continue live when the target is just ahead of the current frame.
        if (CurrentTick >= 0 && tick > CurrentTick && tick - CurrentTick <= KeyInterval)
        {
            Advance(tick - CurrentTick);
            return;
        }

        int at;
        if (Branched && tick >= BranchTick)
        {
            // Nearest branch key at or before the target. The branch always keeps one on the
            // tick it started, so this can never fall back into pre-branch state.
            int k = _branchKeys.Count - 1;
            while (k > 0 && _branchKeys[k].Tick > tick) k--;
            Sim.RestoreSnapshot(_branchKeys[k].Key);
            at = _branchKeys[k].Tick;
        }
        else
        {
            int key = Math.Min((tick - 1) / KeyInterval, _keys.Count - 1);
            Sim.RestoreSnapshot(_keys[key]);
            at = key * KeyInterval;                 // frames completed so far
        }
        Sim.ClearScreens();
        CurrentTick = at;
        while (CurrentTick < tick && Step(draw: CurrentTick >= tick - WarmupDraw)) { }
    }

    /// <summary>
    /// Step forward from the current frame; draws only the trailing ticks. With
    /// <paramref name="audio"/> set, <see cref="OnLiveTick"/> fires once per tick so the
    /// sound queue is drained at the engine's own rate rather than in one burst.
    /// </summary>
    public void Advance(int n, bool audio = false)
    {
        if (n <= 0) return;
        int target = Math.Min(CurrentTick + n, PlayEnd);
        n = target - CurrentTick;
        for (int i = 1; i <= n; i++)
        {
            if (!Step(draw: i > n - WarmupDraw)) break;
            if (audio) OnLiveTick?.Invoke();
        }
    }

    /// <summary>
    /// One frame forward, keeping <see cref="CurrentTick"/> honest and — on the live branch,
    /// past everything it has already lived through — recording what the frame contained.
    /// False when the tick did not complete a frame, which is the level ending.
    /// </summary>
    private bool Step(bool draw)
    {
        if (Sim.Finished) return false;
        int next = CurrentTick + 1;
        bool record = Branched && next > BranchEnd;
        if (record) { Sim.EventLog = _branchEvents; Sim.GatePreviewLog = _branchGates; }
        Sim.Tick(draw);
        if (record) { Sim.EventLog = null; Sim.GatePreviewLog = null; }

        if (Sim.Finished)
        {
            if (record) BranchEndedNaturally = true;
            return false;
        }
        CurrentTick = next;
        if (record) RecordBranchFrame(next);
        return true;
    }

    /// <summary>Re-render the current frame (after a view-option change).</summary>
    public void RedrawCurrent()
    {
        int t = Math.Max(1, CurrentTick);
        CurrentTick = -1;
        SeekTo(t);
    }

    /// <summary>Nothing further to play. On a branch that is the frontier once the branch has
    /// finished — not <see cref="PlayEnd"/>, which is only the cap it is allowed to explore to.</summary>
    public bool AtEnd => Branched
        ? BranchDone && CurrentTick >= BranchEnd
        : CurrentTick >= Duration;

    // =====================================================================
    //  The live branch.
    //
    //  Everything above is a prediction: one headless run of the level with
    //  nobody shooting back. The moment the atlas interferes -- click-to-kill
    //  is the only way in so far -- that prediction stops describing what is
    //  on screen, and the timeline used to have no way to say so. A boss shot
    //  out of a hold left playback pinned to the predicted end with the bar
    //  still hatching a standoff that was over, and the level past it, which
    //  the kill had just unlocked, was unreachable.
    //
    //  So the divergence gets its own recording. From the tick it starts,
    //  live frames are logged exactly as the precompute logs them -- enemy
    //  density, executed events, gate previews -- and keyframed at the same
    //  interval, so the branch scrubs like the baseline does and is free to
    //  run on past where the prediction stopped.
    //
    //  The baseline is never touched. Before BranchTick the two runs are the
    //  same run, so seeking back there is exact and the branch simply waits
    //  to be returned to.
    // =====================================================================

    private readonly List<(int Tick, GameSim.Snapshot Key)> _branchKeys = new();
    private readonly List<byte> _branchDensity = new();          // [i] = tick BranchTick + 1 + i
    private readonly List<GameSim.EventExec> _branchEvents = new();
    private readonly List<GameSim.GatePreview> _branchGates = new();
    private readonly List<LoopRegion> _branchRegions = new();

    /// <summary>Tick the run was first interfered with, or -1 while it still matches the
    /// prediction. Everything before it is common to both.</summary>
    public int BranchTick { get; private set; } = -1;
    /// <summary>Furthest tick the branch has actually been simulated to.</summary>
    public int BranchEnd { get; private set; }
    /// <summary>How many separate interferences fed the branch that is standing now.</summary>
    public int Interferences { get; private set; }
    public bool BranchEndedNaturally { get; private set; }
    public bool Branched => BranchTick > 0;
    /// <summary>The branch has nowhere left to go: the level ended, or it hit the same tick
    /// cap the precompute was given.</summary>
    public bool BranchDone => Branched && (BranchEndedNaturally || BranchEnd >= _maxTicks);

    public IReadOnlyList<LoopRegion> BranchRegions => _branchRegions;
    public IReadOnlyList<GameSim.EventExec> BranchEvents => _branchEvents;

    /// <summary>The widest tick there is anything to show at: the prediction, plus however far
    /// the branch has outlived it. Monotonic, so the bar's scale never shrinks under the hand.</summary>
    public int DisplayEnd => Branched ? Math.Max(Duration, BranchEnd) : Duration;
    /// <summary>How far playing forward may run. A diverged run has no known end, so it may
    /// explore to the cap; an undiverged one stops where the precompute did.</summary>
    public int PlayEnd => Branched ? _maxTicks : Duration;

    /// <summary>
    /// Every retained region that owns <paramref name="tick"/>, from whichever run that tick
    /// belongs to: past the divergence the branch's own gates are the ones being played, and a
    /// gate the prediction had there may simply not exist any more — which is the whole point
    /// of shooting one. Callers get null in that case rather than the ghost's answer.
    /// </summary>
    public IReadOnlyList<LoopRegion> RegionsFor(int tick) =>
        Branched && tick >= BranchTick ? _branchRegions : _loopRegions;

    /// <summary>The region covering <paramref name="tick"/> on the run that owns it, if any.</summary>
    public LoopRegion? RegionAt(int tick) =>
        RegionsFor(tick).FirstOrDefault(r => tick >= r.StartTick && tick <= r.EndTick);

    /// <summary>Enemies on screen at <paramref name="tick"/> along the branch, or -1 where the
    /// branch does not reach.</summary>
    public int BranchDensityAt(int tick)
    {
        int i = tick - BranchTick - 1;
        return !Branched || i < 0 || i >= _branchDensity.Count ? -1 : _branchDensity[i];
    }

    /// <summary>Enemies on screen at <paramref name="tick"/> on whichever run owns it —
    /// the branch where it reaches, the prediction elsewhere. -1 where neither has a reading.
    /// For readouts that just want the truth at a tick; the timeline draws both layers and so
    /// asks for them separately.</summary>
    public int DensityAt(int tick)
    {
        int live = BranchDensityAt(tick);
        if (live >= 0) return live;
        return tick >= 1 && tick <= Density.Length ? Density[tick - 1] : -1;
    }

    /// <summary>
    /// The live sim was just changed out from under the prediction on the current frame.
    /// Starts a branch here, or — if one is already running and this is a second shot part
    /// way along it — rewinds its frontier to here, because everything it had recorded past
    /// this tick was the consequence of a state that no longer exists.
    /// </summary>
    public void NoteLiveChange()
    {
        int t = Math.Max(1, CurrentTick);
        if (Branched && t >= BranchTick)
        {
            _branchKeys.RemoveAll(k => k.Tick > t);
            _branchDensity.RemoveRange(
                Math.Min(_branchDensity.Count, t - BranchTick),
                Math.Max(0, _branchDensity.Count - (t - BranchTick)));
            _branchEvents.RemoveAll(e => e.Tick > t);
            _branchGates.RemoveAll(g => g.EndTick > t);
            _branchRegions.RemoveAll(r => r.EndTick > t);
            Interferences++;
        }
        else
        {
            _branchKeys.Clear();
            _branchDensity.Clear();
            _branchEvents.Clear();
            _branchGates.Clear();
            _branchRegions.Clear();
            BranchTick = t;
            Interferences = 1;
        }
        BranchEnd = t;
        BranchEndedNaturally = false;
        // The post-change state itself, so re-entering the branch restores the shot enemy as
        // shot rather than replaying the frame that still had it.
        _branchKeys.Add((t, Sim.TakeSnapshot()));
    }

    /// <summary>Throw the branch away and put the prediction back on screen.</summary>
    public void DropBranch()
    {
        if (!Branched) return;
        BranchTick = -1;
        BranchEnd = 0;
        Interferences = 0;
        _branchPeak = 0;
        BranchEndedNaturally = false;
        _branchKeys.Clear();
        _branchDensity.Clear();
        _branchEvents.Clear();
        _branchGates.Clear();
        _branchRegions.Clear();
        RedrawCurrent();
    }

    /// <summary>Note what the frame just simulated contained, and keyframe it on the interval.</summary>
    private void RecordBranchFrame(int tick)
    {
        BranchEnd = tick;
        byte live = (byte)Math.Clamp(Sim.EnemyOnScreen, 0, 255);
        _branchDensity.Add(live);
        if (live > _branchPeak) _branchPeak = live;
        if ((tick - BranchTick) % KeyInterval == 0)
            _branchKeys.Add((tick, Sim.TakeSnapshot()));

        // Gates are logged when they release, so they arrive whole and late.
        foreach (var gate in _branchGates)
            _branchRegions.Add(new LoopRegion(
                Math.Max(BranchTick, gate.StartTick), Math.Max(BranchTick, gate.EndTick),
                gate.CycleEnds.Where(t => t >= BranchTick).ToArray(),
                gate.Kind switch
                {
                    GameSim.PreviewKind.EnemyLoop => HoldLoopKind.ScriptedLoop,
                    GameSim.PreviewKind.EnemyHold => HoldLoopKind.EnemyHold,
                    _ => HoldLoopKind.RouteLoop,
                },
                gate.EventIndex));
        _branchGates.Clear();
    }

    public static string FormatTime(int tick)
    {
        double s = tick / GameSim.TicksPerSecond;
        return $"{(int)(s / 60)}:{s % 60:00.0}";
    }
}
