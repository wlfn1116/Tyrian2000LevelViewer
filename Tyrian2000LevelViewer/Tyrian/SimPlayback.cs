namespace T2LV.Tyrian;

/// <summary>
/// Drives a GameSim along a precomputed timeline: runs the whole level once headless to
/// learn its duration (snapshotting keyframes on the way), then serves random access to
/// any tick for play/rewind/fast-forward/scrubbing by restoring the nearest keyframe and
/// re-simulating forward. Deterministic by construction.
/// </summary>
public sealed class SimPlayback
{
    public const int KeyInterval = 120;          // snapshot every ~3.4s of game time

    public readonly GameSim Sim;
    public int Duration { get; private set; }    // last playable tick (0-based position max)
    public bool EndedNaturally { get; private set; }
    public int CurrentTick { get; private set; } = -1;

    /// <summary>Executed events: (tick, event type). For timeline markers.</summary>
    public List<(int Tick, byte Type)> Events = new();
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
        Duration = Math.Max(1, ticks);
        EndedNaturally = sim.Finished;
        Density = density.ToArray();
        PrecomputeMs = sw.ElapsedMilliseconds;

        SeekTo(1);
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
        int at = key * KeyInterval;                 // frames completed so far
        while (at < tick - 1 && !Sim.Finished) { Sim.Tick(draw: false); at++; }
        if (!Sim.Finished) Sim.Tick(draw: true);
        CurrentTick = tick;
    }

    /// <summary>Step forward from the current frame; draws only the final tick.</summary>
    public void Advance(int n)
    {
        if (n <= 0) return;
        int target = Math.Min(CurrentTick + n, Duration);
        n = target - CurrentTick;
        if (n <= 0) return;
        for (int i = 1; i <= n && !Sim.Finished; i++)
            Sim.Tick(draw: i == n);
        CurrentTick = target;
    }

    public bool AtEnd => CurrentTick >= Duration;

    public static string FormatTime(int tick)
    {
        double s = tick / GameSim.TicksPerSecond;
        return $"{(int)(s / 60)}:{s % 60:00.0}";
    }
}
