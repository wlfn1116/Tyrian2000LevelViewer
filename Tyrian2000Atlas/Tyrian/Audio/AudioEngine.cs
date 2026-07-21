using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SdlNs = Hexa.NET.SDL2;

namespace T2LV.Tyrian.Audio;

/// <summary>
/// The viewer's one audio device and mixer. It follows loudness.c: eight sample
/// channels at eight volume steps, a 30 dB volume curve, and the music summed in
/// ahead of them. Two deliberate differences: the device is stereo rather than
/// mono (the game's mono sources are simply written to both sides, but a SoundFont
/// song is not mono and there is no reason to fold it down), and the music can come
/// from any of three devices instead of only the OPL.
/// </summary>
public sealed unsafe class AudioEngine : IDisposable
{
    /// <summary>11025 * 4, the rate the game asks for.</summary>
    public const int OutputRate = 44100;
    private const int BufferFrames = 1024;               // ~23 ms, as the game asks for
    private const int ChannelCount = 8;                  // loudness.c CHANNEL_COUNT
    private const int ChannelVolumeLevels = 8;           // loudness.c CHANNEL_VOLUME_LEVELS

    private const uint SdlInitAudio = 0x00000010u;
    private const ushort AudioS16Sys = 0x8010;           // AUDIO_S16LSB
    // SDL_AUDIO_ALLOW_*: FREQUENCY 0x1, FORMAT 0x2, CHANNELS 0x4, SAMPLES 0x8. Only frequency
    // and buffer size may move, exactly as loudness.c asks. Letting the FORMAT move is what a
    // 0x2 here would do, and a device that came back as float32 would take these 16-bit
    // samples as denormals and play perfect silence.
    private const int AllowFrequencyChange = 0x00000001;
    private const int AllowSamplesChange = 0x00000008;

    /// <summary>Fixed point Q20.12, exactly as the engine's volumeFactorTable.</summary>
    private static readonly int[] VolumeFactor = BuildVolumeTable();

    private static int[] BuildVolumeTable()
    {
        const float volumeRange = 30.0f;   // dB
        var t = new int[256];
        t[0] = 0;
        for (int i = 1; i < 256; i++)
            t[i] = (int)(MathF.Pow(10, (255 - i) * (-volumeRange / (20.0f * 255))) * (1 << 12));
        return t;
    }

    private static AudioEngine? _active;                 // the callback's only way back to us

    private uint _device;
    private bool _subsystemUp;

    private readonly short[]?[] _chanData = new short[ChannelCount][];
    private readonly int[] _chanPos = new int[ChannelCount];
    private readonly int[] _chanLen = new int[ChannelCount];
    private readonly int[] _chanVol = new int[ChannelCount];

    private int[] _accL = new int[BufferFrames];
    private int[] _accR = new int[BufferFrames];

    /// <summary>The sound effects and announcer voices.</summary>
    public SoundBank Sounds { get; } = new();

    /// <summary>The songs.</summary>
    public MusicBank Music { get; } = new();

    /// <summary>The one music player; null until <see cref="Open"/> succeeds.</summary>
    public MusicPlayer? Player { get; private set; }

    /// <summary>0..255, the engine's own scale. 191 is its default.</summary>
    public int MusicVolume { get; set; } = 191;

    /// <summary>0..255, the engine's own scale. 191 is its default.</summary>
    public int SampleVolume { get; set; } = 191;

    /// <summary>Mutes music without stopping it (the engine's music_disabled).</summary>
    public bool MusicDisabled { get; set; }

    /// <summary>Mutes sound effects (the engine's samples_disabled).</summary>
    public bool SamplesDisabled { get; set; }

    /// <summary>True once the device is open and mixing.</summary>
    public bool IsOpen => _device != 0;

    /// <summary>Why <see cref="Open"/> failed, if it did.</summary>
    public string Error { get; private set; } = "";

    /// <summary>The device rate SDL actually gave us.</summary>
    public int SampleRate { get; private set; } = OutputRate;

    /// <summary>Peak level of the last mixed buffer, 0..1, for the level meter.</summary>
    public float Level { get; private set; }

    /// <summary>
    /// Buffers the device has asked us for. If this stays at zero the callback is not running
    /// at all, which is a different fault from "the callback runs but everything it mixes is
    /// silent" -- and from the outside the two are indistinguishable, hence the counter.
    /// </summary>
    public long CallbackCount => Volatile.Read(ref _callbacks);
    private long _callbacks;

    /// <summary>The first exception the audio callback threw, if it ever threw one. The
    /// callback must not let anything escape into SDL, so it swallows -- and reports here.</summary>
    public string CallbackError { get; private set; } = "";

    /// <summary>Frames per buffer the device settled on.</summary>
    public int BufferSize { get; private set; } = BufferFrames;

    /// <summary>The audio driver SDL chose (wasapi, directsound, ...), for the status line.</summary>
    public string Driver { get; private set; } = "";

    /// <summary>
    /// One line describing the state of the whole audio path. It reports health, not a running
    /// total: the device asks for a buffer about forty times a second for as long as it is
    /// open, so a raw count only ever climbs and reads like a leak. What matters is whether it
    /// is still climbing, which is what "running" means here.
    /// </summary>
    public string StatusLine()
    {
        if (!IsOpen) return Error.Length > 0 ? "closed: " + Error : "closed";
        string s = $"{Driver} {SampleRate} Hz, {BufferSize} frames";
        if (CallbackError.Length > 0) s += "  !! " + CallbackError;
        else if (CallbackCount == 0) s += "  !! the device is not asking for audio";
        else s += Running ? "  ·  running" : "  !! stalled";
        return s;
    }

    /// <summary>True while the device is still asking for buffers -- sampled between calls to
    /// <see cref="StatusLine"/>, so it answers "is it alive now", not "has it ever run".</summary>
    private bool Running
    {
        get
        {
            long now = CallbackCount;
            long since = now - _lastSeenCallbacks;
            var elapsed = Environment.TickCount64 - _lastSeenAt;
            if (elapsed < 250) return _wasRunning;      // too soon to tell
            _lastSeenCallbacks = now;
            _lastSeenAt = Environment.TickCount64;
            return _wasRunning = since > 0;
        }
    }

    private long _lastSeenCallbacks;
    private long _lastSeenAt;
    private bool _wasRunning = true;

    /// <summary>
    /// Opens the audio device and loads music.mus plus the sound files from
    /// <paramref name="dataDir"/>. Safe to call again after <see cref="Close"/>;
    /// returns false (with <see cref="Error"/> set) if the machine has no audio.
    /// </summary>
    public bool Open(string dataDir)
    {
        Close();
        try
        {
            if (SdlNs.SDL.InitSubSystem(SdlInitAudio) != 0)
            {
                Error = "SDL audio would not start: " + SdlNs.SDL.GetErrorS();
                return false;
            }
            _subsystemUp = true;

            SdlNs.SDLAudioSpec want = default, got = default;
            want.Freq = OutputRate;
            want.Format = AudioS16Sys;
            want.Channels = 2;
            want.Samples = BufferFrames;
            want.Callback = (void*)(delegate* unmanaged[Cdecl]<void*, byte*, int, void>)&StaticCallback;

            _active = this;
            _device = SdlNs.SDL.OpenAudioDevice((byte*)null, 0, in want, ref got,
                AllowFrequencyChange | AllowSamplesChange);
            if (_device == 0)
            {
                Error = "no audio device: " + SdlNs.SDL.GetErrorS();
                _active = null;
                return false;
            }

            // Belt and braces: the mixer writes interleaved signed 16-bit stereo and nothing
            // else, so if the device somehow came back as anything different, say so rather
            // than filling its buffers with bytes it will misread.
            if (got.Format != AudioS16Sys || got.Channels != 2)
            {
                Error = $"the device wants format 0x{got.Format:X4} / {got.Channels}ch, " +
                        "and this mixer only writes 16-bit stereo";
                SdlNs.SDL.CloseAudioDevice(_device);
                _device = 0;
                _active = null;
                return false;
            }

            SampleRate = got.Freq > 0 ? got.Freq : OutputRate;
            int frames = got.Samples > 0 ? got.Samples : BufferFrames;
            BufferSize = frames;
            Driver = SdlNs.SDL.GetCurrentAudioDriverS() ?? "";
            _callbacks = 0;
            CallbackError = "";
            _accL = new int[frames];
            _accR = new int[frames];

            Player = new MusicPlayer(SampleRate);
            Music.Load(dataDir);
            Sounds.Load(dataDir, SampleRate);

            // A dropped-in libfluidsynth: next to the exe, in the data folder, or in the
            // OpenTyrian install the data folder sits inside.
            FluidSynth.AddSearchPath(AppContext.BaseDirectory);
            FluidSynth.AddSearchPath(dataDir);
            FluidSynth.AddSearchPath(Directory.GetParent(dataDir)?.FullName);

            SdlNs.SDL.PauseAudioDevice(_device, 0);
            Error = "";
            return true;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Close();
            return false;
        }
    }

    /// <summary>Stops everything and closes the device.</summary>
    public void Close()
    {
        if (_device != 0)
        {
            SdlNs.SDL.PauseAudioDevice(_device, 1);
            SdlNs.SDL.CloseAudioDevice(_device);
            _device = 0;
        }
        if (_active == this) _active = null;
        Player?.Dispose();
        Player = null;
        Array.Clear(_chanLen);          // the device is shut: no callback can be reading these
        if (_subsystemUp) { SdlNs.SDL.QuitSubSystem(SdlInitAudio); _subsystemUp = false; }
    }

    public void Dispose() => Close();

    // ------------------------------------------------------------ sound effects

    /// <summary>
    /// Plays a 1-based sound number on one of the eight channels, at one of the eight
    /// volume steps. Like <c>multiSamplePlay</c> this pre-empts whatever that channel
    /// was playing -- the game has no voice stealing, and neither do we.
    /// </summary>
    public void PlaySound(int number, int channel = 0, int volumeLevel = 4)
    {
        if (_device == 0 || SamplesDisabled) return;
        if (number < 1 || number > SoundBank.SoundCount) return;
        var clip = Sounds.Clips[number - 1];
        if (clip == null || clip.Samples.Length == 0) return;

        channel = Math.Clamp(channel, 0, ChannelCount - 1);
        volumeLevel = Math.Clamp(volumeLevel, 0, ChannelVolumeLevels - 1);

        SdlNs.SDL.LockAudioDevice(_device);
        try
        {
            _chanData[channel] = clip.Samples;
            _chanPos[channel] = 0;
            _chanLen[channel] = clip.Samples.Length;
            _chanVol[channel] = volumeLevel;
        }
        finally { SdlNs.SDL.UnlockAudioDevice(_device); }
    }

    /// <summary>The engine's <c>JE_playSampleNum</c>: channel 0 at full effect volume.</summary>
    public void PlaySampleNum(int number) => PlaySound(number, 0, 4);

    /// <summary>
    /// Fires and clears a whole <c>soundQueue[8]</c> under one device lock, at the volumes the
    /// engine's own drain uses: channel 3 is the announcer at full, the Lightning weapon is
    /// quartered, everything else runs at half. One lock rather than up to eight -- after a
    /// dropped frame the simulation can hand over hundreds of ticks at once.
    /// </summary>
    public void PlayQueue(byte[] queue)
    {
        if (_device == 0 || SamplesDisabled) return;
        bool any = false;
        for (int i = 0; i < queue.Length && !any; i++) any = queue[i] != 0;
        if (!any) return;

        SdlNs.SDL.LockAudioDevice(_device);
        try
        {
            for (int ch = 0; ch < Math.Min(queue.Length, ChannelCount); ch++)
            {
                int number = queue[ch];
                queue[ch] = 0;
                if (number < 1 || number > SoundBank.SoundCount) continue;
                var clip = Sounds.Clips[number - 1];
                if (clip == null || clip.Samples.Length == 0) continue;

                _chanData[ch] = clip.Samples;
                _chanPos[ch] = 0;
                _chanLen[ch] = clip.Samples.Length;
                _chanVol[ch] = ch == 3 ? 4 : number == 15 ? 1 : 2;
            }
        }
        finally { SdlNs.SDL.UnlockAudioDevice(_device); }
    }

    /// <summary>
    /// Reloads the sound bank with or without the Christmas announcer (voicesc.snd instead of
    /// voices.snd). The channels are silenced first: they hold references to the sample arrays
    /// this is about to replace, exactly as stop_sample_channels guards the same swap in the
    /// game (opentyr.c's Christmas toggle).
    /// </summary>
    public bool SetXmasVoices(string dataDir, bool xmas)
    {
        StopAllSounds();
        return Sounds.Load(dataDir, SampleRate, xmas);
    }

    /// <summary>Silences every sample channel.</summary>
    public void StopAllSounds()
    {
        if (_device == 0) return;
        SdlNs.SDL.LockAudioDevice(_device);
        try { Array.Clear(_chanLen); }
        finally { SdlNs.SDL.UnlockAudioDevice(_device); }
    }

    /// <summary>True while that channel still has samples to play.</summary>
    public bool ChannelBusy(int channel) =>
        (uint)channel < ChannelCount && Volatile.Read(ref _chanLen[channel]) > 0;

    /// <summary>How far through its clip a channel is, 0..1; 0 when it is idle.</summary>
    public float ChannelProgress(int channel)
    {
        if ((uint)channel >= ChannelCount) return 0f;
        int pos = Volatile.Read(ref _chanPos[channel]);
        int left = Volatile.Read(ref _chanLen[channel]);
        if (left <= 0) return 0f;
        int total = pos + left;
        return total > 0 ? pos / (float)total : 0f;
    }

    /// <summary>Which sound number a channel is playing, or 0 when it is idle.</summary>
    public int ChannelSound(int channel)
    {
        if ((uint)channel >= ChannelCount || Volatile.Read(ref _chanLen[channel]) <= 0) return 0;
        var data = _chanData[channel];
        if (data == null) return 0;
        for (int i = 0; i < SoundBank.SoundCount; i++)
            if (ReferenceEquals(Sounds.Clips[i]?.Samples, data)) return i + 1;
        return 0;
    }

    // ------------------------------------------------------------ the callback

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void StaticCallback(void* userdata, byte* stream, int len)
    {
        var self = _active;
        if (self == null)
        {
            new Span<byte>(stream, len).Clear();
            return;
        }
        try { self.Callback((short*)stream, len / (2 * sizeof(short))); }
        catch (Exception e)
        {
            // Nothing may cross back into SDL, so the fault is recorded and reported in the
            // audio status line instead -- otherwise a throwing mixer is just silence.
            new Span<byte>(stream, len).Clear();
            if (self.CallbackError.Length == 0) self.CallbackError = e.GetType().Name + ": " + e.Message;
        }
    }

    private void Callback(short* outBuf, int frames)
    {
        Interlocked.Increment(ref _callbacks);
        if (frames <= 0) return;
        if (_accL.Length < frames) { _accL = new int[frames]; _accR = new int[frames]; }

        fixed (int* accL = _accL)
        fixed (int* accR = _accR)
        {
            Unsafe.InitBlockUnaligned(accL, 0, (uint)(frames * sizeof(int)));
            Unsafe.InitBlockUnaligned(accR, 0, (uint)(frames * sizeof(int)));

            if (!MusicDisabled && Player != null)
            {
                // The OPL emulator is quiet next to the samples; the engine doubles it here too.
                int factor = VolumeFactor[Math.Clamp(MusicVolume, 0, 255)];
                if (Player.Device == MusicDevice.Opl) factor *= 2;
                Player.Mix(accL, accR, frames, factor);
            }

            if (!SamplesDisabled)
            {
                // Channel state is guarded by SDL's own audio lock, which every writer takes.
                int sampleFactor = VolumeFactor[Math.Clamp(SampleVolume, 0, 255)];
                for (int c = 0; c < ChannelCount; c++)
                {
                    int remaining = _chanLen[c];
                    if (remaining <= 0) continue;
                    var data = _chanData[c];
                    if (data == null) { _chanLen[c] = 0; continue; }

                    int f = sampleFactor * (_chanVol[c] + 1) / ChannelVolumeLevels;
                    int pos = _chanPos[c];
                    int n = Math.Min(frames, remaining);
                    fixed (short* src = data)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            int v = src[pos + i] * f;
                            accL[i] += v;
                            accR[i] += v;
                        }
                    }
                    _chanPos[c] = pos + n;
                    _chanLen[c] = remaining - n;
                }
            }

            int peak = 0;
            for (int i = 0; i < frames; i++)
            {
                int l = accL[i] >> 12, r = accR[i] >> 12;
                if (l > short.MaxValue) l = short.MaxValue; else if (l < short.MinValue) l = short.MinValue;
                if (r > short.MaxValue) r = short.MaxValue; else if (r < short.MinValue) r = short.MinValue;
                outBuf[i * 2] = (short)l;
                outBuf[i * 2 + 1] = (short)r;
                int a = l < 0 ? -l : l, b = r < 0 ? -r : r;
                if (a > peak) peak = a;
                if (b > peak) peak = b;
            }
            Level = peak / 32768f;
        }
    }
}
