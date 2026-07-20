namespace T2LV.Tyrian.Audio;

/// <summary>One LDS instrument ("patch") -- the OPL2 operator settings plus the
/// General-MIDI hints the LOUDNESS editor stored alongside them.</summary>
public sealed class LdsPatch
{
    public byte ModMisc, ModVol, ModAd, ModSr, ModWave;
    public byte CarMisc, CarVol, CarAd, CarSr, CarWave;
    public byte Feedback, Keyoff, Portamento, Glide, Finetune;
    public byte Vibrato, Vibdelay, ModTrem, CarTrem, Tremwait, Arpeggio;
    public readonly byte[] ArpTab = new byte[12];
    public ushort Start, Size;
    public byte Fms;
    public ushort Transp;
    public byte Midinst, Midvelo, Midkey, Midtrans, Middum1, Middum2;
}

/// <summary>One cell of a song's order list: which pattern a channel plays, and its transpose.</summary>
public struct LdsPosition
{
    public ushort PatNum;      // already divided by 2 -- an index into the 16-bit pattern words
    public byte Transpose;
}

/// <summary>
/// A single song parsed out of <c>music.mus</c>. Exact port of <c>lds_load</c>
/// (lds_play.c); the layout is the LOUDNESS/AdLib "LDS" format.
/// </summary>
public sealed class LdsSong
{
    public byte Mode;
    public ushort Speed;
    public byte Tempo;
    public byte PattLen;
    public readonly byte[] ChanDelay = new byte[9];
    public byte RegBd;
    public LdsPatch[] Patches = Array.Empty<LdsPatch>();
    public LdsPosition[] Positions = Array.Empty<LdsPosition>();   // NumPosi * 9, row-major
    public int NumPosi;
    public ushort[] Patterns = Array.Empty<ushort>();

    /// <summary>Parses one song. Returns null if the data is not a valid LDS song.</summary>
    public static LdsSong? Load(ReadOnlySpan<byte> d)
    {
        if (d.Length < 16) return null;

        var s = new LdsSong();
        int p = 0;

        s.Mode = d[p++];
        if (s.Mode > 2) return null;          // lds_load: "error: failed to load music"
        s.Speed = (ushort)(d[p] | (d[p + 1] << 8)); p += 2;
        s.Tempo = d[p++];
        s.PattLen = d[p++];
        for (int i = 0; i < 9; i++) s.ChanDelay[i] = d[p++];
        s.RegBd = d[p++];

        // patches
        int numpatch = d[p] | (d[p + 1] << 8); p += 2;
        if (numpatch < 0 || p + numpatch * 46 > d.Length) return null;
        s.Patches = new LdsPatch[numpatch];
        for (int i = 0; i < numpatch; i++)
        {
            var sb = new LdsPatch();
            sb.ModMisc = d[p++]; sb.ModVol = d[p++]; sb.ModAd = d[p++]; sb.ModSr = d[p++]; sb.ModWave = d[p++];
            sb.CarMisc = d[p++]; sb.CarVol = d[p++]; sb.CarAd = d[p++]; sb.CarSr = d[p++]; sb.CarWave = d[p++];
            sb.Feedback = d[p++]; sb.Keyoff = d[p++]; sb.Portamento = d[p++]; sb.Glide = d[p++]; sb.Finetune = d[p++];
            sb.Vibrato = d[p++]; sb.Vibdelay = d[p++]; sb.ModTrem = d[p++]; sb.CarTrem = d[p++]; sb.Tremwait = d[p++];
            sb.Arpeggio = d[p++];
            for (int j = 0; j < 12; j++) sb.ArpTab[j] = d[p++];
            sb.Start = (ushort)(d[p] | (d[p + 1] << 8)); p += 2;
            sb.Size = (ushort)(d[p] | (d[p + 1] << 8)); p += 2;
            sb.Fms = d[p++];
            sb.Transp = (ushort)(d[p] | (d[p + 1] << 8)); p += 2;
            sb.Midinst = d[p++]; sb.Midvelo = d[p++]; sb.Midkey = d[p++]; sb.Midtrans = d[p++];
            sb.Middum1 = d[p++]; sb.Middum2 = d[p++];
            s.Patches[i] = sb;
        }

        // positions
        if (p + 2 > d.Length) return null;
        int numposi = d[p] | (d[p + 1] << 8); p += 2;
        if (numposi < 0 || p + numposi * 9 * 3 > d.Length) return null;
        s.NumPosi = numposi;
        s.Positions = new LdsPosition[numposi * 9];
        for (int i = 0; i < numposi; i++)
        {
            for (int j = 0; j < 9; j++)
            {
                // patnum is a byte pointer into the pattern space, but patterns are
                // 16-bit words, so halving it gives our word index (lds_load).
                ushort patnum = (ushort)(d[p] | (d[p + 1] << 8)); p += 2;
                byte transpose = d[p++];
                s.Positions[i * 9 + j] = new LdsPosition { PatNum = (ushort)(patnum / 2), Transpose = transpose };
            }
        }

        p += 2;   // ignore # of digital sounds (lds_load does the same)
        if (p > d.Length) return null;

        int numpatterns = (d.Length - p) / 2;
        s.Patterns = new ushort[numpatterns];
        for (int i = 0; i < numpatterns; i++) { s.Patterns[i] = (ushort)(d[p] | (d[p + 1] << 8)); p += 2; }

        return s;
    }

    /// <summary>Number of distinct patterns referenced by the order list (for display).</summary>
    public int UsedPatternCount()
    {
        var seen = new HashSet<ushort>();
        foreach (var pos in Positions) seen.Add(pos.PatNum);
        return seen.Count;
    }
}

/// <summary>
/// Exact port of the game's LOUDNESS song player (<c>lds_play.c</c>), driving an
/// <see cref="OplChip"/>. Instance-based so the game mixer and the music-player
/// window can each run their own song.
/// </summary>
public sealed class LdsPlayer
{
    // Note frequency table (16 notes / octave)
    private static readonly ushort[] Frequency =
    {
        343, 344, 345, 347, 348, 349, 350, 352, 353, 354, 356, 357, 358,
        359, 361, 362, 363, 365, 366, 367, 369, 370, 371, 373, 374, 375,
        377, 378, 379, 381, 382, 384, 385, 386, 388, 389, 391, 392, 393,
        395, 396, 398, 399, 401, 402, 403, 405, 406, 408, 409, 411, 412,
        414, 415, 417, 418, 420, 421, 423, 424, 426, 427, 429, 430, 432,
        434, 435, 437, 438, 440, 442, 443, 445, 446, 448, 450, 451, 453,
        454, 456, 458, 459, 461, 463, 464, 466, 468, 469, 471, 473, 475,
        476, 478, 480, 481, 483, 485, 487, 488, 490, 492, 494, 496, 497,
        499, 501, 503, 505, 506, 508, 510, 512, 514, 516, 518, 519, 521,
        523, 525, 527, 529, 531, 533, 535, 537, 538, 540, 542, 544, 546,
        548, 550, 552, 554, 556, 558, 560, 562, 564, 566, 568, 571, 573,
        575, 577, 579, 581, 583, 585, 587, 589, 591, 594, 596, 598, 600,
        602, 604, 607, 609, 611, 613, 615, 618, 620, 622, 624, 627, 629,
        631, 633, 636, 638, 640, 643, 645, 647, 650, 652, 654, 657, 659,
        662, 664, 666, 669, 671, 674, 676, 678, 681, 683
    };

    // Vibrato (sine) table
    private static readonly byte[] VibTab =
    {
        0, 13, 25, 37, 50, 62, 74, 86, 98, 109, 120, 131, 142, 152, 162,
        171, 180, 189, 197, 205, 212, 219, 225, 231, 236, 240, 244, 247,
        250, 252, 254, 255, 255, 255, 254, 252, 250, 247, 244, 240, 236,
        231, 225, 219, 212, 205, 197, 189, 180, 171, 162, 152, 142, 131,
        120, 109, 98, 86, 74, 62, 50, 37, 25, 13
    };

    // Tremolo (sine * sine) table
    private static readonly byte[] TremTab =
    {
        0, 0, 1, 1, 2, 4, 5, 7, 10, 12, 15, 18, 21, 25, 29, 33, 37, 42, 47,
        52, 57, 62, 67, 73, 79, 85, 90, 97, 103, 109, 115, 121, 128, 134,
        140, 146, 152, 158, 165, 170, 176, 182, 188, 193, 198, 203, 208,
        213, 218, 222, 226, 230, 234, 237, 240, 243, 245, 248, 250, 251,
        253, 254, 254, 255, 255, 255, 254, 254, 253, 251, 250, 248, 245,
        243, 240, 237, 234, 230, 226, 222, 218, 213, 208, 203, 198, 193,
        188, 182, 176, 170, 165, 158, 152, 146, 140, 134, 127, 121, 115,
        109, 103, 97, 90, 85, 79, 73, 67, 62, 57, 52, 47, 42, 37, 33, 29,
        25, 21, 18, 15, 12, 10, 7, 5, 4, 2, 1, 1, 0
    };

    /// <summary>Base register offset of each of the nine channels' operator pair.</summary>
    public static readonly byte[] OpTable = { 0x00, 0x01, 0x02, 0x08, 0x09, 0x0a, 0x10, 0x11, 0x12 };

    private const ushort MaxSound = 0x3f, MaxPos = 0xff;

    private sealed class Channel
    {
        public ushort gototune, lasttune, packpos;
        public byte finetune, glideto, portspeed, nextvol, volmod, volcar,
            vibwait, vibspeed, vibrate, trmstay, trmwait, trmspeed, trmrate, trmcount,
            trcwait, trcspeed, trcrate, trccount, arp_size, arp_speed, keycount,
            vibcount, arp_pos, arp_count, packwait;
        public readonly byte[] arp_tab = new byte[12];
        public byte cheatDelay, cheatSound;
        public ushort cheatHigh;

        public void Clear()
        {
            gototune = lasttune = packpos = 0;
            finetune = glideto = portspeed = nextvol = volmod = volcar = 0;
            vibwait = vibspeed = vibrate = trmstay = trmwait = trmspeed = trmrate = trmcount = 0;
            trcwait = trcspeed = trcrate = trccount = arp_size = arp_speed = keycount = 0;
            vibcount = arp_pos = arp_count = packwait = 0;
            Array.Clear(arp_tab);
            cheatDelay = cheatSound = 0; cheatHigh = 0;
        }
    }

    private readonly OplChip? _opl;
    private readonly Channel[] _channel = new Channel[9];
    private readonly byte[] _fmchip = new byte[256];
    private LdsSong? _song;

    private byte _jumping, _fadeonoff, _allvolume, _hardfade, _tempoNow, _pattplay, _tempo, _regbd;
    private ushort _posplay, _jumppos;
    private ushort _mainvolume;

    /// <summary>False once the song has stopped itself (command 0xfc) or a hard fade finished.</summary>
    public bool Playing { get; private set; }

    /// <summary>Set when the order list jumps backwards, i.e. the song wrapped around.</summary>
    public bool SongLooped { get; private set; }

    /// <summary>Order-list position currently playing.</summary>
    public int OrderPos => _posplay;

    /// <summary>Row within the current pattern.</summary>
    public int Row => _pattplay;

    /// <summary>Current song tempo (ticks per row).</summary>
    public int TempoNow => _tempo;

    /// <summary>Song-wide volume scaler the song itself set (0 = untouched/full).</summary>
    public int AllVolume => _allvolume;

    /// <summary>Per-channel mute, applied at the OPL key-on bit so the shadow register
    /// state stays intact and unmuting resumes cleanly. Viewer-only; not in the game.</summary>
    public readonly bool[] ChannelMuted = new bool[9];

    /// <summary>Last instrument each channel was told to play, for the UI's channel strip. -1 = none.</summary>
    public readonly int[] ChannelInstrument = new int[9];

    /// <summary>Last note (in 1/16-semitone "tunehigh" units) each channel started. -1 = none.</summary>
    public readonly int[] ChannelTune = new int[9];

    public LdsPlayer(OplChip? opl)
    {
        _opl = opl;
        for (int i = 0; i < 9; i++) _channel[i] = new Channel();
    }

    /// <summary>The song currently loaded, if any.</summary>
    public LdsSong? Song => _song;

    /// <summary>Loads a song and rewinds to its start.</summary>
    public void Load(LdsSong? song)
    {
        _song = song;
        _regbd = song?.RegBd ?? 0;
        _tempo = song?.Tempo ?? 0;
        Rewind();
    }

    /// <summary>Port of <c>lds_rewind</c>: resets the player and the OPL chip.</summary>
    public void Rewind()
    {
        _tempoNow = 3;
        Playing = _song != null;
        SongLooped = false;
        _jumping = _fadeonoff = _allvolume = _hardfade = _pattplay = 0;
        _posplay = _jumppos = 0;
        _mainvolume = 0;
        // Deliberately NOT _tempo: lds_rewind leaves it alone, so a song that changed tempo
        // with command 0xfe keeps it across a rewind. Only lds_load re-reads the header, which
        // is what Load does.
        foreach (var c in _channel) c.Clear();
        Array.Clear(_fmchip);
        Array.Fill(ChannelInstrument, -1);
        Array.Fill(ChannelTune, -1);

        // OPL2 init
        _opl?.Init(_opl.SampleRate);
        Write(1, 0x20);
        Write(8, 0);
        Write(0xbd, _regbd);

        for (int i = 0; i < 9; i++)
        {
            Write(0x20 + OpTable[i], 0);
            Write(0x23 + OpTable[i], 0);
            Write(0x40 + OpTable[i], 0x3f);
            Write(0x43 + OpTable[i], 0x3f);
            Write(0x60 + OpTable[i], 0xff);
            Write(0x63 + OpTable[i], 0xff);
            Write(0x80 + OpTable[i], 0xff);
            Write(0x83 + OpTable[i], 0xff);
            Write(0xe0 + OpTable[i], 0);
            Write(0xe3 + OpTable[i], 0);
            Write(0xa0 + i, 0);
            Write(0xb0 + i, 0);
            Write(0xc0 + i, 0);
        }
    }

    private void Write(int reg, int val) => _opl?.Write(reg, (byte)val);

    /// <summary>Port of <c>lds_fade</c>: starts a fade-out at the given speed (1 = the game's slow fade).</summary>
    public void Fade(byte speed) => _fadeonoff = speed;

    private void SetRegs(byte reg, byte val)
    {
        if (_fmchip[reg] == val) return;
        _fmchip[reg] = val;
        _opl?.Write(reg, val);
    }

    private void SetRegsAdv(byte reg, byte mask, byte val) => SetRegs(reg, (byte)((_fmchip[reg] & mask) | val));

    /// <summary>Shadow copy of the OPL register file, as the song has driven it.</summary>
    public byte ReadShadow(int reg) => _fmchip[reg & 0xff];

    /// <summary>
    /// Port of <c>lds_update</c>: advances the song by one Loudness tick (~69.5 Hz)
    /// and pushes the resulting register writes at the chip. Returns false once the
    /// song has ended or looped.
    /// </summary>
    public bool Update()
    {
        if (!Playing || _song == null) return false;

        var song = _song;
        var positions = song.Positions;
        var patterns = song.Patterns;

        // handle fading
        if (_fadeonoff != 0)
        {
            if (_fadeonoff <= 128)
            {
                if (_allvolume > _fadeonoff || _allvolume == 0)
                {
                    _allvolume = unchecked((byte)(_allvolume - _fadeonoff));
                }
                else
                {
                    _allvolume = 1;
                    _fadeonoff = 0;
                    if (_hardfade != 0)
                    {
                        Playing = false;
                        _hardfade = 0;
                        for (int i = 0; i < 9; i++) _channel[i].keycount = 1;
                    }
                }
            }
            else
            {
                if ((byte)((_allvolume + (0x100 - _fadeonoff)) & 0xff) <= _mainvolume)
                    _allvolume = unchecked((byte)(_allvolume + (0x100 - _fadeonoff)));
                else
                {
                    _allvolume = (byte)_mainvolume;
                    _fadeonoff = 0;
                }
            }
        }

        // handle channel delay
        for (int chan = 0; chan < 9; chan++)
        {
            var c = _channel[chan];
            if (c.cheatDelay != 0)
            {
                if (--c.cheatDelay == 0)
                    PlaySound(c.cheatSound, chan, c.cheatHigh);
            }
        }

        // handle notes
        if (_tempoNow == 0 && positions.Length > 0)
        {
            bool vbreak = false;
            for (int chan = 0; chan < 9; chan++)
            {
                var c = _channel[chan];
                if (c.packwait == 0)
                {
                    int idx = _posplay * 9 + chan;
                    ushort patnum = idx < positions.Length ? positions[idx].PatNum : (ushort)0;
                    byte transpose = idx < positions.Length ? positions[idx].Transpose : (byte)0;

                    int wordIdx = patnum + c.packpos;
                    // The C player reads the pattern space unchecked; a truncated song
                    // would walk off the end, so treat out-of-range as an empty cell.
                    ushort comword = (uint)wordIdx < (uint)patterns.Length ? patterns[wordIdx] : (ushort)0;
                    byte comhi = (byte)(comword >> 8), comlo = (byte)(comword & 0xff);
                    if (comword != 0)
                    {
                        if (comhi == 0x80)
                        {
                            c.packwait = comlo;
                        }
                        else if (comhi >= 0x80)
                        {
                            switch (comhi)
                            {
                                case 0xff:
                                    c.volcar = (byte)((((c.volcar & 0x3f) * comlo) >> 6) & 0x3f);
                                    if ((_fmchip[0xc0 + chan] & 1) != 0)
                                        c.volmod = (byte)((((c.volmod & 0x3f) * comlo) >> 6) & 0x3f);
                                    break;

                                case 0xfe:
                                    _tempo = (byte)(comword & 0x3f);
                                    break;

                                case 0xfd:
                                    c.nextvol = comlo;
                                    break;

                                case 0xfc:
                                    Playing = false;
                                    // in the real player there's also a full keyoff here, but we don't need it
                                    break;

                                case 0xfb:
                                    c.keycount = 1;
                                    break;

                                case 0xfa:
                                    vbreak = true;
                                    _jumppos = (ushort)((_posplay + 1) & MaxPos);
                                    break;

                                case 0xf9:
                                    vbreak = true;
                                    _jumppos = (ushort)(comlo & MaxPos);
                                    _jumping = 1;
                                    if (_jumppos < _posplay) SongLooped = true;
                                    break;

                                case 0xf8:
                                    c.lasttune = 0;
                                    break;

                                case 0xf7:
                                    c.vibwait = 0;
                                    c.vibspeed = (byte)((comlo >> 4) + 2);
                                    c.vibrate = (byte)((comlo & 15) + 1);
                                    break;

                                case 0xf6:
                                    c.glideto = comlo;
                                    break;

                                case 0xf5:
                                    c.finetune = comlo;
                                    break;

                                case 0xf4:
                                    if (_hardfade == 0)
                                    {
                                        _allvolume = comlo;
                                        _mainvolume = comlo;
                                        _fadeonoff = 0;
                                    }
                                    break;

                                case 0xf3:
                                    if (_hardfade == 0) _fadeonoff = comlo;
                                    break;

                                case 0xf2:
                                    c.trmstay = comlo;
                                    break;

                                case 0xf1:   // panorama
                                case 0xf0:   // progch
                                    // MIDI commands (unhandled, as in the C player)
                                    break;

                                default:
                                    if (comhi < 0xa0) c.glideto = (byte)(comhi & 0x1f);
                                    break;
                            }
                        }
                        else
                        {
                            byte sound;
                            ushort high;
                            // The original shifted the transpose byte left then arithmetically
                            // right to sign-extend bit 6; we duplicate bit 6 into bit 7 instead.
                            int transp = transpose & 127;
                            if ((transpose & 64) != 0) transp |= 128;
                            transp = (sbyte)transp;

                            if ((transpose & 128) != 0)
                            {
                                sound = (byte)((comlo + transp) & MaxSound);
                                high = (ushort)(comhi << 4);
                            }
                            else
                            {
                                sound = (byte)(comlo & MaxSound);
                                high = (ushort)((comhi + transp) << 4);
                            }

                            if (song.ChanDelay[chan] == 0)
                            {
                                PlaySound(sound, chan, high);
                            }
                            else
                            {
                                c.cheatDelay = song.ChanDelay[chan];
                                c.cheatSound = sound;
                                c.cheatHigh = high;
                            }
                        }
                    }

                    c.packpos++;
                }
                else
                {
                    c.packwait--;
                }
            }

            _tempoNow = _tempo;
            _pattplay++;
            if (vbreak)
            {
                _pattplay = 0;
                for (int i = 0; i < 9; i++) { _channel[i].packpos = 0; _channel[i].packwait = 0; }
                _posplay = _jumppos;
            }
            else if (_pattplay >= song.PattLen)
            {
                _pattplay = 0;
                for (int i = 0; i < 9; i++) { _channel[i].packpos = 0; _channel[i].packwait = 0; }
                _posplay = (ushort)((_posplay + 1) & MaxPos);
            }
        }
        else
        {
            _tempoNow--;
        }

        // make effects
        for (int chan = 0; chan < 9; chan++)
        {
            var c = _channel[chan];
            byte regnum = OpTable[chan];
            ushort arpreg, tune, freq, octave;

            if (c.keycount > 0)
            {
                if (c.keycount == 1)
                {
                    SetRegsAdv((byte)(0xb0 + chan), 0xdf, 0);
                    ChannelTune[chan] = -1;
                }
                c.keycount--;
            }

            // arpeggio
            if (c.arp_size == 0)
            {
                arpreg = 0;
            }
            else
            {
                arpreg = (ushort)(c.arp_tab[c.arp_pos] << 4);
                if (arpreg == 0x800)
                {
                    if (c.arp_pos > 0) c.arp_tab[0] = c.arp_tab[c.arp_pos - 1];
                    c.arp_size = 1; c.arp_pos = 0;
                    arpreg = (ushort)(c.arp_tab[0] << 4);
                }

                if (c.arp_count == c.arp_speed)
                {
                    c.arp_pos++;
                    if (c.arp_pos >= c.arp_size) c.arp_pos = 0;
                    c.arp_count = 0;
                }
                else c.arp_count++;
            }

            // glide & portamento
            if (c.lasttune != 0 && c.lasttune != c.gototune)
            {
                if (c.lasttune > c.gototune)
                {
                    if (c.lasttune - c.gototune < c.portspeed) c.lasttune = c.gototune;
                    else c.lasttune = (ushort)(c.lasttune - c.portspeed);
                }
                else
                {
                    if (c.gototune - c.lasttune < c.portspeed) c.lasttune = c.gototune;
                    else c.lasttune = (ushort)(c.lasttune + c.portspeed);
                }

                if (arpreg >= 0x800) arpreg = (ushort)(c.lasttune - (arpreg ^ 0xff0) - 16);
                else arpreg = (ushort)(arpreg + c.lasttune);

                freq = Frequency[arpreg % (12 * 16)];
                octave = (ushort)(arpreg / (12 * 16) - 1);
                SetRegs((byte)(0xa0 + chan), (byte)(freq & 0xff));
                SetRegsAdv((byte)(0xb0 + chan), 0x20, (byte)(((octave << 2) + (freq >> 8)) & 0xdf));
            }
            else
            {
                // vibrato
                if (c.vibwait == 0)
                {
                    if (c.vibrate != 0)
                    {
                        int wibc = VibTab[c.vibcount & 0x3f] * c.vibrate;

                        if ((c.vibcount & 0x40) == 0) tune = (ushort)(c.lasttune + (wibc >> 8));
                        else tune = (ushort)(c.lasttune - (wibc >> 8));

                        if (arpreg >= 0x800) tune = (ushort)(tune - (arpreg ^ 0xff0) - 16);
                        else tune = (ushort)(tune + arpreg);

                        freq = Frequency[tune % (12 * 16)];
                        octave = (ushort)(tune / (12 * 16) - 1);
                        SetRegs((byte)(0xa0 + chan), (byte)(freq & 0xff));
                        SetRegsAdv((byte)(0xb0 + chan), 0x20, (byte)(((octave << 2) + (freq >> 8)) & 0xdf));
                        c.vibcount = unchecked((byte)(c.vibcount + c.vibspeed));
                    }
                    else if (c.arp_size != 0)   // no vibrato, just arpeggio
                    {
                        if (arpreg >= 0x800) tune = (ushort)(c.lasttune - (arpreg ^ 0xff0) - 16);
                        else tune = (ushort)(c.lasttune + arpreg);

                        freq = Frequency[tune % (12 * 16)];
                        octave = (ushort)(tune / (12 * 16) - 1);
                        SetRegs((byte)(0xa0 + chan), (byte)(freq & 0xff));
                        SetRegsAdv((byte)(0xb0 + chan), 0x20, (byte)(((octave << 2) + (freq >> 8)) & 0xdf));
                    }
                }
                else   // no vibrato, just arpeggio
                {
                    c.vibwait--;

                    if (c.arp_size != 0)
                    {
                        if (arpreg >= 0x800) tune = (ushort)(c.lasttune - (arpreg ^ 0xff0) - 16);
                        else tune = (ushort)(c.lasttune + arpreg);

                        freq = Frequency[tune % (12 * 16)];
                        octave = (ushort)(tune / (12 * 16) - 1);
                        SetRegs((byte)(0xa0 + chan), (byte)(freq & 0xff));
                        SetRegsAdv((byte)(0xb0 + chan), 0x20, (byte)(((octave << 2) + (freq >> 8)) & 0xdf));
                    }
                }
            }

            // tremolo (modulator)
            if (c.trmwait == 0)
            {
                if (c.trmrate != 0)
                {
                    int tremc = TremTab[c.trmcount & 0x7f] * c.trmrate;
                    byte level = (tremc >> 8) <= (c.volmod & 0x3f) ? (byte)((c.volmod & 0x3f) - (tremc >> 8)) : (byte)0;

                    if (_allvolume != 0 && (_fmchip[0xc0 + chan] & 1) != 0)
                        SetRegsAdv((byte)(0x40 + regnum), 0xc0, (byte)(((level * _allvolume) >> 8) ^ 0x3f));
                    else
                        SetRegsAdv((byte)(0x40 + regnum), 0xc0, (byte)(level ^ 0x3f));

                    c.trmcount = unchecked((byte)(c.trmcount + c.trmspeed));
                }
                else if (_allvolume != 0 && (_fmchip[0xc0 + chan] & 1) != 0)
                    SetRegsAdv((byte)(0x40 + regnum), 0xc0, (byte)(((((c.volmod & 0x3f) * _allvolume) >> 8) ^ 0x3f) & 0x3f));
                else
                    SetRegsAdv((byte)(0x40 + regnum), 0xc0, (byte)((c.volmod ^ 0x3f) & 0x3f));
            }
            else
            {
                c.trmwait--;
                if (_allvolume != 0 && (_fmchip[0xc0 + chan] & 1) != 0)
                    SetRegsAdv((byte)(0x40 + regnum), 0xc0, (byte)(((((c.volmod & 0x3f) * _allvolume) >> 8) ^ 0x3f) & 0x3f));
            }

            // tremolo (carrier)
            if (c.trcwait == 0)
            {
                if (c.trcrate != 0)
                {
                    int tremc = TremTab[c.trccount & 0x7f] * c.trcrate;
                    byte level = (tremc >> 8) <= (c.volcar & 0x3f) ? (byte)((c.volcar & 0x3f) - (tremc >> 8)) : (byte)0;

                    if (_allvolume != 0)
                        SetRegsAdv((byte)(0x43 + regnum), 0xc0, (byte)(((level * _allvolume) >> 8) ^ 0x3f));
                    else
                        SetRegsAdv((byte)(0x43 + regnum), 0xc0, (byte)(level ^ 0x3f));
                    c.trccount = unchecked((byte)(c.trccount + c.trcspeed));
                }
                else if (_allvolume != 0)
                    SetRegsAdv((byte)(0x43 + regnum), 0xc0, (byte)(((((c.volcar & 0x3f) * _allvolume) >> 8) ^ 0x3f) & 0x3f));
                else
                    SetRegsAdv((byte)(0x43 + regnum), 0xc0, (byte)((c.volcar ^ 0x3f) & 0x3f));
            }
            else
            {
                c.trcwait--;
                if (_allvolume != 0)
                    SetRegsAdv((byte)(0x43 + regnum), 0xc0, (byte)(((((c.volcar & 0x3f) * _allvolume) >> 8) ^ 0x3f) & 0x3f));
            }

            // Viewer-only: hold a muted channel's key-on bit low at the chip without
            // touching the shadow registers, so unmuting picks the song back up cleanly.
            if (ChannelMuted[chan])
                _opl?.Write(0xb0 + chan, (byte)(_fmchip[0xb0 + chan] & 0xdf));
        }

        return !(!Playing || SongLooped);
    }

    /// <summary>Port of <c>lds_playsound</c>: starts instrument <paramref name="instNumber"/> on a channel.</summary>
    private void PlaySound(int instNumber, int channelNumber, int tunehigh)
    {
        var song = _song;
        if (song == null || instNumber < 0 || instNumber >= song.Patches.Length) return;

        var c = _channel[channelNumber];
        var i = song.Patches[instNumber];
        int regnum = OpTable[channelNumber];

        ChannelInstrument[channelNumber] = instNumber;

        // set fine tune
        tunehigh += ((i.Finetune + c.finetune + 0x80) & 0xff) - 0x80;

        // arpeggio handling
        if (i.Arpeggio == 0)
        {
            int arpcalc = i.ArpTab[0] << 4;

            if (arpcalc > 0x800) tunehigh = tunehigh - (arpcalc ^ 0xff0) - 16;
            else tunehigh += arpcalc;
        }

        // glide handling
        if (c.glideto != 0)
        {
            c.gototune = (ushort)tunehigh;
            c.portspeed = c.glideto;
            c.glideto = c.finetune = 0;
            return;
        }

        // set modulator registers
        SetRegs((byte)(0x20 + regnum), i.ModMisc);
        byte volcalc = i.ModVol;
        if (c.nextvol == 0 || (i.Feedback & 1) == 0) c.volmod = volcalc;
        else c.volmod = (byte)((volcalc & 0xc0) | (((volcalc & 0x3f) * c.nextvol) >> 6));

        if ((i.Feedback & 1) == 1 && _allvolume != 0)
            SetRegs((byte)(0x40 + regnum), (byte)(((c.volmod & 0xc0) | (((c.volmod & 0x3f) * _allvolume) >> 8)) ^ 0x3f));
        else
            SetRegs((byte)(0x40 + regnum), (byte)(c.volmod ^ 0x3f));
        SetRegs((byte)(0x60 + regnum), i.ModAd);
        SetRegs((byte)(0x80 + regnum), i.ModSr);
        SetRegs((byte)(0xe0 + regnum), i.ModWave);

        // Set carrier registers
        SetRegs((byte)(0x23 + regnum), i.CarMisc);
        volcalc = i.CarVol;
        if (c.nextvol == 0) c.volcar = volcalc;
        else c.volcar = (byte)((volcalc & 0xc0) | (((volcalc & 0x3f) * c.nextvol) >> 6));

        if (_allvolume != 0)
            SetRegs((byte)(0x43 + regnum), (byte)(((c.volcar & 0xc0) | (((c.volcar & 0x3f) * _allvolume) >> 8)) ^ 0x3f));
        else
            SetRegs((byte)(0x43 + regnum), (byte)(c.volcar ^ 0x3f));
        SetRegs((byte)(0x63 + regnum), i.CarAd);
        SetRegs((byte)(0x83 + regnum), i.CarSr);
        SetRegs((byte)(0xe3 + regnum), i.CarWave);
        SetRegs((byte)(0xc0 + channelNumber), i.Feedback);
        SetRegsAdv((byte)(0xb0 + channelNumber), 0xdf, 0);      // key off

        // The C code indexes with a raw int; a negative tunehigh would read before the
        // table, so wrap into range instead (the songs shipped with the game never do).
        int ti = ((tunehigh % (12 * 16)) + (12 * 16)) % (12 * 16);
        ushort freq = Frequency[ti];
        byte octave = (byte)(tunehigh / (12 * 16) - 1);
        if (i.Glide == 0)
        {
            if (i.Portamento == 0 || c.lasttune == 0)
            {
                SetRegs((byte)(0xa0 + channelNumber), (byte)(freq & 0xff));
                SetRegs((byte)(0xb0 + channelNumber), (byte)((octave << 2) + 0x20 + (freq >> 8)));
                c.lasttune = c.gototune = (ushort)tunehigh;
            }
            else
            {
                c.gototune = (ushort)tunehigh;
                c.portspeed = i.Portamento;
                SetRegsAdv((byte)(0xb0 + channelNumber), 0xdf, 0x20);   // key on
            }
        }
        else
        {
            SetRegs((byte)(0xa0 + channelNumber), (byte)(freq & 0xff));
            SetRegs((byte)(0xb0 + channelNumber), (byte)((octave << 2) + 0x20 + (freq >> 8)));
            c.lasttune = (ushort)tunehigh;
            c.gototune = (ushort)(tunehigh + ((i.Glide + 0x80) & 0xff) - 0x80);   // set destination
            c.portspeed = i.Portamento;
        }

        ChannelTune[channelNumber] = tunehigh;

        if (i.Vibrato == 0)
        {
            c.vibwait = c.vibspeed = c.vibrate = 0;
        }
        else
        {
            c.vibwait = i.Vibdelay;
            c.vibspeed = (byte)((i.Vibrato >> 4) + 2);
            c.vibrate = (byte)((i.Vibrato & 15) + 1);
        }

        if ((c.trmstay & 0xf0) == 0)
        {
            c.trmwait = (byte)((i.Tremwait & 0xf0) >> 3);
            c.trmspeed = (byte)(i.ModTrem >> 4);
            c.trmrate = (byte)(i.ModTrem & 15);
            c.trmcount = 0;
        }

        if ((c.trmstay & 0x0f) == 0)
        {
            c.trcwait = (byte)((i.Tremwait & 15) << 1);
            c.trcspeed = (byte)(i.CarTrem >> 4);
            c.trcrate = (byte)(i.CarTrem & 15);
            c.trccount = 0;
        }

        c.arp_size = (byte)(i.Arpeggio & 15);
        c.arp_speed = (byte)(i.Arpeggio >> 4);
        Array.Copy(i.ArpTab, c.arp_tab, 12);
        c.keycount = i.Keyoff;
        c.nextvol = c.glideto = c.finetune = c.vibcount = c.arp_pos = c.arp_count = 0;
    }
}
