using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian.Audio;

namespace T2LV;

/// <summary>
/// The sound player: all forty clips the game has -- thirty-one effects out of
/// tyrian.snd and nine announcer lines out of voices.snd -- with what each one is,
/// what it looks like, and everywhere the data set fires it.
///
/// The clips are 8-bit mono at 11 kHz, so the waveform is the whole truth about
/// them and worth drawing large. The interesting question is the other half:
/// which weapon carries a sound, which level event fires it, which of the nine
/// text windows speaks it. All of that is read out of weapon.dat and the level
/// event lists rather than written down here.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showSounds;
    private int _soundSelected;
    private float _soundListW = 250f;
    private readonly byte[] _soundFilter = new byte[64];
    private bool _soundScrollToSelection;
    private int _soundKindTab;            // 0 all, 1 effects, 2 voices
    private int _soundChannel = 7;        // which mixer channel the preview uses
    private int _soundLevel = 4;          // ... at which of the eight volume steps
    private bool _soundAutoPlay = true;   // clicking a row plays it, like the game's jukebox
    private bool _xmasVoices;             // voicesc.snd instead of voices.snd

    /// <summary>The <c>--showsounds N</c> entry point: open the window on one sound.</summary>
    public void ShowSound(int number)
    {
        _showSounds = true;
        if (number > 0) _soundSelected = Math.Clamp(number - 1, 0, SoundBank.SoundCount - 1);
        _soundScrollToSelection = true;
    }

    /// <summary>Open the sound window on a 1-based sound number, from another browser.</summary>
    private void OpenSound(int number)
    {
        _showSounds = true;
        _soundSelected = Math.Clamp(number - 1, 0, SoundBank.SoundCount - 1);
        _soundScrollToSelection = true;
    }

    private SoundBank? SoundsBank => _audio?.Sounds;

    // =====================================================================
    // The window
    // =====================================================================

    private void DrawSoundWindow()
    {
        _soundFocused = false;
        if (!_showSounds) { _wasShowingSounds = false; return; }
        if (!_wasShowingSounds) { _wasShowingSounds = true; ArmTrace("sound window opening"); }

        Trace("begin");
        if (!RefBegin("Sounds", "sounds", ref _showSounds, AcSound,
                new Vector2(1040, 700), new Vector2(640, 400))) { Trace("collapsed"); return; }
        _soundFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

        if (_audio == null || !_audio.IsOpen)
        {
            DrawSoundBand();
            UiEmpty("No audio device",
                _audioProblem.Length > 0 ? _audioProblem : "Nothing here can make a sound.", AcSound);
            RefEnd(AcSound);
            return;
        }
        var bank = SoundsBank!;
        if (!bank.Loaded)
        {
            DrawSoundBand();
            UiEmpty("tyrian.snd and voices.snd are not in this data folder",
                "Thirty-one effects in one, nine announcer lines in the other.", AcSound);
            RefEnd(AcSound);
            return;
        }

        _soundSelected = Math.Clamp(_soundSelected, 0, SoundBank.SoundCount - 1);
        DrawSoundBand();

        float maxList = Math.Max(180f, ImGui.GetContentRegionAvail().X - 380f);
        _soundListW = Math.Clamp(_soundListW, 180f, maxList);

        WellBegin("sndlist", new Vector2(_soundListW, ImGui.GetContentRegionAvail().Y), AcSound);
        DrawSoundList(bank);
        WellEnd();

        ImGui.SameLine(0, 3);
        VSplitter("##sndsplit", ref _soundListW, 180f, maxList);
        ImGui.SameLine(0, 3);

        Trace("detail");
        ImGui.BeginChild("sndmain", new Vector2(0, 0));
        DrawSoundDetail(bank);
        ImGui.EndChild();

        Trace("done");
        if (_audioTraceFrames > 0) _audioTraceFrames--;
        RefEnd(AcSound);
    }

    /// <summary>Whether the window was up last frame, so its opening can be traced once.</summary>
    private bool _wasShowingSounds;

    private void DrawSoundBand()
    {
        // One row, and a second only while a whole-bank export is running, for the clip it is
        // on and the meter. Idle, the band is a single clean line.
        BandBegin("sndband", AcSound, SoundBatchRunning ? 2 : 1);

        UiFilter("##sndfilter", "filter sounds", _soundFilter, 180f, AcSound);

        BandDivider();
        SegBar("##sndkind", ref _soundKindTab, AcSound, 230f,
            ("All", "Everything the game can play."),
            ("Effects", "The thirty-one clips in tyrian.snd."),
            ("Voices", "The nine announcer lines in voices.snd."));

        BandDivider();
        BandLabel("volume");
        ImGui.SetNextItemWidth(110);
        int vol = _fxVolume;
        if (ImGui.SliderInt("##sndvol", ref vol, 0, 255, "%d")) _fxVolume = vol;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The engine's own 0-255 effects volume, on its 30 dB curve.\nThe game ships at 191.");

        BandDivider();
        int xmas = _xmasVoices ? 1 : 0;
        if (SegBar("##sndxmas", ref xmas, AcSound, 190f,
                ("Normal", "The announcer out of voices.snd."),
                ("Christmas", "voicesc.snd -- the Christmas-mode announcer the game swaps in\n" +
                              "with its Xmas graphics. Same nine lines, differently read.")))
        {
            _xmasVoices = xmas == 1;
            if (_audio != null && !_audio.SetXmasVoices(_dataDir, _xmasVoices))
            {
                _xmasVoices = false;
                _audio.SetXmasVoices(_dataDir, false);
                _status = "voicesc.snd is not in this data folder.";
            }
        }

        BandDivider();
        UiToggle("play on select", ref _soundAutoPlay, AcSound,
            "Fire a clip the moment you click it, the way the game's own\nsound browser does.");

        BandDivider();
        bool windows = OperatingSystem.IsWindows();
        bool ready = SoundsBank is { Loaded: true } && _audio is { IsOpen: true };
        if (UiButton("export all sounds", AcSound,
                "Save every clip in both banks to its own .wav, at the 11 kHz they\n" +
                "ship at. Pick a folder; each file is named for its clip, replacing\n" +
                "one already there by that name.",
                0f, ExportBusy || !ready || !windows) && windows)
            StartAudioExportAll(ExportAllKind.Sounds);

        BandDivider();
        string line = _audio is { IsOpen: true } a
            ? a.StatusLine()
            : _audioProblem.Length > 0 ? _audioProblem : "no audio device";
        BandNote(line, line.Contains("!!") || _audio is not { IsOpen: true } ? AcEnemy : UiFaint);

        // --- second row, only while a batch is running ---
        DrawExportAllProgress(AcSound, SoundBatchRunning);

        BandEnd();
    }

    // =====================================================================
    // List
    // =====================================================================

    private void DrawSoundList(SoundBank bank)
    {
        string filter = BufText(_soundFilter).Trim();
        var usage = Usage;

        float longest = 0.01f;
        foreach (var c in bank.Clips) if (c != null) longest = Math.Max(longest, c.Seconds);

        bool anyShown = false;
        for (int section = 0; section < 2; section++)
        {
            if (_soundKindTab == 1 && section == 1) continue;
            if (_soundKindTab == 2 && section == 0) continue;

            int lo = section == 0 ? 0 : SoundBank.SfxCount;
            int hi = section == 0 ? SoundBank.SfxCount : SoundBank.SoundCount;

            var rows = new List<int>();
            for (int i = lo; i < hi; i++)
            {
                var c = bank.Clips[i];
                if (c == null) continue;
                if (filter.Length > 0 &&
                    !Matches(filter, c.Title, c.Symbol, c.Note, (i + 1).ToString())) continue;
                rows.Add(i);
            }
            if (rows.Count == 0) continue;
            anyShown = true;

            UiSection(section == 0 ? "tyrian.snd  ·  effects" : $"{bank.VoiceFile}  ·  the announcer",
                AcSound, rows.Count.ToString());

            foreach (int i in rows)
            {
                var c = bank.Clips[i]!;
                bool sel = i == _soundSelected;
                const float rowH = 32f;
                var box = UiRow($"##snd{i}", sel, AcSound, rowH);
                if (box.Clicked)
                {
                    _soundSelected = i;
                    if (_soundAutoPlay) PlayPreview(i + 1);
                }
                if (box.Hovered)
                    ImGui.SetTooltip($"{c.Symbol}\nsound {c.Number}   ·   {c.Raw.Length:n0} bytes" +
                                     (c.Note.Length > 0 ? $"\n{c.Note}" : ""));
                if (sel && _soundScrollToSelection) { ImGui.SetScrollHereY(0.4f); _soundScrollToSelection = false; }

                int uses = usage?.Sound(c.Number).Count ?? 0;
                string trail = $"{c.Seconds:0.00}s";
                string sub = c.IsVoice ? c.Note : c.Symbol;
                RowText(box, 26f, $"{c.Number:00}  {c.Title}", sub, AcSound, sel, TrailRoom(trail) + 8f);
                RowTrail(box, trail, Shade(AcSound, 1.1f));

                var dl = ImGui.GetWindowDrawList();
                var bar = new Vector2(box.Min.X + 9f, box.Min.Y + 6f);
                MeterBar(dl, bar, new Vector2(bar.X + 5f, box.Max.Y - 6f), 0f, AcSound,
                    Gfx.Rgba(30, 34, 44));
                float frac = Math.Clamp(c.Seconds / longest, 0.03f, 1f);
                float h = (box.Max.Y - 6f) - bar.Y;
                dl.AddRectFilled(new Vector2(bar.X, box.Max.Y - 6f - h * frac),
                    new Vector2(bar.X + 5f, box.Max.Y - 6f),
                    Alpha(Shade(AcSound, uses > 0 ? 1.15f : 0.7f), (byte)(uses > 0 ? 235 : 130)), 2f);
            }
        }
        if (!anyShown) ImGui.TextDisabled("Nothing matches that filter.");
        _soundScrollToSelection = false;
    }

    private void PlayPreview(int number) => _audio?.PlaySound(number, _soundChannel, _soundLevel);

    // =====================================================================
    // Detail
    // =====================================================================

    private void DrawSoundDetail(SoundBank bank)
    {
        var c = bank.Clips[_soundSelected];
        if (c == null) { ImGui.TextDisabled("That slot did not load."); return; }

        UiTitle($"{c.Number:00}  {c.Title}", AcSound, c.Symbol);

        Badge(c.IsVoice ? bank.VoiceFile : "tyrian.snd", AcSound);
        ImGui.SameLine(0, 5f);
        Badge($"{c.Seconds:0.000} s", Gfx.Rgba(150, 162, 185));
        ImGui.SameLine(0, 5f);
        Badge($"{c.Raw.Length:n0} samples", Gfx.Rgba(150, 162, 185));
        ImGui.SameLine(0, 5f);
        Badge($"peak {c.Peak * 100:0}%", c.Peak > 0.97f ? AcEnemy : Gfx.Rgba(150, 162, 185));
        if (SoundBank.ChannelNote(c.Number) is { Length: > 0 } chn)
        {
            ImGui.SameLine(0, 5f);
            Badge(chn, AcGo);
        }

        if (c.Note.Length > 0)
        {
            ImGui.Dummy(new Vector2(0, 2f));
            ImGui.TextColored(ColorOf(Shade(AcSound, 1.05f)), c.Note);
        }

        ImGui.Dummy(new Vector2(0, 4f));
        DrawSoundTransport(c);

        const float useH = 200f;
        float waveH = Math.Max(90f, ImGui.GetContentRegionAvail().Y - useH - 10f);

        WellBegin("sndwave", new Vector2(ImGui.GetContentRegionAvail().X, waveH), AcSound, 6f, 6f);
        DrawWaveform(c);
        WellEnd();

        WellBegin("snduses", ImGui.GetContentRegionAvail(), AcSound, 10f, 8f);
        DrawSoundUses(c);
        WellEnd();
    }

    private void DrawSoundTransport(SoundClip c)
    {
        BandBegin("sndtransport", AcSound);

        bool busy = _audio?.ChannelBusy(_soundChannel) ?? false;
        if (GlyphButton("sndplay", Glyph.Play, AcSound, "Fire it on the chosen channel  (space)",
                ImGui.GetFrameHeight() + 20f, busy))
            PlayPreview(c.Number);
        ImGui.SameLine(0, 4);
        if (GlyphButton("sndstop", Glyph.Stop, AcSound, "Silence every channel"))
            _audio?.StopAllSounds();

        BandDivider();
        BandLabel("channel");
        ImGui.SetNextItemWidth(90);
        ImGui.SliderInt("##sndchan", ref _soundChannel, 0, 7);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The game mixes eight channels and a new sound simply replaces\n" +
                             "whatever that channel was playing. Channel 3 is the announcer's.");

        BandDivider();
        BandLabel("level");
        ImGui.SetNextItemWidth(90);
        ImGui.SliderInt("##sndlevel", ref _soundLevel, 0, 7);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("One of the mixer's eight volume steps. In game: 4 on the announcer\n" +
                             "channel, 2 for most effects, 1 for the Lightning weapon.");

        BandDivider();
        DrawExportRow(AcSound, ExportKind.SoundWav, c.Number, withMidi: false);

        BandDivider();
        BandNote(busy ? "playing" : "idle", busy ? Shade(AcGo, 1f) : UiFaint);

        BandEnd();
    }

    /// <summary>
    /// The clip drawn as a min/max envelope over its 8-bit samples, with a playhead when
    /// the chosen channel is running it. At 11 kHz even a long clip is only a few thousand
    /// samples, so a column-per-pixel pass over the raw data is honest and cheap.
    /// </summary>
    private void DrawWaveform(SoundClip c)
    {
        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 40f || avail.Y < 30f) return;

        var p = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("sndwavehit", avail);
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) PlayPreview(c.Number);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("click the waveform to play it");

        var dl = ImGui.GetWindowDrawList();
        var mx = p + avail;
        float mid = p.Y + avail.Y * 0.5f;

        dl.AddLine(new Vector2(p.X, mid), new Vector2(mx.X, mid), Gfx.Rgba(52, 58, 72));
        for (int i = 1; i < 4; i++)
        {
            float y = p.Y + avail.Y * i / 4f;
            if (Math.Abs(y - mid) < 1f) continue;
            dl.AddLine(new Vector2(p.X, y), new Vector2(mx.X, y), Gfx.Rgba(30, 34, 43));
        }

        int n = c.Raw.Length;
        if (n == 0) { ImGui.TextDisabled("empty"); return; }
        int cols = Math.Max(1, (int)avail.X);
        float half = avail.Y * 0.5f - 3f;
        for (int x = 0; x < cols; x++)
        {
            int a = (int)((long)x * n / cols);
            int b = (int)((long)(x + 1) * n / cols);
            if (b <= a) b = a + 1;
            if (b > n) b = n;
            int lo = 127, hi = -128;
            for (int i = a; i < b; i++) { int v = c.Raw[i]; if (v < lo) lo = v; if (v > hi) hi = v; }
            float y0 = mid - hi / 128f * half;
            float y1 = mid - lo / 128f * half;
            if (y1 - y0 < 1f) y1 = y0 + 1f;
            dl.AddRectFilled(new Vector2(p.X + x, y0), new Vector2(p.X + x + 1f, y1),
                Alpha(Shade(AcSound, 1.1f), 225));
        }

        // Playhead, but only when this channel really is playing this clip.
        if (_audio != null && _audio.ChannelSound(_soundChannel) == c.Number)
        {
            float t = _audio.ChannelProgress(_soundChannel);
            float px = p.X + avail.X * t;
            dl.AddLine(new Vector2(px, p.Y), new Vector2(px, mx.Y), Gfx.Rgba(255, 255, 255, 220), 1.4f);
        }

        float lh = ImGui.GetTextLineHeight();
        dl.AddText(new Vector2(p.X + 4, p.Y + 2), UiFaint, "+127");
        dl.AddText(new Vector2(p.X + 4, mx.Y - lh - 2), UiFaint, "-128");
        string dur = $"{c.Seconds:0.000} s   ·   {c.Raw.Length:n0} samples at {SoundBank.SourceRate} Hz";
        var dsz = ImGui.CalcTextSize(dur);
        dl.AddText(new Vector2(mx.X - dsz.X - 4, mx.Y - lh - 2), UiFaint, dur);
    }

    private void DrawSoundUses(SoundClip c)
    {
        var uses = Usage?.Sound(c.Number) ?? Array.Empty<AudioUse>();
        if (uses.Count == 0)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorOf(UiFaint),
                "Nothing in the data set names this one. Most of the menu vocabulary is like " +
                "that -- the engine plays it from C, not from a table anything here can read.");
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
                UiSection(SoundKindLabel(u.Kind), KindColor(u.Kind),
                    uses.Count(x => x.Kind == u.Kind).ToString());
            }

            const float rowH = 28f;
            bool jumpable = u.Kind is UseKind.LevelEvent or UseKind.TextWindow && u.LevelFile > 0;
            // Keyed on the row: several engine rows share one sound and would otherwise
            // collide on a single ImGui id.
            var box = UiRow($"##su{row}", false, KindColor(u.Kind), rowH);
            if (box.Clicked && jumpable) JumpToLevelAt(u.Episode, u.LevelFile, u.Time);
            if (box.Hovered && jumpable)
                ImGui.SetTooltip("open this level and seek playback to the moment it fires");

            string trail = u.Time > 0 ? $"t={u.Time}" : "";
            RowText(box, 10f, u.Where, u.Detail, KindColor(u.Kind), false, TrailRoom(trail) + 8f);
            if (trail.Length > 0) RowTrail(box, trail, Shade(KindColor(u.Kind), 1.1f));
        }
    }

    private static string SoundKindLabel(UseKind k) => k switch
    {
        UseKind.LevelEvent => "levels that fire it (event 62)",
        UseKind.TextWindow => "text windows that speak it (event 16)",
        UseKind.Weapon => "weapons that carry it",
        _ => "the engine itself",
    };
}
