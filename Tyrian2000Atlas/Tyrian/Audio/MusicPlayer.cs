using System.Diagnostics;

namespace T2A.Tyrian.Audio;

/// <summary>How a song is turned into sound. Mirrors the Engaged fork's
/// <c>MusicDevice</c>: OPL is the authentic AdLib voice, the other two play the
/// same song converted to MIDI.</summary>
public enum MusicDevice
{
    /// <summary>The emulated OPL2 chip -- what the game actually sounds like.</summary>
    Opl = 0,
    /// <summary>A SoundFont through libfluidsynth.</summary>
    FluidSynth = 1,
    /// <summary>The operating system's own MIDI synthesizer.</summary>
    NativeMidi = 2,
}

/// <summary>
/// Plays one song at a time, through any of the three devices, on a single
/// timeline measured in Loudness ticks. OPL and FluidSynth are rendered inside the
/// mixer callback (so they stay in step with the sound effects and share one
/// volume path); native MIDI has no audio to render, so it runs on its own
/// small high-resolution thread.
/// </summary>
public sealed unsafe class MusicPlayer : IDisposable
{
    /// <summary>The rate the game steps the Loudness player at (loudness.c: 69.5 Hz).</summary>
    public const double OplTickRate = 69.5;

    private readonly object _gate = new();
    private readonly int _rate;

    private readonly OplChip _opl;
    private readonly LdsPlayer _lds;
    private FluidSynth? _fluid;
    private NativeMidiOut? _native;
    private Thread? _nativeThread;

    private short[] _scratchL = Array.Empty<short>();
    private short[] _scratchR = Array.Empty<short>();

    private MusicDevice _device = MusicDevice.Opl;
    private MusicTrack? _track;
    private MidiSequence? _seq;
    private double _tick;             // playhead, in ticks
    private int _cursor;              // index of the next event in _seq.Events
    private double _sampleAccum;      // samples owed before the next tick
    private bool _playing, _paused;
    private double _speed = 1.0;
    private bool _loop = true;
    private int _loopA = -1, _loopB = -1;
    private bool _finished;

    private readonly bool[] _mute = new bool[MidiSequence.LaneCount];
    private readonly bool[] _solo = new bool[MidiSequence.LaneCount];

    /// <summary>Rising counter of loop wraps, so the UI can show "pass 3".</summary>
    public int LoopCount { get; private set; }

    /// <summary>Why the selected device could not start, if it could not.</summary>
    public string DeviceError { get; private set; } = "";

    public MusicPlayer(int sampleRate)
    {
        _rate = sampleRate;
        _opl = new OplChip(sampleRate);
        _lds = new LdsPlayer(_opl);
    }

    // ---------------------------------------------------------------- state

    /// <summary>The song loaded, or null.</summary>
    public MusicTrack? Track { get { lock (_gate) return _track; } }

    /// <summary>The flattened song the timeline draws, or null.</summary>
    public MidiSequence? Sequence { get { lock (_gate) return _seq; } }

    /// <summary>Playhead in ticks.</summary>
    public double Tick { get { lock (_gate) return _tick; } }

    /// <summary>Playhead in seconds.</summary>
    public double Seconds
    {
        get { lock (_gate) return _seq == null ? 0 : _tick / TickRateLocked(); }
    }

    /// <summary>True while a song is running (not stopped, not paused).</summary>
    public bool IsPlaying { get { lock (_gate) return _playing && !_paused; } }

    /// <summary>True while a song is loaded but paused.</summary>
    public bool IsPaused { get { lock (_gate) return _playing && _paused; } }

    /// <summary>True once a non-looping song has run off the end.</summary>
    public bool Finished { get { lock (_gate) return _finished; } }

    /// <summary>Playback speed, 0.1x to 4x.</summary>
    public double Speed
    {
        get { lock (_gate) return _speed; }
        set { lock (_gate) _speed = Math.Clamp(value, 0.1, 4.0); }
    }

    /// <summary>Repeat at the end (or at the B point) instead of stopping.</summary>
    public bool Loop
    {
        get { lock (_gate) return _loop; }
        set { lock (_gate) _loop = value; }
    }

    /// <summary>Loop range in ticks. A negative A means "use the song's own loop point".</summary>
    public (int A, int B) LoopRange
    {
        get { lock (_gate) return (_loopA, _loopB); }
        set { lock (_gate) { _loopA = value.A; _loopB = value.B; } }
    }

    /// <summary>Per-lane mute (lane 0..8 = the nine Loudness channels).</summary>
    public bool IsMuted(int lane) { lock (_gate) return (uint)lane < 9 && _mute[lane]; }

    /// <summary>Per-lane solo.</summary>
    public bool IsSolo(int lane) { lock (_gate) return (uint)lane < 9 && _solo[lane]; }

    /// <summary>Sets a lane's mute flag.</summary>
    public void SetMute(int lane, bool on)
    {
        lock (_gate) { if ((uint)lane < 9) { _mute[lane] = on; ApplyLaneGatesLocked(); } }
    }

    /// <summary>Sets a lane's solo flag.</summary>
    public void SetSolo(int lane, bool on)
    {
        lock (_gate) { if ((uint)lane < 9) { _solo[lane] = on; ApplyLaneGatesLocked(); } }
    }

    /// <summary>Clears every mute and solo.</summary>
    public void ClearLaneGates()
    {
        lock (_gate) { Array.Clear(_mute); Array.Clear(_solo); ApplyLaneGatesLocked(); }
    }

    /// <summary>True when a lane should be heard, given the mute/solo state.</summary>
    private bool LaneAudibleLocked(int lane)
    {
        if ((uint)lane >= 9) return true;
        bool anySolo = false;
        for (int i = 0; i < 9; i++) if (_solo[i]) { anySolo = true; break; }
        return anySolo ? _solo[lane] : !_mute[lane];
    }

    private void ApplyLaneGatesLocked()
    {
        for (int i = 0; i < 9; i++) _lds.ChannelMuted[i] = !LaneAudibleLocked(i);
        if (_device != MusicDevice.Opl) SilenceMidiLocked();
    }

    // ---------------------------------------------------------------- device

    /// <summary>The device in use. Setting it re-starts the current song on the new one.</summary>
    public MusicDevice Device
    {
        get { lock (_gate) return _device; }
    }

    /// <summary>The FluidSynth instance, for SoundFont queries. Null until FluidSynth is selected.</summary>
    public FluidSynth? Fluid { get { lock (_gate) return _fluid; } }

    /// <summary>The native MIDI device, for its name. Null until native MIDI is selected.</summary>
    public NativeMidiOut? Native { get { lock (_gate) return _native; } }

    /// <summary>
    /// Switches device, keeping the playhead. Falls back to OPL (and reports why in
    /// <see cref="DeviceError"/>) if the requested device cannot be started, exactly
    /// as init_audio does in the fork.
    /// </summary>
    public void SetDevice(MusicDevice device, string? soundFont = null)
    {
        lock (_gate)
        {
            if (device == _device && device != MusicDevice.FluidSynth) return;

            SilenceMidiLocked();
            StopNativeThreadLocked();
            DeviceError = "";

            switch (device)
            {
                case MusicDevice.FluidSynth:
                    _fluid ??= new FluidSynth();
                    if (!_fluid.IsOpen && !_fluid.Open(_rate))
                    {
                        DeviceError = _fluid.Error;
                        device = MusicDevice.Opl;
                        break;
                    }
                    _fluid.LoadSoundFont(soundFont);
                    if (!_fluid.HasSoundFont)
                        DeviceError = string.IsNullOrWhiteSpace(soundFont)
                            ? "no SoundFont chosen -- FluidSynth will be silent"
                            : _fluid.Error;
                    break;

                case MusicDevice.NativeMidi:
                    _native ??= new NativeMidiOut();
                    if (!_native.IsOpen && !_native.Open())
                    {
                        DeviceError = _native.Error;
                        device = MusicDevice.Opl;
                        break;
                    }
                    // Start every channel at the master level; the song's own CC 7 will
                    // arrive scaled from there.
                    for (int ch = 0; ch < 16; ch++)
                    {
                        _songVolume[ch] = 100;                       // the GM default
                        _native.Send(0xb0 | ch, 7, ScaleVolumeLocked(100));
                    }
                    StartNativeThreadLocked();
                    break;
            }

            _device = device;

            // Re-seat the new device at the current position so the switch is seamless.
            if (_seq != null && _playing)
            {
                double at = _tick;
                SeekLocked(at, restartAudio: true);
            }
        }
    }

    /// <summary>Reloads FluidSynth's SoundFont without disturbing playback position.</summary>
    public bool SetSoundFont(string? path)
    {
        lock (_gate)
        {
            if (_fluid == null || !_fluid.IsOpen) return false;
            SilenceMidiLocked();
            bool ok = _fluid.LoadSoundFont(path);
            DeviceError = ok && !_fluid.HasSoundFont && !string.IsNullOrWhiteSpace(path) ? _fluid.Error : "";
            if (_device == MusicDevice.FluidSynth && _seq != null && _playing) SeekLocked(_tick, true);
            return ok;
        }
    }

    // ---------------------------------------------------------------- transport

    /// <summary>
    /// Decodes a song's LDS and MIDI forms without holding the mixer's lock. Call it before
    /// <see cref="Play"/>: the conversion is a whole-song pass, and doing it inside the lock
    /// costs an audible dropout the first time each song is played.
    /// </summary>
    public static void Prepare(MusicTrack? track)
    {
        if (track == null) return;
        try { _ = track.Lds; _ = track.Midi; } catch { /* Play reports an unplayable song */ }
    }

    /// <summary>Loads and starts a song from the top.</summary>
    public void Play(MusicTrack? track, double startTick = 0)
    {
        lock (_gate)
        {
            if (track == null) { StopLocked(); return; }
            if (!ReferenceEquals(track, _track))
            {
                // track.Midi / track.Lds decode on first touch. Prepare() is meant to have
                // done that off the lock already; if it has not, this is the fallback and it
                // will cost the mixer a buffer.
                var lds = track.Lds;
                var midi = track.Midi;
                _track = track;
                _seq = MidiSequence.From(midi);
                _lds.Load(lds);
                _loopA = _loopB = -1;
                LoopCount = 0;
            }
            _playing = true;
            _paused = false;
            _finished = false;
            SeekLocked(startTick, restartAudio: true);
        }
    }

    /// <summary>Restarts the loaded song from the top.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            if (_track == null) return;
            _playing = true; _paused = false; _finished = false; LoopCount = 0;
            SeekLocked(0, true);
        }
    }

    /// <summary>Stops and silences.</summary>
    public void Stop() { lock (_gate) StopLocked(); }

    private void StopLocked()
    {
        _playing = false;
        _paused = false;
        SilenceMidiLocked();
        _lds.Rewind();
        _tick = 0;
        _cursor = 0;
        _sampleAccum = 0;
    }

    /// <summary>Freezes the playhead and silences the synth, keeping the song loaded.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!_playing || _paused) return;
            _paused = true;
            SilenceMidiLocked();
        }
    }

    /// <summary>Resumes from where <see cref="Pause"/> froze it.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (!_playing || !_paused) return;
            _paused = false;
            if (_device != MusicDevice.Opl) SeekLocked(_tick, true);
        }
    }

    /// <summary>Toggles between playing and paused.</summary>
    public void TogglePause()
    {
        lock (_gate)
        {
            if (!_playing) { if (_track != null) { _playing = true; _paused = false; _finished = false; SeekLocked(_tick, true); } return; }
            if (_paused) { _paused = false; if (_device != MusicDevice.Opl) SeekLocked(_tick, true); }
            else { _paused = true; SilenceMidiLocked(); }
        }
    }

    /// <summary>Jumps the playhead. Every device is re-seated so the new position sounds right.</summary>
    public void Seek(double tick) { lock (_gate) { _finished = false; SeekLocked(tick, true); } }

    /// <summary>A seek the audio thread asked for and must not perform itself. -1 = none.</summary>
    private double _deferredSeek = -1;

    /// <summary>
    /// Performs any seek the mixer deferred. Call once per frame from the UI thread: an OPL
    /// seek re-runs the song from its start, which is milliseconds of work and would underrun
    /// the device if it happened inside the callback.
    /// </summary>
    public void PumpDeferred()
    {
        lock (_gate)
        {
            if (_deferredSeek < 0) return;
            double at = _deferredSeek;
            _deferredSeek = -1;
            SeekLocked(at, restartAudio: true);
        }
    }

    /// <summary>Starts the song's own fade-out (event 34), under the lock the mixer holds.</summary>
    public void Fade(byte speed) { lock (_gate) _lds.Fade(speed); }

    private double TickRateLocked() =>
        _device == MusicDevice.Opl || _seq == null ? OplTickRate : _seq.TicksPerSecond;

    private void SeekLocked(double tick, bool restartAudio)
    {
        double max = _seq?.Duration ?? 0;
        tick = Math.Clamp(tick, 0, Math.Max(0, max));
        _tick = tick;
        _sampleAccum = 0;
        _cursor = _seq?.IndexAt(tick) ?? 0;

        if (_device == MusicDevice.Opl)
        {
            SeekOplLocked((int)tick);
        }
        else if (restartAudio)
        {
            SilenceMidiLocked();
            ResetMidiLocked();
            _seq?.ReplayState(tick, SendLocked);
        }
    }

    /// <summary>
    /// The Loudness player cannot seek, so re-run it from the top. Register writes alone
    /// are cheap, but the chip's envelopes would be wrong, so the last second before the
    /// target is rendered (and thrown away) to settle them.
    /// </summary>
    private void SeekOplLocked(int tick)
    {
        _lds.Rewind();
        if (tick <= 0) return;

        const int settle = 70;                     // ~1 s of ticks
        int silent = Math.Max(0, tick - settle);
        for (int i = 0; i < silent; i++) _lds.Update();

        int rendered = tick - silent;
        if (rendered > 0)
        {
            int per = (int)Math.Round(_rate / OplTickRate);
            EnsureScratch(per);
            for (int i = 0; i < rendered; i++)
            {
                _lds.Update();
                _opl.GetSample(_scratchL.AsSpan(0, per));
            }
        }
    }

    // ---------------------------------------------------------------- rendering

    private void EnsureScratch(int frames)
    {
        if (_scratchL.Length >= frames) return;
        int n = Math.Max(frames, 1024);
        _scratchL = new short[n];
        _scratchR = new short[n];
    }

    /// <summary>
    /// Adds this song into the mixer's accumulators. Called from the audio callback;
    /// contributes nothing when the device is native MIDI, which plays outside our mixer.
    /// </summary>
    public void Mix(int* accL, int* accR, int frames, int volumeFactorQ12)
    {
        lock (_gate)
        {
            // A song with no MIDI conversion still plays on the OPL: the conversion is only
            // needed for the note view and the two MIDI devices.
            if (!_playing || _paused) return;
            if (_seq == null && _device != MusicDevice.Opl) return;
            if (_device == MusicDevice.NativeMidi) return;
            if (_device == MusicDevice.Opl && _lds.Song == null) return;

            EnsureScratch(frames);
            double samplesPerTick = _rate / (TickRateLocked() * _speed);
            if (samplesPerTick < 1) samplesPerTick = 1;

            int done = 0;
            while (done < frames)
            {
                if (_sampleAccum <= 0)
                {
                    if (!AdvanceOneTickLocked()) { break; }
                    _sampleAccum += samplesPerTick;
                }

                int chunk = Math.Min(frames - done, Math.Max(1, (int)_sampleAccum));
                RenderChunkLocked(accL + done, accR + done, chunk, volumeFactorQ12);
                _sampleAccum -= chunk;
                done += chunk;
            }
        }
    }

    private void RenderChunkLocked(int* accL, int* accR, int frames, int factor)
    {
        if (_device == MusicDevice.Opl)
        {
            var span = _scratchL.AsSpan(0, frames);
            _opl.GetSample(span);
            for (int i = 0; i < frames; i++)
            {
                int v = span[i] * factor;
                accL[i] += v;
                accR[i] += v;
            }
        }
        else if (_fluid != null && _fluid.IsOpen)
        {
            fixed (short* l = _scratchL)
            fixed (short* r = _scratchR)
            {
                _fluid.Render(l, r, frames);
                for (int i = 0; i < frames; i++)
                {
                    accL[i] += l[i] * factor;
                    accR[i] += r[i] * factor;
                }
            }
        }
    }

    /// <summary>Steps the song one tick. Returns false once it has run out.</summary>
    private bool AdvanceOneTickLocked()
    {
        if (_device == MusicDevice.Opl) return AdvanceOplTickLocked();
        if (_seq == null) return false;

        uint end = LoopEndLocked();
        if (_tick >= end)
        {
            if (!_loop) { _finished = true; _playing = false; SilenceMidiLocked(); return false; }
            LoopCount++;
            SeekLocked(LoopStartLocked(), restartAudio: true);
        }

        var events = _seq.Events;
        while (_cursor < events.Length && events[_cursor].Tick <= _tick)
        {
            SendLocked(events[_cursor]);
            _cursor++;
        }
        _tick += 1;
        return true;
    }

    /// <summary>
    /// The OPL path does not seek to loop: an LDS song loops itself, because the jump is a
    /// command in its own pattern data. All this has to do is keep stepping the player and
    /// notice when that jump has gone backwards, so the playhead follows it. Re-running the
    /// song from the top inside the mixer callback -- which a tick-based wrap would need --
    /// would stall the audio thread for milliseconds every pass.
    /// </summary>
    private bool AdvanceOplTickLocked()
    {
        // An explicit A-B range is the one case that does need a real seek -- and an OPL seek
        // means re-running the song from the top, which must never happen on the audio thread.
        // Ask for it and stop feeding this buffer; PumpDeferred does it on the UI thread.
        if (_loop && _loopB > 0 && _loopB > _loopA && _tick >= _loopB)
        {
            LoopCount++;
            _deferredSeek = Math.Max(0, _loopA);
            return false;
        }

        bool wasLooped = _lds.SongLooped;
        _lds.Update();

        if (!wasLooped && _lds.SongLooped)
        {
            // The song jumped back to its own loop point; move the playhead with it.
            LoopCount++;
            if (!_loop) { _finished = true; _playing = false; return false; }
            _tick = _seq?.LoopStart ?? 0;
            _cursor = _seq?.IndexAt(_tick) ?? 0;
            return true;
        }

        if (!_lds.Playing)
        {
            // A one-shot ran off its end (command 0xfc).
            if (!_loop) { _finished = true; _playing = false; return false; }
            LoopCount++;
            _lds.Rewind();
            _tick = 0;
            _cursor = 0;
            return true;
        }

        _tick += 1;
        return true;
    }

    private uint LoopEndLocked()
    {
        if (_seq == null) return 0;
        if (_loopB > 0 && _loopB > _loopA) return (uint)_loopB;
        if (_loop && _seq.Loops && _seq.LoopEnd <= _seq.Duration) return _seq.LoopEnd;
        return _seq.Duration;
    }

    private double LoopStartLocked()
    {
        if (_seq == null) return 0;
        if (_loopB > 0 && _loopB > _loopA) return Math.Max(0, _loopA);
        if (_seq.Loops && _seq.LoopEnd <= _seq.Duration) return _seq.LoopStart;
        return 0;
    }

    // ---------------------------------------------------------------- midi sink

    /// <summary>
    /// Master volume for the two MIDI devices, 0..255. FluidSynth renders into our own mixer
    /// so the mixer's fader already covers it, but the OS synth plays outside this process
    /// entirely -- the only handle on its level is the per-channel volume controller, so the
    /// song's own CC 7 values are scaled on the way out and re-sent when this changes.
    /// </summary>
    public void SetMidiVolume(int volume255)
    {
        lock (_gate)
        {
            int v = Math.Clamp(volume255, 0, 255);
            if (v == _midiVolume) return;
            _midiVolume = v;
            if (_device == MusicDevice.NativeMidi && _native is { IsOpen: true })
                for (int ch = 0; ch < 16; ch++)
                    _native.Send(0xb0 | ch, 7, ScaleVolumeLocked(_songVolume[ch]));
        }
    }

    private int _midiVolume = 255;
    private readonly byte[] _songVolume = new byte[16];   // last CC 7 the song asked for

    /// <summary>The song's own channel volume, taken down by the master fader.</summary>
    private int ScaleVolumeLocked(int songValue) => songValue * _midiVolume / 255;

    private void SendLocked(SeqEvent e)
    {
        // The event carries the Loudness channel it came from, which is what a lane mutes;
        // its MIDI channel can be the drum channel and is shared between voices.
        if (e.Type == MidiEventType.NoteOn && e.Velocity > 0 && !LaneAudibleLocked(e.Lane)) return;

        // Remember every channel-volume the song sets, and scale it: on the OS synth this is
        // the only thing standing between the music and full blast.
        if (e.Type == MidiEventType.ControlChange && e.Data.Length >= 2 && e.Data[0] == 7)
        {
            _songVolume[e.Channel & 15] = e.Data[1];
            if (_device == MusicDevice.NativeMidi && _native is { IsOpen: true })
            {
                _native.Send(0xb0 | e.Channel, 7, ScaleVolumeLocked(e.Data[1]));
                return;
            }
        }

        switch (_device)
        {
            case MusicDevice.FluidSynth when _fluid != null && _fluid.IsOpen:
                switch (e.Type)
                {
                    case MidiEventType.NoteOn:
                        if (e.Velocity > 0) _fluid.NoteOn(e.Channel, e.Key, e.Velocity);
                        else _fluid.NoteOff(e.Channel, e.Key);
                        break;
                    case MidiEventType.NoteOff: _fluid.NoteOff(e.Channel, e.Key); break;
                    case MidiEventType.ControlChange when e.Data.Length >= 2: _fluid.ControlChange(e.Channel, e.Data[0], e.Data[1]); break;
                    case MidiEventType.ProgramChange when e.Data.Length >= 1: _fluid.ProgramChange(e.Channel, e.Data[0]); break;
                    case MidiEventType.PitchBendChange when e.Data.Length >= 2: _fluid.PitchBend(e.Channel, e.Data[0] | (e.Data[1] << 7)); break;
                    case MidiEventType.ChannelPressure when e.Data.Length >= 1: _fluid.ChannelPressure(e.Channel, e.Data[0]); break;
                    case MidiEventType.KeyPressure when e.Data.Length >= 2: _fluid.KeyPressure(e.Channel, e.Data[0], e.Data[1]); break;
                }
                break;

            case MusicDevice.NativeMidi when _native != null && _native.IsOpen:
                switch (e.Type)
                {
                    case MidiEventType.NoteOn: _native.Send(0x90 | e.Channel, e.Key, e.Velocity); break;
                    case MidiEventType.NoteOff: _native.Send(0x80 | e.Channel, e.Key, e.Data.Length >= 2 ? e.Data[1] : 0); break;
                    case MidiEventType.ControlChange when e.Data.Length >= 2: _native.Send(0xb0 | e.Channel, e.Data[0], e.Data[1]); break;
                    case MidiEventType.ProgramChange when e.Data.Length >= 1: _native.Send(0xc0 | e.Channel, e.Data[0], 0); break;
                    case MidiEventType.PitchBendChange when e.Data.Length >= 2: _native.Send(0xe0 | e.Channel, e.Data[0], e.Data[1]); break;
                    case MidiEventType.ChannelPressure when e.Data.Length >= 1: _native.Send(0xd0 | e.Channel, e.Data[0], 0); break;
                    case MidiEventType.KeyPressure when e.Data.Length >= 2: _native.Send(0xa0 | e.Channel, e.Data[0], e.Data[1]); break;
                }
                break;
        }
    }

    private void SilenceMidiLocked()
    {
        _fluid?.Silence();
        _native?.Silence();
    }

    private void ResetMidiLocked()
    {
        if (_device == MusicDevice.FluidSynth) _fluid?.Reset();
    }

    // ---------------------------------------------------------------- native thread

    // Each player thread gets its own generation number. Switching device twice in a row
    // must not leave the previous thread alive next to the new one, and a single shared
    // "keep running" flag cannot express that -- the new thread would set it back to true
    // before the old one had noticed it was false.
    private int _nativeGen;

    private void StartNativeThreadLocked()
    {
        if (_nativeThread != null) return;
        NativeMidiOut.BeginHighResolutionTimer();
        int gen = ++_nativeGen;
        _nativeThread = new Thread(() => NativeLoop(gen))
        {
            IsBackground = true,
            Name = "T2A native MIDI",
            Priority = ThreadPriority.AboveNormal,
        };
        _nativeThread.Start();
    }

    private void StopNativeThreadLocked()
    {
        if (_nativeThread == null) return;
        _nativeGen++;               // every live loop's generation is now stale
        _nativeThread = null;
        // Not joined: the loop takes _gate, which this caller holds, so waiting for it here
        // would deadlock. It sees the stale generation on its next pass and exits.
        NativeMidiOut.EndHighResolutionTimer();
    }

    private void NativeLoop(int gen)
    {
        var clock = Stopwatch.StartNew();
        double carry = 0;
        long last = clock.ElapsedTicks;
        while (true)
        {
            long now = clock.ElapsedTicks;
            double dt = (now - last) / (double)Stopwatch.Frequency;
            last = now;

            lock (_gate)
            {
                if (_nativeGen != gen) return;
                if (_device == MusicDevice.NativeMidi && _playing && !_paused && _seq != null)
                {
                    carry += dt * _seq.TicksPerSecond * _speed;
                    int steps = (int)carry;
                    if (steps > 240) steps = 240;      // after a stall, do not spray a minute of notes
                    carry -= steps;
                    for (int i = 0; i < steps; i++)
                        if (!AdvanceOneTickLocked()) break;
                }
                else carry = 0;
            }

            Thread.Sleep(1);
        }
    }

    // ---------------------------------------------------------------- misc

    /// <summary>The Loudness player's live state, for the OPL channel strip.</summary>
    public LdsPlayer Lds => _lds;

    public void Dispose()
    {
        lock (_gate)
        {
            StopNativeThreadLocked();
            SilenceMidiLocked();
        }
        _native?.Dispose();
        _fluid?.Dispose();
    }
}
