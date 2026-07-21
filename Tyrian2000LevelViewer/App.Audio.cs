using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;
using T2LV.Tyrian.Audio;

namespace T2LV;

/// <summary>
/// The viewer's audio: one device, one mixer, shared by the level playback and the two
/// reference browsers. Playback is the interesting half -- the simulation already runs
/// the engine's logic tick for tick, so it can fill the engine's own eight-slot sound
/// queue at exactly the moments the game does, and this drains it once per live tick.
///
/// Two rules keep it from turning into noise. Sound is only emitted from ticks that were
/// stepped live and forward (a scrub re-simulates up to 120 ticks as fast as it can, and
/// every one of those would otherwise fire), and whichever of the level and the music
/// window most recently started a song owns the music until the other takes it back.
/// </summary>
public sealed unsafe partial class App
{
    private AudioEngine? _audio;
    private AudioUsageIndex? _usage;

    /// <summary>Why audio is not working, if it is not. Survives the status line being reset.</summary>
    private string _audioProblem = "";

    private bool _audioEnabled = true;
    private int _musicVolume = 191, _fxVolume = 191;   // the engine's own 0..255 scale
    private bool _gameMusic = true;
    private bool _gameSounds = true;
    private MusicDevice _musicDevice = MusicDevice.Opl;
    private string _soundFont = "";

    /// <summary>Who last started a song: 0 nobody, 1 the level, 2 the music window.</summary>
    private int _musicOwner;
    private bool _wasPlaying;              // for the rising edge of level playback
    private int _levelSongPlaying = -1;    // 0-based song the level put on
    private int _audioLevelKey = -1;       // which level the above belongs to

    /// <summary>True when the device is up and the user has not switched audio off.</summary>
    private bool AudioOn => _audioEnabled && _audio is { IsOpen: true };

    /// <summary>The song browser's cross-reference index, built on first use.</summary>
    private AudioUsageIndex? Usage
    {
        get
        {
            if (_usage == null && _gd != null)
                try { _usage = AudioUsageIndex.Build(_gd); } catch { _usage = null; }
            return _usage;
        }
    }

    /// <summary>Opens the device against a data folder. Silently does nothing if it will not open.</summary>
    private void InitAudio(string dataDir)
    {
        ShutdownAudio();
        _audio = new AudioEngine();
        if (!_audio.Open(dataDir))
        {
            // Sticky: LoadData sets _status right after this, so a transient message here
            // would never be seen. The audio windows and the HUD read this instead.
            _audioProblem = "Audio unavailable: " + _audio.Error;
            return;
        }
        _audioProblem = "";
        _audio.MusicVolume = _musicVolume;
        _audio.SampleVolume = _fxVolume;
        _audio.MusicDisabled = !_audioEnabled;
        _audio.SamplesDisabled = !_audioEnabled;
        if (_xmasVoices && !_audio.SetXmasVoices(dataDir, true)) _xmasVoices = false;

        if (_musicDevice != MusicDevice.Opl && _audio.Player != null)
        {
            _audio.Player.SetDevice(_musicDevice, ResolveSoundFont());
            // A device that would not start falls back to OPL; follow it, so the picker
            // does not claim a synthesizer that is not running.
            _musicDevice = _audio.Player.Device;
        }
    }

    private void ShutdownAudio()
    {
        _audio?.Dispose();
        _audio = null;
        _musicOwner = 0;
        _levelSongPlaying = -1;
        _audioLevelKey = -1;
    }

    /// <summary>The configured SoundFont, or the newest one lying in the data folder.</summary>
    private string ResolveSoundFont()
    {
        if (_soundFont.Length > 0 && File.Exists(_soundFont)) return _soundFont;
        var found = FluidSynth.FindSoundFonts(_dataDir, Directory.GetParent(_dataDir)?.FullName,
            AppContext.BaseDirectory);
        return found.Count > 0 ? found[0] : "";
    }

    /// <summary>The 0-based song the script gives a level, or -1 if the script does not say.</summary>
    private int LevelSong(EpisodeInfo? ep, int fileNum)
    {
        if (ep == null) return -1;
        foreach (var e in ep.ScriptLevels)
            if (e.LvlFileNum == fileNum && e.Song > 0)
                return e.Song - 1;
        return -1;
    }

    /// <summary>
    /// The song that should be playing at a tick: the level's own, overridden by every
    /// event 35 the run has actually executed before then. Scrubbing into the second half
    /// of TORM should land on Deli Shop Quartet, not on the song the level opened with.
    /// </summary>
    private int SongAtTick(int tick)
    {
        int song = LevelSong(CurEpisode, _levelFileNum);
        var pb = _playback;
        if (pb == null || _level == null) return song;
        foreach (var e in pb.Events)
        {
            if (e.Tick > tick) break;
            if (e.Type != 35) continue;
            if ((uint)e.Index >= (uint)_level.Events.Length) continue;
            int dat = _level.Events[e.Index].Dat;
            if (dat > 0) song = dat - 1;
        }
        return song;
    }

    /// <summary>Starts a song on behalf of the level playback.</summary>
    private void PlayLevelSong(int songIndex)
    {
        if (!AudioOn || !_gameMusic || _audio?.Player == null) return;
        var track = _audio.Music[songIndex];
        if (track == null) return;
        MusicPlayer.Prepare(track);      // decode off the mixer's lock
        _audio.Player.Loop = true;
        _audio.Player.LoopRange = (-1, -1);
        _audio.Player.Play(track);
        _levelSongPlaying = songIndex;
        _musicOwner = 1;
    }

    /// <summary>
    /// Volume and the master switch, every frame. Separate from <see cref="UpdateGameAudio"/>
    /// because the music window plays with no level loaded at all, and its volume slider has
    /// to reach the mixer either way.
    /// </summary>
    private void SyncAudioVolumes()
    {
        if (_audio == null) return;
        _audio.MusicVolume = _musicVolume;
        _audio.SampleVolume = _fxVolume;
        _audio.MusicDisabled = !_audioEnabled;
        _audio.SamplesDisabled = !_audioEnabled;
        // The OS synth is outside our mixer, so the fader has to be sent to it as controller
        // data; muting has to mean silence there too.
        _audio.Player?.SetMidiVolume(_audioEnabled ? _musicVolume : 0);
        _audio.Player?.PumpDeferred();     // any seek the mixer handed back to this thread
        PumpAudioExport();

        // Leaving playback stops the level's song: UpdateGameAudio is only called from the
        // playback update, so without this the music would run on with nothing driving it.
        if (!_playbackMode && _musicOwner == 1)
        {
            _audio.Player?.Stop();
            _audio.StopAllSounds();
            _musicOwner = 0;
            _levelSongPlaying = -1;
            _wasPlaying = false;
        }
    }

    /// <summary>
    /// Per-frame audio housekeeping, called from the playback update. Handles taking the
    /// music back when the user presses play, following a level change, and the fade the
    /// level's own event 34 asks for.
    /// </summary>
    private void UpdateGameAudio()
    {
        if (!AudioOn || _playback == null) { _wasPlaying = false; return; }
        var audio = _audio!;
        var player = audio.Player;

        int levelKey = (CurEpisode?.Number ?? 0) * 1000 + _levelFileNum;
        if (levelKey != _audioLevelKey)
        {
            _audioLevelKey = levelKey;
            _levelSongPlaying = -1;
            if (_musicOwner == 1) player?.Stop();
        }

        // Pressing play takes the music back from the music window; so does a new level.
        // Only on those transitions, not every frame: SongAtTick walks the whole executed-
        // event log, and in the steady state the per-tick handler is already following the
        // event-35 changes as they happen.
        bool rising = _playing && !_wasPlaying;
        _wasPlaying = _playing;
        bool needSong = _gameMusic && _playing && (rising || _musicOwner != 1 || _levelSongPlaying < 0);
        if (needSong)
        {
            int want = SongAtTick(_playback.CurrentTick);
            if (want >= 0) PlayLevelSong(want);
        }
        else if (!_gameMusic && _musicOwner == 1)
        {
            player?.Stop();
            _musicOwner = 0;
            _levelSongPlaying = -1;
        }

        // The engine seeds soundQueue[3] with "Good luck" as the level starts; pressing play
        // at the very top of a run is that same moment.
        if (rising && _gameSounds && _playback.CurrentTick <= 2)
            audio.PlaySound(35, 3, 4);

        // Pause the level's music while playback is paused, which the game never has to do.
        if (_musicOwner == 1 && player != null)
        {
            if (!_playing && player.IsPlaying) player.Pause();
            else if (_playing && player.IsPaused) player.Resume();
        }
    }

    /// <summary>
    /// Drains the simulation's sound queue for one live tick, and acts on the music events
    /// that tick executed. Wired to <see cref="SimPlayback.OnLiveTick"/>, so it only ever
    /// runs for ticks that were stepped forward in real time.
    /// </summary>
    private void OnSimAudioTick() => DrainSimAudio(music: true);

    /// <summary>
    /// Drains one tick's worth of intent. <paramref name="music"/> is false for the
    /// click-to-damage path, which calls this between ticks: SongChange and MusicFade stay
    /// latched until the next <c>Tick</c> clears them, so acting on them there would restart
    /// the song every time the user shot something after an event 35.
    /// </summary>
    private void DrainSimAudio(bool music)
    {
        if (!AudioOn || _playback == null) return;
        var sim = _playback.Sim;

        if (_gameSounds) _audio!.PlayQueue(sim.SoundQueue);

        if (music && _gameMusic && _musicOwner == 1 && _audio?.Player != null)
        {
            if (sim.SongChange > 0) PlayLevelSong(sim.SongChange - 1);
            else if (sim.MusicFade) _audio.Player.Fade(1);
        }
    }

    /// <summary>
    /// A transport key for the audio browsers: the same drawn marks the playback bar uses
    /// (<see cref="PaintGlyph"/>), but in the browser's own slab-and-accent language rather
    /// than the playback bar's fixed blue, and with a lit state for the toggles among them.
    /// </summary>
    private static bool GlyphButton(string id, Glyph glyph, uint accent, string tip,
        float width = 0f, bool on = false, bool disabled = false)
    {
        float h = ImGui.GetFrameHeight();
        float w = width > 0f ? width : h + 10f;
        var p = ImGui.GetCursorScreenPos();
        // Not BeginDisabled: a key you cannot press should still explain why, as with UiToggle.
        bool hit = ImGui.InvisibleButton($"##gl{id}", new Vector2(w, h)) && !disabled;
        bool hot = !disabled && ImGui.IsItemHovered();
        bool held = !disabled && ImGui.IsItemActive();
        if (hot && tip.Length > 0) ImGui.SetTooltip(tip);

        var dl = ImGui.GetWindowDrawList();
        var q = p + new Vector2(w, h);
        bool lit = on && !disabled;
        if (lit) GradRect(dl, p, q, Shade(accent, 0.46f, 240), Shade(accent, 0.34f, 232), 4f);
        else dl.AddRectFilled(p, q, disabled ? Gfx.Rgba(28, 31, 40) : held ? Shade(accent, 0.55f, 240)
            : hot ? Gfx.Rgba(52, 59, 74) : Gfx.Rgba(36, 41, 52), 4f);
        dl.AddRect(p, q, lit ? Shade(accent, 0.85f, 200)
            : disabled ? Gfx.Rgba(38, 42, 54) : hot ? Shade(accent, 0.75f, 190) : UiLineSoft, 4f);

        uint ink = disabled ? Gfx.Rgba(84, 90, 106)
            : lit ? Gfx.Rgba(16, 18, 24)
            : hot ? Gfx.Rgba(246, 249, 255) : UiText;
        float r = MathF.Max(3f, MathF.Round(h * 0.22f));
        PaintGlyph(dl, (p + q) * 0.5f, r, MathF.Max(1.5f, r * 0.28f), ink,
            disabled || lit ? ink : Shade(accent, 1.15f), glyph);
        return hit;
    }

    // =====================================================================
    // Opening breadcrumbs
    // =====================================================================

    /// <summary>Frames left to trace. Set when an audio window opens; see <see cref="Trace"/>.</summary>
    private int _audioTraceFrames;

    /// <summary>
    /// Append one step to <c>audio-open.log</c> beside the settings file, flushed as it goes.
    /// An ImGui assertion or an access violation takes the process down from native code with
    /// no managed exception, so the frame-level crash log never sees it and the only way to
    /// find out how far a window got is to have written it down. Armed for a couple of frames
    /// when one of these windows opens, and silent after that.
    /// </summary>
    private void Trace(string step)
    {
        if (_audioTraceFrames <= 0) return;
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tyrian2000LevelViewer");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "audio-open.log"),
                $"{DateTime.Now:HH:mm:ss.fff}  {step}\n");
        }
        catch { /* tracing must never be the thing that breaks */ }
    }

    /// <summary>Arms <see cref="Trace"/> for the next couple of frames.</summary>
    private void ArmTrace(string what)
    {
        _audioTraceFrames = 2;
        Trace($"--- {what} ---");
    }

    /// <summary>True when the music or sound window held the keyboard focus last frame.</summary>
    private bool _audioWindowFocused;
    private bool _musicFocused, _soundFocused;

    /// <summary>
    /// The transport keys, while one of the two audio browsers has the focus: space plays
    /// or pauses, the arrows step, and Home goes back to the start.
    /// </summary>
    private void HandleAudioWindowKeys()
    {
        if (_soundFocused && !_musicFocused)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Space) && _audio is { IsOpen: true })
                PlayPreview(_soundSelected + 1);
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true))
                _soundSelected = Math.Min(_soundSelected + 1, SoundBank.SoundCount - 1);
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true))
                _soundSelected = Math.Max(_soundSelected - 1, 0);
            return;
        }

        // Escape steps back out of a channel's piano roll to the all-channels lanes.
        if (_pianoLane >= 0 && ImGui.IsKeyPressed(ImGuiKey.Escape)) { _pianoLane = -1; return; }

        var player = _audio?.Player;
        if (player == null) return;
        var track = _audio!.Music[_musicSelected];
        if (track != null && ImGui.IsKeyPressed(ImGuiKey.Space)) ToggleTrack(track);
        if (ImGui.IsKeyPressed(ImGuiKey.Home)) player.Seek(0);
        int step = ImGui.GetIO().KeyShift ? 350 : 70;    // 5 s or 1 s of Loudness ticks
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true)) player.Seek(player.Tick + step);
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true)) player.Seek(player.Tick - step);
    }

    /// <summary>Reads the audio settings on startup.</summary>
    private void LoadAudioSettings(AppSettings s)
    {
        _audioEnabled = s.AudioEnabled ?? true;
        _musicVolume = Math.Clamp(s.MusicVolume, 0, 255);
        _fxVolume = Math.Clamp(s.FxVolume, 0, 255);
        _gameMusic = s.GameMusic ?? true;
        _gameSounds = s.GameSounds ?? true;
        _musicDevice = (MusicDevice)Math.Clamp(s.MusicDevice, 0, 2);
        _soundFont = s.SoundFont ?? "";
        _showMusic = s.ShowMusic;
        _showSounds = s.ShowSounds;
        if (s.MusicListWidth > 100f) _musicListW = s.MusicListWidth;
        if (s.SoundListWidth > 100f) _soundListW = s.SoundListWidth;
        _musicZoom = s.MusicZoom;
        _xmasVoices = s.XmasVoices;
        _laneZoom = s.MusicLaneHeight > 0 ? Math.Clamp(s.MusicLaneHeight, MinLaneH, MaxLaneH) : 0f;
        _musicSelected = Math.Max(0, s.MusicSelected);
        _soundSelected = Math.Clamp(s.SoundSelected, 0, SoundBank.SoundCount - 1);
    }

    /// <summary>Writes the audio settings back out.</summary>
    private void SaveAudioSettings(AppSettings s)
    {
        s.AudioEnabled = _audioEnabled;
        s.MusicVolume = _musicVolume;
        s.FxVolume = _fxVolume;
        s.GameMusic = _gameMusic;
        s.GameSounds = _gameSounds;
        s.MusicDevice = (int)_musicDevice;
        s.SoundFont = _soundFont;
        s.ShowMusic = _showMusic;
        s.ShowSounds = _showSounds;
        s.MusicListWidth = _musicListW;
        s.SoundListWidth = _soundListW;
        s.MusicZoom = _musicZoom;
        s.XmasVoices = _xmasVoices;
        s.MusicLaneHeight = _laneZoom;
        s.MusicSelected = _musicSelected;
        s.SoundSelected = _soundSelected;
    }
}
