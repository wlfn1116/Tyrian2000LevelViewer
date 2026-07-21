using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian.Audio;

namespace T2A;

/// <summary>
/// Saving audio out of the two browsers: a song as WAVE or as a Standard MIDI File, a sound
/// effect as WAVE.
///
/// The picker and the render both run off the main thread -- the common file dialog pumps its
/// own message loop, and rendering a six-minute song through the OPL is seconds of work. The
/// frame keeps running and the transport keeps playing throughout; the button just reports
/// what it is doing.
/// </summary>
public sealed unsafe partial class App
{
    /// <summary>What an export is producing, which decides the extension and the renderer.</summary>
    private enum ExportKind { SongWav, SongMidi, SoundWav }

    /// <summary>What a whole-bank export is producing. One file per entry, into a folder.</summary>
    private enum ExportAllKind { SongsWav, SongsMidi, Sounds }

    private volatile bool _expActive;
    private volatile bool _expDone;
    private volatile string? _expMessage;
    private volatile float _expProgress;
    private volatile string _expLabel = "";

    /// <summary>True while a whole-bank export is running, so its row can offer a way out.</summary>
    private volatile bool _expBatch;

    /// <summary>Which batch is running. The two browsers share one export slot and one set of
    /// progress fields, so this is how only the one that started it reports on it.</summary>
    private ExportAllKind _expBatchKind;

    /// <summary>A batch the music browser started is running (either song kind).</summary>
    private bool SongBatchRunning => _expBatch && _expBatchKind != ExportAllKind.Sounds;

    /// <summary>A batch the sounds browser started is running.</summary>
    private bool SoundBatchRunning => _expBatch && _expBatchKind == ExportAllKind.Sounds;

    /// <summary>Set by the stop button; the batch checks it between entries.</summary>
    private volatile bool _expCancel;

    /// <summary>Where the last audio export was saved, so the next one opens there.</summary>
    private string _audioExportDir = "";

    /// <summary>True while a render or a save dialog is in flight.</summary>
    private bool ExportBusy => _expActive;

    /// <summary>Surfaces a finished export into the status line. Called once a frame.</summary>
    private void PumpAudioExport()
    {
        if (!_expDone) return;
        _expDone = false;
        _expActive = false;
        _expBatch = false;
        _expCancel = false;
        if (_expMessage is { Length: > 0 } m) _status = m;
        _expMessage = null;
    }

    /// <summary>
    /// Starts an export: Save-As first, then the render, both on one background thread. The
    /// dialog needs an STA thread anyway, and doing the render there too keeps the whole
    /// operation off the frame.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void StartAudioExport(ExportKind kind, int index, int passes = 2)
    {
        if (_expActive || _audio is not { IsOpen: true }) return;

        string suggested;
        string label;
        var bank = _audio.Music;
        var sounds = _audio.Sounds;

        switch (kind)
        {
            case ExportKind.SongWav:
            case ExportKind.SongMidi:
            {
                var track = bank[index];
                if (track == null) return;
                string ext = kind == ExportKind.SongWav ? "wav" : "mid";
                suggested = $"{index + 1:00} {Safe(track.Title)}.{ext}";
                label = kind == ExportKind.SongWav ? "Rendering the song..." : "Writing the MIDI...";
                break;
            }
            default:
            {
                var clip = index >= 1 && index <= SoundBank.SoundCount ? sounds.Clips[index - 1] : null;
                if (clip == null) return;
                suggested = $"{clip.Number:00} {Safe(clip.Title)}.wav";
                label = "Writing the sound...";
                break;
            }
        }

        _expActive = true;
        _expDone = false;
        _expProgress = 0f;
        _expLabel = label;

        IntPtr owner = NativeFileDialog.ForegroundWindow();
        string startDir = _audioExportDir.Length > 0 && Directory.Exists(_audioExportDir)
            ? _audioExportDir : DefaultExportDir();
        var device = _musicDevice;
        var fluid = _audio.Player?.Fluid;

        var t = new Thread(() =>
        {
            try
            {
                string? path = NativeFileDialog.SaveFileBlocking(startDir, suggested, owner,
                    kind == ExportKind.SongMidi ? "Export MIDI" : "Export WAVE");
                if (path == null) { _expMessage = null; return; }
                _audioExportDir = Path.GetDirectoryName(path) ?? "";

                switch (kind)
                {
                    case ExportKind.SongMidi:
                    {
                        var song = bank[index]?.Midi;
                        if (song == null) { _expMessage = "That song will not convert to MIDI."; return; }
                        File.WriteAllBytes(path, LdsMidi.SerializeSmf(song));
                        _expMessage = $"Wrote {Path.GetFileName(path)} " +
                                      $"({new FileInfo(path).Length / 1024} KB of MIDI).";
                        break;
                    }
                    case ExportKind.SongWav:
                    {
                        var track = bank[index];
                        var seq = MidiSequence.From(track?.Midi);
                        short[]? pcm = null;
                        int channels = 1;

                        // Through the SoundFont if that is what is selected and it can render;
                        // the OS synth plays outside this process, so it falls back to the OPL.
                        if (device == MusicDevice.FluidSynth && fluid is { IsOpen: true, HasSoundFont: true }
                            && seq != null)
                        {
                            pcm = SongRenderer.RenderFluid(fluid, seq, passes, p => _expProgress = p);
                            channels = 2;
                        }
                        if (pcm == null)
                        {
                            var lds = track?.Lds;
                            if (lds == null) { _expMessage = "That song will not parse."; return; }
                            pcm = SongRenderer.RenderOpl(lds, seq, passes, p => _expProgress = p);
                            channels = 1;
                        }

                        T2A.Util.Wav.Write(path, pcm, SongRenderer.SampleRate, channels);
                        double secs = pcm.Length / (double)(SongRenderer.SampleRate * channels);
                        _expMessage = $"Wrote {Path.GetFileName(path)} " +
                                      $"({secs:0.0} s, {(channels == 2 ? "stereo" : "mono")}, " +
                                      $"{(channels == 2 ? "SoundFont" : "OPL")}).";
                        break;
                    }
                    default:
                    {
                        var clip = sounds.Clips[index - 1];
                        // From the original 8-bit data at its own 11 kHz rate, not the copy the
                        // mixer resampled: that is the clip as the game ships it.
                        var pcm = new short[clip.Raw.Length];
                        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(clip.Raw[i] << 8);
                        T2A.Util.Wav.Write(path, pcm, SoundBank.SourceRate, 1);
                        _expMessage = $"Wrote {Path.GetFileName(path)} " +
                                      $"({clip.Seconds:0.000} s at {SoundBank.SourceRate} Hz).";
                        break;
                    }
                }
            }
            catch (Exception e) { _expMessage = "Export failed: " + e.Message; }
            finally { _expProgress = 1f; _expDone = true; }
        })
        { IsBackground = true, Name = "AudioExport" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>
    /// Starts a whole-bank export: pick a folder, then write one file per entry into it. Same
    /// background STA thread and the same progress fields a single export uses, so the two can
    /// never overlap — there is one renderer and one status line between them.
    ///
    /// The progress meter reads overall, not per song: a 41-song render is minutes of work and
    /// a bar that fills and resets forty-one times says nothing about how long is left. The
    /// current song's own progress is folded into it, which is what makes it move smoothly
    /// rather than in forty-one steps.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void StartAudioExportAll(ExportAllKind kind, int passes = 2)
    {
        if (_expActive || _audio is not { IsOpen: true }) return;

        var bank = _audio.Music;
        var sounds = _audio.Sounds;
        if (kind == ExportAllKind.Sounds ? !sounds.Loaded : !bank.Loaded) return;

        _expActive = true;
        _expDone = false;
        _expBatch = true;
        _expBatchKind = kind;
        _expCancel = false;
        _expProgress = 0f;
        _expLabel = "Choosing a folder...";

        IntPtr owner = NativeFileDialog.ForegroundWindow();
        string startDir = _audioExportDir.Length > 0 && Directory.Exists(_audioExportDir)
            ? _audioExportDir : DefaultExportDir();
        var device = _musicDevice;
        var fluid = _audio.Player?.Fluid;

        var t = new Thread(() =>
        {
            try
            {
                string? dir = NativeFileDialog.PickFolderBlocking(startDir, owner, kind switch
                {
                    ExportAllKind.SongsWav  => "Export every song as WAVE - pick a folder",
                    ExportAllKind.SongsMidi => "Export every song as MIDI - pick a folder",
                    _                       => "Export every sound as WAVE - pick a folder",
                });
                if (dir == null) { _expMessage = null; return; }
                _audioExportDir = dir;

                _expMessage = kind == ExportAllKind.Sounds
                    ? ExportAllSounds(sounds, dir)
                    : ExportAllSongs(bank, dir, kind == ExportAllKind.SongsWav, device, fluid, passes);
            }
            catch (Exception e) { _expMessage = "Export failed: " + e.Message; }
            finally { _expProgress = 1f; _expDone = true; }
        })
        { IsBackground = true, Name = "AudioExportAll" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Every song in music.mus, as WAVE or as Standard MIDI. Returns the summary.</summary>
    private string ExportAllSongs(MusicBank bank, string dir, bool wav, MusicDevice device,
        FluidSynth? fluid, int passes)
    {
        // The OS synth's audio never passes through this process, so it cannot be captured;
        // picking it renders the OPL instead, exactly as the single-song export does.
        bool useFluid = wav && device == MusicDevice.FluidSynth
                            && fluid is { IsOpen: true, HasSoundFont: true };

        int total = bank.Tracks.Length;
        int written = 0, skipped = 0, viaFluid = 0;
        long bytes = 0;

        for (int i = 0; i < total; i++)
        {
            if (_expCancel) break;
            var track = bank[i];
            int done = i;                                   // captured per song, for the meter
            _expProgress = i / (float)total;
            _expLabel = $"{(wav ? "Rendering" : "Writing")} {i + 1}/{total}: {bank.TitleOf(i)}";
            if (track == null) { skipped++; continue; }

            string path = Path.Combine(dir, $"{i + 1:00} {Safe(track.Title)}.{(wav ? "wav" : "mid")}");
            if (!wav)
            {
                var song = track.Midi;
                if (song == null) { skipped++; continue; }
                File.WriteAllBytes(path, LdsMidi.SerializeSmf(song));
            }
            else
            {
                var seq = MidiSequence.From(track.Midi);
                short[]? pcm = null;
                int channels = 1;
                if (useFluid && seq != null)
                {
                    pcm = SongRenderer.RenderFluid(fluid!, seq, passes,
                        p => _expProgress = (done + p) / total);
                    channels = 2;
                }
                if (pcm == null)
                {
                    var lds = track.Lds;
                    if (lds == null) { skipped++; continue; }
                    pcm = SongRenderer.RenderOpl(lds, seq, passes,
                        p => _expProgress = (done + p) / total);
                    channels = 1;
                }
                else viaFluid++;
                T2A.Util.Wav.Write(path, pcm, SongRenderer.SampleRate, channels);
            }
            written++;
            bytes += new FileInfo(path).Length;
        }

        string how = !wav ? "MIDI"
            : viaFluid == written && written > 0 ? "SoundFont"
            : viaFluid == 0 ? "OPL"
            : $"{viaFluid} SoundFont, {written - viaFluid} OPL";
        return $"{(_expCancel ? "Stopped after" : "Wrote")} {written} of {total} song" +
               $"{(written == 1 ? "" : "s")} ({how}, {SizeLabel(bytes)}) to {FolderLabel(dir)}" +
               (skipped > 0 ? $"; {skipped} would not convert." : ".");
    }

    /// <summary>Every clip in both sound banks, as WAVE. Returns the summary.</summary>
    private string ExportAllSounds(SoundBank bank, string dir)
    {
        int total = SoundBank.SoundCount;
        int written = 0, skipped = 0;
        long bytes = 0;

        for (int i = 0; i < total; i++)
        {
            if (_expCancel) break;
            var clip = bank.Clips[i];
            _expProgress = i / (float)total;
            _expLabel = $"Writing {i + 1}/{total}: {clip?.Title ?? "-"}";
            if (clip == null) { skipped++; continue; }

            // From the original 8-bit data at its own 11 kHz rate, not the copy the mixer
            // resampled: that is the clip as the game ships it, same as the single export.
            var pcm = new short[clip.Raw.Length];
            for (int k = 0; k < pcm.Length; k++) pcm[k] = (short)(clip.Raw[k] << 8);
            string path = Path.Combine(dir, $"{clip.Number:00} {Safe(clip.Title)}.wav");
            T2A.Util.Wav.Write(path, pcm, SoundBank.SourceRate, 1);
            written++;
            bytes += new FileInfo(path).Length;
        }

        return $"{(_expCancel ? "Stopped after" : "Wrote")} {written} of {total} sound" +
               $"{(written == 1 ? "" : "s")} ({SoundBank.SourceRate} Hz, {SizeLabel(bytes)}) to " +
               $"{FolderLabel(dir)}" +
               (skipped > 0 ? $"; {skipped} were empty." : ".");
    }

    /// <summary>A batch's total, in whichever unit reads as a number rather than as 0 --
    /// forty-one rendered songs are hundreds of megabytes, forty-one MIDIs are a few hundred
    /// kilobytes.</summary>
    private static string SizeLabel(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (double)(1024 * 1024):0.#} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    /// <summary>The folder's own name for the status line, or its whole path when it has none
    /// of its own (a drive root).</summary>
    private static string FolderLabel(string dir)
    {
        string leaf = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        return leaf.Length > 0 ? leaf : dir;
    }

    /// <summary>Strip what Windows will not take in a file name.</summary>
    private static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name) sb.Append(Array.IndexOf(bad, c) >= 0 ? '-' : c);
        return sb.ToString().Trim();
    }

    /// <summary>
    /// The export controls for a browser's detail pane: the buttons, and while one is running,
    /// what it is doing and how far along.
    /// </summary>
    private void DrawExportRow(uint accent, ExportKind wav, int index, bool withMidi)
    {
        bool windows = OperatingSystem.IsWindows();
        if (UiButton("export WAV", accent,
                withMidi
                    ? "Render the song to a .wav -- through the SoundFont if that is the\n" +
                      "selected voice, otherwise through the emulated OPL."
                    : "Save the clip as a .wav, at the 11 kHz it ships at.",
                92f, ExportBusy || !windows) && windows)
            StartAudioExport(wav, index);

        if (withMidi)
        {
            ImGui.SameLine(0, 5);
            if (UiButton("export MIDI", accent,
                    "Save the converted Standard MIDI File -- the same notes the timeline\n" +
                    "draws, playable in any sequencer.", 96f, ExportBusy || !windows) && windows)
                StartAudioExport(ExportKind.SongMidi, index);
        }

        // A batch export reports on its own row up in the band, where there is room for the
        // song it is on; repeating it here would say the same thing twice.
        if (!ExportBusy || _expBatch) return;
        ImGui.SameLine(0, 10);
        DrawExportProgress(accent, 90f);
    }

    /// <summary>What an export in flight is doing and how far along: the label, then a meter
    /// once there is a fraction worth drawing (a Save-As box in front of it has none).
    ///
    /// The label is clipped to a fixed allowance rather than laid out at its own width: a batch
    /// rewrites it with every entry's title, and a band that measures wider than its window
    /// widens the window (BandEnd) — so an unclipped one would shove the window about for as
    /// long as the export ran.</summary>
    private void DrawExportProgress(uint accent, float barWidth, float labelRoom = 230f)
    {
        ImGui.AlignTextToFramePadding();
        string label = _expLabel;
        var lp = ImGui.GetCursorScreenPos();
        ClipText(ImGui.GetWindowDrawList(),
            new Vector2(lp.X, lp.Y + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) * 0.5f),
            labelRoom, Shade(accent, 1.05f), label);
        ImGui.Dummy(new Vector2(Math.Min(ImGui.CalcTextSize(label).X, labelRoom),
            ImGui.GetFrameHeight()));

        float p = _expProgress;
        if (p is <= 0f or >= 1f) return;
        ImGui.SameLine(0, 8);
        var at = ImGui.GetCursorScreenPos();
        MeterBar(ImGui.GetWindowDrawList(), new Vector2(at.X, at.Y + ImGui.GetFrameHeight() * 0.5f - 3f),
            new Vector2(at.X + barWidth, at.Y + ImGui.GetFrameHeight() * 0.5f + 3f), p, accent);
        ImGui.Dummy(new Vector2(barWidth, ImGui.GetFrameHeight()));
    }

    /// <summary>
    /// A second row on a browser's top band while a whole-bank export runs: which entry it is
    /// on, an overall meter, and the way out. Rendering forty-one songs is minutes of work, so
    /// being able to stop it is part of showing that it is running.
    ///
    /// Drawn as a row of its own, and only while one is running -- the band asks for the second
    /// row's height on the same condition, so idle it is one clean line. The caller says whether
    /// the running batch is its own; the two browsers share one export slot, and the Sounds
    /// window reporting "Rendering 3/41: ASTEROID" would be nonsense.
    /// </summary>
    private void DrawExportAllProgress(uint accent, bool mine)
    {
        if (!mine) return;
        DrawExportProgress(accent, 130f);
        ImGui.SameLine(0, 6);
        // Between entries, not inside one: neither renderer can be interrupted mid-song.
        if (UiButton("stop", AcEnemy, "Stop once the entry it is on is written.", 44f, _expCancel))
            _expCancel = true;
    }
}
