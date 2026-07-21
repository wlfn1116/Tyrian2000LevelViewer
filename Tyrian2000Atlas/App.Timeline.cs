using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The playback transport row and the level timeline under it.
///
/// The timeline used to be one 26px strip drawing one thing: the precomputed route. That was
/// fine while the route was the only thing there was, but the atlas can now reach into the
/// running simulation -- click-to-kill takes a boss out of a hold, and everything after it is
/// a level the precompute never saw. A bar that only knows the prediction goes quietly wrong
/// exactly when the interesting thing happens.
///
/// So it is built in layers instead, over three lanes (ruler / events / route):
///
///   * the prediction, which is what the precompute found, drawn solid up to the point the
///     run diverged and as a dashed ghost after it -- still there to compare against, plainly
///     no longer what is happening;
///   * the live branch, recorded frame by frame as it happens (see SimPlayback's branch
///     section), painted over the ghost in its own green and free to run out past the
///     predicted end, which is where a shot-out boss takes you;
///   * gates and holds as blocks rather than hatching, coloured by kind to match ROUTES &
///     GATES in the HUD, with their cycles marked off inside them and the one being played
///     filling as it runs -- so "cycle 2/3" is something the bar shows, not just the OSD.
///
/// The horizontal scale is therefore a live quantity, and it eases between values rather than
/// snapping, quantised to whole 15-second steps so a recording branch widens the bar in
/// occasional visible moves instead of creeping every frame.
/// </summary>
public sealed unsafe partial class App
{
    private const float TimelineH = 50f;
    private const float TlRulerH = 14f;     // 13px labels, which is the only size the font has
    private const float TlEventH = 6f;

    // Eased horizontal span, in ticks, keyed on the LEVEL rather than the SimPlayback. A
    // rebuild swaps the whole SimPlayback, and most rebuilds are a slider nudge -- another
    // loop cycle kept, a longer hold watched -- where the level is the same one and only its
    // length changed. Easing across those is the point: the bar visibly grows by the cycle you
    // just asked for, instead of the whole thing silently re-scaling between frames. Changing
    // level does snap, because easing from an unrelated length is a meaningless animation.
    private float _tlSpan;
    private object? _tlSpanFor;
    /// <summary>The bar is being dragged: <see cref="UpdatePlayback"/> holds the clock while
    /// it is, so a scrub and the transport are not both moving the playhead.</summary>
    private bool _tlScrubbing;
    private int _tlPulseFor = -1;           // branch tick the pulse belongs to
    private float _tlPulse;                 // seconds of highlight left on a fresh branch

    private static readonly uint TlGate   = Gfx.Rgba(255, 150,  90);   // boss / enemy gate
    private static readonly uint TlRoute  = Gfx.Rgba(120, 200, 255);   // conditional route loop
    private static readonly uint TlHold   = Gfx.Rgba(230, 120, 210);   // enemy-gated standoff
    private static readonly uint TlLive   = Gfx.Rgba(110, 230, 150);   // the branch's own colour
    private static readonly uint TlHead   = Gfx.Rgba(255, 235, 130);   // playhead

    /// <summary>Name and colour for a retained region, matching the HUD's ROUTES &amp; GATES swatches.</summary>
    private static (string Kind, uint Col) GateLook(SimPlayback.HoldLoopKind kind) => kind switch
    {
        SimPlayback.HoldLoopKind.ScriptedLoop => ("gate", TlGate),
        SimPlayback.HoldLoopKind.RouteLoop => ("route", TlRoute),
        _ => ("hold", TlHold),
    };

    /// <summary>Which cycle of a gate <paramref name="tick"/> falls in, 1-based.</summary>
    private static int GateCycle(SimPlayback.LoopRegion r, int tick)
    {
        int c = 1;
        while (c <= r.CycleEnds.Length && tick > r.CycleEnds[c - 1]) c++;
        return Math.Min(c, Math.Max(1, r.CycleEnds.Length));
    }

    // =====================================================================
    //  Transport row
    // =====================================================================

    /// <summary>
    /// The transport, its two layout toggles, the branch control and the speed tail. The
    /// media keys sit in a rounded well of their own so the row reads as one instrument
    /// rather than nine loose buttons, and the toggles that were text ("Fit" / "UI fit" /
    /// "Pin right") are icons now -- they were the first thing to be abbreviated away on a
    /// narrow canvas, and a glyph says the same thing in a third of the width.
    /// </summary>
    private void DrawTransportRow(SimPlayback pb)
    {
        var dl = ImGui.GetWindowDrawList();
        float fh = ImGui.GetFrameHeight();
        var bsz = new Vector2(MathF.Round(fh * 1.35f), fh);   // step / skip keys
        var psz = new Vector2(MathF.Round(fh * 2.0f), fh);    // hero play / pause
        var tsz = new Vector2(MathF.Round(fh * 1.5f), fh);    // the layout toggles
        const float gap = 3f, pad = 4f;

        bool fwd = _playing && _playDirection > 0;
        bool rewinding = _playing && _playDirection < 0;

        // The well behind the media keys. Drawn first and never moved into, so the row's
        // layout is exactly the buttons' -- the panel only spills into the padding around them.
        var gp = ImGui.GetCursorScreenPos();
        float wellW = bsz.X * 6 + psz.X + gap * 6 + pad;
        dl.AddRectFilled(gp - new Vector2(pad, pad), gp + new Vector2(wellW, fh + pad),
            Gfx.Rgba(24, 26, 34, 200), 7f);
        dl.AddRect(gp - new Vector2(pad, pad), gp + new Vector2(wellW, fh + pad),
            Gfx.Rgba(58, 62, 78, 190), 7f);

        if (TransportBtn("##pbstart", Glyph.JumpStart, "Jump to start (Home)", bsz, gap: gap))
        { pb.SeekTo(1); _playing = false; }
        if (TransportBtn("##pbrewind", Glyph.Rewind, rewinding ? "Stop rewind" : "Play backwards",
                bsz, active: rewinding, gap: gap))
        {
            if (rewinding) _playing = false;
            else { _playing = true; _playDirection = -1; _playAccum = 0; }
        }
        if (TransportBtn("##pbback1", Glyph.StepBack, "Step one tick back (Left)", bsz, gap: gap))
        { pb.SeekTo(pb.CurrentTick - 1); _playing = false; }

        if (TransportBtn("##pbplay", fwd ? Glyph.Pause : Glyph.Play,
                fwd ? "Pause (Space)" : "Play (Space)", psz, primary: true, gap: gap))
        {
            if (fwd) _playing = false;
            else { if (pb.AtEnd) pb.SeekTo(1); _playing = true; _playDirection = 1; _playAccum = 0; }
        }

        if (TransportBtn("##pbfwd1", Glyph.StepFwd, "Step one tick forward (Right)", bsz, gap: gap))
        { pb.SeekTo(pb.CurrentTick + 1); _playing = false; }
        if (TransportBtn("##pbff", Glyph.FastFwd,
                "Fast-forward (cycles 2x / 4x / 8x; Play resets to 1x)", bsz,
                active: fwd && _playSpeed > 1f, gap: gap))
        {
            _playing = true; _playDirection = 1; _playAccum = 0;
            _playSpeed = _playSpeed switch { < 2f => 2f, < 4f => 4f, < 8f => 8f, _ => 1f };
        }
        if (TransportBtn("##pbend", Glyph.JumpEnd,
                pb.Branched
                    ? "Jump to the end of what the live run has reached (End)"
                    : "Jump to end (End)", bsz, gap: pad + 10f))
        { pb.SeekTo(pb.DisplayEnd); _playing = false; }

        // --- layout toggles ---
        if (IconChip("##pbfit", Glyph.Fit, false, AcDisplay, tsz.X,
                "Fit the whole panel, zoom and pan reset (or double-click the view).\n" +
                "The frame is free to sit under the controls HUD.\nwheel = zoom, drag = pan"))
        { _fitAroundHud = false; _playZoom = 0; _playPan = Vector2.Zero; }
        ImGui.SameLine(0, gap);
        if (IconChip("##pbuifit", Glyph.FitUi, _fitAroundHud, AcDisplay, tsz.X,
                "UI fit: fit the view into what the floating \"Playback controls\" HUD leaves\n" +
                "free, so the frame sits beside the panel instead of under it. Stays armed:\n" +
                "resizes and folding HUD sections re-fit to the space actually left.\n" +
                "Pinned right, the HUD is already out of the view and this does nothing."))
        { _fitAroundHud = !_fitAroundHud; _playZoom = 0; _playPan = Vector2.Zero; }
        ImGui.SameLine(0, gap);
        if (IconChip("##pbpin", Glyph.PinRight, _hudPinRight, AcDisplay, tsz.X,
                "Pin right: turn the \"Playback controls\" HUD into a fixed column down the\n" +
                "right edge of the window -- the mirror of the controls column on the left --\n" +
                "instead of a panel floating over the playfield. The view keeps the rest."))
        { _hudPinRight = !_hudPinRight; _playZoom = 0; _playPan = Vector2.Zero; }
        ImGui.SameLine(0, 10);

        // --- the branch, while there is one ---
        if (pb.Branched)
        {
            if (IconChip("##pbrevert", Glyph.Revert, true, TlLive, tsz.X,
                    $"LIVE: this run stopped matching the prediction at " +
                    $"{SimPlayback.FormatTime(pb.BranchTick)} and has been recording itself\n" +
                    $"since ({pb.Interferences} interference{(pb.Interferences == 1 ? "" : "s")}). " +
                    "It is free to run past the predicted end.\n\n" +
                    "Click to throw the branch away and put the prediction back."))
                pb.DropBranch();
            ImGui.SameLine(0, 10);
        }

        // What is left for the tail, measured rather than modelled: pinning the HUD into its
        // own column takes ~300px out of the row and nothing here wraps or scrolls, so the
        // parts give way in order of how well they earn their space.
        const float comboW = 60f;
        float W(string s) => ImGui.CalcTextSize(s).X;
        float left = ImGui.GetContentRegionAvail().X;

        string endTag = pb.Branched
            ? pb.BranchDone ? "  [live: ended]" : "  [live]"
            : pb.LoopDetected
                ? $"  [{pb.LoopRegions.Count} loop/hold section{(pb.LoopRegions.Count == 1 ? "" : "s")}]"
                : pb.EndedNaturally ? "" : "  [capped]";
        string ticks = $"{pb.CurrentTick}/{pb.DisplayEnd}";
        var tails = new (bool Word, string Tick)[]
        {
            (true,  $"tick {ticks}{endTag}"),
            (true,  $"tick {ticks}"),
            (false, ticks),
            (false, ""),
        };
        float TailW(bool word, string tick) =>
            (word ? W("speed") + 6f : 0f) + comboW + (tick.Length > 0 ? 12f + W(tick) : 0f);
        var tail = tails.FirstOrDefault(t => TailW(t.Word, t.Tick) <= left, tails[^1]);

        if (tail.Word)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("speed");
            ImGui.SameLine(0, 6);
        }
        ImGui.SetNextItemWidth(comboW);
        int speedIdx = Array.IndexOf(SpeedSteps, _playSpeed);
        if (speedIdx < 0) speedIdx = 2;
        var speedNames = SpeedSteps.Select(s => $"x{s:0.##}").ToArray();
        if (ImGui.Combo("##speed", &speedIdx, speedNames, speedNames.Length))
            _playSpeed = SpeedSteps[speedIdx];
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Playback speed (game runs at 35 ticks/s)");

        if (tail.Tick.Length > 0)
        {
            ImGui.SameLine(0, 12);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(tail.Tick);
            if (endTag.Length > 0 && !tail.Tick.EndsWith(']') && ImGui.IsItemHovered())
                ImGui.SetTooltip(endTag.Trim());
        }
    }

    // =====================================================================
    //  Timeline
    // =====================================================================

    /// <summary>The scrubbable level bar: time ruler, event lane, and the route track with
    /// its density, gates, live branch and playhead.</summary>
    private void DrawTimeline(SimPlayback pb)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        const float h = TimelineH;
        if (w < 40) return;

        // --- the span, eased ---
        float dt = ImGui.GetIO().DeltaTime;
        int target = SpanTarget(pb);
        object key = (object?)_level ?? pb;
        if (!ReferenceEquals(_tlSpanFor, key)) { _tlSpanFor = key; _tlSpan = target; }
        _tlSpan += (target - _tlSpan) * (1f - MathF.Exp(-dt * 9f));
        if (MathF.Abs(target - _tlSpan) < 1.5f) _tlSpan = target;
        float span = MathF.Max(2f, _tlSpan);

        if (pb.Branched && pb.BranchTick != _tlPulseFor) { _tlPulseFor = pb.BranchTick; _tlPulse = 1.1f; }
        if (!pb.Branched) _tlPulseFor = -1;
        _tlPulse = MathF.Max(0f, _tlPulse - dt);

        float X(int t) => pos.X + Math.Clamp((t - 1) / (span - 1f), 0f, 1f) * w;
        int TickAt(float x) => 1 + (int)MathF.Round(Math.Clamp((x - pos.X) / w, 0f, 1f) * (span - 1f));

        // --- input ---
        ImGui.InvisibleButton("timeline", new Vector2(w, h));
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        var mouse = ImGui.GetMousePos();

        // A scrub holds the clock still for its duration so the drag is not fighting playback
        // advancing underneath it -- but through a flag UpdatePlayback reads, NOT by clearing
        // _playing. Pausing for the length of a click made every seek a pause/play edge, and
        // the audio layer hears one of those as "take the music back", which is how clicking
        // the timeline came to restart the song.
        _tlScrubbing = active;
        if (active) pb.SeekTo(TickAt(mouse.X));
        if (hovered && ImGui.GetIO().MouseWheel != 0)
        {
            int step = ImGui.GetIO().KeyShift ? 10 : 1;
            pb.SeekTo(pb.CurrentTick + (int)MathF.Sign(ImGui.GetIO().MouseWheel) * step);
            _playing = false;
        }

        // --- lanes ---
        float rulerY = pos.Y;
        float evY = rulerY + TlRulerH;
        float trkY = evY + TlEventH;
        float trkH = h - TlRulerH - TlEventH;
        var trk0 = new Vector2(pos.X, trkY);
        var trk1 = new Vector2(pos.X + w, trkY + trkH);

        dl.AddRectFilled(pos, pos + new Vector2(w, h), Gfx.Rgba(15, 16, 21), 4f);
        dl.AddRectFilled(pos, new Vector2(pos.X + w, trkY), Gfx.Rgba(22, 24, 31));
        dl.AddLine(new Vector2(pos.X, trkY - 0.5f), new Vector2(pos.X + w, trkY - 0.5f),
            Gfx.Rgba(52, 56, 70));
        dl.AddRectFilled(trk0, trk1, Gfx.Rgba(28, 30, 38));

        // Where the run stopped matching its prediction. Everything past it is drawn twice:
        // the prediction as a ghost, the branch over the top.
        float branchX = pb.Branched ? X(pb.BranchTick) : pos.X + w + 4f;

        DrawTimeRuler(dl, pos, w, h, span, X);
        DrawDensity(dl, pos, w, trkY, trkH, span, pb, branchX);
        DrawGates(dl, pb.LoopRegions, pb, X, trkY, trkH, branchX, live: false);
        if (pb.Branched) DrawGates(dl, pb.BranchRegions, pb, X, trkY, trkH, branchX, live: true);
        DrawEventLane(dl, pb, X, evY, branchX);
        if (pb.Branched) DrawBranchMark(dl, pb, X, pos, w, h, trkY, trkH, branchX);
        DrawPlayhead(dl, pb, X, pos, w, h, trkY, trkH, hovered, active, mouse);

        dl.AddRect(pos, pos + new Vector2(w, h), Gfx.Rgba(74, 78, 94), 4f);

        if (hovered || active) TimelineTooltip(pb, TickAt(mouse.X));
    }

    /// <summary>
    /// How wide the bar wants to be. Undiverged that is simply the precomputed length; a branch
    /// that has outlived the prediction pushes it out, but in whole 15-second steps -- scaled to
    /// the frontier itself the bar would creep by a pixel every frame while the run records.
    /// </summary>
    private static int SpanTarget(SimPlayback pb)
    {
        if (!pb.Branched || pb.BranchEnd <= pb.Duration) return pb.Duration;
        int step = (int)(15 * GameSim.TicksPerSecond);
        return Math.Max(pb.Duration, (pb.BranchEnd + step - 1) / step * step);
    }

    /// <summary>Minute/second gridlines, spaced to whatever the current scale can label.</summary>
    private static void DrawTimeRuler(ImDrawListPtr dl, Vector2 pos, float w, float h, float span,
        Func<int, float> X)
    {
        double secs = span / GameSim.TicksPerSecond;
        double pxPerSec = w / Math.Max(0.001, secs);
        int[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        int stepS = steps.FirstOrDefault(s => s * pxPerSec >= 62f, steps[^1]);

        for (int s = stepS; s < secs; s += stepS)
        {
            float x = X(1 + (int)(s * GameSim.TicksPerSecond));
            dl.AddLine(new Vector2(x, pos.Y + 1), new Vector2(x, pos.Y + h - 1),
                Gfx.Rgba(255, 255, 255, 14));
            dl.AddLine(new Vector2(x, pos.Y + TlRulerH - 4), new Vector2(x, pos.Y + TlRulerH - 1),
                Gfx.Rgba(150, 158, 180, 130));
            string lbl = $"{s / 60}:{s % 60:00}";
            if (x + 3 + ImGui.CalcTextSize(lbl).X <= pos.X + w - 2)   // no half-labels off the end
                dl.AddText(new Vector2(x + 3, pos.Y), Gfx.Rgba(132, 140, 162), lbl);
        }
    }

    /// <summary>
    /// Enemies on screen, as a filled area rather than the old scatter of maroon hairlines:
    /// each column takes the peak of the ticks behind it and colours by how busy that is, so
    /// the shape of a level -- lulls, waves, the wall of a boss -- reads at a glance. The
    /// branch draws its own on top in green, and the prediction it replaced goes to a ghost.
    /// </summary>
    private static void DrawDensity(ImDrawListPtr dl, Vector2 pos, float w, float trkY, float trkH,
        float span, SimPlayback pb, float branchX)
    {
        float baseY = trkY + trkH - 1;
        float maxH = trkH - 3;
        var cold = Gfx.Rgba(74, 96, 132);
        var hot = Gfx.Rgba(226, 104, 84);
        var liveCold = Gfx.Rgba(64, 128, 96);
        var liveHot = Gfx.Rgba(150, 240, 140);

        float norm = pb.DensityScale;
        int cols = (int)w;
        for (int x = 0; x < cols; x++)
        {
            int t0 = 1 + (int)(x / w * (span - 1));
            int t1 = Math.Max(t0 + 1, 1 + (int)((x + 1) / w * (span - 1)));

            int bPeak = 0, lPeak = -1;
            for (int t = t0; t < t1; t++)
            {
                if (t >= 1 && t <= pb.Density.Length && pb.Density[t - 1] > bPeak) bPeak = pb.Density[t - 1];
                if (pb.Branched)
                {
                    int d = pb.BranchDensityAt(t);
                    if (d > lPeak) lPeak = d;
                }
            }

            float px = pos.X + x;
            bool ghost = px >= branchX;
            if (bPeak > 0)
            {
                float f = Math.Min(1f, bPeak / norm);
                dl.AddLine(new Vector2(px, baseY - f * maxH), new Vector2(px, baseY),
                    Alpha(Mix(cold, hot, f), ghost ? (byte)46 : (byte)210));
            }
            if (lPeak > 0)
            {
                float f = Math.Min(1f, lPeak / norm);
                dl.AddLine(new Vector2(px, baseY - f * maxH), new Vector2(px, baseY),
                    Alpha(Mix(liveCold, liveHot, f), 225));
            }
        }
    }

    /// <summary>
    /// One bracket per retained gate/hold, coloured by kind: a solid rail along the top of the
    /// track, end caps down its full height, and only the faintest tint in the body. Filling
    /// the block instead -- which is what it did first -- buried the density underneath it, and
    /// a boss section with the enemy count hidden is the one place that count is worth seeing.
    ///
    /// The cycles are marked off along the rail, and the one the playhead is inside lights up:
    /// a gate is a counter, not a static stretch of level, and watching it count is most of
    /// what the section is.
    /// </summary>
    private void DrawGates(ImDrawListPtr dl, IReadOnlyList<SimPlayback.LoopRegion> regions,
        SimPlayback pb, Func<int, float> X, float trkY, float trkH, float branchX, bool live)
    {
        const float railH = 4f;
        foreach (var r in regions)
        {
            float lx = X(r.StartTick), rx = X(r.EndTick);
            if (rx - lx < 1.5f) rx = lx + 1.5f;
            var (kind, col) = GateLook(r.Kind);
            bool ghost = !live && pb.Branched && lx >= branchX - 0.5f;
            bool act = pb.CurrentTick >= r.StartTick && pb.CurrentTick <= r.EndTick && !ghost;
            float railY = trkY + 1f;

            dl.AddRectFilled(new Vector2(lx, railY), new Vector2(rx, trkY + trkH - 1),
                Alpha(col, ghost ? (byte)8 : act ? (byte)26 : (byte)15));
            dl.AddRectFilled(new Vector2(lx, railY), new Vector2(rx, railY + railH),
                Alpha(col, ghost ? (byte)55 : act ? (byte)235 : (byte)175));

            // The cycle currently running, lit along the rail.
            if (act && r.CycleEnds.Length > 0)
            {
                int c = GateCycle(r, pb.CurrentTick);
                float cs = X(c == 1 ? r.StartTick : r.CycleEnds[c - 2]);
                float ce = X(r.CycleEnds[c - 1]);
                dl.AddRectFilled(new Vector2(cs, railY - 1f), new Vector2(ce, railY + railH + 1f),
                    Mix(col, Gfx.Rgba(255, 255, 255), 0.55f));
            }
            for (int n = 0; n < r.CycleEnds.Length; n++)
            {
                float cx = X(r.CycleEnds[n]);
                dl.AddLine(new Vector2(cx, railY), new Vector2(cx, trkY + trkH - 1),
                    Alpha(col, ghost ? (byte)30 : (byte)110));
                dl.AddLine(new Vector2(cx, railY - 2f), new Vector2(cx, railY + railH + 2f),
                    Alpha(Gfx.Rgba(22, 23, 29), ghost ? (byte)90 : (byte)235), 1.5f);
            }

            dl.AddLine(new Vector2(lx, railY), new Vector2(lx, trkY + trkH - 1),
                Alpha(col, ghost ? (byte)70 : (byte)225), 1.5f);
            dl.AddLine(new Vector2(rx, railY), new Vector2(rx, trkY + trkH - 1),
                Alpha(col, ghost ? (byte)50 : (byte)150));

            // Kind and count, on its own backing so the density behind cannot swallow it.
            float room = rx - lx - 8f;
            if (room < 26f) continue;
            string text = r.CycleEnds.Length > 0
                ? act ? $"{kind} {GateCycle(r, pb.CurrentTick)}/{r.CycleEnds.Length}"
                      : $"{kind} x{r.CycleEnds.Length}"
                : $"{kind} {SimPlayback.RegionSeconds(r)}s";
            var at = new Vector2(lx + 4f, railY + railH + 2f);
            float tw = MathF.Min(room, ImGui.CalcTextSize(text).X);
            dl.AddRectFilled(at - new Vector2(2f, 0f),
                at + new Vector2(tw + 2f, ImGui.GetTextLineHeight()),
                Alpha(Gfx.Rgba(18, 19, 25), ghost ? (byte)120 : (byte)205), 2f);
            ClipText(dl, at, room,
                Alpha(Mix(col, Gfx.Rgba(255, 255, 255), 0.35f), ghost ? (byte)90 : (byte)245), text);
        }
    }

    /// <summary>Executed events, one hairline each, colour per class. The branch's own events
    /// sit in the same lane; the prediction's fade out behind them.</summary>
    private static void DrawEventLane(ImDrawListPtr dl, SimPlayback pb, Func<int, float> X,
        float evY, float branchX)
    {
        static bool Colour(byte type, out uint col)
        {
            switch (type)
            {
                case 2 or 3 or 30: col = Gfx.Rgba(80, 210, 230); return true;      // scroll speed
                case 4 or 83: col = Gfx.Rgba(255, 90, 90); return true;            // map stop
                case 38 or 54 or 70 or 71 or 75 or 76: col = Gfx.Rgba(255, 170, 60); return true; // flow
                case 11 or 36: col = Gfx.Rgba(240, 240, 240); return true;         // level end
                case 79: col = Gfx.Rgba(230, 90, 220); return true;                // boss bar
                default: col = 0; return false;
            }
        }

        foreach (var e in pb.Events)
        {
            if (!Colour(e.Type, out uint col)) continue;
            float ex = X(e.Tick);
            dl.AddLine(new Vector2(ex, evY + 1), new Vector2(ex, evY + TlEventH),
                Alpha(col, ex >= branchX ? (byte)40 : (byte)210));
        }
        if (!pb.Branched) return;
        foreach (var e in pb.BranchEvents)
        {
            if (!Colour(e.Type, out uint col)) continue;
            float ex = X(e.Tick);
            dl.AddLine(new Vector2(ex, evY), new Vector2(ex, evY + TlEventH), Alpha(col, 235));
        }
    }

    /// <summary>The divergence: a marker at the tick it happened, the recorded extent under
    /// the track, and the frontier the run is still writing to.</summary>
    private void DrawBranchMark(ImDrawListPtr dl, SimPlayback pb, Func<int, float> X,
        Vector2 pos, float w, float h, float trkY, float trkH, float branchX)
    {
        // The stretch of prediction the branch has already outlived, struck through, so the
        // ghost underneath is plainly superseded rather than merely dim.
        float endX = X(Math.Min(pb.BranchEnd, pb.Duration));
        for (float x = branchX; x < endX; x += 7f)
            dl.AddLine(new Vector2(x, trkY + 2), new Vector2(MathF.Min(x + 3f, endX), trkY + 2),
                Alpha(Gfx.Rgba(190, 200, 220), 55));

        // What the branch has actually lived through, as a rail under the track.
        float liveX = X(pb.BranchEnd);
        dl.AddRectFilled(new Vector2(branchX, trkY + trkH - 2.5f),
            new Vector2(liveX, trkY + trkH), Alpha(TlLive, 235));

        // The frontier, while it is still moving: a soft cap that says the run is recording.
        if (!pb.BranchDone)
        {
            dl.AddRectFilled(new Vector2(liveX - 1f, trkY + 1), new Vector2(liveX + 1f, trkY + trkH),
                Alpha(TlLive, 130));
            dl.AddTriangleFilled(new Vector2(liveX, trkY + trkH - 6f),
                new Vector2(liveX + 5f, trkY + trkH), new Vector2(liveX - 5f, trkY + trkH),
                Alpha(TlLive, 200));
        }

        // The mark itself, pulsed for a moment when it is new so a shot is visibly felt here.
        float pulse = _tlPulse > 0f ? _tlPulse / 1.1f : 0f;
        dl.AddLine(new Vector2(branchX, pos.Y + 1), new Vector2(branchX, pos.Y + h - 1),
            Alpha(TlLive, (byte)(150 + 105 * pulse)), 1f + pulse);
        if (pulse > 0f)
            dl.AddRectFilled(new Vector2(branchX - 9f * pulse, trkY),
                new Vector2(branchX + 9f * pulse, trkY + trkH), Alpha(TlLive, (byte)(70 * pulse)));

        // The tag rides in the ruler lane, not on the track: sat over the route it blanked out
        // the first second of the very branch it was labelling. It falls to the left of the
        // marker when the branch is close enough to the end that it would otherwise drift off
        // the mark it belongs to.
        string tag = pb.Interferences > 1 ? $"LIVE x{pb.Interferences}" : "LIVE";
        var sz = ImGui.CalcTextSize(tag);
        float tx = branchX + 3f + sz.X + 3f <= pos.X + w ? branchX + 3f : branchX - 3f - sz.X;
        tx = Math.Clamp(tx, pos.X + 2f, pos.X + w - sz.X - 3f);
        dl.AddRectFilled(new Vector2(tx - 2f, pos.Y + 1f), new Vector2(tx + sz.X + 2f, pos.Y + sz.Y),
            Alpha(Shade(TlLive, 0.34f), 240), 2f);
        dl.AddText(new Vector2(tx, pos.Y), Shade(TlLive, 1.15f), tag);
    }

    /// <summary>Progress fill, the playhead and its grab handle, and the hover preview.</summary>
    private void DrawPlayhead(ImDrawListPtr dl, SimPlayback pb, Func<int, float> X,
        Vector2 pos, float w, float h, float trkY, float trkH,
        bool hovered, bool active, Vector2 mouse)
    {
        float px = X(pb.CurrentTick);

        // Everything played, washed rather than filled -- the density and the gates under it
        // have to stay readable through it.
        dl.AddRectFilled(new Vector2(pos.X, trkY), new Vector2(px, trkY + trkH),
            Alpha(Gfx.Rgba(120, 160, 235), 40));

        if (hovered && !active)
        {
            float hx = Math.Clamp(mouse.X, pos.X, pos.X + w);
            dl.AddLine(new Vector2(hx, pos.Y + 1), new Vector2(hx, pos.Y + h - 1),
                Gfx.Rgba(255, 255, 255, 70));
            string t = SimPlayback.FormatTime(1 + (int)MathF.Round(
                Math.Clamp((hx - pos.X) / w, 0f, 1f) * (MathF.Max(2f, _tlSpan) - 1f)));
            var sz = ImGui.CalcTextSize(t);
            float bx = Math.Clamp(hx - sz.X * 0.5f, pos.X + 2, pos.X + w - sz.X - 4);
            dl.AddRectFilled(new Vector2(bx - 3, pos.Y + 1), new Vector2(bx + sz.X + 3, pos.Y + sz.Y),
                Gfx.Rgba(16, 18, 24, 235), 2f);
            dl.AddText(new Vector2(bx, pos.Y), Gfx.Rgba(226, 230, 240), t);
        }

        dl.AddLine(new Vector2(px, pos.Y + 1), new Vector2(px, pos.Y + h - 1), TlHead, 2f);

        // A real handle to take hold of: a pennant hanging into the track, fattened while the
        // bar is being worked, so the grab point is somewhere rather than the whole strip.
        float g = active ? 6.5f : hovered ? 5.5f : 4.5f;
        float ty = trkY + 1f;
        dl.AddTriangleFilled(new Vector2(px - g, ty), new Vector2(px + g, ty),
            new Vector2(px, ty + g * 1.5f), TlHead);
        dl.AddCircleFilled(new Vector2(px, trkY + trkH - 1.5f), g * 0.55f, TlHead);
    }

    /// <summary>What the bar is showing under the cursor, in words.</summary>
    private static void TimelineTooltip(SimPlayback pb, int t)
    {
        string where = pb.Branched
            ? t < pb.BranchTick
                ? "\n\nbefore the divergence: prediction and live run are the same run here"
                : t <= pb.BranchEnd
                    ? "\n\ngreen = the live run, recorded as it happened; the faded bars behind\n" +
                      "it are what the prediction had expected instead"
                    : "\n\npast the live run's frontier -- the faded bars are the prediction only.\n" +
                      "Play on and the branch records over them"
            : "";

        var gate = pb.RegionAt(t);
        string loopNote = gate == null ? "" : gate.Kind switch
        {
            SimPlayback.HoldLoopKind.ScriptedLoop =>
                $"\n\nenemy-gated script: repeats until the boss/linked enemies die.\n" +
                $"cycle {GateCycle(gate, t)} of {gate.CycleEnds.Length} kept",
            SimPlayback.HoldLoopKind.RouteLoop =>
                $"\n\nconditional route loop; {gate.CycleEnds.Length} cycles kept, " +
                "then playback continues",
            _ => $"\n\nenemy-gated hold: gameplay waits until the boss/linked enemies die.\n" +
                 $"{SimPlayback.RegionSeconds(gate)} seconds " +
                 (SimPlayback.RegionSeconds(gate) < pb.Sim.PreviewHoldSeconds
                     ? "of it were watched before the cap ended the run"
                     : "are retained for inspection"),
        };

        ImGui.SetTooltip($"{SimPlayback.FormatTime(t)}  (tick {t})\n" +
            "cyan = scroll speed  red = map stop  orange = jump/flow\n" +
            "white = level end  magenta = boss bar" +
            "\ndrag to scrub, wheel to step (shift = x10)" +
            loopNote + where);
    }
}
