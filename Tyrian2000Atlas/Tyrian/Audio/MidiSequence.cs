namespace T2A.Tyrian.Audio;

/// <summary>One event of a flattened song, tagged with the Loudness channel it came from
/// (0..8, or -1 for the conductor track) so the player can mute a single voice.</summary>
public readonly struct SeqEvent
{
    public readonly uint Tick;
    public readonly MidiEventType Type;
    public readonly byte Channel;
    public readonly sbyte Lane;
    public readonly byte[] Data;

    public SeqEvent(uint tick, MidiEventType type, int channel, int lane, byte[] data)
    {
        Tick = tick; Type = type; Channel = (byte)channel; Lane = (sbyte)lane; Data = data;
    }

    public bool IsNoteOn => Type == MidiEventType.NoteOn && Data.Length >= 2 && Data[1] > 0;
    public bool IsNoteOff => Type == MidiEventType.NoteOff || (Type == MidiEventType.NoteOn && Data.Length >= 2 && Data[1] == 0);
    public int Key => Data.Length >= 1 ? Data[0] : 0;
    public int Velocity => Data.Length >= 2 ? Data[1] : 0;
}

/// <summary>A note as the timeline draws it: one bar on one lane (0..8 = Loudness channel).</summary>
public readonly record struct SeqNote(uint Start, uint End, byte Lane, byte Channel, byte Key, byte Velocity, byte Program)
{
    public uint Length => End > Start ? End - Start : 1;

    /// <summary>
    /// Where the note stops being heard, which is past <see cref="End"/> whenever the OPL is
    /// still ringing -- see <see cref="MidiSequence.MeasureRingOut"/>. Equal to <c>End</c> when
    /// the voice really does stop with the key, and when there is no OPL measurement.
    /// </summary>
    public uint Ring { get; init; }

    /// <summary>Ticks the note sings on for after its key is released. 0 for most notes.</summary>
    public uint RingLength => Ring > End ? Ring - End : 0;

    /// <summary>
    /// True when the next note on this lane repeats this pitch and starts the moment this one
    /// ends. The conversion runs a note up to whatever follows it, so a drum part -- one pitch
    /// struck over and over -- comes out as notes that tile the timeline end to start. Drawn
    /// as duration bars that is one unbroken line; the timeline draws these as onset marks
    /// instead so the beat is visible. TRANSON's channel 7 is the case that found this.
    /// </summary>
    public bool Restruck { get; init; }
}

/// <summary>Anything worth marking on the ruler above the timeline.</summary>
public readonly record struct SeqMarker(uint Tick, string Text);

/// <summary>
/// A converted song flattened into one tick-ordered event list, plus the note
/// rectangles and per-track summary the music window's timeline draws from.
/// </summary>
public sealed class MidiSequence
{
    /// <summary>The nine Loudness channels become MIDI tracks 1..9; track 0 is the meta track.</summary>
    public const int LaneCount = 9;

    public SeqEvent[] Events { get; private set; } = Array.Empty<SeqEvent>();
    public SeqMarker[] Markers { get; private set; } = Array.Empty<SeqMarker>();

    private SeqNote[] _notes = Array.Empty<SeqNote>();

    /// <summary>
    /// The notes the note views draw. Replaced whole rather than edited in place:
    /// <see cref="MeasureRingOut"/> runs on its own thread and publishes a finished array, so
    /// a frame drawn while it is in flight sees every note without its ring-out rather than
    /// some notes with and some without.
    /// </summary>
    public SeqNote[] Notes => Volatile.Read(ref _notes);

    /// <summary>Last event tick.</summary>
    public uint Duration { get; private set; }

    /// <summary>Loop points the song itself declares (ticks).</summary>
    public uint LoopStart { get; private set; }
    public uint LoopEnd { get; private set; }
    public bool Loops { get; private set; }

    /// <summary>Ticks per second the MIDI backends run this song at.</summary>
    public double TicksPerSecond { get; private set; } = 70.0;

    /// <summary>Per-lane note count, for the track headers.</summary>
    public readonly int[] LaneNotes = new int[LaneCount];

    /// <summary>Per-lane MIDI channel (the converter is free to reassign them).</summary>
    public readonly int[] LaneChannel = new int[LaneCount];

    /// <summary>Per-lane General MIDI programs actually used, in first-use order.</summary>
    public readonly List<int>[] LanePrograms = new List<int>[LaneCount];

    /// <summary>Lowest and highest key over the whole song, for the timeline's vertical fit.</summary>
    public int LowKey { get; private set; } = 127;
    public int HighKey { get; private set; }

    /// <summary>
    /// Ticks between the pattern rows the notes actually fall on -- the grid a piano roll draws
    /// so its notes sit on lines. These are AdLib tracker songs: the player steps one pattern
    /// row every few ticks and notes only ever start on a row, but the row rate is the LDS
    /// <c>tempo</c>, which a pattern command can change mid-song, so the file header does not
    /// give it away. It is recovered from the note data instead -- see <see cref="ComputeRowTicks"/>.
    /// 1 when a song is too sparse to tell (its grid is then simply not drawn).
    /// </summary>
    public int RowTicks { get; private set; } = 1;

    /// <summary>The first note's start tick -- the whole song's, for phasing the row grid on in
    /// the all-channels view, where there is no one channel to take it from. Notes are in start
    /// order, so it is simply the first.</summary>
    public uint FirstNoteTick => Notes.Length > 0 ? Notes[0].Start : 0;

    private MidiSequence()
    {
        for (int i = 0; i < LaneCount; i++) { LanePrograms[i] = new List<int>(); LaneChannel[i] = -1; }
    }

    /// <summary>True once <see cref="MeasureRingOut"/> has published the notes' ring-out.
    /// Read it before <see cref="Notes"/>: false only ever means the tails are not in yet.</summary>
    public bool HasRingOut => _hasRingOut;
    private volatile bool _hasRingOut;

    /// <summary>Flattens a converted song. Returns null for a song that would not convert.</summary>
    public static MidiSequence? From(LdsMidiSong? song)
    {
        if (song == null || song.Tracks.Count == 0) return null;
        var seq = new MidiSequence();

        // The lane a track belongs to is the Loudness channel it was converted from, which
        // the conversion records: neither the track's position (silent channels are dropped
        // from the file) nor its MIDI channel (percussion is re-routed to the drum channel)
        // can be used for it.
        var events = new List<SeqEvent>(4096);
        for (int t = 0; t < song.Tracks.Count; t++)
        {
            int lane = song.Tracks[t].LoudnessChannel;
            foreach (var e in song.Tracks[t].Events)
                events.Add(new SeqEvent(e.Timestamp, e.Type, e.Channel, lane, e.Data ?? Array.Empty<byte>()));
        }

        // Stable by tick, then by track: a program change written on the same tick as the
        // note that needs it must go out first, and the converter emits it on the lower track
        // index only by luck -- ordering non-notes ahead of notes makes it deterministic.
        events.Sort((a, b) =>
        {
            int c = a.Tick.CompareTo(b.Tick);
            if (c != 0) return c;
            int ra = a.Type == MidiEventType.NoteOn ? 2 : a.Type == MidiEventType.NoteOff ? 0 : 1;
            int rb = b.Type == MidiEventType.NoteOn ? 2 : b.Type == MidiEventType.NoteOff ? 0 : 1;
            if (ra != rb) return ra.CompareTo(rb);
            return a.Lane.CompareTo(b.Lane);
        });
        seq.Events = events.ToArray();

        seq.Duration = song.Duration;
        seq.LoopStart = song.LoopStart;
        seq.LoopEnd = song.LoopEnd;
        seq.Loops = song.Loops;
        if (song.TimeDivision > 0 && song.TempoUsPerQuarter > 0)
            seq.TicksPerSecond = 1_000_000.0 / song.TempoUsPerQuarter * song.TimeDivision;

        seq.BuildNotes();
        seq.BuildMarkers();
        seq.ComputeRowTicks();
        return seq;
    }

    private static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }

    /// <summary>
    /// Work out <see cref="RowTicks"/> from the notes. Every onset on one lane is a whole number
    /// of rows from the last, so the gaps on that lane are all multiples of the row size, and the
    /// gap-GCD recovers it (times however sparsely the lane plays). Combined across the lanes
    /// that GCD collapses to the true row. Done per lane on purpose: between lanes the channel
    /// delays offset some notes by a tick or two, and a whole-song GCD would collapse to 1.
    /// </summary>
    private void ComputeRowTicks()
    {
        long all = 0;
        for (int lane = 0; lane < LaneCount; lane++)
        {
            long lane_g = 0;
            uint prev = 0;
            bool have = false;
            foreach (var n in Notes)
            {
                if (n.Lane != lane) continue;
                if (have && n.Start > prev) lane_g = Gcd(lane_g, n.Start - prev);
                prev = n.Start;
                have = true;
            }
            if (lane_g > 0) all = Gcd(all, lane_g);
        }
        RowTicks = all > 0 ? (int)all : 1;
    }

    private static int LaneOf(in SeqEvent e) => (uint)e.Lane < LaneCount ? e.Lane : -1;

    private void BuildNotes()
    {
        var notes = new List<SeqNote>(2048);
        var program = new byte[16];
        // open[lane, key] -> index into notes, -1 when that key is not sounding
        var open = new Dictionary<(int Lane, byte Key), int>();

        foreach (var e in Events)
        {
            int lane = LaneOf(e);
            if (e.Type == MidiEventType.ProgramChange && e.Data.Length >= 1)
            {
                program[e.Channel & 15] = e.Data[0];
                if (lane >= 0 && !LanePrograms[lane].Contains(e.Data[0]))
                    LanePrograms[lane].Add(e.Data[0]);
                continue;
            }
            if (lane < 0) continue;
            LaneChannel[lane] = e.Channel;

            if (e.IsNoteOn)
            {
                byte key = (byte)e.Key;
                var slot = (lane, key);
                if (open.TryGetValue(slot, out int prev) && prev >= 0)
                    notes[prev] = notes[prev] with { End = e.Tick };   // retrigger closes the old one
                notes.Add(new SeqNote(e.Tick, e.Tick + 1, (byte)lane, e.Channel, key,
                    (byte)e.Velocity, program[e.Channel & 15]));
                open[slot] = notes.Count - 1;
                LaneNotes[lane]++;
                LowKey = Math.Min(LowKey, key);
                HighKey = Math.Max(HighKey, key);
            }
            else if (e.IsNoteOff)
            {
                byte key = (byte)e.Key;
                var slot = (lane, key);
                if (open.TryGetValue(slot, out int idx) && idx >= 0)
                {
                    notes[idx] = notes[idx] with { End = Math.Max(e.Tick, notes[idx].Start + 1) };
                    open[slot] = -1;
                }
            }
        }

        // A note still sounding at the end runs to the end of the song.
        foreach (var idx in open.Values)
            if (idx >= 0) notes[idx] = notes[idx] with { End = Math.Max(Duration, notes[idx].Start + 1) };

        if (LowKey > HighKey) { LowKey = 48; HighKey = 72; }

        // Mark the notes a following strike of the same pitch runs into. One backward pass:
        // the list is in start order, so the last note seen per (lane, key) is the next one
        // for whatever came before it.
        // The same pass seats Ring on the key-off, which is where it stays unless an OPL
        // measurement moves it out (MeasureRingOut).
        var next = new Dictionary<(byte Lane, byte Key), uint>();
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            var n = notes[i];
            var slot = (n.Lane, n.Key);
            bool restruck = next.TryGetValue(slot, out uint nextStart) && nextStart <= n.End;
            notes[i] = n with { Restruck = restruck, Ring = n.End };
            next[slot] = n.Start;
        }

        _notes = notes.ToArray();
    }

    /// <summary>
    /// Fills in each note's <see cref="SeqNote.Ring"/>: where the voice actually stops being
    /// heard, which is not where its bar ends. A Loudness key-off only drops the OPL's key-on
    /// bit, putting the operator into its release phase -- it does not silence it, and these
    /// patches release slowly. CAMANIS is the case that shows it up: 2759 of its 3675 notes
    /// ring on past their key, by a median of 6 ticks against a median 7-tick bar, so the roll
    /// draws a field of detached blips where the ear hears a legato arpeggio.
    ///
    /// It has to be measured rather than inferred from the patch. Halloween Ramble's release
    /// rates are slower still, yet its gaps really are silent -- those envelopes have decayed
    /// to nothing long before the key lifts. So the song is run once through a chip whose
    /// samples are thrown away and every channel's envelope is read off per tick.
    ///
    /// Meant to be called on a thread of its own (MusicTrack.WantRingOut starts one): it is a
    /// tenth of a second on a long song, and the envelope stepping inside the chip costs the
    /// same per second of music whatever the sample rate, so there is no rate cheap enough to
    /// make it free. The rate below is simply where the waveform work stops mattering -- it
    /// agrees note for note with the same pass at 49716 Hz. The finished notes are published
    /// in one assignment, so a frame either has every tail or none.
    /// </summary>
    public void MeasureRingOut(LdsSong lds)
    {
        const int rate = 4000;
        const float quiet = 0.04f;             // -28 dB from the note's own peak
        int ticks = (int)Duration + 1;
        if (ticks < 2 || Notes.Length == 0) return;

        int perTick = Math.Max(1, (int)Math.Round(rate / MusicBank.LdsUpdateRate));
        var opl = new OplChip(rate);
        var player = new LdsPlayer(opl);
        player.Load(lds);

        var level = new float[LaneCount][];
        for (int i = 0; i < LaneCount; i++) level[i] = new float[ticks];
        var scratch = new short[perTick];

        // Stepping stops where the conversion stopped -- the song's loop jump, or its end --
        // but the rendering carries on, which is what lets the last notes ring out.
        bool running = true;
        for (int t = 0; t < ticks; t++)
        {
            if (running) running = player.Update();
            opl.GetSample(scratch);
            for (int ch = 0; ch < LaneCount; ch++) level[ch][t] = (float)opl.ChannelEnvelope(ch);
        }

        // A floor per lane as well as per note: a note that was quiet to begin with should not
        // trail a tail of something nothing else in the song would let you hear.
        var laneFloor = new float[LaneCount];
        for (int ch = 0; ch < LaneCount; ch++)
        {
            float peak = 0f;
            foreach (float v in level[ch]) if (v > peak) peak = v;
            laneFloor[ch] = peak * 0.01f;
        }

        // Whatever is left of a note, the next strike on its lane takes the voice over.
        var notes = (SeqNote[])Notes.Clone();
        var limit = new int[notes.Length];
        var pending = new int[LaneCount];
        Array.Fill(pending, ticks - 1);
        for (int i = notes.Length - 1; i >= 0; i--)
        {
            int lane = notes[i].Lane;
            if ((uint)lane >= LaneCount) { limit[i] = ticks - 1; continue; }
            limit[i] = pending[lane];
            pending[lane] = (int)Math.Min(notes[i].Start, ticks - 1);
        }

        for (int i = 0; i < notes.Length; i++)
        {
            var n = notes[i];
            int lane = n.Lane;
            if ((uint)lane >= LaneCount) continue;
            int start = (int)Math.Min(n.Start, ticks - 1), end = (int)Math.Min(n.End, ticks - 1);

            float pk = 0f;
            for (int t = start; t <= end; t++) if (level[lane][t] > pk) pk = level[lane][t];
            float floor = Math.Max(pk * quiet, laneFloor[lane]);
            if (pk <= floor) continue;

            int ring = end;
            while (ring < limit[i] && level[lane][ring] > floor) ring++;
            if (ring > end) notes[i] = n with { Ring = (uint)ring };
        }

        // The flag goes last: a reader that sees it set is guaranteed the measured array.
        Volatile.Write(ref _notes, notes);
        _hasRingOut = true;
    }

    private void BuildMarkers()
    {
        var marks = new List<SeqMarker>();
        foreach (var e in Events)
        {
            if (e.Type != MidiEventType.Extended || e.Data.Length < 3) continue;
            // FF 06 <len> <text> -- the converter writes "loopStart" / "loopEnd" markers.
            if (e.Data[0] != 0xff || e.Data[1] != 0x06) continue;
            int len = e.Data[2];
            if (e.Data.Length < 3 + len) continue;
            string text = System.Text.Encoding.ASCII.GetString(e.Data, 3, len);
            marks.Add(new SeqMarker(e.Tick, text));
        }
        Markers = marks.ToArray();
    }

    /// <summary>Index of the first event at or after a tick.</summary>
    public int IndexAt(double tick)
    {
        int lo = 0, hi = Events.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Events[mid].Tick < tick) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Replays every program change, controller and pitch bend before a tick, skipping
    /// notes. Seeking (and the loop seam) needs this: the conversion only emits changes,
    /// so without it a channel keeps whatever instrument it happened to end on.
    /// </summary>
    public void ReplayState(double tick, Action<SeqEvent> send)
    {
        int end = IndexAt(tick);
        // Last-write-wins per (channel, kind, controller), so replay the winners only.
        var latest = new Dictionary<(int Ch, int Kind, int Sub), SeqEvent>();
        for (int i = 0; i < end; i++)
        {
            ref readonly var e = ref Events[i];
            switch (e.Type)
            {
                case MidiEventType.ProgramChange:
                    latest[(e.Channel, 1, 0)] = e; break;
                case MidiEventType.ControlChange when e.Data.Length >= 1:
                    latest[(e.Channel, 2, e.Data[0])] = e; break;
                case MidiEventType.PitchBendChange:
                    latest[(e.Channel, 3, 0)] = e; break;
                case MidiEventType.ChannelPressure:
                    latest[(e.Channel, 4, 0)] = e; break;
            }
        }
        // Programs, then controllers, then bends and pressure -- the order the Kind numbers
        // give. What matters is that the RPN pair that sets the bend range is replayed before
        // the bend itself, which this satisfies; nothing here depends on bank select, which
        // the conversion never emits.
        foreach (var kv in latest.OrderBy(k => k.Key.Kind).ThenBy(k => k.Value.Tick))
            send(kv.Value);
    }

    /// <summary>Seconds a tick lands at, at the given speed multiplier.</summary>
    public double SecondsAt(double tick, double speed = 1.0) =>
        TicksPerSecond > 0 ? tick / (TicksPerSecond * Math.Max(0.01, speed)) : 0;
}
