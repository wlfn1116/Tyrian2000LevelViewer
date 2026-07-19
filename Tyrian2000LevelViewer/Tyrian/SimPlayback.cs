namespace T2LV.Tyrian;

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
            "enemy-gated loop, 3 cycles kept",
        1 when _loopRegions[0].Kind == HoldLoopKind.EnemyHold =>
            "enemy-gated hold, 20 s preview",
        1 => "conditional route loop, 3 cycles kept",
        _ => $"{_loopRegions.Count} loop/hold sections previewed",
    };

    /// <summary>Executed events (tick, type, index, backward-jump), for timeline markers.</summary>
    public List<GameSim.EventExec> Events = new();
    /// <summary>Per-tick enemies-on-screen count (clamped to 255), for the density strip.</summary>
    public byte[] Density = Array.Empty<byte>();

    private readonly List<GameSim.Snapshot> _keys = new();
    public long PrecomputeMs { get; private set; }

    public SimPlayback(GameSim sim, int maxTicks)
    {
        Sim = sim;
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
            DetectHoldingLoop();

        PrecomputeMs = sw.ElapsedMilliseconds;
        SeekTo(1);
    }

    /// <summary>
    /// A level that never ends without a player is usually replaying a scripted attack
    /// loop while it waits for the boss (or linked enemies) to die. Only accept a loop
    /// whose event still recurs near the precompute cap, then retain
    /// GameSim.PreviewLoopCycles cycles (matching the scripted-gate preview).
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
            int keep = GameSim.PreviewLoopCycles;
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
        if (Duration - lastEv > (int)(30 * GameSim.TicksPerSecond))
        {
            int end = Math.Min(Duration, lastEv + (int)(20 * GameSim.TicksPerSecond));
            _loopRegions.Add(new LoopRegion(lastEv, end, Array.Empty<int>(),
                HoldLoopKind.EnemyHold, EventIndex: -1));
            Trim(end);
        }
    }

    private void Trim(int newDuration)
    {
        Duration = Math.Max(1, newDuration);
        if (Density.Length > Duration) Density = Density[..Duration];
        Events.RemoveAll(e => e.Tick > Duration);
        int lastKey = Duration / KeyInterval;
        if (_keys.Count > lastKey + 1)
            _keys.RemoveRange(lastKey + 1, _keys.Count - lastKey - 1);
    }

    /// <summary>Position on frame <paramref name="tick"/> (1..Duration) and draw it.</summary>
    public void SeekTo(int tick)
    {
        tick = Math.Clamp(tick, 1, Duration);
        if (tick == CurrentTick) return;

        // Continue live when the target is just ahead of the current frame.
        if (CurrentTick >= 0 && tick > CurrentTick && tick - CurrentTick <= KeyInterval)
        {
            Advance(tick - CurrentTick);
            return;
        }

        int key = Math.Min((tick - 1) / KeyInterval, _keys.Count - 1);
        Sim.RestoreSnapshot(_keys[key]);
        Sim.ClearScreens();
        int at = key * KeyInterval;                 // frames completed so far
        while (at < tick && !Sim.Finished)
        {
            at++;
            Sim.Tick(draw: at > tick - WarmupDraw);
        }
        CurrentTick = tick;
    }

    /// <summary>Step forward from the current frame; draws only the trailing ticks.</summary>
    public void Advance(int n)
    {
        if (n <= 0) return;
        int target = Math.Min(CurrentTick + n, Duration);
        n = target - CurrentTick;
        if (n <= 0) return;
        for (int i = 1; i <= n && !Sim.Finished; i++)
            Sim.Tick(draw: i > n - WarmupDraw);
        CurrentTick = target;
    }

    /// <summary>Re-render the current frame (after a view-option change).</summary>
    public void RedrawCurrent()
    {
        int t = Math.Max(1, CurrentTick);
        CurrentTick = -1;
        SeekTo(t);
    }

    public bool AtEnd => CurrentTick >= Duration;

    public static string FormatTime(int tick)
    {
        double s = tick / GameSim.TicksPerSecond;
        return $"{(int)(s / 60)}:{s % 60:00.0}";
    }
}
