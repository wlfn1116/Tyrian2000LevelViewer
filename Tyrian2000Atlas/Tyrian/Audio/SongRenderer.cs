namespace T2A.Tyrian.Audio;

/// <summary>
/// Renders a song to samples away from the audio device, for export. It runs the same
/// Loudness player and OPL chip the mixer does, just as fast as it can rather than in
/// real time, so what comes out is what the song sounds like -- not a recording of it.
///
/// FluidSynth can render offline the same way; the OS synth cannot, since its audio never
/// passes through this process at all, so a native-MIDI export falls back to the OPL.
/// </summary>
public static class SongRenderer
{
    /// <summary>The rate exports are written at, matching the game's own device.</summary>
    public const int SampleRate = 44100;

    /// <summary>How long to keep rendering after the last tick, so the final note's release
    /// is not cut off mid-decay.</summary>
    private const double TailSeconds = 1.5;

    /// <summary>
    /// Renders <paramref name="passes"/> times through the song on the emulated OPL. A looping
    /// song is followed round its own loop point, exactly as the game plays it, so two passes
    /// give the intro plus one repeat rather than the file twice.
    /// </summary>
    public static short[] RenderOpl(LdsSong song, MidiSequence? seq, int passes = 1,
        Action<float>? progress = null)
    {
        var opl = new OplChip(SampleRate);
        var lds = new LdsPlayer(opl);
        lds.Load(song);

        double samplesPerTick = SampleRate / MusicPlayer.OplTickRate;
        int totalTicks = EstimateTicks(seq, passes);
        var outBuf = new List<short>((int)(totalTicks * samplesPerTick) + SampleRate * 2);

        var chunk = new short[(int)samplesPerTick + 2];
        double owed = 0;
        int pass = 1, tick = 0;

        while (true)
        {
            bool wasLooped = lds.SongLooped;
            lds.Update();
            tick++;

            if (!wasLooped && lds.SongLooped) { if (++pass > passes) break; }
            else if (!lds.Playing) { if (++pass > passes) break; lds.Rewind(); }
            // A song that neither loops nor stops still has to end somewhere.
            else if (seq != null && tick > (long)seq.Duration * passes + 16) break;

            owed += samplesPerTick;
            int n = (int)owed;
            owed -= n;
            if (n > 0)
            {
                if (n > chunk.Length) chunk = new short[n];
                opl.GetSample(chunk.AsSpan(0, n));
                for (int i = 0; i < n; i++) outBuf.Add(chunk[i]);
            }
            if ((tick & 255) == 0 && totalTicks > 0) progress?.Invoke(Math.Min(1f, tick / (float)totalTicks));
        }

        // Let the chip ring out rather than stopping on a hard edge.
        int tail = (int)(TailSeconds * SampleRate);
        if (tail > chunk.Length) chunk = new short[tail];
        opl.GetSample(chunk.AsSpan(0, tail));
        for (int i = 0; i < tail; i++) outBuf.Add(chunk[i]);

        progress?.Invoke(1f);
        return outBuf.ToArray();
    }

    /// <summary>
    /// The same, through a SoundFont. Returns null if the synth is not available -- the caller
    /// then renders the OPL instead. Interleaved stereo.
    /// </summary>
    public static unsafe short[]? RenderFluid(FluidSynth fluid, MidiSequence seq, int passes = 1,
        Action<float>? progress = null)
    {
        if (!fluid.IsOpen) return null;

        double samplesPerTick = SampleRate / seq.TicksPerSecond;
        int totalTicks = EstimateTicks(seq, passes);
        var outBuf = new List<short>(totalTicks * (int)samplesPerTick * 2 + SampleRate * 4);

        int block = (int)samplesPerTick + 2;
        var left = new short[block];
        var right = new short[block];

        fluid.Reset();
        fluid.Silence();

        int cursor = 0;
        double owed = 0;
        for (int pass = 1; pass <= passes; pass++)
        {
            double startTick = pass == 1 ? 0 : seq.Loops ? seq.LoopStart : 0;
            double endTick = seq.Loops && pass < passes ? seq.LoopEnd : seq.Duration;
            if (pass > 1) { fluid.Silence(); seq.ReplayState(startTick, e => Send(fluid, e)); }
            cursor = seq.IndexAt(startTick);

            for (double t = startTick; t <= endTick; t += 1)
            {
                while (cursor < seq.Events.Length && seq.Events[cursor].Tick <= t)
                    Send(fluid, seq.Events[cursor++]);

                owed += samplesPerTick;
                int n = (int)owed;
                owed -= n;
                if (n <= 0) continue;
                if (n > left.Length) { left = new short[n]; right = new short[n]; }
                fixed (short* l = left)
                fixed (short* r = right)
                    fluid.Render(l, r, n);
                for (int i = 0; i < n; i++) { outBuf.Add(left[i]); outBuf.Add(right[i]); }
                if (((int)t & 255) == 0 && totalTicks > 0)
                    progress?.Invoke(Math.Min(1f, ((pass - 1) * (float)seq.Duration + (float)t) / totalTicks));
            }
        }

        fluid.Silence();
        int tail = (int)(TailSeconds * SampleRate);
        if (tail > left.Length) { left = new short[tail]; right = new short[tail]; }
        fixed (short* l = left)
        fixed (short* r = right)
            fluid.Render(l, r, tail);
        for (int i = 0; i < tail; i++) { outBuf.Add(left[i]); outBuf.Add(right[i]); }

        progress?.Invoke(1f);
        return outBuf.ToArray();
    }

    private static void Send(FluidSynth fluid, in SeqEvent e)
    {
        switch (e.Type)
        {
            case MidiEventType.NoteOn:
                if (e.Velocity > 0) fluid.NoteOn(e.Channel, e.Key, e.Velocity);
                else fluid.NoteOff(e.Channel, e.Key);
                break;
            case MidiEventType.NoteOff: fluid.NoteOff(e.Channel, e.Key); break;
            case MidiEventType.ControlChange when e.Data.Length >= 2:
                fluid.ControlChange(e.Channel, e.Data[0], e.Data[1]); break;
            case MidiEventType.ProgramChange when e.Data.Length >= 1:
                fluid.ProgramChange(e.Channel, e.Data[0]); break;
            case MidiEventType.PitchBendChange when e.Data.Length >= 2:
                fluid.PitchBend(e.Channel, e.Data[0] | (e.Data[1] << 7)); break;
            case MidiEventType.ChannelPressure when e.Data.Length >= 1:
                fluid.ChannelPressure(e.Channel, e.Data[0]); break;
        }
    }

    private static int EstimateTicks(MidiSequence? seq, int passes)
    {
        if (seq == null) return 0;
        if (!seq.Loops) return (int)seq.Duration;
        return (int)seq.LoopEnd + (passes - 1) * (int)Math.Max(1, seq.LoopEnd - seq.LoopStart);
    }
}
