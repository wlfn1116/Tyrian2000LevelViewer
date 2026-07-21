using System.Runtime.InteropServices;

namespace T2A.Tyrian.Audio;

/// <summary>
/// A minimal binding to libfluidsynth, used as a SoundFont MIDI voice for the
/// converted songs. Unlike the Engaged fork we do not let FluidSynth own an
/// audio driver: the synth is rendered into our own mixer, so a SoundFont song
/// and the game's sound effects come out of one device and one volume path.
///
/// The DLL is not shipped with the atlas. It is looked for next to the exe, in
/// the Tyrian data folder and the folder above it (an OpenTyrian2000 install has
/// one), and finally on PATH.
/// </summary>
public sealed unsafe class FluidSynth : IDisposable
{
    private const string Lib = "libfluidsynth-3";

    [DllImport(Lib)] private static extern IntPtr new_fluid_settings();
    [DllImport(Lib)] private static extern void delete_fluid_settings(IntPtr settings);
    // Strings go across as explicit UTF-8 bytes, never as marshalled `string`. The default
    // marshalling is ANSI, i.e. the system code page -- and libfluidsynth reads paths as
    // UTF-8. A data folder like "G:\Můj disk\..." then arrives as mojibake and the SoundFont
    // "cannot be loaded" for no visible reason.
    [DllImport(Lib)] private static extern int fluid_settings_setnum(IntPtr settings, byte[] name, double val);
    [DllImport(Lib)] private static extern int fluid_settings_setint(IntPtr settings, byte[] name, int val);
    [DllImport(Lib)] private static extern IntPtr new_fluid_synth(IntPtr settings);
    [DllImport(Lib)] private static extern void delete_fluid_synth(IntPtr synth);
    [DllImport(Lib)] private static extern int fluid_synth_sfload(IntPtr synth, byte[] filename, int resetPresets);

    /// <summary>A null-terminated UTF-8 copy of a string, for the calls above.</summary>
    private static byte[] Utf8(string s)
    {
        int n = System.Text.Encoding.UTF8.GetByteCount(s);
        var b = new byte[n + 1];
        System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }
    [DllImport(Lib)] private static extern int fluid_synth_sfunload(IntPtr synth, int id, int resetPresets);
    [DllImport(Lib)] private static extern int fluid_synth_noteon(IntPtr synth, int chan, int key, int vel);
    [DllImport(Lib)] private static extern int fluid_synth_noteoff(IntPtr synth, int chan, int key);
    [DllImport(Lib)] private static extern int fluid_synth_cc(IntPtr synth, int chan, int ctrl, int val);
    [DllImport(Lib)] private static extern int fluid_synth_program_change(IntPtr synth, int chan, int program);
    [DllImport(Lib)] private static extern int fluid_synth_pitch_bend(IntPtr synth, int chan, int val);
    [DllImport(Lib)] private static extern int fluid_synth_channel_pressure(IntPtr synth, int chan, int val);
    [DllImport(Lib)] private static extern int fluid_synth_key_pressure(IntPtr synth, int chan, int key, int val);
    [DllImport(Lib)] private static extern int fluid_synth_system_reset(IntPtr synth);
    [DllImport(Lib)] private static extern int fluid_synth_all_notes_off(IntPtr synth, int chan);
    [DllImport(Lib)] private static extern int fluid_synth_all_sounds_off(IntPtr synth, int chan);
    [DllImport(Lib)] private static extern void fluid_synth_set_gain(IntPtr synth, float gain);
    [DllImport(Lib)] private static extern int fluid_synth_write_s16(IntPtr synth, int len,
        void* lout, int loff, int lincr, void* rout, int roff, int rincr);
    [DllImport(Lib)] private static extern IntPtr fluid_version_str();

    private static bool _resolverInstalled;
    private static readonly List<string> _searchDirs = new();

    /// <summary>Adds folders to look in for the DLL. Call before <see cref="Available"/>.</summary>
    public static void AddSearchPath(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        lock (_searchDirs)
            if (!_searchDirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                _searchDirs.Add(dir);
    }

    private static void InstallResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        NativeLibrary.SetDllImportResolver(typeof(FluidSynth).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            var names = new[] { "libfluidsynth-3.dll", "libfluidsynth-2.dll", "fluidsynth.dll" };
            lock (_searchDirs)
            {
                foreach (var dir in _searchDirs)
                    foreach (var n in names)
                    {
                        string full = Path.Combine(dir, n);
                        if (File.Exists(full) && NativeLibrary.TryLoad(full, out var h)) return h;
                    }
            }
            foreach (var n in names)
                if (NativeLibrary.TryLoad(n, asm, path, out var h)) return h;
            return IntPtr.Zero;
        });
    }

    private static bool? _available;
    private static string _version = "";

    /// <summary>True if the library loaded. Cheap after the first call.</summary>
    public static bool Available
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            try
            {
                InstallResolver();   // inside the try: SetDllImportResolver throws if one is already set
                IntPtr p = fluid_version_str();
                _version = p != IntPtr.Zero ? Marshal.PtrToStringAnsi(p) ?? "" : "";
                _available = true;
            }
            catch (DllNotFoundException) { _available = false; }
            catch (EntryPointNotFoundException) { _available = false; }
            catch (BadImageFormatException) { _available = false; }
            catch { _available = false; }
            return _available.Value;
        }
    }

    /// <summary>libfluidsynth's own version string, once it has loaded.</summary>
    public static string Version => Available ? _version : "";

    private IntPtr _settings, _synth;
    private int _sfId = -1;

    /// <summary>The SoundFont currently loaded, or "" if the synth is running silent.</summary>
    public string SoundFontPath { get; private set; } = "";

    /// <summary>Why the last call failed, for display.</summary>
    public string Error { get; private set; } = "";

    /// <summary>True once the synth exists.</summary>
    public bool IsOpen => _synth != IntPtr.Zero;

    /// <summary>True when a SoundFont actually loaded -- without one the synth is silent.</summary>
    public bool HasSoundFont => _sfId >= 0;

    /// <summary>Creates the synth at the mixer's sample rate.</summary>
    public bool Open(int sampleRate)
    {
        if (_synth != IntPtr.Zero) return true;
        if (!Available) { Error = "libfluidsynth-3.dll was not found"; return false; }
        try
        {
            _settings = new_fluid_settings();
            if (_settings == IntPtr.Zero) { Error = "new_fluid_settings failed"; return false; }
            fluid_settings_setnum(_settings, Utf8("synth.sample-rate"), sampleRate);
            fluid_settings_setint(_settings, Utf8("synth.midi-channels"), 16);
            fluid_settings_setint(_settings, Utf8("synth.reverb.active"), 1);
            fluid_settings_setint(_settings, Utf8("synth.chorus.active"), 1);
            _synth = new_fluid_synth(_settings);
            if (_synth == IntPtr.Zero)
            {
                delete_fluid_settings(_settings); _settings = IntPtr.Zero;
                Error = "new_fluid_synth failed";
                return false;
            }
            fluid_synth_set_gain(_synth, 0.6f);
            Error = "";
            return true;
        }
        catch (Exception e) { Error = e.Message; Close(); return false; }
    }

    /// <summary>Loads (or replaces) the SoundFont. An empty path unloads and runs silent.</summary>
    public bool LoadSoundFont(string? path)
    {
        if (_synth == IntPtr.Zero) return false;
        try
        {
            if (_sfId >= 0) { fluid_synth_sfunload(_synth, _sfId, 1); _sfId = -1; SoundFontPath = ""; }
            if (string.IsNullOrWhiteSpace(path)) return true;
            if (!File.Exists(path)) { Error = "SoundFont not found: " + path; return false; }

            // FluidSynth 2.2+ converts the path from UTF-8 to wide characters itself; older
            // builds hand it straight to fopen, i.e. the system code page. Try the documented
            // encoding, then the other one, so a path like "G:\Můj disk\..." loads on both.
            int id = fluid_synth_sfload(_synth, Utf8(path), 1);
            if (id == -1)
            {
                var ansi = System.Text.Encoding.Default.GetBytes(path + "\0");
                id = fluid_synth_sfload(_synth, ansi, 1);
            }
            if (id == -1)
            {
                // Almost always the file itself: .sfz and .sfArk are different formats that
                // FluidSynth cannot read, and a partial download fails the same way.
                Error = $"FluidSynth would not load {Path.GetFileName(path)} " +
                        $"({new FileInfo(path).Length / (1024 * 1024)} MB). It reads .sf2/.sf3/.sf only.";
                return false;
            }
            _sfId = id;
            SoundFontPath = path;
            Error = "";
            return true;
        }
        catch (Exception e) { Error = e.Message; return false; }
    }

    /// <summary>Master gain, 0..1 (the game's music volume feeds this).</summary>
    public void SetGain(float gain)
    {
        if (_synth != IntPtr.Zero) fluid_synth_set_gain(_synth, Math.Clamp(gain, 0f, 10f));
    }

    public void NoteOn(int chan, int key, int vel) { if (_synth != IntPtr.Zero) fluid_synth_noteon(_synth, chan, key, vel); }
    public void NoteOff(int chan, int key) { if (_synth != IntPtr.Zero) fluid_synth_noteoff(_synth, chan, key); }
    public void ControlChange(int chan, int ctrl, int val) { if (_synth != IntPtr.Zero) fluid_synth_cc(_synth, chan, ctrl, val); }
    public void ProgramChange(int chan, int prog) { if (_synth != IntPtr.Zero) fluid_synth_program_change(_synth, chan, prog); }
    public void PitchBend(int chan, int value14) { if (_synth != IntPtr.Zero) fluid_synth_pitch_bend(_synth, chan, value14); }
    public void ChannelPressure(int chan, int val) { if (_synth != IntPtr.Zero) fluid_synth_channel_pressure(_synth, chan, val); }
    public void KeyPressure(int chan, int key, int val) { if (_synth != IntPtr.Zero) fluid_synth_key_pressure(_synth, chan, key, val); }

    /// <summary>Silences every channel without tearing the synth down.</summary>
    public void Silence()
    {
        if (_synth == IntPtr.Zero) return;
        for (int ch = 0; ch < 16; ch++) { fluid_synth_all_notes_off(_synth, ch); fluid_synth_all_sounds_off(_synth, ch); }
    }

    /// <summary>Full reset: notes off, controllers and programs back to defaults.</summary>
    public void Reset()
    {
        if (_synth == IntPtr.Zero) return;
        fluid_synth_system_reset(_synth);
    }

    /// <summary>Renders <paramref name="frames"/> stereo frames into two mono buffers.</summary>
    public void Render(short* left, short* right, int frames)
    {
        if (_synth == IntPtr.Zero || frames <= 0) return;
        fluid_synth_write_s16(_synth, frames, left, 0, 1, right, 0, 1);
    }

    public void Close()
    {
        if (_synth != IntPtr.Zero) { try { delete_fluid_synth(_synth); } catch { } _synth = IntPtr.Zero; }
        if (_settings != IntPtr.Zero) { try { delete_fluid_settings(_settings); } catch { } _settings = IntPtr.Zero; }
        _sfId = -1;
        SoundFontPath = "";
    }

    public void Dispose() => Close();

    /// <summary>The .sf2/.sf3/.sf files FluidSynth can load, newest first, found in a folder.</summary>
    public static List<string> FindSoundFonts(params string?[] dirs)
    {
        var found = new List<(string Path, DateTime When)>();
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    string ext = Path.GetExtension(f);
                    if (!ext.Equals(".sf2", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".sf3", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".sf", StringComparison.OrdinalIgnoreCase)) continue;
                    if (found.Any(p => string.Equals(p.Path, f, StringComparison.OrdinalIgnoreCase))) continue;
                    found.Add((f, File.GetLastWriteTimeUtc(f)));
                }
            }
            catch { /* an unreadable folder simply contributes nothing */ }
        }
        return found.OrderByDescending(f => f.When).Select(f => f.Path).ToList();
    }
}
