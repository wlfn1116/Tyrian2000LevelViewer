using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;
using T2LV.Tyrian.Audio;

namespace T2LV;

/// <summary>
/// The music player: every song in music.mus, its notes laid out the way a DAW lays
/// out a MIDI clip, and the three voices the widescreen build can speak them in.
///
/// The timeline is the point of the window. A Loudness song is nine hardware channels
/// and nothing else, so the lanes are the song -- there is no arranging above them and
/// no reason to invent any. A song that leaves a channel silent gets no lane for it:
/// the row would be a blank stripe charged against the ones carrying the music. Time is
/// measured in Loudness ticks because that
/// is the unit both the OPL player and the converted MIDI advance in (one tick per
/// lds_update, 35 ticks per quarter note at the converter's fixed 120 bpm), so a
/// playhead drawn from either device lands on the same note.
///
/// "Where it's used" is read out of the data, not out of a table: the ]L song field,
/// every event 35, the ]M cutscene cues and the ]i shop-music command. Only the
/// handful of songs the engine names in C -- the title screen, the shop, the game
/// over -- come from a static list, because nothing in the data files knows about them.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showMusic;
    private int _musicSelected;
    private float _musicListW = 260f;
    private readonly byte[] _musicFilter = new byte[64];
    private bool _musicScrollToSelection;

    /// <summary>Pixels per Loudness tick. 0 = fit the song to the panel.</summary>
    private float _musicZoom;
    private float _musicScrollTicks;              // leftmost tick shown
    private float _laneH = 44f;                   // this frame's resolved lane height
    private float _laneZoom;                      // the height the user asked for; 0 = fit
    private float _laneScroll;                    // first pixel of the lane strip shown
    private const float MinLaneH = 14f, MaxLaneH = 220f;
    private bool _musicFollow = true;             // keep the playhead in view while playing
    private int _musicLoopA = -1, _musicLoopB = -1;
    private bool _musicDragRuler, _musicDragLoop;
    private bool _musicDragOverview;      // dragging the viewport box along the overview strip
    private float _musicOverviewGrab;     // ... where inside the box it was grabbed, in ticks
    private float _musicLoopAnchor;
    private float _musicScrubTick = -1f;   // where a live scrub is pointing; -1 = not scrubbing
    private int _musicUseTab;                     // 0 = where used, 1 = channels, 2 = song data

    /// <summary>Flattened songs, kept so switching tracks does not re-convert every time.</summary>
    private readonly Dictionary<int, MidiSequence?> _seqCache = new();

    private static readonly uint AcMusic = Gfx.Rgba(150, 200, 255);
    private static readonly uint AcSound = Gfx.Rgba(255, 175, 120);

    /// <summary>One colour per Loudness channel, so a lane is recognisable at a glance.</summary>
    private static readonly uint[] LaneColors =
    {
        Gfx.Rgba(120, 200, 255), Gfx.Rgba(255, 170, 110), Gfx.Rgba(150, 230, 160),
        Gfx.Rgba(230, 150, 245), Gfx.Rgba(255, 220, 120), Gfx.Rgba(130, 180, 250),
        Gfx.Rgba(250, 140, 150), Gfx.Rgba(140, 235, 225), Gfx.Rgba(200, 195, 140),
    };

    /// <summary>The <c>--showmusic N</c> entry point: open the window on one song.</summary>
    public void ShowTrack(int songNumber)
    {
        _showMusic = true;
        if (songNumber > 0) _musicSelected = songNumber - 1;   // the CLI takes the 1-based number
        _musicScrollToSelection = true;
    }

    /// <summary>Open the music window on a song, from another browser.</summary>
    private void OpenTrack(int songIndex)
    {
        _showMusic = true;
        _musicSelected = Math.Max(0, songIndex);
        _musicScrollToSelection = true;
        _musicScrollTicks = 0;
    }

    private MusicBank? Bank => _audio?.Music;

    /// <summary>The flattened song for a track, converted once and kept.</summary>
    private MidiSequence? SeqFor(int index)
    {
        if (_seqCache.TryGetValue(index, out var hit)) return hit;
        var track = Bank?[index];
        MidiSequence? seq = null;
        try { seq = MidiSequence.From(track?.Midi); } catch { /* a song that will not convert has no timeline */ }
        _seqCache[index] = seq;
        return seq;
    }

    // =====================================================================
    // The window
    // =====================================================================

    private void DrawMusicWindow()
    {
        _musicFocused = false;
        if (!_showMusic) { _wasShowingMusic = false; return; }
        if (!_wasShowingMusic) { _wasShowingMusic = true; ArmTrace("music window opening"); }

        PumpSoundFontPicker();

        Trace("begin");
        if (!RefBegin("Music", "music", ref _showMusic, AcMusic,
                new Vector2(1180, 780), new Vector2(720, 460))) { Trace("collapsed"); return; }
        _musicFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

        if (_audio == null || !_audio.IsOpen)
        {
            DrawMusicBand();
            UiEmpty("No audio device",
                _audioProblem.Length > 0 ? _audioProblem : "Nothing here can make a sound.", AcMusic);
            RefEnd(AcMusic);
            return;
        }
        var bank = Bank!;
        if (!bank.Loaded)
        {
            DrawMusicBand();
            UiEmpty("music.mus is not in this data folder",
                "The 41 songs live in one packed file next to tyrian1.lvl.", AcMusic);
            RefEnd(AcMusic);
            return;
        }

        _musicSelected = Math.Clamp(_musicSelected, 0, bank.Tracks.Length - 1);
        Trace($"band (song {_musicSelected + 1}, device {_musicDevice})");
        DrawMusicBand();

        float maxList = Math.Max(180f, ImGui.GetContentRegionAvail().X - 460f);
        _musicListW = Math.Clamp(_musicListW, 180f, maxList);

        Trace("track list");
        WellBegin("mustracks", new Vector2(_musicListW, ImGui.GetContentRegionAvail().Y), AcMusic);
        DrawTrackList(bank);
        WellEnd();

        ImGui.SameLine(0, 3);
        VSplitter("##mussplit", ref _musicListW, 180f, maxList);
        ImGui.SameLine(0, 3);

        Trace("detail");
        ImGui.BeginChild("musmain", new Vector2(0, 0));
        DrawTrackDetail(bank);
        ImGui.EndChild();

        Trace("done");
        if (_audioTraceFrames > 0) _audioTraceFrames--;
        RefEnd(AcMusic);
    }

    /// <summary>Whether the window was up last frame, so its opening can be traced once.</summary>
    private bool _wasShowingMusic;

    /// <summary>The top band: filter, which synthesizer, the SoundFont, and the volume.</summary>
    private void DrawMusicBand()
    {
        // One row, and a second only while a whole-bank export is running, for the song it is
        // on and the meter. Idle, the band is a single clean line.
        BandBegin("musband", AcMusic, SongBatchRunning ? 2 : 1);

        UiFilter("##musfilter", "filter songs", _musicFilter, 190f, AcMusic);

        BandDivider();
        BandLabel("voice");
        int dev = (int)_musicDevice;
        if (SegBar("##musdev", ref dev, AcMusic, 306f,
                ("OPL3", "The emulated AdLib chip -- what the game actually sounds like.\n" +
                         "Every song in music.mus is written for it."),
                ("FluidSynth", "The same song converted to MIDI and played through a SoundFont.\n" +
                               "Needs libfluidsynth-3.dll beside the viewer or in the data folder,\n" +
                               "and a .sf2 bank to play out of."),
                ("Native MIDI", "The operating system's own synthesizer (the Microsoft GS\n" +
                                "wavetable, unless you have another one installed).")))
            SetMusicDevice((MusicDevice)dev);

        if (_musicDevice == MusicDevice.FluidSynth)
        {
            BandDivider();
            DrawSoundFontPicker();
        }

        BandDivider();
        BandLabel("volume");
        ImGui.SetNextItemWidth(120);
        int vol = _musicVolume;
        if (ImGui.SliderInt("##musvol", ref vol, 0, 255, "%d")) _musicVolume = vol;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The engine's own 0-255 music volume, on its 30 dB curve.\nThe game ships at 191.");

        BandDivider();
        DrawExportAllSongsRow();

        BandDivider();
        string note = _audio?.Player?.DeviceError is { Length: > 0 } err
            ? err
            : _musicDevice == MusicDevice.NativeMidi && _audio?.Player?.Native is { IsOpen: true } n
                ? n.DeviceName
                : _musicDevice == MusicDevice.FluidSynth && FluidSynth.Available
                    ? $"libfluidsynth {FluidSynth.Version}"
                    : Bank is { Loaded: true } b ? $"{b.Tracks.Length} songs in music.mus" : "";
        BandNote(note, UiFaint);

        // --- second row, only while a batch is running ---
        DrawExportAllProgress(AcMusic, SongBatchRunning);

        BandEnd();
    }

    /// <summary>
    /// The whole bank out in one go, next to the voice picker that decides how it will sound.
    /// WAVE renders through whatever is selected -- except Native MIDI, whose audio never
    /// passes through this process, so that one falls back to the OPL the same way a single
    /// song's export does.
    /// </summary>
    private void DrawExportAllSongsRow()
    {
        bool windows = OperatingSystem.IsWindows();
        bool ready = Bank is { Loaded: true } && _audio is { IsOpen: true };
        string voice = _musicDevice switch
        {
            MusicDevice.FluidSynth when _audio?.Player?.Fluid is { IsOpen: true, HasSoundFont: true }
                => "through the SoundFont, in stereo",
            MusicDevice.NativeMidi
                => "through the OPL -- the OS synth plays outside this process\nand cannot be recorded",
            _   => "through the emulated OPL",
        };

        if (UiButton("export all songs (WAV)", AcMusic,
                $"Render every song in music.mus to its own .wav, {voice}.\n" +
                "Pick a folder; each file is named for its song, replacing one already\n" +
                "there by that name. This takes minutes -- the meter says how far in.",
                0f, ExportBusy || !ready || !windows) && windows)
            StartAudioExportAll(ExportAllKind.SongsWav);

        ImGui.SameLine(0, 5);
        if (UiButton("export all songs (MIDI)", AcMusic,
                "Save every song as a Standard MIDI File -- the same notes the timeline\n" +
                "draws. Pick a folder; each file is named for its song, replacing one\n" +
                "already there by that name.",
                0f, ExportBusy || !ready || !windows) && windows)
            StartAudioExportAll(ExportAllKind.SongsMidi);
    }

    private void DrawSoundFontPicker()
    {
        BandLabel("sound bank");
        var fluid = _audio?.Player?.Fluid;
        string current = fluid is { HasSoundFont: true }
            ? Path.GetFileName(fluid.SoundFontPath) : "(none)";
        ImGui.SetNextItemWidth(190);
        if (ImGui.BeginCombo("##mussf", current))
        {
            var found = FluidSynth.FindSoundFonts(_dataDir, Directory.GetParent(_dataDir)?.FullName,
                AppContext.BaseDirectory);
            if (found.Count == 0) ImGui.TextDisabled("no .sf2 found nearby -- use Browse");
            foreach (var f in found)
            {
                bool sel = string.Equals(f, fluid?.SoundFontPath, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(Path.GetFileName(f), sel)) ApplySoundFont(f);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(f);
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Any .sf2/.sf3 next to the viewer, in the data folder, or in the\n" +
                             "OpenTyrian install above it. The widescreen build ships WeedsGM3.sf2.");

        ImGui.SameLine(0, 5);
        if (UiButton("Browse...", AcMusic, "Choose a SoundFont from anywhere on disk", 78f,
                _sfPickActive || !OperatingSystem.IsWindows()) && OperatingSystem.IsWindows())
            OpenSoundFontPicker();
    }

    /// <summary>Loads a SoundFont and remembers it, reporting a refusal where it can be read.</summary>
    private void ApplySoundFont(string path)
    {
        _soundFont = path;
        var player = _audio?.Player;
        if (player == null) return;
        player.SetSoundFont(path);
        var fluid = player.Fluid;
        _status = fluid is { HasSoundFont: true }
            ? $"SoundFont: {Path.GetFileName(path)}"
            : fluid?.Error ?? "SoundFont could not be loaded.";
    }

    /// <summary>
    /// The Browse box, on its own STA thread like the data-folder picker: the common file
    /// dialog pumps its own message loop, and running it inline would freeze the frame.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OpenSoundFontPicker()
    {
        if (_sfPickActive) return;
        _sfPickActive = true;
        IntPtr owner = NativeFileDialog.ForegroundWindow();
        string? start = _soundFont.Length > 0 ? Path.GetDirectoryName(_soundFont) : _dataDir;
        var t = new Thread(() =>
        {
            try { _sfPickResult = NativeFileDialog.OpenFileBlocking(start, owner, "Choose a SoundFont (.sf2)"); }
            catch (Exception e) { _sfPickError = e.Message; }
            finally { _sfPickDone = true; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    private volatile bool _sfPickActive, _sfPickDone;
    private volatile string? _sfPickResult, _sfPickError;

    /// <summary>Applies a finished Browse. Called once a frame from the music window.</summary>
    private void PumpSoundFontPicker()
    {
        if (!_sfPickDone) return;
        _sfPickDone = false;
        _sfPickActive = false;
        if (_sfPickError is { Length: > 0 } err) { _status = "SoundFont picker failed: " + err; _sfPickError = null; }
        else if (_sfPickResult is { Length: > 0 } path) ApplySoundFont(path);
        _sfPickResult = null;
    }

    private void SetMusicDevice(MusicDevice device)
    {
        _musicDevice = device;
        _audio?.Player?.SetDevice(device, ResolveSoundFont());
        // A fallback to OPL is reported through DeviceError; reflect it so the segmented
        // control does not claim a device that never started.
        if (_audio?.Player != null) _musicDevice = _audio.Player.Device;
    }

    // =====================================================================
    // Track list
    // =====================================================================

    private static readonly (string Label, string Note)[] TrackGroups =
    {
        ("in levels", "played while you are flying"),
        ("menus, cutscenes and jingles", "everywhere else the engine puts music"),
        ("never played", "only reachable from the jukebox"),
    };

    private void DrawTrackList(MusicBank bank)
    {
        string filter = BufText(_musicFilter).Trim();
        var usage = Usage;

        var group = new List<int>[3];
        for (int i = 0; i < 3; i++) group[i] = new List<int>();
        for (int i = 0; i < bank.Tracks.Length; i++)
        {
            if (filter.Length > 0 && !Matches(filter, bank.Tracks[i].Title, (i + 1).ToString())) continue;
            int levels = usage?.SongLevelCount(i) ?? 0;
            var uses = usage?.Song(i) ?? Array.Empty<AudioUse>();
            group[levels > 0 ? 0 : uses.Count > 0 ? 1 : 2].Add(i);
        }

        int mostLevels = 1;
        for (int i = 0; i < bank.Tracks.Length; i++)
            mostLevels = Math.Max(mostLevels, usage?.SongLevelCount(i) ?? 0);

        bool any = false;
        for (int g = 0; g < 3; g++)
        {
            if (group[g].Count == 0) continue;
            any = true;
            UiSection(TrackGroups[g].Label, AcMusic, group[g].Count.ToString());
            foreach (int i in group[g])
            {
                var t = bank.Tracks[i];
                bool sel = i == _musicSelected;
                const float rowH = 34f;
                var box = UiRow($"##trk{i}", sel, AcMusic, rowH);
                if (box.Clicked) { _musicSelected = i; _musicScrollTicks = 0; }
                if (box.Hovered)
                    ImGui.SetTooltip($"{t.Title}\nsong {i + 1} of {bank.Tracks.Length}   ·   " +
                                     $"{t.Raw.Length:n0} bytes of LDS\n\n{TrackGroups[g].Note}");
                if (sel && _musicScrollToSelection) { ImGui.SetScrollHereY(0.4f); _musicScrollToSelection = false; }

                int levels = usage?.SongLevelCount(i) ?? 0;
                string trail = levels > 0 ? $"{levels} lv" : "";
                var seq = _seqCache.TryGetValue(i, out var cached) ? cached : null;
                string sub = seq != null
                    ? $"{FormatTicks(seq.Duration)}   ·   {seq.Notes.Length:n0} notes"
                    : $"#{i + 1}";
                RowText(box, 10f, t.Title, sub, AcMusic, sel, TrailRoom(trail) + 30f);

                if (levels > 0)
                {
                    var dl = ImGui.GetWindowDrawList();
                    var bar = new Vector2(box.Max.X - TrailRoom(trail) - 26f, box.Min.Y + rowH * 0.5f - 3f);
                    MeterBar(dl, bar, bar + new Vector2(22f, 6f), levels / (float)mostLevels, AcMusic);
                    RowTrail(box, trail, Shade(AcMusic, 1.1f));
                }
                if (_musicOwner == 2 && _audio?.Player?.Track == t && _audio.Player.IsPlaying)
                    ImGui.GetWindowDrawList().AddCircleFilled(
                        new Vector2(box.Min.X + 4f, (box.Min.Y + box.Max.Y) * 0.5f), 3f, AcGo);
            }
        }
        if (!any) ImGui.TextDisabled("No song matches that filter.");
        _musicScrollToSelection = false;
    }

    // =====================================================================
    // Detail: header, transport, timeline, cross-reference
    // =====================================================================

    private void DrawTrackDetail(MusicBank bank)
    {
        var track = bank.Tracks[_musicSelected];
        var seq = SeqFor(_musicSelected);
        var lds = track.Lds;

        Trace($"  detail: lds={(lds != null)} seq={(seq != null)} notes={seq?.Notes.Length ?? -1}");
        UiTitle(track.Title.ToUpperInvariant(), AcMusic, $"song {_musicSelected + 1} of {bank.Tracks.Length}");

        Badge(seq != null ? FormatTicks(seq.Duration) : "not converted", AcMusic);
        ImGui.SameLine(0, 5f);
        Badge(seq != null ? $"{seq.Notes.Length:n0} notes" : "-", Gfx.Rgba(150, 162, 185));
        ImGui.SameLine(0, 5f);
        Badge(lds != null ? $"{lds.Patches.Length} instruments" : "-", Gfx.Rgba(150, 162, 185));
        ImGui.SameLine(0, 5f);
        Badge(lds != null ? $"{lds.NumPosi} orders / {lds.UsedPatternCount()} patterns" : "-",
            Gfx.Rgba(150, 162, 185));
        ImGui.SameLine(0, 5f);
        if (seq is { Loops: true })
            Badge($"the game loops it at {FormatTicks(seq.LoopStart)}", Gfx.Rgba(150, 162, 185));
        else if (seq != null)
            Badge("plays once, no loop", Gfx.Rgba(150, 162, 185));

        ImGui.Dummy(new Vector2(0, 4f));
        DrawTransport(track, seq);

        const float useH = 186f;
        float timelineH = Math.Max(150f, ImGui.GetContentRegionAvail().Y - useH - 10f);

        Trace("  timeline");
        WellBegin("mustimeline", new Vector2(ImGui.GetContentRegionAvail().X, timelineH), AcMusic, 5f, 5f);
        if (seq == null)
            UiEmpty("This song will not convert",
                "The OPL player can still play it; only the note view needs the MIDI pass.", AcMusic);
        else
            DrawTimeline(seq);
        Trace("  timeline returned");
        WellEnd();
        Trace("  timeline well closed");

        Trace("  lower pane");
        WellBegin("mususes", ImGui.GetContentRegionAvail(), AcMusic, 10f, 8f);
        DrawMusicLowerPane(track, seq, lds);
        WellEnd();
        Trace("  detail done");
    }

    /// <summary>Play/stop, loop, A-B, speed and the position bar.</summary>
    private void DrawTransport(MusicTrack track, MidiSequence? seq)
    {
        var player = _audio!.Player!;
        bool isThis = ReferenceEquals(player.Track, track);
        bool playing = isThis && player.IsPlaying;

        BandBegin("mustransport", AcMusic);

        if (GlyphButton("tostart", Glyph.JumpStart, AcMusic, "Back to the start  (Home)", 0f, false, !isThis))
            player.Seek(_musicLoopA > 0 ? _musicLoopA : 0);
        ImGui.SameLine(0, 4);
        if (GlyphButton("back", Glyph.Rewind, AcMusic, "Back one second  (left arrow, shift for five)",
                0f, false, !isThis))
            player.Seek(player.Tick - 70);
        ImGui.SameLine(0, 4);
        if (GlyphButton("play", playing ? Glyph.Pause : Glyph.Play, playing ? AcGo : AcMusic,
                playing ? "Pause  (space)" : "Play  (space)", ImGui.GetFrameHeight() + 20f, playing))
            ToggleTrack(track);
        ImGui.SameLine(0, 4);
        if (GlyphButton("fwd", Glyph.FastFwd, AcMusic, "Forward one second  (right arrow, shift for five)",
                0f, false, !isThis))
            player.Seek(player.Tick + 70);
        ImGui.SameLine(0, 4);
        if (GlyphButton("stop", Glyph.Stop, AcMusic, "Stop and silence", 0f, false, !isThis))
        {
            player.Stop();
            if (_musicOwner == 2) _musicOwner = 0;
        }

        BandDivider();
        bool loop = player.Loop;
        if (GlyphButton("loop", Glyph.Loop, AcMusic,
                "Repeat instead of stopping. With no A-B range set, a song that declares\n" +
                "its own loop point restarts there, exactly as the game does.", 0f, loop))
            player.Loop = !loop;

        ImGui.SameLine(0, 4);
        if (GlyphButton("marka", Glyph.MarkA, AcMusic,
                "Start the loop range at the playhead\n(or ctrl+drag across the timeline)",
                0f, _musicLoopA >= 0, !isThis))
        { _musicLoopA = (int)player.Tick; ApplyLoopRange(); }
        ImGui.SameLine(0, 4);
        if (GlyphButton("markb", Glyph.MarkB, AcMusic, "End the loop range at the playhead",
                0f, _musicLoopB >= 0, !isThis))
        { _musicLoopB = (int)player.Tick; ApplyLoopRange(); }
        ImGui.SameLine(0, 5);
        if (UiButton("clear", AcMusic, "Drop the A-B range and go back to the song's own loop", 50f,
                _musicLoopA < 0 && _musicLoopB < 0))
        { _musicLoopA = _musicLoopB = -1; ApplyLoopRange(); }

        BandDivider();
        BandLabel("speed");
        ImGui.SetNextItemWidth(96);
        float speed = (float)player.Speed;
        if (ImGui.SliderFloat("##musspeed", ref speed, 0.25f, 2f, "x%.2f")) player.Speed = speed;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scales the Loudness tick rate. The song is the same, just slower or faster.");
        ImGui.SameLine(0, 5);
        // A slider cannot be nudged back to exactly 1 by hand, and anything off it is no longer
        // the song at the rate the game plays it.
        if (UiButton("x1", AcMusic, "Back to the game's own rate", 32f,
                Math.Abs(player.Speed - 1.0) < 0.0005))
            player.Speed = 1.0;

        BandDivider();
        UiToggle("follow", ref _musicFollow, AcMusic, "Scroll the timeline to keep the playhead in view.");

        ImGui.SameLine(0, 5);
        BandLabel("height");
        ImGui.SetNextItemWidth(90);
        float laneH = _laneZoom > 0 ? _laneZoom : _laneH;
        if (ImGui.SliderFloat("##muslaneh", ref laneH, MinLaneH, MaxLaneH, "%.0f px"))
            _laneZoom = laneH;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How tall each channel lane is drawn.\n" +
                             "Also shift+wheel over the timeline; alt+wheel scrolls down them.");
        ImGui.SameLine(0, 5);
        if (UiButton("fit", AcMusic, "Back to every used channel in the panel", 34f, _laneZoom <= 0))
        { _laneZoom = 0; _laneScroll = 0; }

        BandDivider();
        DrawExportRow(AcMusic, ExportKind.SongWav, _musicSelected, withMidi: true);

        BandDivider();
        ImGui.AlignTextToFramePadding();
        double at = isThis ? player.Tick : 0;
        uint timeCol = playing ? Shade(AcGo, 1f) : UiFaint;
        ImGui.TextColored(ColorOf(timeCol),
            seq != null ? $"{FormatTicks((uint)at)} / {FormatTicks(seq.Duration)}" : "--:--");
        if (isThis && player.LoopCount > 0)
        {
            ImGui.SameLine(0, 8);
            BandNote($"pass {player.LoopCount + 1}", UiFaint);
        }

        BandEnd();
    }

    private void ToggleTrack(MusicTrack track)
    {
        var player = _audio!.Player!;
        if (ReferenceEquals(player.Track, track))
        {
            player.TogglePause();
        }
        else
        {
            MusicPlayer.Prepare(track);      // decode off the mixer's lock
            player.Loop = true;
            player.Play(track);
            ApplyLoopRange();
        }
        _musicOwner = 2;   // the window has the music until the level takes it back
    }

    private void ApplyLoopRange()
    {
        var player = _audio?.Player;
        if (player == null) return;
        int a = _musicLoopA, b = _musicLoopB;
        if (a >= 0 && b >= 0 && b < a) (a, b) = (b, a);
        player.LoopRange = (a, b);
    }

    // =====================================================================
    // The timeline
    // =====================================================================

    private const float LaneHeaderW = 132f;
    private const float OverviewH = 26f;

    /// <summary>The loop bar across the top of the ruler, sized to hold ordinary text: the
    /// labels used to be drawn scaled down, which the font atlas cannot do sharply.</summary>
    private static float LoopBarH => ImGui.GetTextLineHeight() + 5f;

    /// <summary>The ruler: the loop bar, then the time numbers under it. They shared one
    /// strip once, and a loop label landed straight on top of "1:30".</summary>
    private static float RulerH => LoopBarH + ImGui.GetTextLineHeight() + 5f;

    /// <summary>The colour the song's own loop is drawn in -- the same green the app uses
    /// everywhere for "this is what the game does", as against the window's own blue.</summary>
    private static uint AcGameLoop => AcGo;

    /// <summary>
    /// How many note rectangles the timeline will hand ImGui in one frame. ImGui indexes a
    /// draw list with 16-bit indices and the SDL_Renderer backend cannot split on overflow,
    /// so a window that submits more than 65536 vertices aborts the process from native code.
    /// Four vertices a rectangle, and the rest of the window wants room too.
    /// </summary>
    private const int NoteDrawBudget = 6000;

    /// <summary>The overview strip's share of the same budget. It only draws while the
    /// timeline is zoomed in -- with the whole song already on screen it would be an exact,
    /// and expensive, duplicate of the lanes above it.</summary>
    private const int OverviewDrawBudget = 4000;

    /// <summary>The General MIDI drum channel. The conversion routes a percussion patch here
    /// whatever Loudness channel it came from, so it is how a drum part is recognised.</summary>
    private const int PercussionChannel = 9;

    // Per-lane scratch for the note pass, kept as fields so a frame allocates nothing.
    private readonly float[] _runX0 = new float[MidiSequence.LaneCount];
    private readonly float[] _runX1 = new float[MidiSequence.LaneCount];
    private readonly float[] _runY = new float[MidiSequence.LaneCount];
    private readonly int[] _runVel = new int[MidiSequence.LaneCount];
    private readonly bool[] _runOpen = new bool[MidiSequence.LaneCount];
    private readonly uint[] _laneCol = new uint[MidiSequence.LaneCount];
    private readonly float[] _laneBottom = new float[MidiSequence.LaneCount];
    /// <summary>Which row a Loudness channel is drawn on, or -1 when it carries no notes at
    /// all and is left out. See <see cref="LaneRows"/>.</summary>
    private readonly int[] _laneSlot = new int[MidiSequence.LaneCount];

    /// <summary>
    /// Rows the timeline actually draws: the channels this song puts a note on. Nine lanes is
    /// what the hardware has, not what a song uses -- most use five or six, and the silent
    /// ones cost the used ones height for nothing. The headers still say CH n, so the numbering
    /// stays the hardware's however few rows are left.
    /// </summary>
    private int LaneRows(MidiSequence seq)
    {
        int rows = 0;
        for (int i = 0; i < MidiSequence.LaneCount; i++)
            _laneSlot[i] = seq.LaneNotes[i] > 0 ? rows++ : -1;
        // A song with no notes anywhere never reaches the timeline, but if one did, one row
        // per channel is a better answer than none at all.
        if (rows == 0)
            for (int i = 0; i < MidiSequence.LaneCount; i++) _laneSlot[i] = rows++;
        return rows;
    }

    private void DrawTimeline(MidiSequence seq)
    {
        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 200f || avail.Y < 90f) { ImGui.TextDisabled("Not enough room for the timeline."); return; }
        if (seq.Duration == 0 || seq.Notes.Length == 0)
        {
            UiEmpty("This song converted to no notes", "There is nothing to lay out.", AcMusic);
            return;
        }

        var player = _audio!.Player!;
        bool isThis = ReferenceEquals(player.Track, Bank?[_musicSelected]);
        double playhead = isThis ? player.Tick : 0;

        float gridW = Math.Max(60f, avail.X - LaneHeaderW);
        float viewH = Math.Max(40f, avail.Y - RulerH - OverviewH - 6f);
        int laneRows = LaneRows(seq);
        // Lane height is the user's, not a division of whatever room is left: nine channels
        // squeezed into a short pane leaves each one too flat to read a melody off. 0 means
        // "fit", which is what it does until the height slider or ctrl+shift+wheel is used.
        _laneH = _laneZoom > 0
            ? Math.Clamp(_laneZoom, MinLaneH, MaxLaneH)
            : Math.Clamp(viewH / laneRows, MinLaneH, MaxLaneH);
        float lanesFull = _laneH * laneRows;
        float lanesH = Math.Min(viewH, lanesFull);

        // A short song fitted to a wide panel wants more than 6 px/tick, which would put the
        // floor above the ceiling -- Math.Clamp throws on that, and this window used to take
        // the whole app down with it. Both bounds go through MinZoom/MaxZoom.
        float fit = gridW / Math.Max(1f, seq.Duration);
        float zoomMax = Math.Max(6f, fit);
        float zoomMin = Math.Min(fit * 0.5f, zoomMax);
        float zoom = Math.Clamp(_musicZoom > 0 ? _musicZoom : fit, zoomMin, zoomMax);

        var origin = ImGui.GetCursorScreenPos();
        var gridPos = origin + new Vector2(LaneHeaderW, 0);

        ImGui.InvisibleButton("mustl", avail,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();

        // Wheel scrolls the song; ctrl+wheel zooms time about the cursor; shift+wheel makes
        // the channels taller or shorter; alt+wheel scrolls down the channels themselves.
        if (hovered && io.MouseWheel != 0)
        {
            if (io.KeyCtrl)
            {
                float relTick = _musicScrollTicks + (mouse.X - gridPos.X) / zoom;
                float newZoom = Math.Clamp(zoom * MathF.Pow(1.2f, io.MouseWheel), zoomMin, zoomMax);
                _musicScrollTicks = relTick - (mouse.X - gridPos.X) / newZoom;
                _musicZoom = newZoom;
                zoom = newZoom;
            }
            else if (io.KeyShift)
            {
                _laneZoom = Math.Clamp(_laneH * MathF.Pow(1.15f, io.MouseWheel), MinLaneH, MaxLaneH);
                _laneH = _laneZoom;
                lanesFull = _laneH * laneRows;
                lanesH = Math.Min(viewH, lanesFull);
            }
            else if (io.KeyAlt) _laneScroll -= io.MouseWheel * _laneH * 0.5f;
            else _musicScrollTicks -= io.MouseWheel * (gridW * 0.15f) / zoom;
        }
        _laneScroll = Math.Clamp(_laneScroll, 0f, Math.Max(0f, lanesFull - lanesH));

        float visibleTicks = gridW / zoom;
        float maxScroll = Math.Max(0, seq.Duration - visibleTicks);
        // Following the playhead stands down while the overview is being dragged, and picks
        // up again on release -- otherwise the two fight over the scroll every frame.
        if (_musicFollow && isThis && player.IsPlaying && !_musicDragOverview)
        {
            if (playhead < _musicScrollTicks || playhead > _musicScrollTicks + visibleTicks * 0.92f)
                _musicScrollTicks = (float)Math.Max(0, playhead - visibleTicks * 0.25f);
        }
        _musicScrollTicks = Math.Clamp(_musicScrollTicks, 0, maxScroll);

        float TickX(double t) => gridPos.X + (float)(t - _musicScrollTicks) * zoom;
        double XTick(float x) => _musicScrollTicks + (x - gridPos.X) / zoom;

        var rulerMin = gridPos;
        var rulerMax = gridPos + new Vector2(gridW, RulerH);

        // --- the overview strip doubles as a scrollbar: grab the viewport box and drag ---
        var ovMin = new Vector2(gridPos.X, rulerMax.Y + lanesH + 4f);
        var ovMax = ovMin + new Vector2(gridW, OverviewH - 6f);
        bool inOverview = hovered && mouse.Y >= ovMin.Y - 2f && mouse.Y <= ovMax.Y + 2f &&
                          mouse.X >= ovMin.X && mouse.X <= ovMax.X;
        float ovScale = gridW / Math.Max(1f, seq.Duration);           // pixels per tick, whole song

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _musicDragOverview = false;
        if (inOverview && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyCtrl)
        {
            // Clicking outside the box centres it on the click and carries on from there, so
            // one gesture works whether you aim at the box or just at where you want to be.
            float atTick = (mouse.X - ovMin.X) / ovScale;
            float half = gridW / zoom * 0.5f;
            bool onBox = atTick >= _musicScrollTicks && atTick <= _musicScrollTicks + gridW / zoom;
            _musicOverviewGrab = onBox ? atTick - _musicScrollTicks : half;
            _musicDragOverview = true;
        }
        if (_musicDragOverview)
        {
            _musicScrollTicks = Math.Clamp((mouse.X - ovMin.X) / ovScale - _musicOverviewGrab,
                0f, maxScroll);
        }
        if (inOverview && !_musicDragRuler && !_musicDragLoop)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);

        // Seek by dragging in the ruler or the lanes; ctrl-drag lays out an A-B range.
        bool inGrid = hovered && !_musicDragOverview && !inOverview &&
                      mouse.X >= gridPos.X && mouse.Y <= gridPos.Y + RulerH + lanesH;
        if (inGrid && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (io.KeyCtrl) { _musicDragLoop = true; _musicLoopAnchor = (float)XTick(mouse.X); }
            else _musicDragRuler = true;
        }
        if (_musicDragRuler)
        {
            // Scrubbing shows the target live but only seeks when the drag settles: an OPL
            // seek re-runs the song from its start, and doing that every dragged frame locks
            // the mixer out for as long as it takes.
            _musicScrubTick = (float)Math.Clamp(XTick(mouse.X), 0, seq.Duration);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                _musicDragRuler = false;
                if (isThis) player.Seek(_musicScrubTick);
                _musicScrubTick = -1f;
            }
        }
        if (_musicDragLoop)
        {
            float other = (float)Math.Clamp(XTick(mouse.X), 0, seq.Duration);
            _musicLoopA = (int)Math.Min(_musicLoopAnchor, other);
            _musicLoopB = (int)Math.Max(_musicLoopAnchor, other);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) { _musicDragLoop = false; ApplyLoopRange(); }
        }

        Trace($"    timeline: zoom={zoom:0.####} gridW={gridW:0} lanes={lanesH:0} dur={seq.Duration}");
        // No manual PushClipRect here: the well around this is a child window and already
        // clips everything drawn into it. Pushing a raw clip rect onto the draw list and then
        // submitting widgets inside it (the lane mute/solo keys used to) leaves ImGui's own
        // clip bookkeeping out of step, and EndChild takes the process down over it.
        var dl = ImGui.GetWindowDrawList();

        // --- ruler: a loop bar across the top, then the time scale under it ---
        float loopBarY = rulerMin.Y;
        float scaleY = rulerMin.Y + LoopBarH;
        dl.AddRectFilled(rulerMin, rulerMax, Gfx.Rgba(22, 25, 34), 0f);
        dl.AddRectFilled(rulerMin, new Vector2(rulerMax.X, scaleY), Gfx.Rgba(14, 16, 22));
        dl.AddLine(new Vector2(rulerMin.X, scaleY), new Vector2(rulerMax.X, scaleY), Gfx.Rgba(38, 42, 54));

        double secPerTick = 1.0 / MusicBank.LdsUpdateRate;
        double spanSec = visibleTicks * secPerTick;
        double step = spanSec > 120 ? 30 : spanSec > 50 ? 10 : spanSec > 20 ? 5 : spanSec > 6 ? 2 : 1;
        for (double s = 0; s * MusicBank.LdsUpdateRate <= seq.Duration; s += step)
        {
            float x = TickX(s * MusicBank.LdsUpdateRate);
            if (x < gridPos.X - 40 || x > gridPos.X + gridW) continue;
            dl.AddLine(new Vector2(x, scaleY + 2f), new Vector2(x, rulerMax.Y), Gfx.Rgba(70, 78, 96));
            dl.AddText(new Vector2(x + 3, scaleY + 2f), UiFaint, $"{(int)s / 60}:{(int)s % 60:00}");
            dl.AddLine(new Vector2(x, rulerMax.Y), new Vector2(x, rulerMax.Y + lanesH), Gfx.Rgba(38, 42, 54));
        }

        // --- lanes ---
        int low = Math.Max(0, seq.LowKey - 1), high = Math.Min(127, seq.HighKey + 1);
        float span = Math.Max(1, high - low);

        bool anySolo = false;
        for (int i = 0; i < MidiSequence.LaneCount; i++) if (player.IsSolo(i)) anySolo = true;

        var laneCol = _laneCol;
        var laneBottom = _laneBottom;
        float lanesTop = rulerMax.Y, lanesEnd = rulerMax.Y + lanesH;
        for (int lane = 0; lane < MidiSequence.LaneCount; lane++)
        {
            int slot = _laneSlot[lane];
            if (slot < 0) { laneBottom[lane] = float.NegativeInfinity; continue; }   // silent: no row
            float top = lanesTop + slot * _laneH - _laneScroll;
            var laneMax = new Vector2(gridPos.X + gridW, top + _laneH - 1);
            bool audible = anySolo ? player.IsSolo(lane) : !player.IsMuted(lane);

            uint col = LaneColors[lane % LaneColors.Length];
            laneCol[lane] = audible ? col : Alpha(Shade(col, 0.45f), 90);
            laneBottom[lane] = laneMax.Y;
            // Scrolled out of the strip: still needed above for the note pass to place it,
            // but nothing of it is drawn.
            if (laneMax.Y < lanesTop || top > lanesEnd) continue;

            dl.AddRectFilled(new Vector2(gridPos.X, Math.Max(top, lanesTop)),
                new Vector2(laneMax.X, Math.Min(laneMax.Y, lanesEnd)),
                slot % 2 == 0 ? Gfx.Rgba(19, 21, 29) : Gfx.Rgba(23, 26, 35));
            if (top >= lanesTop - 1f && laneMax.Y <= lanesEnd + 1f)
                DrawLaneHeader(dl, new Vector2(origin.X, top), lane, seq, audible, hovered, mouse);
            if (laneMax.Y <= lanesEnd)
                dl.AddLine(new Vector2(gridPos.X, laneMax.Y), new Vector2(gridPos.X + gridW, laneMax.Y),
                    Gfx.Rgba(34, 38, 48));
        }

        // One pass over the notes -- not one pass per lane, which used to rescan the whole
        // song nine times.
        //
        // Every note gets its own bar. An earlier version merged touching bars at the same
        // height into one, which is fatal for exactly the thing this window is for: a drum
        // part is the same pitch struck over and over, and merging turned TRANSON's opening
        // beat into one unbroken line. Instead each bar gives up half a pixel at its right
        // edge, so a repeated note reads as a row of strikes at any zoom.
        float hNote = Math.Clamp((_laneH - 6f) / span * 1.6f, 2f, 6f);
        float left = gridPos.X, right = gridPos.X + gridW;
        int shown = 0, clipped = 0;

        foreach (var n in seq.Notes)
        {
            int lane = n.Lane;
            if ((uint)lane >= MidiSequence.LaneCount || _laneSlot[lane] < 0) continue;
            float x0 = TickX(n.Start), x1 = TickX(n.End);
            if (x1 < left || x0 > right) continue;
            if (shown >= NoteDrawBudget) { clipped++; continue; }
            shown++;

            float y = laneBottom[lane] - 2f - (n.Key - low) / span * (_laneH - 6f);
            if (y < lanesTop || y > lanesEnd) { shown--; continue; }   // that lane is scrolled away
            uint c = Alpha(laneCol[lane], (byte)Math.Clamp(120 + n.Velocity, 130, 255));
            float xa = Math.Max(x0, left);

            // A note the same pitch strikes again the moment it ends carries no readable
            // duration -- a drum part is nothing but those, and as duration bars it comes out
            // as one unbroken line. Those draw as a short onset mark; everything else keeps
            // its length, minus a hair so neighbours do not fuse.
            float xb = n.Restruck
                ? Math.Min(xa + Math.Clamp(x1 - x0, 1.5f, 5f) - 0.75f, right)
                : Math.Min(Math.Max(x1 - 0.75f, x0 + 1f), right);
            dl.AddRectFilled(new Vector2(xa, y - hNote * 0.5f),
                new Vector2(Math.Max(xb, xa + 0.75f), y + hNote * 0.5f), c, 1f);

            // On a held note wide enough to show one, a brighter cap marks where it was struck.
            if (!n.Restruck && xb - xa >= 5f)
                dl.AddRectFilled(new Vector2(xa, y - hNote * 0.5f),
                    new Vector2(xa + 1.5f, y + hNote * 0.5f), Alpha(Shade(laneCol[lane], 1.5f), 255), 1f);
        }

        // --- the song's own loop, and the A-B range, OVER the notes ---
        // Drawn last on purpose: underneath, each lane's opaque background painted straight
        // over them and the range was all but invisible.
        if (seq.Loops)
            DrawSpan(dl, TickX(seq.LoopStart), TickX(seq.LoopEnd), loopBarY, rulerMax.Y, lanesH,
                gridPos.X, gridW, AcGameLoop, "game loop",
                $"The song declares this itself, and the game repeats it here rather than\n" +
                $"from the top: {FormatTicks(seq.LoopStart)} back round from {FormatTicks(seq.LoopEnd)}.",
                hovered, mouse);
        if (_musicLoopA >= 0 && _musicLoopB > _musicLoopA)
            DrawSpan(dl, TickX(_musicLoopA), TickX(_musicLoopB), loopBarY, rulerMax.Y, lanesH,
                gridPos.X, gridW, AcMusic, "A - B",
                "Your own loop range. It overrides the song's while it is set.", hovered, mouse);

        // --- playhead, and the scrub target while a drag is in flight ---
        if (isThis)
        {
            float px = TickX(playhead);
            if (px >= gridPos.X - 1 && px <= gridPos.X + gridW + 1)
            {
                dl.AddLine(new Vector2(px, rulerMin.Y), new Vector2(px, rulerMax.Y + lanesH),
                    Gfx.Rgba(255, 255, 255, 210), 1.4f);
                dl.AddTriangleFilled(new Vector2(px - 5, rulerMin.Y), new Vector2(px + 5, rulerMin.Y),
                    new Vector2(px, rulerMin.Y + 7), Gfx.Rgba(255, 255, 255, 230));
            }
        }
        if (_musicScrubTick >= 0f)
        {
            float sx2 = TickX(_musicScrubTick);
            dl.AddLine(new Vector2(sx2, rulerMin.Y), new Vector2(sx2, rulerMax.Y + lanesH),
                Alpha(AcMusic, 190), 1.2f);
            // In the time scale, not the loop bar above it, where the brackets live.
            ClipText(dl, new Vector2(sx2 + 4, scaleY + 2f), 90f, Shade(AcMusic, 1.15f),
                FormatTicks((uint)_musicScrubTick));
        }

        Trace($"    notes drawn {shown}, collapsed {clipped}");

        // --- overview strip: the whole song, with the viewport marked ---
        // Only worth its cost once the lanes above are showing less than the whole song.
        bool zoomedIn = visibleTicks < seq.Duration * 0.98f;
        DrawTimelineOverview(dl, ovMin, gridW, seq, playhead, isThis, visibleTicks, zoomedIn,
            inOverview || _musicDragOverview, laneRows);
        Trace("    overview done");

        if (clipped > 0 && _laneH > 20f)
            ClipText(dl, new Vector2(gridPos.X + 6f, rulerMax.Y + 3f), gridW - 12f, Alpha(UiFaint, 190),
                $"{shown:n0} of {shown + clipped:n0} notes drawn -- zoom in to separate the rest");

        Trace("    labels done");
        if (inOverview && !_musicDragOverview)
            ImGui.SetTooltip("Drag to scroll the timeline.\nClick anywhere on the strip to jump there.");
        else if (hovered && !_musicDragRuler && !_musicDragLoop && !_musicDragOverview
                 && mouse.X > gridPos.X)
        {
            double t = XTick(mouse.X);
            if (t >= 0 && t <= seq.Duration)
                ImGui.SetTooltip($"{FormatTicks((uint)t)}   ·   tick {(int)t}\n" +
                                 "click to seek   ·   ctrl+drag sets an A-B loop   ·   ctrl+wheel zooms");
        }
    }

    /// <summary>
    /// A marked span of the timeline: a solid bracket in the ruler's loop bar carrying the
    /// label, posts down both ends, and a wash over the lanes. The label lives in the loop
    /// bar and not in the time scale below it, where it used to land on top of the numbers.
    /// </summary>
    private void DrawSpan(ImDrawListPtr dl, float x0, float x1, float barY, float lanesTop,
        float lanesH, float clipX, float clipW, uint accent, string label,
        string tip, bool areaHovered, Vector2 mouse)
    {
        float a = Math.Max(x0, clipX), b = Math.Min(x1, clipX + clipW);
        if (b <= a) return;

        var bar0 = new Vector2(a, barY + 2f);
        var bar1 = new Vector2(b, barY + LoopBarH - 2f);
        bool hot = areaHovered &&
            mouse.X >= bar0.X && mouse.X <= bar1.X && mouse.Y >= bar0.Y && mouse.Y <= bar1.Y;

        // Everything here is deliberately faint. This is context, not content -- the notes are
        // what the eye is meant to rest on -- so the wash is barely a tint, the posts are thin,
        // and the bracket only comes up when the pointer is actually on it.
        dl.AddRectFilled(new Vector2(a, lanesTop), new Vector2(b, lanesTop + lanesH), Alpha(accent, 12));
        if (x0 >= clipX)
            dl.AddRectFilled(new Vector2(x0, barY), new Vector2(x0 + 1f, lanesTop + lanesH), Alpha(accent, 90));
        if (x1 <= clipX + clipW)
            dl.AddRectFilled(new Vector2(x1 - 1f, barY), new Vector2(x1, lanesTop + lanesH), Alpha(accent, 90));

        dl.AddRectFilled(bar0, bar1, Alpha(accent, hot ? (byte)90 : (byte)45), 3f);
        dl.AddRect(bar0, bar1, Alpha(accent, hot ? (byte)170 : (byte)105), 3f);
        if (hot && tip.Length > 0) ImGui.SetTooltip(tip);

        // Ordinary text, at the font's own size: the atlas is a bitmap, so anything scaled
        // off it comes out soft.
        var sz = ImGui.CalcTextSize(label);
        if (sz.X + 12f > bar1.X - bar0.X) return;      // too narrow a span to letter
        dl.AddText(new Vector2(bar0.X + 6f, (bar0.Y + bar1.Y) * 0.5f - sz.Y * 0.5f),
            Alpha(Shade(accent, 1.05f), hot ? (byte)255 : (byte)190), label);
    }

    private void DrawLaneHeader(ImDrawListPtr dl, Vector2 at, int lane, MidiSequence seq,
        bool audible, bool areaHovered, Vector2 mouse)
    {
        var player = _audio!.Player!;
        uint col = LaneColors[lane % LaneColors.Length];
        var mn = at + new Vector2(2, 1);
        var mx = at + new Vector2(LaneHeaderW - 4, _laneH - 2);
        dl.AddRectFilled(mn, mx, Gfx.Rgba(24, 27, 36), 4f);
        dl.AddRectFilled(mn, new Vector2(mn.X + 3, mx.Y), audible ? col : Alpha(col, 70), 2f);

        float lh = ImGui.GetTextLineHeight();
        int notes = seq.LaneNotes[lane];
        // A percussion lane carries no program change -- the drum channel is the instrument.
        string inst = seq.LanePrograms[lane].Count > 0 ? GeneralMidi.Name(seq.LanePrograms[lane][0])
            : seq.LaneChannel[lane] == PercussionChannel ? "Percussion"
            : "-";
        ClipText(dl, new Vector2(mn.X + 8, mn.Y + 2), LaneHeaderW - 62f,
            audible ? UiText : UiFaint, $"CH {lane + 1}");
        if (_laneH > 26f)
            ClipText(dl, new Vector2(mn.X + 8, mn.Y + 2 + lh), LaneHeaderW - 16f, UiFaint,
                notes > 0 ? inst : "silent");

        float bw = 20f, bh = Math.Min(16f, _laneH - 6f);
        LaneKey(dl, new Vector2(mx.X - bw * 2 - 6, mn.Y + 2), bw, bh, "M", player.IsMuted(lane),
            Gfx.Rgba(250, 140, 150), areaHovered, mouse,
            $"mute channel {lane + 1}", () => player.SetMute(lane, !player.IsMuted(lane)));
        LaneKey(dl, new Vector2(mx.X - bw - 3, mn.Y + 2), bw, bh, "S", player.IsSolo(lane),
            AcGo, areaHovered, mouse,
            $"solo channel {lane + 1}", () => player.SetSolo(lane, !player.IsSolo(lane)));
    }

    /// <summary>
    /// A mute/solo key on a lane header. Hit-tested against the mouse by hand rather than
    /// submitted as a widget: the whole timeline is already one InvisibleButton, and adding
    /// eighteen more items inside the drawing pass -- by moving the layout cursor backwards
    /// and putting it back -- is what used to bring the window down at EndChild.
    /// </summary>
    private static void LaneKey(ImDrawListPtr dl, Vector2 at, float w, float h, string label,
        bool on, uint accent, bool areaHovered, Vector2 mouse, string tip, Action click)
    {
        var q = at + new Vector2(w, h);
        bool hot = areaHovered && mouse.X >= at.X && mouse.X <= q.X && mouse.Y >= at.Y && mouse.Y <= q.Y;
        if (hot)
        {
            ImGui.SetTooltip(tip);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) click();
        }

        dl.AddRectFilled(at, q, on ? Alpha(accent, 210) : hot ? Gfx.Rgba(56, 62, 78) : Gfx.Rgba(38, 42, 54), 3f);
        var sz = ImGui.CalcTextSize(label);
        dl.AddText(at + new Vector2((w - sz.X) * 0.5f, (h - sz.Y) * 0.5f),
            on ? Gfx.Rgba(16, 18, 24) : UiDim, label);
    }

    private void DrawTimelineOverview(ImDrawListPtr dl, Vector2 at, float w, MidiSequence seq,
        double playhead, bool isThis, float visibleTicks, bool withNotes, bool hot, int laneRows)
    {
        var mn = at;
        var mx = at + new Vector2(w, OverviewH - 6f);
        dl.AddRectFilled(mn, mx, Gfx.Rgba(16, 18, 25), 3f);
        if (hot) dl.AddRect(mn, mx, Alpha(AcMusic, 70), 3f);
        if (seq.Duration == 0) return;
        if (!withNotes)
        {
            ClipText(dl, new Vector2(mn.X + 6f, mn.Y + 2f), w - 12f, Alpha(UiFaint, 160),
                "the whole song is on screen -- ctrl+wheel to zoom in");
            return;
        }

        // The whole song at a few hundred pixels: most notes land on a pixel column another
        // note already claimed, so one per column per lane is all that can be seen -- and all
        // that is drawn, for the same 16-bit index-buffer reason as the lanes above.
        // The whole song at a few hundred pixels: one bar per note still, but notes landing on
        // a column a bar already covers add nothing that can be seen, so they are skipped.
        // Merging them instead would erase the repeated-note structure, which is exactly what
        // this strip is meant to show the shape of.
        float sx = w / seq.Duration;
        Span<float> covered = stackalloc float[MidiSequence.LaneCount];
        for (int i = 0; i < MidiSequence.LaneCount; i++) covered[i] = float.NegativeInfinity;
        int drawn = 0;
        foreach (var n in seq.Notes)
        {
            int lane = n.Lane;
            if ((uint)lane >= MidiSequence.LaneCount) continue;
            int slot = _laneSlot[lane];
            if (slot < 0) continue;                       // a channel the lanes above leave out
            float x = mn.X + n.Start * sx;
            if (x < covered[lane]) continue;
            float wNote = n.Restruck ? 1f : Math.Max(1f, n.Length * sx - 0.5f);
            covered[lane] = x + wNote;
            if (++drawn > OverviewDrawBudget) break;
            float y = mx.Y - 2f - (slot + 0.5f) / laneRows * (OverviewH - 10f);
            dl.AddRectFilled(new Vector2(x, y), new Vector2(x + wNote, y + 1.6f),
                Alpha(LaneColors[lane % LaneColors.Length], 190));
        }

        // Where the game's own loop sits in the whole song -- a thin bar along the bottom, so
        // it is answerable at a glance even when the loop point is scrolled off the side.
        if (seq.Loops)
        {
            float lx0 = mn.X + seq.LoopStart * sx;
            float lx1 = mn.X + Math.Min(seq.LoopEnd, seq.Duration) * sx;
            dl.AddRectFilled(new Vector2(lx0, mx.Y - 2f), new Vector2(lx1, mx.Y), Alpha(AcGameLoop, 110), 1f);
        }

        // The viewport box, which is also the grab handle: a filled slab rather than an
        // outline, so it reads as something to take hold of.
        float vx0 = mn.X + _musicScrollTicks * sx;
        float vx1 = mn.X + Math.Min(seq.Duration, _musicScrollTicks + visibleTicks) * sx;
        if (vx1 - vx0 < 6f) vx1 = vx0 + 6f;      // always wide enough to catch
        var v0 = new Vector2(vx0, mn.Y);
        var v1 = new Vector2(vx1, mx.Y);
        dl.AddRectFilled(v0, v1, Alpha(AcMusic, hot ? (byte)46 : (byte)26), 2f);
        dl.AddRect(v0, v1, Alpha(AcMusic, hot ? (byte)235 : (byte)165), 2f, 0, 1.2f);

        // Three grip lines down the middle, the way a handle is drawn everywhere.
        float cx = (vx0 + vx1) * 0.5f, gy0 = mn.Y + 4f, gy1 = mx.Y - 4f;
        if (vx1 - vx0 > 16f && gy1 > gy0)
            for (int g = -1; g <= 1; g++)
                dl.AddRectFilled(new Vector2(cx + g * 3f - 0.5f, gy0), new Vector2(cx + g * 3f + 0.5f, gy1),
                    Alpha(AcMusic, hot ? (byte)210 : (byte)120));
        if (isThis)
        {
            float px = mn.X + (float)playhead * sx;
            dl.AddLine(new Vector2(px, mn.Y), new Vector2(px, mx.Y), Gfx.Rgba(255, 255, 255, 200), 1.2f);
        }
    }

    // =====================================================================
    // Lower pane
    // =====================================================================

    private void DrawMusicLowerPane(MusicTrack track, MidiSequence? seq, LdsSong? lds)
    {
        SegBar("##mustab", ref _musicUseTab, AcMusic, 330f,
            ("Where it's used", "Every level, cutscene and menu that plays this song."),
            ("Channels", "What each of the nine Loudness channels plays."),
            ("Song data", "How the LDS file itself is put together."));

        ImGui.Dummy(new Vector2(0, 3f));
        ImGui.BeginChild("muslower", new Vector2(0, 0));
        switch (_musicUseTab)
        {
            case 0: DrawSongUses(); break;
            case 1: DrawSongChannels(seq); break;
            default: DrawSongData(track, seq, lds); break;
        }
        ImGui.EndChild();
    }

    private void DrawSongUses()
    {
        var uses = Usage?.Song(_musicSelected) ?? Array.Empty<AudioUse>();
        if (uses.Count == 0)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorOf(UiFaint),
                "Nothing in the game plays this one. It is reachable only from the jukebox, " +
                "the debug menu, and the random-music cheat.");
            ImGui.PopTextWrapPos();
            return;
        }

        UseKind? last = null;
        for (int row = 0; row < uses.Count; row++)
        {
            var u = uses[row];
            if (last != u.Kind)
            {
                last = u.Kind;
                UiSection(KindLabel(u.Kind), KindColor(u.Kind),
                    uses.Count(x => x.Kind == u.Kind).ToString());
            }

            const float rowH = 28f;
            bool jumpable = u.Kind is UseKind.LevelStart or UseKind.LevelEvent && u.LevelFile > 0;
            // Keyed on the row, not the use: several engine rows share one song and would
            // otherwise collide on a single ImGui id.
            var box = UiRow($"##use{row}", false, KindColor(u.Kind), rowH);
            if (box.Clicked && jumpable) JumpToLevelAt(u.Episode, u.LevelFile, u.Time);
            if (box.Hovered && jumpable)
                ImGui.SetTooltip(u.Time > 0
                    ? "open this level and seek playback to the moment it switches"
                    : "open this level");

            string trail = u.Time > 0 ? $"t={u.Time}" : "";
            RowText(box, 10f, u.Where, u.Detail, KindColor(u.Kind), false, TrailRoom(trail) + 8f);
            if (trail.Length > 0) RowTrail(box, trail, Shade(KindColor(u.Kind), 1.1f));
        }
    }

    private static string KindLabel(UseKind k) => k switch
    {
        UseKind.LevelStart => "levels that open on it",
        UseKind.LevelEvent => "levels that switch to it",
        UseKind.Cutscene => "cutscenes",
        UseKind.ShopMusic => "outpost music",
        UseKind.TextWindow => "announcer lines",
        UseKind.Weapon => "weapons",
        _ => "the engine itself",
    };

    private static uint KindColor(UseKind k) => k switch
    {
        UseKind.LevelStart => AcRoutes,
        UseKind.LevelEvent => AcSim,
        UseKind.Cutscene => AcDisplay,
        UseKind.ShopMusic => AcBuild,
        UseKind.TextWindow => AcPlayer,
        UseKind.Weapon => AcEnemy,
        _ => AcStatus,
    };

    /// <summary>Select a level in the browser and, if the use has a time, seek playback to it.</summary>
    private void JumpToLevelAt(int episode, int fileNum, int time)
    {
        if (_gd == null) return;
        int epIdx = _gd.Episodes.FindIndex(e => e.Number == episode);
        if (epIdx < 0) return;
        SelectLevelFile(epIdx, fileNum);
        if (time > 0) _pendingJump = new MapJump((ushort)time, Array.Empty<int>());
    }

    private void DrawSongChannels(MidiSequence? seq)
    {
        if (seq == null) { ImGui.TextDisabled("No note data for this song."); return; }
        var player = _audio!.Player!;

        for (int lane = 0; lane < MidiSequence.LaneCount; lane++)
        {
            int notes = seq.LaneNotes[lane];
            uint col = LaneColors[lane % LaneColors.Length];
            string inst = seq.LanePrograms[lane].Count > 0
                ? string.Join(", ", seq.LanePrograms[lane].Take(3).Select(GeneralMidi.Name))
                  + (seq.LanePrograms[lane].Count > 3 ? $" (+{seq.LanePrograms[lane].Count - 3})" : "")
                : seq.LaneChannel[lane] == PercussionChannel ? "Percussion (the GM drum kit)"
                : "-";

            var box = UiRow($"##chn{lane}", false, col, 30f);
            if (box.Clicked) player.SetMute(lane, !player.IsMuted(lane));
            if (box.Hovered) ImGui.SetTooltip("click to mute or unmute this channel");
            string trail = notes > 0 ? $"{notes:n0}" : "-";
            RowText(box, 10f, $"Channel {lane + 1}" + (player.IsMuted(lane) ? "   (muted)" : ""),
                notes > 0 ? inst : "never used by this song", col, false, TrailRoom(trail) + 8f);
            RowTrail(box, trail, Shade(col, 1.1f));
        }
    }

    private void DrawSongData(MusicTrack track, MidiSequence? seq, LdsSong? lds)
    {
        KV("song number", $"{track.Index + 1} of {Bank?.Tracks.Length ?? 0}");
        KV("offset in music.mus", $"0x{track.Offset:X}");
        KV("size", $"{track.Raw.Length:n0} bytes");
        if (lds != null)
        {
            KV("mode", lds.Mode.ToString());
            KV("speed / tempo", $"{lds.Speed} / {lds.Tempo}");
            KV("pattern length", lds.PattLen.ToString());
            KV("order list", $"{lds.NumPosi} positions");
            KV("patterns used", lds.UsedPatternCount().ToString());
            KV("instruments", lds.Patches.Length.ToString());
            KV("channel delays", string.Join(" ", lds.ChanDelay));
        }
        if (seq != null)
        {
            KV("length", $"{FormatTicks(seq.Duration)} ({seq.Duration} ticks)");
            KV("notes", seq.Notes.Length.ToString("n0"));
            KV("key range", $"{GeneralMidi.NoteName(seq.LowKey)} - {GeneralMidi.NoteName(seq.HighKey)}");
            KV("loop", seq.Loops
                ? $"{FormatTicks(seq.LoopStart)} to {FormatTicks(seq.LoopEnd)}  (the song's own)"
                : "plays once -- a jingle, not a level track");
            KV("midi events", seq.Events.Length.ToString("n0"));
        }
    }

    // =====================================================================
    // The AUDIO section of the playback HUD
    // =====================================================================

    private string AudioBadge()
    {
        if (_audio is not { IsOpen: true }) return "off";
        if (!_audioEnabled) return "muted";
        string dev = _musicDevice switch
        {
            MusicDevice.FluidSynth => "sf2", MusicDevice.NativeMidi => "midi", _ => "opl",
        };
        return _musicOwner == 1 && _levelSongPlaying >= 0
            ? $"{dev} · {Bank?.TitleOf(_levelSongPlaying) ?? ""}"
            : dev;
    }

    private void DrawAudioSection()
    {
        if (_audio is not { IsOpen: true })
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorOf(AcEnemy),
                _audioProblem.Length > 0 ? _audioProblem : "No audio device.");
            ImGui.PopTextWrapPos();
            if (UiButton("try again", AcMusic, "Re-open the audio device", HudW) && _gd != null)
                InitAudio(_dataDir);
            return;
        }

        float w = (HudW - 5f) / 2f;
        if (Chip("sound", _audioEnabled, AcMusic, w,
                "Everything: the level's music and the sounds the simulation fires."))
            _audioEnabled = !_audioEnabled;
        ImGui.SameLine(0, 5);
        if (Chip("music", _gameMusic, AcMusic, w,
                "Play the song the level's script names, and follow its event-35 changes."))
            _gameMusic = !_gameMusic;

        if (Chip("effects", _gameSounds, AcMusic, w,
                "Explosions, enemy fire, the announcer -- drained from the engine's own\n" +
                "eight-slot sound queue once per live tick."))
            _gameSounds = !_gameSounds;
        ImGui.SameLine(0, 5);
        if (Chip("music player", _showMusic, AcMusic, w, "Open the music browser."))
            _showMusic = !_showMusic;

        ImGui.Dummy(new Vector2(0, 2f));
        ImGui.SetNextItemWidth(HudW - 74f);
        int mv = _musicVolume;
        if (ImGui.SliderInt("music##hudmv", ref mv, 0, 255)) _musicVolume = mv;
        ImGui.SetNextItemWidth(HudW - 74f);
        int fv = _fxVolume;
        if (ImGui.SliderInt("effects##hudfv", ref fv, 0, 255)) _fxVolume = fv;

        int dev = (int)_musicDevice;
        if (SegBar("##hudmusdev", ref dev, AcMusic, HudW,
                ("OPL3", "The emulated AdLib chip."),
                ("SoundFont", "FluidSynth, if libfluidsynth-3.dll is available."),
                ("Native", "The OS synthesizer.")))
            SetMusicDevice((MusicDevice)dev);

        string now = _musicOwner switch
        {
            1 when _levelSongPlaying >= 0 =>
                $"level: {Bank?.TitleOf(_levelSongPlaying)}",
            2 => $"music window: {_audio.Player?.Track?.Title ?? ""}",
            _ => "nothing playing",
        };
        ImGui.TextColored(ColorOf(UiFaint), now);

        // The device's own account of itself. If "buffers" is not climbing, nothing this
        // window does can be heard and the fault is below us, not in the mixer.
        string line = _audio.StatusLine();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(ColorOf(line.Contains("!!") ? AcEnemy : UiFaint), line);
        ImGui.PopTextWrapPos();

        // Output meter: the peak of the last buffer the mixer actually wrote, so "is anything
        // coming out" is answerable without unplugging the headphones.
        var dl = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        float lvl = _audio.Level;
        MeterBar(dl, at, at + new Vector2(HudW, 5f), lvl, lvl > 0.98f ? AcEnemy : AcGo);
        ImGui.Dummy(new Vector2(HudW, 7f));

        // ... and the device's own account of itself under it. A buffer count that is not
        // climbing means nothing above this line can be heard.
        string devLine = _audio.StatusLine();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(ColorOf(devLine.Contains("!!") ? AcEnemy : UiFaint), devLine);
        ImGui.PopTextWrapPos();
    }

    /// <summary>Loudness ticks as m:ss.t, at the rate the OPL player runs them.</summary>
    private static string FormatTicks(uint ticks)
    {
        double s = ticks / MusicBank.LdsUpdateRate;
        return $"{(int)(s / 60)}:{s % 60:00.0}";
    }
}
