using System;
using System.Collections.Generic;
using System.Linq;

namespace T2A.Tyrian.Audio;

/// <summary>MIDI event kinds emitted by the LDS converter.</summary>
public enum MidiEventType
{
    /// <summary>Note Off (0x80).</summary>
    NoteOff,
    /// <summary>Note On (0x90).</summary>
    NoteOn,
    /// <summary>Polyphonic key pressure / aftertouch (0xA0).</summary>
    KeyPressure,
    /// <summary>Control Change (0xB0).</summary>
    ControlChange,
    /// <summary>Program Change (0xC0).</summary>
    ProgramChange,
    /// <summary>Channel pressure / aftertouch (0xD0).</summary>
    ChannelPressure,
    /// <summary>Pitch Bend Change (0xE0).</summary>
    PitchBendChange,
    /// <summary>Meta or System Exclusive message (0xF0).</summary>
    Extended,
}

/// <summary>One MIDI event at a tick timestamp. <c>Extended</c> carries meta/sysex bytes.</summary>
public readonly struct LdsMidiEvent
{
    /// <summary>Absolute event time, in ticks.</summary>
    public readonly uint Timestamp;

    /// <summary>Kind of MIDI message.</summary>
    public readonly MidiEventType Type;

    /// <summary>MIDI channel, 0..15. Ignored for <see cref="MidiEventType.Extended"/>.</summary>
    public readonly int Channel;

    /// <summary>1 or 2 bytes for channel messages; the full meta/sysex bytes (including
    /// the leading 0xFF or 0xF0) for <see cref="MidiEventType.Extended"/>.</summary>
    public readonly byte[] Data;

    /// <summary>Creates a MIDI event.</summary>
    public LdsMidiEvent(uint timestamp, MidiEventType type, int channel, byte[] data)
    {
        Timestamp = timestamp;
        Type = type;
        Channel = channel;
        Data = data;
    }
}

/// <summary>One converted track: track 0 is the conductor/meta track, the rest are the LDS channels.</summary>
public sealed class LdsMidiTrack
{
    /// <summary>Events in ascending timestamp order; the End Of Track meta is always last.</summary>
    public List<LdsMidiEvent> Events { get; } = new List<LdsMidiEvent>();

    /// <summary>
    /// Which of the nine Loudness channels this track carries, or -1 for the conductor
    /// track. Not something the SMF records: a channel that never plays a note is dropped
    /// from the file, so the track's position is not its channel number, and the MIDI
    /// channel is not either (a percussion patch is re-routed to the GM drum channel).
    /// </summary>
    public int LoudnessChannel { get; internal set; } = -1;

    /// <summary>
    /// 1:1 port of <c>MIDITrack::AddEvent</c>: inserts the event in timestamp order (after
    /// any events with the same timestamp) while keeping a trailing End Of Track meta last,
    /// pushing its timestamp forward as needed.
    /// </summary>
    internal void AddEvent(in LdsMidiEvent newEvent)
    {
        int it = Events.Count;

        if (Events.Count > 0)
        {
            LdsMidiEvent Event = Events[it - 1];

            if ((Event.Type == MidiEventType.Extended) && (Event.Data.Length >= 2) && (Event.Data[0] == MetaDataStatus) && (Event.Data[1] == MetaEndOfTrack))
            {
                --it;

                if (Event.Timestamp < newEvent.Timestamp)
                    Events[it] = new LdsMidiEvent(newEvent.Timestamp, Event.Type, Event.Channel, Event.Data);
            }

            while (it > 0)
            {
                if (Events[it - 1].Timestamp <= newEvent.Timestamp)
                    break;

                --it;
            }
        }

        Events.Insert(it, newEvent);
    }

    /// <summary>Copies the track, the way C++ copies a <c>MIDITrack</c> by value.</summary>
    internal LdsMidiTrack Clone()
    {
        LdsMidiTrack Track = new LdsMidiTrack();

        Track.Events.AddRange(Events);

        return Track;
    }

    private const byte MetaDataStatus = 0xFF;
    private const byte MetaEndOfTrack = 0x2F;
}

/// <summary>A whole converted song.</summary>
public sealed class LdsMidiSong
{
    /// <summary>Converted tracks. Track 0 is the conductor track; the rest are LDS channels.</summary>
    public List<LdsMidiTrack> Tracks { get; }

    /// <summary>Ticks per quarter note (35 for LDS).</summary>
    public int TimeDivision { get; }

    /// <summary>Microseconds per quarter note, from the emitted Set Tempo meta event.</summary>
    public uint TempoUsPerQuarter { get; }

    /// <summary>Timestamp of the last event of the song, in ticks.</summary>
    public uint Duration { get; }

    /// <summary>Tick of the loopStart marker, or 0 if there is none.</summary>
    public uint LoopStart { get; }

    /// <summary>Tick of the loopEnd marker, or <see cref="uint.MaxValue"/> if the song does not loop.</summary>
    public uint LoopEnd { get; }

    /// <summary>True when the song has a usable loop, i.e. <see cref="LoopEnd"/> &lt;= <see cref="Duration"/>.</summary>
    public bool Loops => LoopEnd <= Duration;

    internal LdsMidiSong(List<LdsMidiTrack> tracks, int timeDivision, uint tempoUsPerQuarter, uint duration, uint loopStart, uint loopEnd)
    {
        Tracks = tracks;
        TimeDivision = timeDivision;
        TempoUsPerQuarter = tempoUsPerQuarter;
        Duration = duration;
        LoopStart = loopStart;
        LoopEnd = loopEnd;
    }
}

/// <summary>
/// 1:1 port of midiproc's <c>MIDIProcessorLDS.cpp</c>: converts one LDS (AdLib
/// LOUDNESS) song from <c>music.mus</c> into MIDI events.
/// </summary>
public static class LdsMidi
{
    // The C++ source is compiled with ENABLE_WHEEL defined and ENABLE_VIB / ENABLE_ARP /
    // ENABLE_TREM undefined, so the vibrato, arpeggio and tremolo code (and their sine
    // tables) never exist in the shipped converter and are absent here as well.

    private const int WHEEL_RANGE_HIGH = 12;
    private const int WHEEL_RANGE_LOW = 0;

    private static int WHEEL_SCALE(int x) => x * 512 / WHEEL_RANGE_HIGH;
    private static int WHEEL_SCALE_LOW(int x) => WHEEL_SCALE(x) & 127;
    private static int WHEEL_SCALE_HIGH(int x) => ((WHEEL_SCALE(x) >> 7) + 64) & 127;

    private const byte StatusMetaData = 0xFF;    // StatusCodes::MetaData
    private const byte StatusSysEx = 0xF0;       // StatusCodes::SysEx
    private const byte MetaMarker = 0x06;        // MetaDataTypes::Marker
    private const byte MetaEndOfTrack = 0x2F;    // MetaDataTypes::EndOfTrack
    private const byte MetaSetTempo = 0x51;      // MetaDataTypes::SetTempo

    private static readonly byte[] MIDIEventEndOfTrack = { StatusMetaData, MetaEndOfTrack };
    private static readonly byte[] LoopBeginMarker = { StatusMetaData, MetaMarker, (byte) 'l', (byte) 'o', (byte) 'o', (byte) 'p', (byte) 'S', (byte) 't', (byte) 'a', (byte) 'r', (byte) 't' };
    private static readonly byte[] LoopEndMarker = { StatusMetaData, MetaMarker, (byte) 'l', (byte) 'o', (byte) 'o', (byte) 'p', (byte) 'E', (byte) 'n', (byte) 'd' };
    private static readonly byte[] DefaultTempoLDS = { StatusMetaData, MetaSetTempo, 0x07, 0xA1, 0x20 };

    /// <summary>One LDS instrument. Only the fields the converter reads are kept; the
    /// skipped bytes are exactly the ones the C++ struct comments call "Adlib crap".</summary>
    private sealed class SoundPatch
    {
        // skip 11 bytes worth of Adlib crap
        public byte keyoff;
        public byte portamento;
        public sbyte glide;
        // skip 1 byte
        // skip 4 bytes worth of digital instrument crap
        // skip 3 more bytes worth of Adlib crap that isn't even used
        public byte midi_instrument;
        public byte midi_velocity;
        public byte midi_key;
        public sbyte midi_transpose;
        // skip 2 bytes worth of MIDI dummy fields or whatever
    }

    /// <summary>Per-channel player state (<c>channel_state</c>).</summary>
    private sealed class ChannelState
    {
        public short gototune, lasttune;
        public ushort packpos;
        public sbyte finetune;
        public byte glideto, portspeed;
        public byte nextvol;
        public byte volmod = 0, volcar = 0;
        public byte keycount, packwait;

        public ChanCheat chancheat;

        public struct ChanCheat
        {
            public byte chandelay, sound;
            public ushort high;
        }
    }

    /// <summary>One order-list cell (<c>position_data</c>).</summary>
    private struct PositionData
    {
        public ushort pattern_number;
        public byte transpose;
    }

    /// <summary>Minimal stand-in for <c>MIDIContainer</c>: only what ProcessLDS uses.</summary>
    private sealed class Container
    {
        public int Format;
        public int TimeDivision;
        public readonly List<LdsMidiTrack> Tracks = new List<LdsMidiTrack>();
        public uint EndTimestamp;

        public void Initialize(int format, int division)
        {
            Format = format;
            TimeDivision = division;
            EndTimestamp = 0;
        }

        public void AddTrack(LdsMidiTrack track)
        {
            Tracks.Add(track);

            int EventIndex = track.Events.Count;

            if ((EventIndex > 0) && (track.Events[EventIndex - 1].Timestamp > EndTimestamp))
                EndTimestamp = track.Events[EventIndex - 1].Timestamp;
        }

        public void AddEventToTrack(int trackIndex, in LdsMidiEvent midiEvent)
        {
            Tracks[trackIndex].AddEvent(midiEvent);

            if (midiEvent.Timestamp > EndTimestamp)
                EndTimestamp = midiEvent.Timestamp;
        }
    }

    /// <summary>1:1 port of the file-static <c>PlaySound</c> helper.</summary>
    private static void PlaySound(byte[] currentInstrument, SoundPatch[] patches, byte[] last_note, byte[] last_channel, byte[] last_instrument, byte[] last_volume, byte[] last_sent_volume,
        short[] last_pitch_wheel,
        ChannelState c, byte allvolume, uint Timestamp, uint sound, int chan, uint high, LdsMidiTrack track)
    {
        unchecked
        {
            byte[] buffer = new byte[2];

            currentInstrument[chan] = (byte) sound;

            if (sound >= (uint) patches.Length)
                return;

            SoundPatch patch = patches[currentInstrument[chan]];

            int channel = (patch.midi_instrument >= 0x80) ? 9 : (chan == 9) ? 10 : chan;
            uint saved_last_note = last_note[chan];
            uint note;

            if (channel != 9)
            {
                // set fine tune
                high += (uint) (int) c.finetune;

                // and MIDI transpose
                high = (uint) ((int) high + (patch.midi_transpose << 4));

                note = high - (uint) (int) c.lasttune;

                // glide handling
                if (c.glideto != 0)
                {
                    c.gototune  = (short) (note - (uint) (last_note[chan] << 4) + (uint) (int) c.lasttune);
                    c.portspeed = c.glideto;
                    c.glideto   = 0;
                    c.finetune  = 0;
                    return;
                }

                if (patch.midi_instrument != last_instrument[chan])
                {
                    buffer[0] = patch.midi_instrument;
                    track.AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.ProgramChange, channel, new byte[] { buffer[0] }));
                    last_instrument[chan] = patch.midi_instrument;
                }
            }
            else
            {
                note = (uint) ((patch.midi_instrument & 0x7F) << 4);
            }

            uint volume = 127;

            if (c.nextvol != 0)
            {
                volume = (uint) ((c.nextvol & 0x3F) * 127 / 63);
                last_volume[chan] = (byte) volume;
            }

            if (allvolume != 0)
                volume = volume * allvolume / 255;

            if (volume != last_sent_volume[channel])
            {
                buffer[0] = 7;
                buffer[1] = (byte) volume;
                track.AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.ControlChange, last_channel[chan], new byte[] { buffer[0], buffer[1] }));
                last_sent_volume[channel] = (byte) volume;
            }

            if (saved_last_note != 0xFF)
            {
                buffer[0] = (byte) saved_last_note;
                buffer[1] = 127;

                track.AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.NoteOff, last_channel[chan], new byte[] { buffer[0], buffer[1] }));

                last_note[chan] = 0xFF;

                if (channel != 9)
                {
                    note += (uint) (int) c.lasttune;
                    c.lasttune = 0;

                    if (last_pitch_wheel[channel] != 0)
                    {
                        buffer[0] = 0;
                        buffer[1] = 64;

                        track.AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.PitchBendChange, last_channel[chan], new byte[] { buffer[0], buffer[1] }));

                        last_pitch_wheel[channel] = 0;
                    }
                }
            }

            if (c.lasttune != last_pitch_wheel[channel])
            {
                buffer[0] = (byte) WHEEL_SCALE_LOW(c.lasttune);
                buffer[1] = (byte) WHEEL_SCALE_HIGH(c.lasttune);

                track.AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.PitchBendChange, channel, new byte[] { buffer[0], buffer[1] }));

                last_pitch_wheel[channel] = c.lasttune;
            }

            if ((patch.glide == 0) || (last_note[chan] == 0xFF))
            {
                if ((patch.portamento == 0) || (last_note[chan] == 0xFF))
                {
                    buffer[0] = (byte) (note >> 4);
                    buffer[1] = patch.midi_velocity;

                    track.AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.NoteOn, channel, new byte[] { buffer[0], buffer[1] }));

                    last_note[chan] = (byte) (note >> 4);
                    last_channel[chan] = (byte) channel;
                    c.gototune = c.lasttune;
                }
                else
                {
                    c.gototune = (short) (note - (uint) (last_note[chan] << 4) + (uint) (int) c.lasttune);
                    c.portspeed = patch.portamento;

                    buffer[0] = last_note[chan] = (byte) saved_last_note;
                    buffer[1] = patch.midi_velocity;

                    track.AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.NoteOn, channel, new byte[] { buffer[0], buffer[1] }));
                }
            }
            else
            {
                buffer[0] = (byte) (note >> 4);
                buffer[1] = patch.midi_velocity;

                track.AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.NoteOn, channel, new byte[] { buffer[0], buffer[1] }));

                last_note[chan] = (byte) (note >> 4);
                last_channel[chan] = (byte) channel;

                c.gototune = patch.glide;
                c.portspeed = patch.portamento;
            }

            c.glideto  = 0;
            c.keycount = patch.keyoff;
            c.nextvol  = 0;
            c.finetune = 0;
        }
    }

    /// <summary>Converts one raw LDS song. Returns null if the data is not valid LDS.</summary>
    public static LdsMidiSong? Convert(ReadOnlySpan<byte> lds)
    {
        unchecked
        {
        //  uint16_t speed;
        //  uint8_t register_bd;

            ushort PatchCount;

            int it  = 0;
            int end = lds.Length;

            if (end == it)
                return null;

            byte mode = lds[it++];

            if (mode > 2)
                return null; /*throw exception_io_data( "Invalid LDS mode" );*/

        //  speed = it[ 0 ] | ( it[ 1 ] << 8 );

            if (end - it < 4)
                return null;

            byte Tempo = lds[it + 2];
            byte pattern_length = lds[it + 3];
            it += 4;

            if (end - it < 9)
                return null;

            byte[] ChannelDelay = new byte[9];

            for (int i = 0; i < 9; ++i)
                ChannelDelay[i] = lds[it++];

        //  register_bd = *it++;
            it++;

            if (end - it < 2)
                return null;

            PatchCount = (ushort) (lds[it + 0] | (lds[it + 1] << 8));

            if (PatchCount == 0)
                return null;

            it += 2;

            if (end - it < 46 * PatchCount)
                return null;

            SoundPatch[] Patches = new SoundPatch[PatchCount];

            for (int i = 0; i < PatchCount; ++i)
            {
                SoundPatch patch = new SoundPatch();

                Patches[i] = patch;

                it += 11;
                patch.keyoff = lds[it++];
                patch.portamento = lds[it++];
                patch.glide = (sbyte) lds[it++];
                it++;
                it += 2;    // vibrato, vibrato_delay
                it += 3;    // modulator_tremolo, carrier_tremolo, tremolo_delay
                it += 20;   // arpeggio, arpeggio_table[12], 7 unused
                patch.midi_instrument = lds[it++];
                patch.midi_velocity = lds[it++];
                patch.midi_key = lds[it++];
                patch.midi_transpose = (sbyte) lds[it++];
                it += 2;

                // hax
                if (patch.midi_instrument >= 0x80)
                    patch.glide = 0;
            }

            if (end - it < 2)
                return null;

            ushort PositionCount = (ushort) (lds[it + 0] | (lds[it + 1] << 8));

            if (PositionCount == 0)
                return null;

            it += 2;

            PositionData[] Positions = new PositionData[9 * PositionCount];

            if (end - it < 3 * PositionCount)
                return null;

            for (int i = 0; i < PositionCount; ++i)
            {
                for (int j = 0; j < 9; ++j)
                {
                    ushort pattern_number = (ushort) (lds[it + 0] | (lds[it + 1] << 8));

                    if ((pattern_number & 1) != 0)
                        return null; /*throw exception_io_data( "Odd LDS pattern number" );*/

                    pattern_number >>= 1;

                    Positions[i * 9 + j].pattern_number = pattern_number;
                    Positions[i * 9 + j].transpose = lds[it + 2];
                    it += 3;
                }
            }

            if (end - it < 2)
                return null;

            it += 2;

            int PatternCount = (end - it) / 2;

            ushort[] Patterns = new ushort[PatternCount];

            for (int i = 0; i < PatternCount; ++i)
            {
                Patterns[i] = (ushort) (lds[it + 0] | ((ushort) lds[it + 1] << 8));
                it += 2;
            }

            byte /*jumping,*/ fadeonoff, allvolume, hardfade, tempo_now, pattplay;
            ushort posplay, jumppos;
            uint mainvolume;

            ChannelState[] Channel = new ChannelState[9];

            for (int i = 0; i < 9; ++i)
                Channel[i] = new ChannelState();

            uint[] PositionTimestamps = new uint[PositionCount];

            Array.Fill(PositionTimestamps, ~0u);

            byte[] current_instrument = new byte[9];

            byte[] last_channel = new byte[9];
            byte[] last_instrument = new byte[9];
            byte[] last_note = new byte[9];
            byte[] last_volume = new byte[9];
            byte[] last_sent_volume = new byte[11];
            short[] last_pitch_wheel = new short[11];
            byte[] ticks_without_notes = new byte[11];

            Array.Fill(last_channel, (byte) 0);
            Array.Fill(last_instrument, (byte) 0xFF);
            Array.Fill(last_note, (byte) 0xFF);
            Array.Fill(last_volume, (byte) 127);
            Array.Fill(last_sent_volume, (byte) 127);
            Array.Fill(last_pitch_wheel, (short) 0);
            Array.Fill(ticks_without_notes, (byte) 0);

            uint Timestamp = 0;

            byte[] buffer = new byte[2];

            Container container = new Container();

            container.Initialize(1, 35);

            {
                LdsMidiTrack Track = new LdsMidiTrack();

                Track.AddEvent(new LdsMidiEvent(0, MidiEventType.Extended, 0, (byte[]) DefaultTempoLDS.Clone()));

                for (int i = 0; i < 11; ++i)
                {
                    buffer[0] = 120;
                    buffer[1] = 0;

                    Track.AddEvent(new LdsMidiEvent(0, MidiEventType.ControlChange, i, new byte[] { buffer[0], buffer[1] }));

                    buffer[0] = 121;

                    Track.AddEvent(new LdsMidiEvent(0, MidiEventType.ControlChange, i, new byte[] { buffer[0], buffer[1] }));

                    buffer[0] = 0x65;

                    Track.AddEvent(new LdsMidiEvent(0, MidiEventType.ControlChange, i, new byte[] { buffer[0], buffer[1] }));

                    buffer[0] = 0x64;

                    Track.AddEvent(new LdsMidiEvent(0, MidiEventType.ControlChange, i, new byte[] { buffer[0], buffer[1] }));

                    buffer[0] = 0x06;
                    buffer[1] = WHEEL_RANGE_HIGH;

                    Track.AddEvent(new LdsMidiEvent(0, MidiEventType.ControlChange, i, new byte[] { buffer[0], buffer[1] }));

                    buffer[0] = 0x26;
                    buffer[1] = WHEEL_RANGE_LOW;

                    Track.AddEvent(new LdsMidiEvent(0, MidiEventType.ControlChange, i, new byte[] { buffer[0], buffer[1] }));

                    buffer[0] = 0;
                    buffer[1] = 64;

                    Track.AddEvent(new LdsMidiEvent(0, MidiEventType.PitchBendChange, i, new byte[] { buffer[0], buffer[1] }));
                }

                Track.AddEvent(new LdsMidiEvent(0, MidiEventType.Extended, 0, (byte[]) MIDIEventEndOfTrack.Clone()));

                container.AddTrack(Track);
            }

            List<LdsMidiTrack> Tracks = new List<LdsMidiTrack>();

            {
                LdsMidiTrack Track = new LdsMidiTrack();

                Track.AddEvent(new LdsMidiEvent(0, MidiEventType.Extended, 0, (byte[]) MIDIEventEndOfTrack.Clone()));

                for (int i = 0; i < 10; ++i)
                    Tracks.Add(Track.Clone());
            }

            tempo_now = 3;
            /*jumping = 0;*/
            fadeonoff = 0;
            allvolume = 0;
            hardfade = 0;
            pattplay = 0;
            posplay = 0;
            jumppos = 0;
            mainvolume = 0;

            const ushort maxsound = 0x3F;
            const ushort maxpos = 0xFF;

            bool playing = true;

            while (playing)
            {
                int chan;
                bool vbreak;
                int i;
                ChannelState c;

                if (fadeonoff != 0)
                {
                    if (fadeonoff <= 128)
                    {
                        if (allvolume > fadeonoff || allvolume == 0)
                        {
                            allvolume -= fadeonoff;
                        }
                        else
                        {
                            allvolume = 1;
                            fadeonoff = 0;

                            if (hardfade != 0)
                            {
                                playing = false;
                                hardfade = 0;

                                for (i = 0; i < 9; i++)
                                    Channel[i].keycount = 1;
                            }
                        }
                    }
                    else
                    if ((uint) ((allvolume + (0x100 - fadeonoff)) & 0xff) <= mainvolume)
                    {
                        allvolume += (byte) (0x100 - fadeonoff);
                    }
                    else
                    {
                        allvolume = (byte) mainvolume;
                        fadeonoff = 0;
                    }
                }

                // handle channel delay
                for (chan = 0; chan < 9; ++chan)
                {
                    ChannelState _c = Channel[chan];

                    if (_c.chancheat.chandelay != 0)
                    {
                        if (--_c.chancheat.chandelay == 0)
                        {
                            PlaySound(current_instrument, Patches, last_note, last_channel, last_instrument, last_volume, last_sent_volume,
                                last_pitch_wheel,
                                _c, allvolume, Timestamp, _c.chancheat.sound, chan, _c.chancheat.high, Tracks[chan]);
                            ticks_without_notes[last_channel[chan]] = 0;
                        }
                    }
                }

                // handle notes
                if (tempo_now == 0)
                {
                    if (pattplay == 0 && PositionTimestamps[posplay] == ~0u)
                        PositionTimestamps[posplay] = Timestamp;

                    vbreak = false;

                    for (int _chan = 0; _chan < 9; _chan++)
                    {
                        ChannelState _c = Channel[_chan];

                        if (_c.packwait == 0)
                        {
                            ushort patnum = Positions[posplay * 9 + _chan].pattern_number;
                            byte   transpose = Positions[posplay * 9 + _chan].transpose;

                            if ((uint) (patnum + _c.packpos) >= (uint) Patterns.Length)
                                return null; /*throw exception_io_data( "Invalid LDS pattern number" );*/

                            uint comword = Patterns[patnum + _c.packpos];
                            uint comhi = comword >> 8;
                            uint comlo = comword & 0xff;

                            if (comword != 0)
                            {
                                if (comhi == 0x80)
                                {
                                    _c.packwait = (byte) comlo;
                                }
                                else
                                if (comhi >= 0x80)
                                {
                                    switch (comhi)
                                    {
                                        case 0xff:
                                        {
                                            uint volume = (comlo & 0x3F) * 127 / 63;

                                            last_volume[_chan] = (byte) volume;

                                            if (volume != last_sent_volume[last_channel[_chan]])
                                            {
                                                buffer[0] = 7;
                                                buffer[1] = (byte) volume;

                                                Tracks[_chan].AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.ControlChange, last_channel[_chan], new byte[] { buffer[0], buffer[1] }));

                                                last_sent_volume[last_channel[_chan]] = (byte) volume;
                                            }
                                            break;
                                        }

                                        case 0xfe:
                                            Tempo = (byte) (comword & 0x3f);
                                            break;

                                        case 0xfd:
                                            _c.nextvol = (byte) comlo;
                                            break;

                                        case 0xfc:
                                            playing = false;
                                            // in real player there's also full keyoff here, but we don't need it
                                            break;

                                        case 0xfb:
                                            _c.keycount = 1;
                                            break;

                                        case 0xfa:
                                            vbreak = true;
                                            jumppos = (ushort) ((posplay + 1) & maxpos);
                                            break;

                                        case 0xf9:
                                            vbreak = true;
                                            jumppos = (ushort) (comlo & maxpos);
                                            /*jumping = 1;*/
                                            if (jumppos <= posplay)
                                            {
                                                container.AddEventToTrack(0, new LdsMidiEvent(PositionTimestamps[jumppos], MidiEventType.Extended, 0, (byte[]) LoopBeginMarker.Clone()));
                                                container.AddEventToTrack(0, new LdsMidiEvent(Timestamp + Tempo - 1, MidiEventType.Extended, 0, (byte[]) LoopEndMarker.Clone()));
                                                playing = false;
                                            }
                                            break;

                                        case 0xf8:
                                            _c.lasttune = 0;
                                            break;

                                        case 0xf7:
                                            // Set vibrato: the whole body is ENABLE_VIB-only, so nothing
                                            // happens. The case still has to exist to keep 0xF7 out of
                                            // the default branch below.
                                            break;

                                        case 0xf6:
                                            _c.glideto = (byte) comlo;
                                            break;

                                        case 0xf5:
                                            _c.finetune = (sbyte) comlo;
                                            break;

                                        case 0xf4:
                                            if (hardfade == 0)
                                            {
                                                allvolume = (byte) comlo;
                                                mainvolume = comlo;
                                                fadeonoff = 0;
                                            }
                                            break;

                                        case 0xf3:
                                            if (hardfade == 0)
                                                fadeonoff = (byte) comlo;
                                            break;

                                        case 0xf2:
                                            // Set tremolo stay: ENABLE_TREM-only, likewise a no-op that
                                            // must still not fall through to the default branch.
                                            break;

                                        case 0xf1:
                                            buffer[0] = 10;
                                            buffer[1] = (byte) ((comlo & 0x3F) * 127 / 63);

                                            Tracks[_chan].AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.ControlChange, last_channel[_chan], new byte[] { buffer[0], buffer[1] }));
                                            break;

                                        case 0xf0:
                                            buffer[0] = (byte) (comlo & 0x7F);

                                            Tracks[_chan].AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.ProgramChange, last_channel[_chan], new byte[] { buffer[0] }));
                                            break;

                                        default:
                                            if (comhi < 0xa0)
                                                _c.glideto = (byte) (comhi & 0x1f);
                                            break;
                                    }
                                }
                                else
                                {
                                    byte sound;
                                    ushort high;

                                    sbyte transp = (sbyte) (transpose << 1);

                                    transp = (sbyte) (transp >> 1);

                                    if ((transpose & 128) != 0)
                                    {
                                        sound = (byte) ((comlo + (uint) (int) transp) & maxsound);
                                        high  = (ushort) (comhi << 4);
                                    }
                                    else
                                    {
                                        sound = (byte) (comlo & maxsound);
                                        high = (ushort) ((comhi + (uint) (int) transp) << 4);
                                    }

                                    /*
                                    PASCAL:
                                    sound = comlo & maxsound;
                                    high = (comhi + (((transpose + 0x24) & 0xff) - 0x24)) << 4;
                                    */

                                    if (ChannelDelay[_chan] == 0)
                                    {
                                        PlaySound(current_instrument, Patches, last_note, last_channel, last_instrument, last_volume, last_sent_volume,
                                            last_pitch_wheel,
                                            _c, allvolume, Timestamp, sound, _chan, high, Tracks[_chan]);

                                        ticks_without_notes[last_channel[_chan]] = 0;
                                    }
                                    else
                                    {
                                        _c.chancheat.chandelay = ChannelDelay[_chan];
                                        _c.chancheat.sound = sound;
                                        _c.chancheat.high = high;
                                    }
                                }
                            }

                            _c.packpos++;
                        }
                        else
                        {
                            _c.packwait--;
                        }
                    }

                    tempo_now = Tempo;
                    /*
                    The continue table is updated here, but this is only used in the
                    original player, which can be paused in the middle of a song and then
                    unpaused. Since AdPlug does all this for us automatically, we don't
                    have a continue table here. The continue table update code is noted
                    here for reference only.

                    if(!pattplay) {
                    conttab[speed & maxcont].position = posplay & 0xff;
                    conttab[speed & maxcont].tempo = tempo;
                    }
                    */
                    pattplay++;

                    if (vbreak)
                    {
                        pattplay = 0;

                        for (i = 0; i < 9; i++)
                        {
                            Channel[i].packwait = 0;
                            Channel[i].packpos = 0;
                        }

                        posplay = jumppos;

                        if (posplay >= PositionCount)
                            return null; /*throw exception_io_data( "Invalid LDS position jump" );*/
                    }
                    else
                    if (pattplay >= pattern_length)
                    {
                        pattplay = 0;

                        for (i = 0; i < 9; i++)
                        {
                            Channel[i].packwait = 0;
                            Channel[i].packpos = 0;
                        }

                        posplay = (ushort) ((posplay + 1) & maxpos);

                        if (posplay >= PositionCount)
                            playing = false; //throw exception_io_data( "LDS reached the end without a loop or end command" );
                    }
                }
                else
                {
                    tempo_now--;
                }

                // make effects
                for (chan = 0; chan < 9; ++chan)
                {
                    c = Channel[chan];

                    if (c.keycount > 0)
                    {
                        if (c.keycount == 1 && last_note[chan] != 0xFF)
                        {
                            buffer[0] = last_note[chan];
                            buffer[1] = 127;

                            Tracks[chan].AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.NoteOff, last_channel[chan], new byte[] { buffer[0], buffer[1] }));

                            last_note[chan] = 0xFF;

                            if (0 != last_pitch_wheel[last_channel[chan]])
                            {
                                buffer[0] = 0;
                                buffer[1] = 64;

                                Tracks[chan].AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.PitchBendChange, last_channel[chan], new byte[] { buffer[0], buffer[1] }));

                                last_pitch_wheel[last_channel[chan]] = 0;

                                c.lasttune = 0;
                                c.gototune = 0;
                            }
                        }

                        c.keycount--;
                    }

                    // glide & portamento
                    if (c.lasttune != c.gototune)
                    {
                        if (c.lasttune > c.gototune)
                        {
                            if (c.lasttune - c.gototune < c.portspeed)
                            {
                                c.lasttune = c.gototune;
                            }
                            else
                            {
                                c.lasttune -= c.portspeed;
                            }
                        }
                        else
                        {
                            if (c.gototune - c.lasttune < c.portspeed)
                            {
                                c.lasttune = c.gototune;
                            }
                            else
                            {
                                c.lasttune += c.portspeed;
                            }
                        }

                        short arpreg = c.lasttune;

                        if (arpreg != last_pitch_wheel[last_channel[chan]])
                        {
                            buffer[0] = (byte) WHEEL_SCALE_LOW(arpreg);
                            buffer[1] = (byte) WHEEL_SCALE_HIGH(arpreg);

                            Tracks[chan].AddEvent(new LdsMidiEvent(Timestamp, MidiEventType.PitchBendChange, last_channel[chan], new byte[] { buffer[0], buffer[1] }));

                            last_pitch_wheel[last_channel[chan]] = arpreg;
                        }
                    }
                }

                ++Timestamp;
            }

            --Timestamp;

            for (int i = 0; i < 9; ++i)
            {
                LdsMidiTrack Track = Tracks[i];

                int Count = Track.Events.Count;

                if (Count > 1)
                {
                    if (last_note[i] != 0xFF)
                    {
                        buffer[0] = last_note[i];
                        buffer[1] = 127;

                        Track.AddEvent(new LdsMidiEvent(Timestamp + Channel[i].keycount, MidiEventType.NoteOff, last_channel[i], new byte[] { buffer[0], buffer[1] }));

                        if (last_pitch_wheel[last_channel[i]] != 0)
                        {
                            buffer[0] = 0;
                            buffer[1] = 0x40;

                            Track.AddEvent(new LdsMidiEvent(Timestamp + Channel[i].keycount, MidiEventType.PitchBendChange, last_channel[i], new byte[] { buffer[0], buffer[1] }));
                        }
                    }

                    Track.LoudnessChannel = i;
                    container.AddTrack(Track);
                }
            }

            // Loop metadata: the loopStart / loopEnd markers stay in track 0; their
            // timestamps are surfaced on the song so the player does not have to re-scan.
            uint LoopStart = 0;
            uint LoopEnd = uint.MaxValue;

            foreach (LdsMidiTrack Track in container.Tracks)
            {
                foreach (LdsMidiEvent Event in Track.Events)
                {
                    if ((Event.Type == MidiEventType.Extended) && (Event.Data.Length >= 9) && (Event.Data[0] == StatusMetaData) && (Event.Data[1] == MetaMarker))
                    {
                        int Size = Event.Data.Length - 2;

                        if ((Size == 9) && MarkerIs(Event.Data, "loopStart"))
                            LoopStart = Event.Timestamp;
                        else
                        if ((Size == 7) && MarkerIs(Event.Data, "loopEnd"))
                            LoopEnd = Event.Timestamp;
                    }
                }
            }

            uint TempoUsPerQuarter = (uint) ((DefaultTempoLDS[2] << 16) | (DefaultTempoLDS[3] << 8) | DefaultTempoLDS[4]);

            return new LdsMidiSong(container.Tracks, container.TimeDivision, TempoUsPerQuarter, container.EndTimestamp, LoopStart, LoopEnd);
        }
    }

    /// <summary>Case-insensitive comparison of a marker meta event's text payload.</summary>
    private static bool MarkerIs(byte[] data, string text)
    {
        if (data.Length - 2 != text.Length)
            return false;

        for (int i = 0; i < text.Length; ++i)
        {
            if (char.ToLowerInvariant((char) data[i + 2]) != char.ToLowerInvariant(text[i]))
                return false;
        }

        return true;
    }

    /// <summary>Serializes a converted song as a type-1 Standard MIDI File.</summary>
    public static byte[] SerializeSmf(LdsMidiSong song)
    {
        List<byte> midiStream = new List<byte>();

        // MThd
        midiStream.Add(0x4D); midiStream.Add(0x54); midiStream.Add(0x68); midiStream.Add(0x64);

        midiStream.Add(0);
        midiStream.Add(0);
        midiStream.Add(0);
        midiStream.Add(6);

        midiStream.Add(0);
        midiStream.Add(1);                                          // Format 1
        midiStream.Add((byte) (song.Tracks.Count >> 8));
        midiStream.Add((byte) song.Tracks.Count);
        midiStream.Add((byte) (song.TimeDivision >> 8));
        midiStream.Add((byte) song.TimeDivision);

        foreach (LdsMidiTrack Track in song.Tracks)
        {
            // MTrk
            midiStream.Add(0x4D); midiStream.Add(0x54); midiStream.Add(0x72); midiStream.Add(0x6B);

            int ChunkSizeOffset = midiStream.Count;

            midiStream.Add(0);
            midiStream.Add(0);
            midiStream.Add(0);
            midiStream.Add(0);

            uint LastTimestamp = 0;
            bool EndsWithEndOfTrack = false;

            // OrderBy is a stable sort, so events that already share a timestamp keep
            // their emission order (in particular the End Of Track meta stays last).
            foreach (LdsMidiEvent Event in Track.Events.OrderBy(e => e.Timestamp))
            {
                EncodeVariableLengthQuantity(midiStream, Event.Timestamp - LastTimestamp);

                LastTimestamp = Event.Timestamp;
                EndsWithEndOfTrack = false;

                if (Event.Type != MidiEventType.Extended)
                {
                    // Full status byte on every event: running status is valid but not required.
                    midiStream.Add((byte) (((((int) Event.Type) + 8) << 4) + Event.Channel));
                    midiStream.AddRange(Event.Data);
                }
                else
                {
                    int DataSize = Event.Data.Length;

                    if (DataSize >= 1)
                    {
                        if (Event.Data[0] == StatusSysEx)
                        {
                            --DataSize;

                            midiStream.Add(StatusSysEx);
                            EncodeVariableLengthQuantity(midiStream, (uint) DataSize);

                            for (int i = 1; i < Event.Data.Length; ++i)
                                midiStream.Add(Event.Data[i]);
                        }
                        else
                        if (Event.Data[0] == StatusMetaData && (DataSize >= 2))
                        {
                            DataSize -= 2;

                            midiStream.Add(StatusMetaData);
                            midiStream.Add(Event.Data[1]);

                            EncodeVariableLengthQuantity(midiStream, (uint) DataSize);

                            for (int i = 2; i < Event.Data.Length; ++i)
                                midiStream.Add(Event.Data[i]);

                            EndsWithEndOfTrack = (Event.Data[1] == MetaEndOfTrack);
                        }
                        else
                        {
                            for (int i = 1; i < Event.Data.Length; ++i)
                                midiStream.Add(Event.Data[i]);
                        }
                    }
                }
            }

            if (!EndsWithEndOfTrack)
            {
                EncodeVariableLengthQuantity(midiStream, 0);
                midiStream.Add(StatusMetaData);
                midiStream.Add(MetaEndOfTrack);
                midiStream.Add(0);
            }

            int TrackLength = midiStream.Count - ChunkSizeOffset - 4;

            midiStream[ChunkSizeOffset + 0] = (byte) (TrackLength >> 24);
            midiStream[ChunkSizeOffset + 1] = (byte) (TrackLength >> 16);
            midiStream[ChunkSizeOffset + 2] = (byte) (TrackLength >>  8);
            midiStream[ChunkSizeOffset + 3] = (byte)  TrackLength;
        }

        return midiStream.ToArray();
    }

    /// <summary>1:1 port of <c>MIDIContainer::EncodeVariableLengthQuantity</c>.</summary>
    private static void EncodeVariableLengthQuantity(List<byte> data, uint quantity)
    {
        int Shift = 7 * 4;

        while (Shift != 0 && (quantity >> Shift) == 0)
        {
            Shift -= 7;
        }

        while (Shift > 0)
        {
            data.Add((byte) (((quantity >> Shift) & 0x7F) | 0x80));
            Shift -= 7;
        }

        data.Add((byte) (quantity & 0x7F));
    }
}
