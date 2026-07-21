namespace T2A.Tyrian.Audio;

/// <summary>One decoded sound: the original 8-bit data as shipped, plus the
/// 16-bit copy resampled to the mixer's rate.</summary>
public sealed class SoundClip
{
    /// <summary>1-based number, the way <c>JE_playSampleNum</c> and the level data refer to it.</summary>
    public int Number;

    /// <summary>The eight-character name the game's own <c>soundTitle</c> table gives it.</summary>
    public string Title = "";

    /// <summary>The engine's constant for it (<c>S_POWERUP</c>, <c>V_BOSS</c>, ...).</summary>
    public string Symbol = "";

    /// <summary>What the sound is used for, in plain words (atlas-only annotation).</summary>
    public string Note = "";

    /// <summary>True for the nine announcer lines out of voices.snd rather than tyrian.snd.</summary>
    public bool IsVoice;

    /// <summary>Raw signed 8-bit mono samples at 11025 Hz, exactly as the file stores them.</summary>
    public sbyte[] Raw = Array.Empty<sbyte>();

    /// <summary>The same sound resampled to the mixer rate, signed 16-bit mono.</summary>
    public short[] Samples = Array.Empty<short>();

    /// <summary>Length in seconds.</summary>
    public float Seconds => Raw.Length / (float)SoundBank.SourceRate;

    /// <summary>Peak amplitude, 0..1, over the raw data.</summary>
    public float Peak;
}

/// <summary>
/// The game's sound effects (<c>tyrian.snd</c>) and announcer voices
/// (<c>voices.snd</c> / <c>voicesc.snd</c>), loaded exactly as <c>loadSndFile</c>
/// in nortsong.c does: a count, a table of file offsets, then signed 8-bit mono
/// PCM at 11025 Hz. Voice clips carry 100 bytes of junk at the end, which the
/// engine trims and so do we.
/// </summary>
public sealed class SoundBank
{
    public const int SfxCount = 31;
    public const int VoiceCount = 9;
    public const int SoundCount = SfxCount + VoiceCount;
    public const int SourceRate = 11025;

    /// <summary>The engine's own <c>soundTitle</c> table (sndmast.c), index 0 = sound number 1.</summary>
    public static readonly string[] Titles =
    {
        "SCALEDN2", "F2", "TEMP10", "EXPLSM", "PASS3", "TEMP2", "BYPASS1", "EXP1RT", "EXPLLOW",
        "TEMP13", "EXPRETAP", "MT2BOOM", "TEMP3", "LAZB", "LAZGUN2", "SPRING", "WARNING", "ITEM",
        "HIT2", "MACHNGUN", "HYPERD2", "EXPLHUG", "CLINK1", "CLICK", "SCALEDN1", "TEMP11",
        "TEMP16", "SMALL1", "POWERUP", "MARS3", "NEEDLE2",
        "VOICE1", "VOICE2", "VOICE3", "VOICE4", "VOICE5", "VOICE6", "VOICE7", "VOICE8", "VOICE9"
    };

    /// <summary>The engine's constant for each sound (sndmast.h). Number 8 has two names --
    /// <c>S_SELECT</c> and <c>S_EXPLOSION_8</c> are the same clip, deliberately.</summary>
    public static readonly string[] Symbols =
    {
        "S_WEAPON_1", "S_WEAPON_2", "S_ENEMY_HIT", "S_EXPLOSION_4", "S_WEAPON_5", "S_WEAPON_6",
        "S_WEAPON_7", "S_SELECT / S_EXPLOSION_8", "S_EXPLOSION_9", "S_WEAPON_10", "S_EXPLOSION_11",
        "S_EXPLOSION_12", "S_WEAPON_13", "S_WEAPON_14", "S_WEAPON_15", "S_SPRING", "S_WARNING",
        "S_ITEM", "S_HULL_HIT", "S_MACHINE_GUN", "S_SOUL_OF_ZINGLON", "S_EXPLOSION_22", "S_CLINK",
        "S_CLICK", "S_WEAPON_25", "S_WEAPON_26", "S_SHIELD_HIT", "S_CURSOR", "S_POWERUP",
        "S_MARS3", "S_NEEDLE2",
        "V_CLEARED_PLATFORM", "V_BOSS", "V_ENEMIES", "V_GOOD_LUCK", "V_LEVEL_END", "V_DANGER",
        "V_SPIKES", "V_DATA_CUBE", "V_ACCELERATE"
    };

    /// <summary>What each announcer voice says, from the sndmast.c comments.</summary>
    public static readonly string[] VoiceLines =
    {
        "Cleared enemy platform.", "Large enemy approaching.", "Enemies ahead.", "Good luck.",
        "Level completed.", "Danger.", "Warning: spikes ahead.", "Data acquired.",
        "Unexplained speed increase."
    };

    /// <summary>
    /// Sound number -> the nine text-window slots (event 16) that speak it. From
    /// sndmast.c's <c>windowTextSamples</c>; V_DANGER covers three of the nine.
    /// </summary>
    public static readonly byte[] WindowTextSamples = { 37, 33, 34, 32, 37, 38, 40, 37, 34 };

    /// <summary>Which mixer channel the engine queues each sound on, where it is fixed.
    /// Channel 3 is the announcer channel and plays at full volume.</summary>
    public static string ChannelNote(int number) => number switch
    {
        >= 32 and <= 40 => "channel 3 -- the announcer channel, full volume",
        29 or 18 or 17 or 19 or 27 or 21 => "channel 7",
        9 or 24 => "channel 6",
        3 or 4 => "channel 5",
        22 => "channel 1",
        23 => "channel 2",
        _ => "",
    };

    /// <summary>The loaded clips, index 0 = sound number 1.</summary>
    public readonly SoundClip[] Clips = new SoundClip[SoundCount];

    /// <summary>True when both files parsed.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Which voice file the announcer lines came from.</summary>
    public string VoiceFile { get; private set; } = "";

    /// <summary>Mixer rate the <see cref="SoundClip.Samples"/> arrays were built for.</summary>
    public int OutputRate { get; private set; }

    /// <summary>
    /// Loads tyrian.snd plus the voice file and resamples everything to
    /// <paramref name="outputRate"/>. Returns false (with the clips left empty)
    /// if either file is missing or malformed.
    /// </summary>
    public bool Load(string dataDir, int outputRate, bool xmas = false)
    {
        OutputRate = outputRate;
        try
        {
            var sfx = ReadSndFile(Path.Combine(dataDir, "tyrian.snd"), SfxCount, trimTail: 0);
            string voiceName = xmas ? "voicesc.snd" : "voices.snd";
            // Voice clips end in 100 bytes of garbage; loadSndFile drops them.
            var voice = ReadSndFile(Path.Combine(dataDir, voiceName), VoiceCount, trimTail: 100);
            if (sfx == null || voice == null) return false;
            VoiceFile = voiceName;

            for (int i = 0; i < SoundCount; i++)
            {
                bool isVoice = i >= SfxCount;
                sbyte[] raw = isVoice ? voice[i - SfxCount] : sfx[i];
                var clip = new SoundClip
                {
                    Number = i + 1,
                    Title = i < Titles.Length ? Titles[i] : $"#{i + 1}",
                    Symbol = i < Symbols.Length ? Symbols[i] : "",
                    IsVoice = isVoice,
                    Note = isVoice ? $"“{VoiceLines[i - SfxCount]}”" : "",
                    Raw = raw,
                    Samples = Resample(raw, SourceRate, outputRate),
                };
                int peak = 0;
                foreach (sbyte s in raw) peak = Math.Max(peak, Math.Abs((int)s));
                clip.Peak = peak / 128f;
                Clips[i] = clip;
            }

            Loaded = true;
            return true;
        }
        catch
        {
            Loaded = false;
            return false;
        }
    }

    /// <summary>Reads one .snd container: u16 count, u32 offsets, then raw signed 8-bit PCM.</summary>
    private static sbyte[][]? ReadSndFile(string path, int expectCount, int trimTail)
    {
        if (!File.Exists(path)) return null;
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 2 + expectCount * 4) return null;

        int count = data[0] | (data[1] << 8);
        if (count != expectCount) return null;

        var offsets = new int[count + 1];
        for (int i = 0; i < count; i++)
            offsets[i] = data[2 + i * 4] | (data[3 + i * 4] << 8) | (data[4 + i * 4] << 16) | (data[5 + i * 4] << 24);
        offsets[count] = data.Length;

        var result = new sbyte[count][];
        for (int i = 0; i < count; i++)
        {
            int start = offsets[i];
            int len = offsets[i + 1] - start;
            if (start < 0 || len < 0 || start + len > data.Length) return null;
            len = Math.Max(0, len - trimTail);
            if (len > ushort.MaxValue) return null;   // the engine refuses these too
            var buf = new sbyte[len];
            for (int j = 0; j < len; j++) buf[j] = unchecked((sbyte)data[start + j]);
            result[i] = buf;
        }
        return result;
    }

    /// <summary>
    /// 11025 Hz signed 8-bit to the mixer's signed 16-bit rate. The engine hands this
    /// to SDL_ConvertAudio; linear interpolation matches what SDL does closely enough
    /// that the difference is inaudible, and it needs no SDL audio device to exist.
    /// </summary>
    private static short[] Resample(sbyte[] src, int srcRate, int dstRate)
    {
        if (src.Length == 0) return Array.Empty<short>();
        if (srcRate == dstRate)
        {
            var same = new short[src.Length];
            for (int i = 0; i < src.Length; i++) same[i] = (short)(src[i] << 8);
            return same;
        }

        long outLen = (long)src.Length * dstRate / srcRate;
        var dst = new short[Math.Max(1, (int)outLen)];
        double step = (double)srcRate / dstRate;
        for (int i = 0; i < dst.Length; i++)
        {
            double pos = i * step;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, src.Length - 1);
            double f = pos - i0;
            double v = src[Math.Min(i0, src.Length - 1)] * (1 - f) + src[i1] * f;
            dst[i] = (short)Math.Clamp((int)Math.Round(v * 256), short.MinValue, short.MaxValue);
        }
        return dst;
    }
}
