using System.Runtime.InteropServices;

namespace T2LV.Tyrian.Audio;

/// <summary>
/// The OS synthesizer (the Windows MIDI mapper / Microsoft GS Wavetable), driven
/// straight through <c>midiOut*</c>. The widescreen fork does the same thing in
/// win_native_midi.c, and for the same reason: it is the one music device that
/// costs nothing to set up and needs no SoundFont.
/// </summary>
public sealed class NativeMidiOut : IDisposable
{
    private const int MidiMapper = -1;   // MIDI_MAPPER

    [DllImport("winmm.dll")] private static extern int midiOutOpen(out IntPtr h, int deviceId, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] private static extern int midiOutClose(IntPtr h);
    [DllImport("winmm.dll")] private static extern int midiOutReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern int midiOutShortMsg(IntPtr h, uint msg);
    [DllImport("winmm.dll")] private static extern int midiOutGetNumDevs();
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private static int _timerRefs;

    /// <summary>
    /// Raises the system timer resolution to 1 ms. The player thread sleeps between
    /// dispatches, and at the default 15.6 ms tick a 14.3 ms MIDI tick would land
    /// wherever it liked -- the song would swing audibly.
    /// </summary>
    public static void BeginHighResolutionTimer()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Interlocked.Increment(ref _timerRefs) == 1)
            try { timeBeginPeriod(1); } catch { /* best effort */ }
    }

    /// <summary>Drops the 1 ms timer request raised by <see cref="BeginHighResolutionTimer"/>.</summary>
    public static void EndHighResolutionTimer()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Interlocked.Decrement(ref _timerRefs) == 0)
            try { timeEndPeriod(1); } catch { /* best effort */ }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiOutCaps
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public ushort wTechnology, wVoices, wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "midiOutGetDevCapsW")]
    private static extern int midiOutGetDevCaps(IntPtr deviceId, out MidiOutCaps caps, int size);

    private IntPtr _handle;
    private readonly object _gate = new();

    /// <summary>True once the device is open.</summary>
    public bool IsOpen => _handle != IntPtr.Zero;

    /// <summary>Name of the OS device the mapper resolved to, for display.</summary>
    public string DeviceName { get; private set; } = "";

    /// <summary>Why the last <see cref="Open"/> failed, if it did.</summary>
    public string Error { get; private set; } = "";

    /// <summary>Opens the MIDI mapper. Returns false if no synth is available.</summary>
    public bool Open()
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero) return true;
            if (!OperatingSystem.IsWindows()) { Error = "native MIDI is Windows-only"; return false; }
            try
            {
                if (midiOutGetNumDevs() == 0) { Error = "no MIDI output device"; return false; }
                int rc = midiOutOpen(out _handle, MidiMapper, IntPtr.Zero, IntPtr.Zero, 0);
                if (rc != 0) { _handle = IntPtr.Zero; Error = $"midiOutOpen failed ({rc})"; return false; }
                DeviceName = QueryName();
                Error = "";
                return true;
            }
            catch (DllNotFoundException) { Error = "winmm.dll not available"; return false; }
            catch (Exception e) { Error = e.Message; return false; }
        }
    }

    private static string QueryName()
    {
        try
        {
            // The mapper itself has no name; report what device 0 is, which is what it
            // resolves to on a default Windows install.
            if (midiOutGetNumDevs() > 0 &&
                midiOutGetDevCaps(IntPtr.Zero, out var caps, Marshal.SizeOf<MidiOutCaps>()) == 0)
                return caps.szPname;
        }
        catch { /* naming is cosmetic */ }
        return "MIDI mapper";
    }

    /// <summary>Sends one short (1-3 byte) MIDI message.</summary>
    public void Send(int status, int data1, int data2)
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            uint msg = (uint)((status & 0xff) | ((data1 & 0x7f) << 8) | ((data2 & 0x7f) << 16));
            midiOutShortMsg(_handle, msg);
        }
    }

    /// <summary>All-notes-off plus sustain-off on every channel.</summary>
    public void Silence()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            for (int ch = 0; ch < 16; ch++)
            {
                midiOutShortMsg(_handle, (uint)(0xb0 | ch | (0x40 << 8)));    // sustain off
                midiOutShortMsg(_handle, (uint)(0xb0 | ch | (0x7b << 8)));    // all notes off
                midiOutShortMsg(_handle, (uint)(0xb0 | ch | (0x78 << 8)));    // all sound off
            }
        }
    }

    /// <summary>Closes the device.</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            try { midiOutReset(_handle); midiOutClose(_handle); } catch { /* shutting down anyway */ }
            _handle = IntPtr.Zero;
        }
    }

    public void Dispose() => Close();
}
