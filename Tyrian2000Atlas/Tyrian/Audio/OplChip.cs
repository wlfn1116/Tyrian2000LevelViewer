/*
 *  Copyright (C) 2002-2010  The DOSBox Team
 *  OPL2/OPL3 emulation library
 *
 *  This library is free software; you can redistribute it and/or
 *  modify it under the terms of the GNU Lesser General Public
 *  License as published by the Free Software Foundation; either
 *  version 2.1 of the License, or (at your option) any later version.
 *
 *  Originally based on ADLIBEMU.C, an AdLib/OPL2 emulation library by Ken Silverman
 *  Copyright (C) 1998-2001 Ken Silverman
 *
 * Port notes:
 *  - OPLTYPE_IS_OPL3 is NOT defined in the game's build, so only the OPL2 paths
 *    exist here; the OPL3-only branches were dropped, not #if'd out.
 *  - fltype -> double, Bit32s -> int, Bit32u -> uint, Bit16s -> short,
 *    Bit8u -> byte, Bits -> long where the C type is pointer sized and used for
 *    envelope state, int elsewhere (all such values are small and positive).
 *  - Original C identifiers are kept verbatim so this file can be diffed against
 *    opl.c line by line.
 */

namespace T2A.Tyrian.Audio;

/// <summary>
/// 1:1 port of the OPL2 (AdLib/YM3812) emulator the game uses for its music
/// (DOSBox <c>opl.c</c>, originally Ken Silverman's ADLIBEMU). Instance-based so
/// the game mixer and the music-player window can each drive their own chip.
/// </summary>
public sealed class OplChip
{
    // ------------------------------------------------------------------ //
    // constants (opl.c defines)                                          //
    // ------------------------------------------------------------------ //

    private const int NUM_CHANNELS = 9;
    private const int MAXOPERATORS = NUM_CHANNELS * 2;

    private const double FL05 = 0.5;
    private const double FL2 = 2.0;
    private const double PI = 3.1415926535897932384626433832795;

    private const int FIXEDPT = 0x10000;        // fixed-point calculations using 16+16
    private const int FIXEDPT_LFO = 0x1000000;  // fixed-point calculations using 8+24

    private const int WAVEPREC = 1024;          // waveform precision (10 bits)

    private const double INTFREQU = 14318180.0 / 288.0;     // clocking of the chip

    private const uint OF_TYPE_ATT = 0;
    private const uint OF_TYPE_DEC = 1;
    private const uint OF_TYPE_REL = 2;
    private const uint OF_TYPE_SUS = 3;
    private const uint OF_TYPE_SUS_NOKEEP = 4;
    private const uint OF_TYPE_OFF = 5;

    private const int ARC_CONTROL = 0x00;
    private const int ARC_TVS_KSR_MUL = 0x20;
    private const int ARC_KSL_OUTLEV = 0x40;
    private const int ARC_ATTR_DECR = 0x60;
    private const int ARC_SUSL_RELR = 0x80;
    private const int ARC_FREQ_NUM = 0xa0;
    private const int ARC_KON_BNUM = 0xb0;
    private const int ARC_PERC_MODE = 0xbd;
    private const int ARC_FEEDBACK = 0xc0;
    private const int ARC_WAVE_SEL = 0xe0;

    private const int ARC_SECONDSET = 0x100;    // second operator set for OPL3

    private const int ARC_SECONDSET_MASK = 0;   // OPL2 build

    private const uint OP_ACT_OFF = 0x00;
    private const uint OP_ACT_NORMAL = 0x01;    // regular channel activated (bitmasked)
    private const uint OP_ACT_PERC = 0x02;      // percussion channel activated (bitmasked)

    private const int BLOCKBUF_SIZE = 512;

    // vibrato constants
    private const int VIBTAB_SIZE = 8;
    // VIBFAC is "70/50000" in C, a macro without braces so that the surrounding
    // expression keeps doing integer mul/div; it is spelled out at every use site.

    // tremolo constants and table
    private const int TREMTAB_SIZE = 53;
    private const double TREM_FREQ = 3.7;       // tremolo at 3.7hz


    /* operator struct definition
    For OPL2 all 9 channels consist of two operators each, carrier and modulator.
    Channel x has operators x as modulator and operators (9+x) as carrier.
    */
    private sealed class op_type
    {
        public int cval, lastcval;          // current output/last output (used for feedback)
        public uint tcount, wfpos, tinc;    // time (position in waveform) and time increment
        public double amp, step_amp;        // and amplification (envelope)
        public double vol;                  // volume
        public double sustain_level;        // sustain level
        public int mfbi;                    // feedback amount
        public double a0, a1, a2, a3;       // attack rate function coefficients
        public double decaymul, releasemul; // decay/release rate functions
        public uint op_state;               // current state of operator (attack/decay/sustain/release/off)
        public uint toff;
        public int freq_high;               // highest three bits of the frequency, used for vibrato calculations
        public int cur_wform;               // start of selected waveform (C: Bit16s* into wavtable -> index here)
        public uint cur_wmask;              // mask for selected waveform
        public uint act_state;              // activity state (regular, percussion)
        public bool sus_keep;               // keep sustain level when decay finished
        public bool vibrato, tremolo;       // vibrato/tremolo enable bits

        // variables used to provide non-continuous envelopes
        public uint generator_pos;          // for non-standard sample rates we need to determine how many samples have passed
        public long cur_env_step;           // current (standardized) sample position
        public long env_step_a, env_step_d, env_step_r;  // number of std samples of one step (for attack/decay/release mode)
        public byte step_skip_pos_a;        // position of 8-cyclic step skipping (always 2^x to check against mask)
        public long env_step_skip_a;        // bitmask that determines if a step is skipped (respective bit is zero then)
    }

    // per-chip variables
    private readonly op_type[] op = new op_type[MAXOPERATORS];

    private int int_samplerate;

    private byte status;
    private uint opl_index;
    private readonly byte[] adlibreg = new byte[256];    // adlib register set
    private readonly byte[] wave_sel = new byte[22];     // waveform selection

    // vibrato/tremolo increment/counter
    private uint vibtab_pos;
    private uint vibtab_add;
    private uint tremtab_pos;
    private uint tremtab_add;

    private uint generator_add;     // should be a chip parameter

    private double recipsamp;       // inverse of sampling rate

    // wave form table - built once, sample rate independent (C: guarded by the
    // "initfirstime" static local inside adlib_init), hence static here
    private static readonly short[] wavtable = new short[WAVEPREC * 3];

    // vibrato/tremolo tables
    private readonly int[] vib_table = new int[VIBTAB_SIZE];
    private readonly int[] trem_table = new int[TREMTAB_SIZE * 2];

    private readonly int[] vibval_const = new int[BLOCKBUF_SIZE];
    private readonly int[] tremval_const = new int[BLOCKBUF_SIZE];

    // vibrato value tables (used per-operator)
    private readonly int[] vibval_var1 = new int[BLOCKBUF_SIZE];
    private readonly int[] vibval_var2 = new int[BLOCKBUF_SIZE];

    // vibrato value table pointers
    private int[] vibval1, vibval2, vibval3, vibval4;

    // C: locals of adlib_getsample (Bit32s outbufl/vib_lut/trem_lut[BLOCKBUF_SIZE]);
    // kept as per-chip scratch buffers so no allocation happens per call
    private readonly int[] outbufl = new int[BLOCKBUF_SIZE];
    private readonly int[] vib_lut = new int[BLOCKBUF_SIZE];
    private readonly int[] trem_lut = new int[BLOCKBUF_SIZE];

    // key scale level lookup table
    private static readonly double[] kslmul = {
        0.0, 0.5, 0.25, 1.0     // -> 0, 3, 1.5, 6 dB/oct
    };

    // frequency multiplicator lookup table
    private static readonly double[] frqmul_tab = {
        0.5,1,2,3,4,5,6,7,8,9,10,10,12,12,15,15
    };
    // calculated frequency multiplication values (depend on sampling rate)
    private readonly double[] frqmul = new double[16];

    // key scale levels - built once, sample rate independent (see wavtable)
    private static readonly byte[,] kslev = new byte[8, 16];

    // map a channel number to the register offset of the modulator (=register base)
    private static readonly byte[] modulatorbase = {
        0,1,2,
        8,9,10,
        16,17,18
    };

    // map a register base to a modulator operator number or operator number
    private static readonly byte[] regbase2modop = {
        0,1,2,0,1,2,0,0,3,4,5,3,4,5,0,0,6,7,8,6,7,8
    };
    private static readonly byte[] regbase2op = {
        0,1,2,9,10,11,0,0,3,4,5,12,13,14,0,0,6,7,8,15,16,17
    };

    // start of the waveform
    private static readonly uint[] waveform = {
        WAVEPREC,
        WAVEPREC>>1,
        WAVEPREC,
        (WAVEPREC*3)>>2,
        0,
        0,
        (WAVEPREC*5)>>2,
        WAVEPREC<<1
    };

    // length of the waveform as mask
    private static readonly uint[] wavemask = {
        WAVEPREC-1,
        WAVEPREC-1,
        (WAVEPREC>>1)-1,
        (WAVEPREC>>1)-1,
        WAVEPREC-1,
        ((WAVEPREC*3)>>2)-1,
        WAVEPREC>>1,
        WAVEPREC-1
    };

    // where the first entry resides
    private static readonly uint[] wavestart = {
        0,
        WAVEPREC>>1,
        0,
        WAVEPREC>>2,
        0,
        0,
        0,
        WAVEPREC>>3
    };

    // envelope generator function constants
    private static readonly double[] attackconst = {
        1/2.82624,
        1/2.25280,
        1/1.88416,
        1/1.59744
    };
    private static readonly double[] decrelconst = {
        1/39.28064,
        1/31.41608,
        1/26.17344,
        1/22.44608
    };

    private static readonly byte[] step_skip_mask = { 0xff, 0xfe, 0xee, 0xba, 0xaa };


    // ------------------------------------------------------------------ //
    // rand()                                                             //
    // ------------------------------------------------------------------ //

    // The C code calls the CRT rand() from the noise generator of the percussion
    // mode. Replaced with a per-instance LCG (same shape as the classic ANSI C
    // one) so that each chip is self-contained and deterministic; the sequence
    // differs from any particular CRT but only feeds the hihat/snare noise bit.
    // Not reset by Init(), matching C where adlib_init() does not call srand().
    private uint _randState = 1;

    private int rand()
    {
        unchecked
        {
            _randState = _randState * 1103515245 + 12345;
            return (int)((_randState >> 16) & 0x7fff);
        }
    }


    // ------------------------------------------------------------------ //
    // public API                                                         //
    // ------------------------------------------------------------------ //

    /// <summary>Creates a chip and resets it for the given output sample rate.</summary>
    public OplChip(int sampleRate)
    {
        vibval1 = vibval_const;
        vibval2 = vibval_const;
        vibval3 = vibval_const;
        vibval4 = vibval_const;
        Init(sampleRate);
    }

    /// <summary>Sample rate the chip was last initialised for.</summary>
    public int SampleRate => int_samplerate;

    /// <summary>Full chip reset for the given output sample rate (C: adlib_init).</summary>
    public void Init(int sampleRate) => adlib_init((uint)sampleRate);

    /// <summary>Writes an OPL register (C: adlib_write).</summary>
    public void Write(int idx, byte val) => adlib_write(idx, val);

    /// <summary>
    /// Renders <c>dst.Length</c> mono samples, overwriting <paramref name="dst"/>
    /// (C: adlib_getsample).
    /// </summary>
    public void GetSample(Span<short> dst) => adlib_getsample(dst, dst.Length);

    /// <summary>Reads the status port (C: adlib_reg_read).</summary>
    public byte ReadRegister(int port) => adlib_reg_read(port);

    /// <summary>Latches the register index (C: adlib_write_index).</summary>
    public void WriteIndex(int port, byte val) => adlib_write_index(port, val);


    // ------------------------------------------------------------------ //
    // static tables that are built exactly once in the C code             //
    // ------------------------------------------------------------------ //

    static OplChip()
    {
        unchecked
        {
            int i, j, oct;

            // create waveform tables
            for (i = 0; i < (WAVEPREC >> 1); i++)
            {
                wavtable[(i << 1) + WAVEPREC] = (short)(16384 * Math.Sin((double)((i << 1)) * PI * 2 / WAVEPREC));
                wavtable[(i << 1) + 1 + WAVEPREC] = (short)(16384 * Math.Sin((double)((i << 1) + 1) * PI * 2 / WAVEPREC));
                wavtable[i] = wavtable[(i << 1) + WAVEPREC];
            }
            for (i = 0; i < (WAVEPREC >> 3); i++)
            {
                wavtable[i + (WAVEPREC << 1)] = (short)(wavtable[i + (WAVEPREC >> 3)] - 16384);
                wavtable[i + ((WAVEPREC * 17) >> 3)] = (short)(wavtable[i + (WAVEPREC >> 2)] + 16384);
            }

            // key scale level table verified ([table in book]*8/3)
            kslev[7, 0] = 0; kslev[7, 1] = 24; kslev[7, 2] = 32; kslev[7, 3] = 37;
            kslev[7, 4] = 40; kslev[7, 5] = 43; kslev[7, 6] = 45; kslev[7, 7] = 47;
            kslev[7, 8] = 48;
            for (i = 9; i < 16; i++) kslev[7, i] = (byte)(i + 41);
            for (j = 6; j >= 0; j--)
            {
                for (i = 0; i < 16; i++)
                {
                    oct = (int)kslev[j + 1, i] - 8;
                    if (oct < 0) oct = 0;
                    kslev[j, i] = (byte)oct;
                }
            }
        }
    }


    // ------------------------------------------------------------------ //
    // operator advance/output                                            //
    // ------------------------------------------------------------------ //

    private void operator_advance(op_type op_pt, int vib)
    {
        unchecked
        {
            op_pt.wfpos = op_pt.tcount;                     // waveform position

            // advance waveform time
            op_pt.tcount += op_pt.tinc;
            op_pt.tcount += (uint)((int)(op_pt.tinc) * vib / FIXEDPT);

            op_pt.generator_pos += generator_add;
        }
    }

    private void operator_advance_drums(op_type op_pt1, int vib1, op_type op_pt2, int vib2, op_type op_pt3, int vib3)
    {
        unchecked
        {
            uint c1 = op_pt1.tcount / (uint)FIXEDPT;
            uint c3 = op_pt3.tcount / (uint)FIXEDPT;
            uint phasebit = ((((c1 & 0x88) ^ ((c1 << 5) & 0x80)) | ((c3 ^ (c3 << 2)) & 0x20)) != 0) ? 0x02u : 0x00u;

            uint noisebit = (uint)(rand() & 1);

            uint snare_phase_bit = (((op_pt1.tcount / (uint)FIXEDPT) / 0x100u) & 1);

            //Hihat
            uint inttm = (phasebit << 8) | (uint)(0x34 << (int)(phasebit ^ (noisebit << 1)));
            op_pt1.wfpos = inttm * (uint)FIXEDPT;           // waveform position
            // advance waveform time
            op_pt1.tcount += op_pt1.tinc;
            op_pt1.tcount += (uint)((int)(op_pt1.tinc) * vib1 / FIXEDPT);
            op_pt1.generator_pos += generator_add;

            //Snare
            inttm = ((1 + snare_phase_bit) ^ noisebit) << 8;
            op_pt2.wfpos = inttm * (uint)FIXEDPT;           // waveform position
            // advance waveform time
            op_pt2.tcount += op_pt2.tinc;
            op_pt2.tcount += (uint)((int)(op_pt2.tinc) * vib2 / FIXEDPT);
            op_pt2.generator_pos += generator_add;

            //Cymbal
            inttm = (1 + phasebit) << 8;
            op_pt3.wfpos = inttm * (uint)FIXEDPT;           // waveform position
            // advance waveform time
            op_pt3.tcount += op_pt3.tinc;
            op_pt3.tcount += (uint)((int)(op_pt3.tinc) * vib3 / FIXEDPT);
            op_pt3.generator_pos += generator_add;
        }
    }


    // output level is sustained, mode changes only when operator is turned off (->release)
    // or when the keep-sustained bit is turned off (->sustain_nokeep)
    private void operator_output(op_type op_pt, int modulator, int trem)
    {
        unchecked
        {
            if (op_pt.op_state != OF_TYPE_OFF)
            {
                op_pt.lastcval = op_pt.cval;
                uint i = (op_pt.wfpos + (uint)modulator) / (uint)FIXEDPT;

                // wform: -16384 to 16383 (0x4000)
                // trem :  32768 to 65535 (0x10000)
                // step_amp: 0.0 to 1.0
                // vol  : 1/2^14 to 1/2^29 (/0x4000; /1../0x8000)

                op_pt.cval = (int)(op_pt.step_amp * op_pt.vol * wavtable[op_pt.cur_wform + (int)(i & op_pt.cur_wmask)] * trem / 16.0);
            }
        }
    }


    // no action, operator is off
    private void operator_off(op_type op_pt)
    {
    }

    // output level is sustained, mode changes only when operator is turned off (->release)
    // or when the keep-sustained bit is turned off (->sustain_nokeep)
    private void operator_sustain(op_type op_pt)
    {
        unchecked
        {
            uint num_steps_add = op_pt.generator_pos / (uint)FIXEDPT;    // number of (standardized) samples
            for (uint ct = 0; ct < num_steps_add; ct++)
            {
                op_pt.cur_env_step++;
            }
            op_pt.generator_pos -= num_steps_add * (uint)FIXEDPT;
        }
    }

    // operator in release mode, if output level reaches zero the operator is turned off
    private void operator_release(op_type op_pt)
    {
        unchecked
        {
            // ??? boundary?
            if (op_pt.amp > 0.00000001)
            {
                // release phase
                op_pt.amp *= op_pt.releasemul;
            }

            uint num_steps_add = op_pt.generator_pos / (uint)FIXEDPT;    // number of (standardized) samples
            for (uint ct = 0; ct < num_steps_add; ct++)
            {
                op_pt.cur_env_step++;                       // sample counter
                if ((op_pt.cur_env_step & op_pt.env_step_r) == 0)
                {
                    if (op_pt.amp <= 0.00000001)
                    {
                        // release phase finished, turn off this operator
                        op_pt.amp = 0.0;
                        if (op_pt.op_state == OF_TYPE_REL)
                        {
                            op_pt.op_state = OF_TYPE_OFF;
                        }
                    }
                    op_pt.step_amp = op_pt.amp;
                }
            }
            op_pt.generator_pos -= num_steps_add * (uint)FIXEDPT;
        }
    }

    // operator in decay mode, if sustain level is reached the output level is either
    // kept (sustain level keep enabled) or the operator is switched into release mode
    private void operator_decay(op_type op_pt)
    {
        unchecked
        {
            if (op_pt.amp > op_pt.sustain_level)
            {
                // decay phase
                op_pt.amp *= op_pt.decaymul;
            }

            uint num_steps_add = op_pt.generator_pos / (uint)FIXEDPT;    // number of (standardized) samples
            for (uint ct = 0; ct < num_steps_add; ct++)
            {
                op_pt.cur_env_step++;
                if ((op_pt.cur_env_step & op_pt.env_step_d) == 0)
                {
                    if (op_pt.amp <= op_pt.sustain_level)
                    {
                        // decay phase finished, sustain level reached
                        if (op_pt.sus_keep)
                        {
                            // keep sustain level (until turned off)
                            op_pt.op_state = OF_TYPE_SUS;
                            op_pt.amp = op_pt.sustain_level;
                        }
                        else
                        {
                            // next: release phase
                            op_pt.op_state = OF_TYPE_SUS_NOKEEP;
                        }
                    }
                    op_pt.step_amp = op_pt.amp;
                }
            }
            op_pt.generator_pos -= num_steps_add * (uint)FIXEDPT;
        }
    }

    // operator in attack mode, if full output level is reached,
    // the operator is switched into decay mode
    private void operator_attack(op_type op_pt)
    {
        unchecked
        {
            op_pt.amp = ((op_pt.a3 * op_pt.amp + op_pt.a2) * op_pt.amp + op_pt.a1) * op_pt.amp + op_pt.a0;

            uint num_steps_add = op_pt.generator_pos / (uint)FIXEDPT;    // number of (standardized) samples
            for (uint ct = 0; ct < num_steps_add; ct++)
            {
                op_pt.cur_env_step++;   // next sample
                if ((op_pt.cur_env_step & op_pt.env_step_a) == 0)        // check if next step already reached
                {
                    if (op_pt.amp > 1.0)
                    {
                        // attack phase finished, next: decay
                        op_pt.op_state = OF_TYPE_DEC;
                        op_pt.amp = 1.0;
                        op_pt.step_amp = 1.0;
                    }
                    op_pt.step_skip_pos_a <<= 1;
                    if (op_pt.step_skip_pos_a == 0) op_pt.step_skip_pos_a = 1;
                    if ((op_pt.step_skip_pos_a & op_pt.env_step_skip_a) != 0)    // check if required to skip next step
                    {
                        op_pt.step_amp = op_pt.amp;
                    }
                }
            }
            op_pt.generator_pos -= num_steps_add * (uint)FIXEDPT;
        }
    }


    // C: const optype_fptr opfuncs[6] = { operator_attack, operator_decay,
    //    operator_release, operator_sustain, operator_release, operator_off };
    // dispatched as opfuncs[op_pt->op_state](op_pt)
    private void opfuncs(op_type op_pt)
    {
        switch (op_pt.op_state)
        {
            case OF_TYPE_ATT: operator_attack(op_pt); break;
            case OF_TYPE_DEC: operator_decay(op_pt); break;
            case OF_TYPE_REL: operator_release(op_pt); break;
            case OF_TYPE_SUS: operator_sustain(op_pt); break;           // sustain phase (keeping level)
            case OF_TYPE_SUS_NOKEEP: operator_release(op_pt); break;    // sustain_nokeep phase (release-style)
            case OF_TYPE_OFF: operator_off(op_pt); break;
            default: break;
        }
    }


    // ------------------------------------------------------------------ //
    // parameter changes                                                  //
    // ------------------------------------------------------------------ //

    private void change_attackrate(int regbase, op_type op_pt)
    {
        int attackrate = adlibreg[ARC_ATTR_DECR + regbase] >> 4;
        if (attackrate != 0)
        {
            double f = Math.Pow(FL2, (double)attackrate + (op_pt.toff >> 2) - 1) * attackconst[op_pt.toff & 3] * recipsamp;
            // attack rate coefficients
            op_pt.a0 = 0.0377 * f;
            op_pt.a1 = 10.73 * f + 1;
            op_pt.a2 = -17.57 * f;
            op_pt.a3 = 7.42 * f;

            int step_skip = attackrate * 4 + (int)op_pt.toff;
            int steps = step_skip >> 2;
            op_pt.env_step_a = (1 << (steps <= 12 ? 12 - steps : 0)) - 1;

            int step_num = (step_skip <= 48) ? (4 - (step_skip & 3)) : 0;
            op_pt.env_step_skip_a = step_skip_mask[step_num];

            if (step_skip >= 62)
            {
                op_pt.a0 = 2.0;     // something that triggers an immediate transition to amp:=1.0
                op_pt.a1 = 0.0;
                op_pt.a2 = 0.0;
                op_pt.a3 = 0.0;
            }
        }
        else
        {
            // attack disabled
            op_pt.a0 = 0.0;
            op_pt.a1 = 1.0;
            op_pt.a2 = 0.0;
            op_pt.a3 = 0.0;
            op_pt.env_step_a = 0;
            op_pt.env_step_skip_a = 0;
        }
    }

    private void change_decayrate(int regbase, op_type op_pt)
    {
        int decayrate = adlibreg[ARC_ATTR_DECR + regbase] & 15;
        // decaymul should be 1.0 when decayrate==0
        if (decayrate != 0)
        {
            double f = -7.4493 * decrelconst[op_pt.toff & 3] * recipsamp;
            op_pt.decaymul = Math.Pow(FL2, f * Math.Pow(FL2, (double)(decayrate + (op_pt.toff >> 2))));
            int steps = (decayrate * 4 + (int)op_pt.toff) >> 2;
            op_pt.env_step_d = (1 << (steps <= 12 ? 12 - steps : 0)) - 1;
        }
        else
        {
            op_pt.decaymul = 1.0;
            op_pt.env_step_d = 0;
        }
    }

    private void change_releaserate(int regbase, op_type op_pt)
    {
        int releaserate = adlibreg[ARC_SUSL_RELR + regbase] & 15;
        // releasemul should be 1.0 when releaserate==0
        if (releaserate != 0)
        {
            double f = -7.4493 * decrelconst[op_pt.toff & 3] * recipsamp;
            op_pt.releasemul = Math.Pow(FL2, f * Math.Pow(FL2, (double)(releaserate + (op_pt.toff >> 2))));
            int steps = (releaserate * 4 + (int)op_pt.toff) >> 2;
            op_pt.env_step_r = (1 << (steps <= 12 ? 12 - steps : 0)) - 1;
        }
        else
        {
            op_pt.releasemul = 1.0;
            op_pt.env_step_r = 0;
        }
    }

    private void change_sustainlevel(int regbase, op_type op_pt)
    {
        int sustainlevel = adlibreg[ARC_SUSL_RELR + regbase] >> 4;
        // sustainlevel should be 0.0 when sustainlevel==15 (max)
        if (sustainlevel < 15)
        {
            op_pt.sustain_level = Math.Pow(FL2, (double)sustainlevel * (-FL05));
        }
        else
        {
            op_pt.sustain_level = 0.0;
        }
    }

    private void change_waveform(int regbase, op_type op_pt)
    {
        // waveform selection
        op_pt.cur_wmask = wavemask[wave_sel[regbase]];
        op_pt.cur_wform = (int)waveform[wave_sel[regbase]];
        // (might need to be adapted to waveform type here...)
    }

    private void change_keepsustain(int regbase, op_type op_pt)
    {
        op_pt.sus_keep = (adlibreg[ARC_TVS_KSR_MUL + regbase] & 0x20) > 0;
        if (op_pt.op_state == OF_TYPE_SUS)
        {
            if (!op_pt.sus_keep) op_pt.op_state = OF_TYPE_SUS_NOKEEP;
        }
        else if (op_pt.op_state == OF_TYPE_SUS_NOKEEP)
        {
            if (op_pt.sus_keep) op_pt.op_state = OF_TYPE_SUS;
        }
    }

    // enable/disable vibrato/tremolo LFO effects
    private void change_vibrato(int regbase, op_type op_pt)
    {
        op_pt.vibrato = (adlibreg[ARC_TVS_KSR_MUL + regbase] & 0x40) != 0;
        op_pt.tremolo = (adlibreg[ARC_TVS_KSR_MUL + regbase] & 0x80) != 0;
    }

    // change amount of self-feedback
    private void change_feedback(int chanbase, op_type op_pt)
    {
        int feedback = adlibreg[ARC_FEEDBACK + chanbase] & 14;
        if (feedback != 0) op_pt.mfbi = (int)Math.Pow(FL2, (double)((feedback >> 1) + 8));
        else op_pt.mfbi = 0;
    }

    private void change_frequency(int chanbase, int regbase, op_type op_pt)
    {
        unchecked
        {
            // frequency
            uint frn = ((((uint)adlibreg[ARC_KON_BNUM + chanbase]) & 3) << 8) + (uint)adlibreg[ARC_FREQ_NUM + chanbase];
            // block number/octave
            uint oct = ((((uint)adlibreg[ARC_KON_BNUM + chanbase]) >> 2) & 7);
            op_pt.freq_high = (int)((frn >> 7) & 7);

            // keysplit
            uint note_sel = (uint)((adlibreg[8] >> 6) & 1);
            op_pt.toff = ((frn >> 9) & (note_sel ^ 1)) | ((frn >> 8) & note_sel);
            op_pt.toff += (oct << 1);

            // envelope scaling (KSR)
            if ((adlibreg[ARC_TVS_KSR_MUL + regbase] & 0x10) == 0) op_pt.toff >>= 2;

            // 20+a0+b0:
            op_pt.tinc = (uint)((((double)(frn << (int)oct)) * frqmul[adlibreg[ARC_TVS_KSR_MUL + regbase] & 15]));
            // 40+a0+b0:
            double vol_in = (double)(adlibreg[ARC_KSL_OUTLEV + regbase] & 63) +
                            kslmul[adlibreg[ARC_KSL_OUTLEV + regbase] >> 6] * kslev[(int)oct, (int)(frn >> 6)];
            op_pt.vol = Math.Pow(FL2, vol_in * -0.125 - 14);

            // operator frequency changed, care about features that depend on it
            change_attackrate(regbase, op_pt);
            change_decayrate(regbase, op_pt);
            change_releaserate(regbase, op_pt);
        }
    }

    private void enable_operator(int regbase, op_type op_pt, uint act_type)
    {
        unchecked
        {
            // check if this is really an off-on transition
            if (op_pt.act_state == OP_ACT_OFF)
            {
                int wselbase = regbase;
                if (wselbase >= ARC_SECONDSET) wselbase -= (ARC_SECONDSET - 22);  // second set starts at 22

                op_pt.tcount = wavestart[wave_sel[wselbase]] * (uint)FIXEDPT;

                // start with attack mode
                op_pt.op_state = OF_TYPE_ATT;
                op_pt.act_state |= act_type;
            }
        }
    }

    private void disable_operator(op_type op_pt, uint act_type)
    {
        // check if this is really an on-off transition
        if (op_pt.act_state != OP_ACT_OFF)
        {
            op_pt.act_state &= (~act_type);
            if (op_pt.act_state == OP_ACT_OFF)
            {
                if (op_pt.op_state != OF_TYPE_OFF) op_pt.op_state = OF_TYPE_REL;
            }
        }
    }


    // ------------------------------------------------------------------ //
    // init                                                               //
    // ------------------------------------------------------------------ //

    private void adlib_init(uint samplerate)
    {
        unchecked
        {
            int i;

            int_samplerate = (int)samplerate;

            generator_add = (uint)(INTFREQU * FIXEDPT / int_samplerate);


            Array.Clear(adlibreg, 0, adlibreg.Length);
            for (i = 0; i < MAXOPERATORS; i++) op[i] = new op_type();   // memset(op,0,...)
            Array.Clear(wave_sel, 0, wave_sel.Length);

            for (i = 0; i < MAXOPERATORS; i++)
            {
                op[i].op_state = OF_TYPE_OFF;
                op[i].act_state = OP_ACT_OFF;
                op[i].amp = 0.0;
                op[i].step_amp = 0.0;
                op[i].vol = 0.0;
                op[i].tcount = 0;
                op[i].tinc = 0;
                op[i].toff = 0;
                op[i].cur_wmask = wavemask[0];
                op[i].cur_wform = (int)waveform[0];
                op[i].freq_high = 0;

                op[i].generator_pos = 0;
                op[i].cur_env_step = 0;
                op[i].env_step_a = 0;
                op[i].env_step_d = 0;
                op[i].env_step_r = 0;
                op[i].step_skip_pos_a = 0;
                op[i].env_step_skip_a = 0;
            }

            recipsamp = 1.0 / (double)int_samplerate;
            for (i = 15; i >= 0; i--)
            {
                frqmul[i] = frqmul_tab[i] * INTFREQU / (double)WAVEPREC * (double)FIXEDPT * recipsamp;
            }

            status = 0;
            opl_index = 0;


            // create vibrato table
            vib_table[0] = 8;
            vib_table[1] = 4;
            vib_table[2] = 0;
            vib_table[3] = -4;
            for (i = 4; i < VIBTAB_SIZE; i++) vib_table[i] = vib_table[i - 4] * -1;

            // vibrato at ~6.1 ?? (opl3 docs say 6.1, opl4 docs say 6.0, y8950 docs say 6.4)
            vibtab_add = (uint)(VIBTAB_SIZE * FIXEDPT_LFO / 8192 * INTFREQU / int_samplerate);
            vibtab_pos = 0;

            for (i = 0; i < BLOCKBUF_SIZE; i++) vibval_const[i] = 0;


            // create tremolo table
            int[] trem_table_int = new int[TREMTAB_SIZE];
            for (i = 0; i < 14; i++) trem_table_int[i] = i - 13;        // upwards (13 to 26 -> -0.5/6 to 0)
            for (i = 14; i < 41; i++) trem_table_int[i] = -i + 14;      // downwards (26 to 0 -> 0 to -1/6)
            for (i = 41; i < 53; i++) trem_table_int[i] = i - 40 - 26;  // upwards (1 to 12 -> -1/6 to -0.5/6)

            for (i = 0; i < TREMTAB_SIZE; i++)
            {
                // 0.0 .. -26/26*4.8/6 == [0.0 .. -0.8], 4/53 steps == [1 .. 0.57]
                double trem_val1 = ((double)trem_table_int[i]) * 4.8 / 26.0 / 6.0;              // 4.8db
                double trem_val2 = (double)((int)(trem_table_int[i] / 4)) * 1.2 / 6.0 / 6.0;    // 1.2db (larger stepping)

                trem_table[i] = (int)(Math.Pow(FL2, trem_val1) * FIXEDPT);
                trem_table[TREMTAB_SIZE + i] = (int)(Math.Pow(FL2, trem_val2) * FIXEDPT);
            }

            // tremolo at 3.7hz
            tremtab_add = (uint)((double)TREMTAB_SIZE * TREM_FREQ * FIXEDPT_LFO / (double)int_samplerate);
            tremtab_pos = 0;

            for (i = 0; i < BLOCKBUF_SIZE; i++) tremval_const[i] = FIXEDPT;

            // (C's "initfirstime" one-shot block - waveform tables and the key
            //  scale level table - lives in the static constructor)
        }
    }


    // ------------------------------------------------------------------ //
    // register write                                                     //
    // ------------------------------------------------------------------ //

    private void adlib_write(int idx, byte val)
    {
        unchecked
        {
            uint second_set = (uint)(idx & ARC_SECONDSET_MASK);
            adlibreg[idx] = val;

            switch (idx & 0xf0)
            {
                case ARC_CONTROL:
                    // here we check for the second set registers, too:
                    switch (idx)
                    {
                        case 0x02:  // timer1 counter
                        case 0x03:  // timer2 counter
                            break;
                        case 0x04:
                            // IRQ reset, timer mask/start
                            if ((val & 0x80) != 0)
                            {
                                // clear IRQ bits in status register
                                status = (byte)(status & ~0x60);
                            }
                            else
                            {
                                status = 0;
                            }
                            break;
                        case 0x08:
                            // CSW, note select
                            break;
                        default:
                            break;
                    }
                    break;
                case ARC_TVS_KSR_MUL:
                case ARC_TVS_KSR_MUL + 0x10:
                    {
                        // tremolo/vibrato/sustain keeping enabled; key scale rate; frequency multiplication
                        int num = idx & 7;
                        int @base = (idx - ARC_TVS_KSR_MUL) & 0xff;
                        if ((num < 6) && (@base < 22))
                        {
                            int modop = regbase2modop[second_set != 0 ? (@base + 22) : @base];
                            int regbase = @base + (int)second_set;
                            int chanbase = second_set != 0 ? (modop - 18 + ARC_SECONDSET) : modop;

                            // change tremolo/vibrato and sustain keeping of this operator
                            op_type op_ptr = op[modop + ((num < 3) ? 0 : 9)];
                            change_keepsustain(regbase, op_ptr);
                            change_vibrato(regbase, op_ptr);

                            // change frequency calculations of this operator as
                            // key scale rate and frequency multiplicator can be changed
                            change_frequency(chanbase, @base, op_ptr);
                        }
                    }
                    break;
                case ARC_KSL_OUTLEV:
                case ARC_KSL_OUTLEV + 0x10:
                    {
                        // key scale level; output rate
                        int num = idx & 7;
                        int @base = (idx - ARC_KSL_OUTLEV) & 0xff;
                        if ((num < 6) && (@base < 22))
                        {
                            int modop = regbase2modop[second_set != 0 ? (@base + 22) : @base];
                            int chanbase = second_set != 0 ? (modop - 18 + ARC_SECONDSET) : modop;

                            // change frequency calculations of this operator as
                            // key scale level and output rate can be changed
                            op_type op_ptr = op[modop + ((num < 3) ? 0 : 9)];
                            change_frequency(chanbase, @base, op_ptr);
                        }
                    }
                    break;
                case ARC_ATTR_DECR:
                case ARC_ATTR_DECR + 0x10:
                    {
                        // attack/decay rates
                        int num = idx & 7;
                        int @base = (idx - ARC_ATTR_DECR) & 0xff;
                        if ((num < 6) && (@base < 22))
                        {
                            int regbase = @base + (int)second_set;

                            // change attack rate and decay rate of this operator
                            op_type op_ptr = op[regbase2op[second_set != 0 ? (@base + 22) : @base]];
                            change_attackrate(regbase, op_ptr);
                            change_decayrate(regbase, op_ptr);
                        }
                    }
                    break;
                case ARC_SUSL_RELR:
                case ARC_SUSL_RELR + 0x10:
                    {
                        // sustain level; release rate
                        int num = idx & 7;
                        int @base = (idx - ARC_SUSL_RELR) & 0xff;
                        if ((num < 6) && (@base < 22))
                        {
                            int regbase = @base + (int)second_set;

                            // change sustain level and release rate of this operator
                            op_type op_ptr = op[regbase2op[second_set != 0 ? (@base + 22) : @base]];
                            change_releaserate(regbase, op_ptr);
                            change_sustainlevel(regbase, op_ptr);
                        }
                    }
                    break;
                case ARC_FREQ_NUM:
                    {
                        // 0xa0-0xa8 low8 frequency
                        int @base = (idx - ARC_FREQ_NUM) & 0xff;
                        if (@base < 9)
                        {
                            int opbase = second_set != 0 ? (@base + 18) : @base;
                            // regbase of modulator:
                            int modbase = modulatorbase[@base] + (int)second_set;

                            int chanbase = @base + (int)second_set;

                            change_frequency(chanbase, modbase, op[opbase]);
                            change_frequency(chanbase, modbase + 3, op[opbase + 9]);
                        }
                    }
                    break;
                case ARC_KON_BNUM:
                    {
                        if (idx == ARC_PERC_MODE)
                        {
                            if ((val & 0x30) == 0x30)       // BassDrum active
                            {
                                enable_operator(16, op[6], OP_ACT_PERC);
                                change_frequency(6, 16, op[6]);
                                enable_operator(16 + 3, op[6 + 9], OP_ACT_PERC);
                                change_frequency(6, 16 + 3, op[6 + 9]);
                            }
                            else
                            {
                                disable_operator(op[6], OP_ACT_PERC);
                                disable_operator(op[6 + 9], OP_ACT_PERC);
                            }
                            if ((val & 0x28) == 0x28)       // Snare active
                            {
                                enable_operator(17 + 3, op[16], OP_ACT_PERC);
                                change_frequency(7, 17 + 3, op[16]);
                            }
                            else
                            {
                                disable_operator(op[16], OP_ACT_PERC);
                            }
                            if ((val & 0x24) == 0x24)       // TomTom active
                            {
                                enable_operator(18, op[8], OP_ACT_PERC);
                                change_frequency(8, 18, op[8]);
                            }
                            else
                            {
                                disable_operator(op[8], OP_ACT_PERC);
                            }
                            if ((val & 0x22) == 0x22)       // Cymbal active
                            {
                                enable_operator(18 + 3, op[8 + 9], OP_ACT_PERC);
                                change_frequency(8, 18 + 3, op[8 + 9]);
                            }
                            else
                            {
                                disable_operator(op[8 + 9], OP_ACT_PERC);
                            }
                            if ((val & 0x21) == 0x21)       // Hihat active
                            {
                                enable_operator(17, op[7], OP_ACT_PERC);
                                change_frequency(7, 17, op[7]);
                            }
                            else
                            {
                                disable_operator(op[7], OP_ACT_PERC);
                            }

                            break;
                        }
                        // regular 0xb0-0xb8
                        int @base = (idx - ARC_KON_BNUM) & 0xff;
                        if (@base < 9)
                        {
                            int opbase = second_set != 0 ? (@base + 18) : @base;
                            // regbase of modulator:
                            int modbase = modulatorbase[@base] + (int)second_set;

                            if ((val & 32) != 0)
                            {
                                // operator switched on
                                enable_operator(modbase, op[opbase], OP_ACT_NORMAL);         // modulator (if 2op)
                                enable_operator(modbase + 3, op[opbase + 9], OP_ACT_NORMAL); // carrier (if 2op)
                            }
                            else
                            {
                                // operator switched off
                                disable_operator(op[opbase], OP_ACT_NORMAL);
                                disable_operator(op[opbase + 9], OP_ACT_NORMAL);
                            }

                            int chanbase = @base + (int)second_set;

                            // change frequency calculations of modulator and carrier (2op) as
                            // the frequency of the channel has changed
                            change_frequency(chanbase, modbase, op[opbase]);
                            change_frequency(chanbase, modbase + 3, op[opbase + 9]);
                        }
                    }
                    break;
                case ARC_FEEDBACK:
                    {
                        // 0xc0-0xc8 feedback/modulation type (AM/FM)
                        int @base = (idx - ARC_FEEDBACK) & 0xff;
                        if (@base < 9)
                        {
                            int opbase = second_set != 0 ? (@base + 18) : @base;
                            int chanbase = @base + (int)second_set;
                            change_feedback(chanbase, op[opbase]);
                        }
                    }
                    break;
                case ARC_WAVE_SEL:
                case ARC_WAVE_SEL + 0x10:
                    {
                        int num = idx & 7;
                        int @base = (idx - ARC_WAVE_SEL) & 0xff;
                        if ((num < 6) && (@base < 22))
                        {
                            if ((adlibreg[0x01] & 0x20) != 0)
                            {
                                // wave selection enabled, change waveform
                                wave_sel[@base] = (byte)(val & 3);
                                op_type op_ptr = op[regbase2modop[@base] + ((num < 3) ? 0 : 9)];
                                change_waveform(@base, op_ptr);
                            }
                        }
                    }
                    break;
                default:
                    break;
            }
        }
    }


    private byte adlib_reg_read(int port)
    {
        // opl2-detection routines require ret&6 to be 6
        if ((port & 1) == 0)
        {
            return (byte)(status | 6);
        }
        return 0xff;
    }

    private void adlib_write_index(int port, byte val)
    {
        opl_index = val;
    }

    private static void clipit16(int ival, Span<short> outval, int idx)
    {
        if (ival < 32768)
        {
            if (ival > -32769)
            {
                outval[idx] = (short)ival;
            }
            else
            {
                outval[idx] = -32768;
            }
        }
        else
        {
            outval[idx] = 32767;
        }
    }


    // ------------------------------------------------------------------ //
    // sample generation                                                  //
    // ------------------------------------------------------------------ //

    private void adlib_getsample(Span<short> sndptr_buf, int numsamples)
    {
        unchecked
        {
            int i, endsamples;
            int cptr;               // C: op_type* cptr -> base index into op[]
            int sndptr = 0;         // C: the sndptr++ output cursor

            int samples_to_process = numsamples;

            for (int cursmp = 0; cursmp < samples_to_process; cursmp += endsamples)
            {
                endsamples = samples_to_process - cursmp;
                if (endsamples > BLOCKBUF_SIZE) endsamples = BLOCKBUF_SIZE;

                Array.Clear(outbufl, 0, endsamples);

                // tremolo value table pointers
                int[] tremval1 = tremval_const, tremval2 = tremval_const, tremval3 = tremval_const, tremval4 = tremval_const;

                // calculate vibrato/tremolo lookup tables
                int vib_tshift = ((adlibreg[ARC_PERC_MODE] & 0x40) == 0) ? 1 : 0;   // 14cents/7cents switching
                for (i = 0; i < endsamples; i++)
                {
                    // cycle through vibrato table
                    vibtab_pos += vibtab_add;
                    if (vibtab_pos / (uint)FIXEDPT_LFO >= (uint)VIBTAB_SIZE) vibtab_pos -= (uint)(VIBTAB_SIZE * FIXEDPT_LFO);
                    vib_lut[i] = vib_table[(int)(vibtab_pos / (uint)FIXEDPT_LFO)] >> vib_tshift;    // 14cents (14/100 of a semitone) or 7cents

                    // cycle through tremolo table
                    tremtab_pos += tremtab_add;
                    if (tremtab_pos / (uint)FIXEDPT_LFO >= (uint)TREMTAB_SIZE) tremtab_pos -= (uint)(TREMTAB_SIZE * FIXEDPT_LFO);
                    if ((adlibreg[ARC_PERC_MODE] & 0x80) != 0) trem_lut[i] = trem_table[(int)(tremtab_pos / (uint)FIXEDPT_LFO)];
                    else trem_lut[i] = trem_table[TREMTAB_SIZE + (int)(tremtab_pos / (uint)FIXEDPT_LFO)];
                }

                if ((adlibreg[ARC_PERC_MODE] & 0x20) != 0)
                {
                    //BassDrum
                    cptr = 6;
                    if ((adlibreg[ARC_FEEDBACK + 6] & 1) != 0)
                    {
                        // additive synthesis
                        if (op[cptr + 9].op_state != OF_TYPE_OFF)
                        {
                            if (op[cptr + 9].vibrato)
                            {
                                vibval1 = vibval_var1;
                                for (i = 0; i < endsamples; i++)
                                    vibval1[i] = (int)((vib_lut[i] * op[cptr + 9].freq_high / 8) * FIXEDPT * 70 / 50000);
                            }
                            else vibval1 = vibval_const;
                            if (op[cptr + 9].tremolo) tremval1 = trem_lut;  // tremolo enabled, use table
                            else tremval1 = tremval_const;

                            // calculate channel output
                            for (i = 0; i < endsamples; i++)
                            {
                                operator_advance(op[cptr + 9], vibval1[i]);
                                opfuncs(op[cptr + 9]);
                                operator_output(op[cptr + 9], 0, tremval1[i]);

                                int chanval = op[cptr + 9].cval * 2;
                                outbufl[i] += chanval;
                            }
                        }
                    }
                    else
                    {
                        // frequency modulation
                        if ((op[cptr + 9].op_state != OF_TYPE_OFF) || (op[cptr + 0].op_state != OF_TYPE_OFF))
                        {
                            if ((op[cptr + 0].vibrato) && (op[cptr + 0].op_state != OF_TYPE_OFF))
                            {
                                vibval1 = vibval_var1;
                                for (i = 0; i < endsamples; i++)
                                    vibval1[i] = (int)((vib_lut[i] * op[cptr + 0].freq_high / 8) * FIXEDPT * 70 / 50000);
                            }
                            else vibval1 = vibval_const;
                            if ((op[cptr + 9].vibrato) && (op[cptr + 9].op_state != OF_TYPE_OFF))
                            {
                                vibval2 = vibval_var2;
                                for (i = 0; i < endsamples; i++)
                                    vibval2[i] = (int)((vib_lut[i] * op[cptr + 9].freq_high / 8) * FIXEDPT * 70 / 50000);
                            }
                            else vibval2 = vibval_const;
                            if (op[cptr + 0].tremolo) tremval1 = trem_lut;  // tremolo enabled, use table
                            else tremval1 = tremval_const;
                            if (op[cptr + 9].tremolo) tremval2 = trem_lut;  // tremolo enabled, use table
                            else tremval2 = tremval_const;

                            // calculate channel output
                            for (i = 0; i < endsamples; i++)
                            {
                                operator_advance(op[cptr + 0], vibval1[i]);
                                opfuncs(op[cptr + 0]);
                                operator_output(op[cptr + 0], (op[cptr + 0].lastcval + op[cptr + 0].cval) * op[cptr + 0].mfbi / 2, tremval1[i]);

                                operator_advance(op[cptr + 9], vibval2[i]);
                                opfuncs(op[cptr + 9]);
                                operator_output(op[cptr + 9], op[cptr + 0].cval * FIXEDPT, tremval2[i]);

                                int chanval = op[cptr + 9].cval * 2;
                                outbufl[i] += chanval;
                            }
                        }
                    }

                    //TomTom (j=8)
                    if (op[8].op_state != OF_TYPE_OFF)
                    {
                        cptr = 8;
                        if (op[cptr + 0].vibrato)
                        {
                            vibval3 = vibval_var1;
                            for (i = 0; i < endsamples; i++)
                                vibval3[i] = (int)((vib_lut[i] * op[cptr + 0].freq_high / 8) * FIXEDPT * 70 / 50000);
                        }
                        else vibval3 = vibval_const;

                        if (op[cptr + 0].tremolo) tremval3 = trem_lut;  // tremolo enabled, use table
                        else tremval3 = tremval_const;

                        // calculate channel output
                        for (i = 0; i < endsamples; i++)
                        {
                            operator_advance(op[cptr + 0], vibval3[i]);
                            opfuncs(op[cptr + 0]);      //TomTom
                            operator_output(op[cptr + 0], 0, tremval3[i]);
                            int chanval = op[cptr + 0].cval * 2;
                            outbufl[i] += chanval;
                        }
                    }

                    //Snare/Hihat (j=7), Cymbal (j=8)
                    if ((op[7].op_state != OF_TYPE_OFF) || (op[16].op_state != OF_TYPE_OFF) ||
                        (op[17].op_state != OF_TYPE_OFF))
                    {
                        cptr = 7;
                        if ((op[cptr + 0].vibrato) && (op[cptr + 0].op_state != OF_TYPE_OFF))
                        {
                            vibval1 = vibval_var1;
                            for (i = 0; i < endsamples; i++)
                                vibval1[i] = (int)((vib_lut[i] * op[cptr + 0].freq_high / 8) * FIXEDPT * 70 / 50000);
                        }
                        else vibval1 = vibval_const;
                        if ((op[cptr + 9].vibrato) && (op[cptr + 9].op_state == OF_TYPE_OFF))
                        {
                            vibval2 = vibval_var2;
                            for (i = 0; i < endsamples; i++)
                                vibval2[i] = (int)((vib_lut[i] * op[cptr + 9].freq_high / 8) * FIXEDPT * 70 / 50000);
                        }
                        else vibval2 = vibval_const;

                        if (op[cptr + 0].tremolo) tremval1 = trem_lut;  // tremolo enabled, use table
                        else tremval1 = tremval_const;
                        if (op[cptr + 9].tremolo) tremval2 = trem_lut;  // tremolo enabled, use table
                        else tremval2 = tremval_const;

                        cptr = 8;
                        if ((op[cptr + 9].vibrato) && (op[cptr + 9].op_state == OF_TYPE_OFF))
                        {
                            vibval4 = vibval_var2;
                            for (i = 0; i < endsamples; i++)
                                vibval4[i] = (int)((vib_lut[i] * op[cptr + 9].freq_high / 8) * FIXEDPT * 70 / 50000);
                        }
                        else vibval4 = vibval_const;

                        if (op[cptr + 9].tremolo) tremval4 = trem_lut;  // tremolo enabled, use table
                        else tremval4 = tremval_const;

                        // calculate channel output
                        for (i = 0; i < endsamples; i++)
                        {
                            operator_advance_drums(op[7], vibval1[i], op[7 + 9], vibval2[i], op[8 + 9], vibval4[i]);

                            opfuncs(op[7]);             //Hihat
                            operator_output(op[7], 0, tremval1[i]);

                            opfuncs(op[7 + 9]);         //Snare
                            operator_output(op[7 + 9], 0, tremval2[i]);

                            opfuncs(op[8 + 9]);         //Cymbal
                            operator_output(op[8 + 9], 0, tremval4[i]);

                            int chanval = (op[7].cval + op[7 + 9].cval + op[8 + 9].cval) * 2;
                            outbufl[i] += chanval;
                        }
                    }
                }

                int max_channel = NUM_CHANNELS;
                for (int cur_ch = max_channel - 1; cur_ch >= 0; cur_ch--)
                {
                    // skip drum/percussion operators
                    if (((adlibreg[ARC_PERC_MODE] & 0x20) != 0) && (cur_ch >= 6) && (cur_ch < 9)) continue;

                    int k = cur_ch;
                    cptr = cur_ch;

                    // check for FM/AM
                    if ((adlibreg[ARC_FEEDBACK + k] & 1) != 0)
                    {
                        // 2op additive synthesis
                        if ((op[cptr + 9].op_state == OF_TYPE_OFF) && (op[cptr + 0].op_state == OF_TYPE_OFF)) continue;
                        if ((op[cptr + 0].vibrato) && (op[cptr + 0].op_state != OF_TYPE_OFF))
                        {
                            vibval1 = vibval_var1;
                            for (i = 0; i < endsamples; i++)
                                vibval1[i] = (int)((vib_lut[i] * op[cptr + 0].freq_high / 8) * FIXEDPT * 70 / 50000);
                        }
                        else vibval1 = vibval_const;
                        if ((op[cptr + 9].vibrato) && (op[cptr + 9].op_state != OF_TYPE_OFF))
                        {
                            vibval2 = vibval_var2;
                            for (i = 0; i < endsamples; i++)
                                vibval2[i] = (int)((vib_lut[i] * op[cptr + 9].freq_high / 8) * FIXEDPT * 70 / 50000);
                        }
                        else vibval2 = vibval_const;
                        if (op[cptr + 0].tremolo) tremval1 = trem_lut;  // tremolo enabled, use table
                        else tremval1 = tremval_const;
                        if (op[cptr + 9].tremolo) tremval2 = trem_lut;  // tremolo enabled, use table
                        else tremval2 = tremval_const;

                        // calculate channel output
                        for (i = 0; i < endsamples; i++)
                        {
                            // carrier1
                            operator_advance(op[cptr + 0], vibval1[i]);
                            opfuncs(op[cptr + 0]);
                            operator_output(op[cptr + 0], (op[cptr + 0].lastcval + op[cptr + 0].cval) * op[cptr + 0].mfbi / 2, tremval1[i]);

                            // carrier2
                            operator_advance(op[cptr + 9], vibval2[i]);
                            opfuncs(op[cptr + 9]);
                            operator_output(op[cptr + 9], 0, tremval2[i]);

                            int chanval = op[cptr + 9].cval + op[cptr + 0].cval;
                            outbufl[i] += chanval;
                        }
                    }
                    else
                    {
                        // 2op frequency modulation
                        if ((op[cptr + 9].op_state == OF_TYPE_OFF) && (op[cptr + 0].op_state == OF_TYPE_OFF)) continue;
                        if ((op[cptr + 0].vibrato) && (op[cptr + 0].op_state != OF_TYPE_OFF))
                        {
                            vibval1 = vibval_var1;
                            for (i = 0; i < endsamples; i++)
                                vibval1[i] = (int)((vib_lut[i] * op[cptr + 0].freq_high / 8) * FIXEDPT * 70 / 50000);
                        }
                        else vibval1 = vibval_const;
                        if ((op[cptr + 9].vibrato) && (op[cptr + 9].op_state != OF_TYPE_OFF))
                        {
                            vibval2 = vibval_var2;
                            for (i = 0; i < endsamples; i++)
                                vibval2[i] = (int)((vib_lut[i] * op[cptr + 9].freq_high / 8) * FIXEDPT * 70 / 50000);
                        }
                        else vibval2 = vibval_const;
                        if (op[cptr + 0].tremolo) tremval1 = trem_lut;  // tremolo enabled, use table
                        else tremval1 = tremval_const;
                        if (op[cptr + 9].tremolo) tremval2 = trem_lut;  // tremolo enabled, use table
                        else tremval2 = tremval_const;

                        // calculate channel output
                        for (i = 0; i < endsamples; i++)
                        {
                            // modulator
                            operator_advance(op[cptr + 0], vibval1[i]);
                            opfuncs(op[cptr + 0]);
                            operator_output(op[cptr + 0], (op[cptr + 0].lastcval + op[cptr + 0].cval) * op[cptr + 0].mfbi / 2, tremval1[i]);

                            // carrier
                            operator_advance(op[cptr + 9], vibval2[i]);
                            opfuncs(op[cptr + 9]);
                            operator_output(op[cptr + 9], op[cptr + 0].cval * FIXEDPT, tremval2[i]);

                            int chanval = op[cptr + 9].cval;
                            outbufl[i] += chanval;
                        }
                    }
                }

                // convert to 16bit samples
                for (i = 0; i < endsamples; i++)
                    clipit16(outbufl[i], sndptr_buf, sndptr++);
            }
        }
    }
}
