using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;

namespace T2LV;

/// <summary>
/// The shared chrome the seven reference browsers are built out of. They used to be seven
/// piles of default ImGui widgets that each invented their own toolbar, list and heading, so
/// nothing looked related and every window taught you a different set of habits. This file is
/// the one visual language they now share: a window frame tinted with the browser's own accent
/// (the same colour as its launcher chip in the left column), a toolbar band, segmented mode
/// switches, panels, two-line list rows, stat tiles and meters.
///
/// Everything here draws through <see cref="ImDrawListPtr"/> over plain ImGui items rather than
/// theming ImGui's own widgets alone, because the parts that carry the information -- a sprite
/// thumbnail beside a name, an armour bar behind a number -- are not widgets ImGui has.
/// </summary>
public sealed unsafe partial class App
{
    // =====================================================================
    // Palette
    // =====================================================================

    private static readonly uint UiWinBg    = Gfx.Rgba(17, 19, 25, 252);
    private static readonly uint UiPanel    = Gfx.Rgba(24, 27, 35);
    private static readonly uint UiPanelHi  = Gfx.Rgba(31, 35, 45);
    private static readonly uint UiBand     = Gfx.Rgba(27, 31, 40);
    private static readonly uint UiSunken   = Gfx.Rgba(12, 14, 19);
    private static readonly uint UiLine     = Gfx.Rgba(50, 56, 71);
    private static readonly uint UiLineSoft = Gfx.Rgba(40, 45, 58);
    private static readonly uint UiText     = Gfx.Rgba(228, 233, 244);
    private static readonly uint UiDim      = Gfx.Rgba(148, 156, 174);
    private static readonly uint UiFaint    = Gfx.Rgba(106, 114, 132);

    /// <summary>Blend two packed colours; alpha comes from <paramref name="a"/>.</summary>
    private static uint Mix(uint a, uint b, float t)
    {
        static byte Ch(uint x, uint y, int shift, float t) =>
            (byte)Math.Clamp(((x >> shift) & 0xFF) * (1f - t) + ((y >> shift) & 0xFF) * t, 0f, 255f);
        return Gfx.Rgba(Ch(a, b, 0, t), Ch(a, b, 8, t), Ch(a, b, 16, t), (byte)((a >> 24) & 0xFF));
    }

    /// <summary>The same colour at a different alpha.</summary>
    private static uint Alpha(uint c, byte a) => (c & 0x00FFFFFFu) | ((uint)a << 24);

    // =====================================================================
    // Global style
    // =====================================================================

    /// <summary>
    /// The house geometry, applied once to the ImGui style at startup so that *every* window
    /// the app opens carries it -- the reference browsers, the floating playback HUD, popups
    /// and tooltips alike. Only window-level shape is set here on purpose: padding, spacing
    /// and frame metrics stay at ImGui's defaults so the main viewer's own layout is not
    /// reflowed, and the reference windows push their tighter metrics themselves.
    /// </summary>
    public static void ApplyGlobalStyle()
    {
        var st = ImGui.GetStyle();
        st.WindowRounding = 8f;
        st.WindowBorderSize = 1f;
        // ChildRounding stays at 0 on purpose: the viewer's own columns are flush against the
        // window edges and rounding them would notch the corners. The reference browsers'
        // children are transparent and sit on wells that draw their own rounded shape.
        st.PopupRounding = 7f;
        st.FrameRounding = 4f;
        st.GrabRounding = 3f;
        st.ScrollbarRounding = 5f;
        st.TabRounding = 5f;
        st.WindowTitleAlign = new Vector2(0.015f, 0.5f);
    }

    // =====================================================================
    // Window frame
    // =====================================================================

    /// <summary>
    /// One reference window's whole style, keyed by its accent. Built once per accent rather
    /// than per frame: seven windows re-pushing forty colours each is not worth the garbage.
    /// </summary>
    private static readonly Dictionary<uint, (ImGuiCol Slot, uint Col)[]> _refStyles = new();

    private static (ImGuiCol, uint)[] RefStyle(uint ac)
    {
        if (_refStyles.TryGetValue(ac, out var hit)) return hit;
        var made = new (ImGuiCol, uint)[]
        {
            (ImGuiCol.WindowBg,             UiWinBg),
            (ImGuiCol.ChildBg,              Gfx.Rgba(0, 0, 0, 0)),
            (ImGuiCol.PopupBg,              Gfx.Rgba(22, 25, 33, 250)),
            (ImGuiCol.Border,               Shade(ac, 0.52f, 120)),
            (ImGuiCol.Text,                 UiText),
            (ImGuiCol.TextDisabled,         UiDim),
            (ImGuiCol.TitleBg,              Mix(Gfx.Rgba(20, 23, 30), ac, 0.10f)),
            (ImGuiCol.TitleBgActive,        Mix(Gfx.Rgba(24, 28, 37), ac, 0.24f)),
            (ImGuiCol.TitleBgCollapsed,     Mix(Gfx.Rgba(18, 21, 27), ac, 0.08f)),
            (ImGuiCol.FrameBg,              Gfx.Rgba(33, 37, 48)),
            (ImGuiCol.FrameBgHovered,       Gfx.Rgba(44, 50, 64)),
            (ImGuiCol.FrameBgActive,        Gfx.Rgba(54, 61, 78)),
            (ImGuiCol.Button,               Gfx.Rgba(41, 46, 59)),
            (ImGuiCol.ButtonHovered,        Shade(ac, 0.44f, 225)),
            (ImGuiCol.ButtonActive,         Shade(ac, 0.62f, 245)),
            (ImGuiCol.Header,               Shade(ac, 0.30f, 105)),
            (ImGuiCol.HeaderHovered,        Shade(ac, 0.42f, 155)),
            (ImGuiCol.HeaderActive,         Shade(ac, 0.56f, 200)),
            (ImGuiCol.Separator,            UiLineSoft),
            (ImGuiCol.SeparatorHovered,     Shade(ac, 0.70f, 200)),
            (ImGuiCol.SliderGrab,           Shade(ac, 0.82f)),
            (ImGuiCol.SliderGrabActive,     Shade(ac, 1.02f)),
            (ImGuiCol.CheckMark,            Shade(ac, 1.05f)),
            (ImGuiCol.Tab,                  Gfx.Rgba(27, 31, 40)),
            (ImGuiCol.TabHovered,           Shade(ac, 0.46f, 220)),
            (ImGuiCol.TabSelected,          Shade(ac, 0.34f, 255)),
            (ImGuiCol.TabDimmed,            Gfx.Rgba(22, 25, 33)),
            (ImGuiCol.TabDimmedSelected,    Shade(ac, 0.22f, 235)),
            (ImGuiCol.ScrollbarBg,          Gfx.Rgba(0, 0, 0, 0)),
            (ImGuiCol.ScrollbarGrab,        Gfx.Rgba(46, 52, 66)),
            (ImGuiCol.ScrollbarGrabHovered, Gfx.Rgba(64, 72, 90)),
            (ImGuiCol.ScrollbarGrabActive,  Shade(ac, 0.60f)),
            (ImGuiCol.ResizeGrip,           Shade(ac, 0.45f, 80)),
            (ImGuiCol.ResizeGripHovered,    Shade(ac, 0.70f, 170)),
            (ImGuiCol.ResizeGripActive,     Shade(ac, 0.95f, 220)),
            (ImGuiCol.TextSelectedBg,       Shade(ac, 0.50f, 130)),
            (ImGuiCol.TableHeaderBg,        Gfx.Rgba(29, 33, 43)),
            (ImGuiCol.TableRowBg,           Gfx.Rgba(0, 0, 0, 0)),
            (ImGuiCol.TableRowBgAlt,        Gfx.Rgba(255, 255, 255, 8)),
            (ImGuiCol.TableBorderLight,     Gfx.Rgba(44, 50, 64)),
            (ImGuiCol.TableBorderStrong,    Gfx.Rgba(56, 63, 80)),
        };
        _refStyles[ac] = made;
        return made;
    }

    // The shape lives in ApplyGlobalStyle; these are only the metrics a browser wants tighter
    // than the rest of the app.
    private static readonly (ImGuiStyleVar V, float F)[] RefVarsF =
    {
        (ImGuiStyleVar.ScrollbarSize, 11f),
    };

    private static readonly (ImGuiStyleVar V, Vector2 F)[] RefVarsV =
    {
        (ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f)),
        (ImGuiStyleVar.FramePadding, new Vector2(7f, 3f)),
        (ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f)),
        (ImGuiStyleVar.ItemInnerSpacing, new Vector2(6f, 4f)),
        (ImGuiStyleVar.CellPadding, new Vector2(7f, 3f)),
    };

    private static void PushRefStyle(uint accent)
    {
        foreach (var (slot, col) in RefStyle(accent)) ImGui.PushStyleColor(slot, col);
        foreach (var (v, f) in RefVarsF) ImGui.PushStyleVar(v, f);
        foreach (var (v, f) in RefVarsV) ImGui.PushStyleVar(v, f);
    }

    private static void PopRefStyle(uint accent)
    {
        ImGui.PopStyleVar(RefVarsF.Length + RefVarsV.Length);
        ImGui.PopStyleColor(RefStyle(accent).Length);
    }

    /// <summary>
    /// Where each reference window sat the last time it was drawn, and the work area it was
    /// fitted to, so <see cref="RefBegin"/> can put it back inside the app window when it is
    /// reopened or when the app window has changed size under it. Carried across runs in
    /// settings.json: ImGui's own imgui.ini is switched off, so this is the only thing that
    /// remembers a window you have resized.
    /// </summary>
    private readonly Dictionary<string, RefFrame> _refGeom = new();

    /// <summary>
    /// What is remembered about one reference window. <paramref name="Drawn"/> is the frame it
    /// was last drawn on, so the next Begin can tell being opened from being drawn again;
    /// <paramref name="Opened"/> the frame it was opened on; <paramref name="Fitted"/> whether
    /// it has a size of its own yet -- see <see cref="RefBegin"/>.
    /// </summary>
    private record struct RefFrame(Vector2 Pos, Vector2 Size, Vector2 Work,
        int Drawn, int Opened, bool Fitted);

    /// <summary>The gap a reference window keeps from the edge of the app window, so its border
    /// and its resize grip are always there to be grabbed.</summary>
    private const float RefMargin = 10f;

    /// <summary>
    /// The window width each browser's toolbar bands turned out to want, measured as they were
    /// drawn (see <see cref="BandEnd"/>). A band is a fixed-size child, so a row of controls
    /// wider than the window is not squeezed or wrapped -- it is clipped, and the far end of it
    /// simply is not on screen. Feeding the measurement back as the window's minimum width is
    /// what makes a window open wide enough to show its own controls.
    /// </summary>
    private static readonly Dictionary<string, float> _bandNeed = new();

    /// <summary>The reference window being drawn and how wide it is, so the bands inside it --
    /// including the ones nested in a detail pane, which have their own narrower width -- all
    /// report what the *window* would have to be.</summary>
    private static string _bandOwner = "";
    private static float _bandOwnerW;

    /// <summary>Restore the remembered frames at startup. What is read is taken on trust; it is
    /// only fitted to the app window, in case that has changed size between runs. A size that
    /// was saved is a size somebody chose, so it counts as fitted and is never widened.</summary>
    private void LoadRefWindows(List<WindowGeom> saved)
    {
        foreach (var g in saved)
        {
            if (g.Id.Length == 0 || !(g.W > 80f) || !(g.H > 60f)) continue;
            // Work = zero: this frame was fitted to no viewport we know of, so opening it re-fits.
            _refGeom[g.Id] = new RefFrame(new Vector2(g.X, g.Y), new Vector2(g.W, g.H),
                Vector2.Zero, int.MinValue, 0, Fitted: true);
        }
    }

    private List<WindowGeom> SaveRefWindows() =>
        _refGeom.Select(kv => new WindowGeom
        {
            Id = kv.Key,
            X = kv.Value.Pos.X, Y = kv.Value.Pos.Y,
            W = kv.Value.Size.X, H = kv.Value.Size.Y,
        }).ToList();

    /// <summary>
    /// Open one reference window. The accent is the browser's identity: the same colour its
    /// launcher chip carries, running through the title bar, the selection, the sliders and
    /// every section rule inside, so a window is recognisable before a word of it is read.
    /// Returns false when the window is collapsed or hidden -- do nothing and skip RefEnd.
    ///
    /// A window is also fitted to the room the app window has. The sizes the browsers ask for
    /// were drawn up for a roomy desktop, and handed to ImGui as-is they open a window whose
    /// far edge is off the viewport -- and since ImGui takes the minimum size as a hard floor,
    /// one too wide for the viewport could not even be dragged back to a width that fits. So
    /// the room actually available caps the opening size, the minimum and every later resize,
    /// and the position is clamped so the whole frame lands inside. That happens on the frame
    /// the window opens and again if the app window is resized under it; in between, where the
    /// user dragged it is where it stays.
    ///
    /// A window with no size of its own yet is also widened to whatever its toolbar bands
    /// overran by, so that it opens showing every control it carries rather than clipping the
    /// far end of the row. That is a first fit and nothing more: the moment the window has
    /// settled at a width that fits, it is marked fitted and never resized from here again --
    /// drag it down to a size that clips half the toolbar and it will stay there, and reopen
    /// there, because a size somebody chose beats a size measured for them. A size restored
    /// from settings.json counts as chosen too.
    /// </summary>
    private bool RefBegin(string title, string id, ref bool show, uint accent,
        Vector2 size, Vector2 minSize)
    {
        PushRefStyle(accent);

        var vp = ImGui.GetMainViewport();
        var room = new Vector2(Math.Max(240f, vp.WorkSize.X - RefMargin * 2f),
                               Math.Max(160f, vp.WorkSize.Y - RefMargin * 2f));
        minSize = Vector2.Min(minSize, room);

        bool known = _refGeom.TryGetValue(id, out var was);
        int frame = ImGui.GetFrameCount();
        // Opened rather than drawn again -- or still open, but with the app window resized
        // under it, which is the one other time the frame has to be put back where it fits.
        bool opening = !known || was.Drawn < frame - 1 || was.Work != vp.WorkSize;
        int opened = opening ? frame : was.Opened;
        float need = known && was.Fitted ? 0f : Math.Min(room.X, _bandNeed.GetValueOrDefault(id));

        if (opening || need > 0f)
        {
            var want = Vector2.Clamp(known ? was.Size : size, minSize, room);
            want.X = Math.Max(want.X, need);
            // Only on the way in: a window being widened to fit its bands has been left
            // wherever it was put.
            if (opening)
            {
                var lo = vp.WorkPos + new Vector2(RefMargin, RefMargin);
                var hi = Vector2.Max(lo, vp.WorkPos + vp.WorkSize - want - new Vector2(RefMargin, RefMargin));
                ImGui.SetNextWindowPos(Vector2.Clamp(known ? was.Pos : lo + new Vector2(48f, 38f), lo, hi));
            }
            ImGui.SetNextWindowSize(want);
        }
        ImGui.SetNextWindowSizeConstraints(minSize, room);

        bool open = show;
        bool vis = ImGui.Begin($"{title}###{id}", ref open);
        show = open;
        // Fitted once the bands have had frames enough to be measured and the window is as wide
        // as they asked for -- or as wide as the app window can make it, when they asked for
        // more than there is. Either way the fitting is over and the size becomes the user's.
        bool fitted = (known && was.Fitted)
                      || (vis && frame - opened >= 4 && need <= ImGui.GetWindowWidth() + 0.5f);
        // Collapsed, ImGui reports the title bar's height as the window size, which is not a
        // size to reopen at: keep the last full one instead.
        _refGeom[id] = new RefFrame(ImGui.GetWindowPos(),
            vis ? ImGui.GetWindowSize() : known ? was.Size : size,
            vp.WorkSize, frame, opened, fitted);
        if (vis)
        {
            // Collect this frame's band measurements afresh: a band that has since lost a
            // control has to be able to give the width back, not just ask for more.
            _bandOwner = fitted ? "" : id;
            _bandOwnerW = ImGui.GetWindowWidth();
            _bandNeed[id] = 0f;
            return true;
        }
        ImGui.End();
        PopRefStyle(accent);
        return false;
    }

    private static void RefEnd(uint accent)
    {
        _bandOwner = "";
        ImGui.End();
        PopRefStyle(accent);
    }

    // =====================================================================
    // Toolbar band
    // =====================================================================

    /// <summary>The height a band needs for <paramref name="rows"/> rows of ordinary widgets.</summary>
    private static float BandHeight(int rows = 1) =>
        rows * ImGui.GetFrameHeight() + (rows - 1) * 6f + 13f;

    /// <summary>
    /// The strip of controls across the top of a browser: a rounded slab with the window's
    /// accent lit down its left edge, so the controls read as one instrument panel instead of
    /// a row of loose widgets floating on the window background.
    /// </summary>
    private static void BandBegin(string id, uint accent, int rows = 1)
    {
        float h = BandHeight(rows);
        var p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        var dl = ImGui.GetWindowDrawList();
        var q = p + new Vector2(w, h);

        FlatRect(dl, p, q, Mix(UiBand, accent, 0.07f), Mix(UiPanelHi, accent, 0.16f), 7f);
        dl.AddRect(p, q, UiLineSoft, 7f);
        dl.AddRectFilled(new Vector2(p.X + 1f, p.Y + 5f), new Vector2(p.X + 3.5f, q.Y - 5f),
            Shade(accent, 1f, 235), 2f);

        // Inset like a well: a row of chips wider than the band would otherwise clip straight
        // over the right-hand border.
        var (ip, isz) = WellInner(p, new Vector2(w, h));
        _frameStack.Add((p, new Vector2(w, h)));
        ImGui.SetCursorScreenPos(ip);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(11f - WellInset, 6.5f - WellInset));
        ImGui.BeginChild(id, isz, ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
    }

    private static void BandEnd()
    {
        // How far past its right edge the row of controls ran, if it did: ImGui's own measure
        // of this child's contents, taken before the child closes because it is the child that
        // holds it. Computed in Begin, so it describes the frame before -- which is soon
        // enough, the window grows on the frame after that and nobody sees the difference.
        //
        // Deliberately not the last item's rectangle, the obvious way to measure the current
        // frame: a control with a tooltip leaves the *tooltip's* text as the last item, and the
        // band would measure that instead and shove the window wider whenever one was hovered.
        float over = ImGui.GetScrollMaxX();

        ImGui.EndChild();
        ImGui.PopStyleVar();
        PopFrameLayout();
        ImGui.Dummy(new Vector2(0, 1f));

        // Only an overrun is reported, never "it fits, and this is what it fits in": a band
        // whose chips wrap to fill the width (the level tree's legend) would otherwise measure
        // as needing every pixel it was given, and the window could never be shrunk again.
        // Reported as what the *window* would have to be, since a band nested in a detail pane
        // is narrower than the window and widening the window widens it one for one -- and that
        // figure is the window width the overrun disappears at, so it settles in one step.
        if (over > 0.5f && _bandOwner.Length > 0)
            _bandNeed[_bandOwner] = Math.Max(_bandNeed.GetValueOrDefault(_bandOwner), _bandOwnerW + over);
    }

    /// <summary>A hairline divider between two clusters of controls inside a band.</summary>
    private static void BandDivider(float gap = 10f)
    {
        ImGui.SameLine(0, gap);
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetFrameHeight();
        ImGui.GetWindowDrawList().AddRectFilled(new Vector2(p.X, p.Y + 2f),
            new Vector2(p.X + 1f, p.Y + h - 2f), UiLine);
        ImGui.Dummy(new Vector2(1f, h));
        ImGui.SameLine(0, gap);
    }

    // =====================================================================
    // Primitives
    // =====================================================================

    /// <summary>
    /// A rounded rect lit from the top. The gradient itself has square corners, so the lit
    /// half is laid in two pieces: a properly rounded cap over the corner radius, then the
    /// fade below it where the shape's own edges are already straight.
    ///
    /// Deliberately reserved for small pressed/lit controls -- a toggle, a segment. On a large
    /// surface a gradient reads as a smear rather than as material, which is what made the
    /// first pass of this UI look over-shaded; big surfaces use <see cref="FlatRect"/>.
    /// </summary>
    private static void GradRect(ImDrawListPtr dl, Vector2 mn, Vector2 mx, uint top, uint bot, float round)
    {
        dl.AddRectFilled(mn, mx, bot, round);
        float mid = mn.Y + (mx.Y - mn.Y) * 0.55f;
        if (round >= 1f && mn.Y + round < mx.Y)
            dl.AddRectFilled(mn, new Vector2(mx.X, Math.Min(mn.Y + round, mx.Y)), top, round,
                ImDrawFlags.RoundCornersTop);
        float y0 = mn.Y + Math.Max(0f, round);
        if (mid > y0) dl.AddRectFilledMultiColor(new Vector2(mn.X, y0), new Vector2(mx.X, mid),
            top, top, bot, bot);
    }

    /// <summary>
    /// A flat panel with one lit hairline along its top edge. That single line is enough to
    /// say "raised" without shading the whole surface, and it is what every large panel in
    /// this UI uses. The line is inset by the corner radius so it never pokes out of the
    /// rounded shape.
    /// </summary>
    private static void FlatRect(ImDrawListPtr dl, Vector2 mn, Vector2 mx, uint fill, uint lip, float round)
    {
        dl.AddRectFilled(mn, mx, fill, round);
        float x0 = mn.X + round, x1 = mx.X - round;
        if (x1 > x0) dl.AddLine(new Vector2(x0, mn.Y + 0.5f), new Vector2(x1, mn.Y + 0.5f), lip);
    }

    // =====================================================================
    // Text that has to stay inside its box
    // =====================================================================

    /// <summary>
    /// The base font is ProggyClean, a pixel font drawn for exactly 13px: its stems only land
    /// on the pixel grid at whole multiples of that size, and anything in between -- 1.15x,
    /// 1.32x -- rasterises soft. So "larger text" here means exactly 2x, never 1.4x.
    ///
    /// Every helper that draws at a size takes a whole <c>multiple</c> rather than a free
    /// float, so a soft size is not something a caller can ask for by accident. That is the
    /// only way the rule holds across seven browsers and a graph view.
    /// </summary>
    private static float UiFontSize(int multiple) => ImGui.GetFontSize() * Math.Max(1, multiple);

    /// <summary>The base font measured at a whole multiple of its size.</summary>
    private static Vector2 Measure(string text, int multiple) =>
        ImGui.CalcTextSize(text) * Math.Max(1, multiple);

    /// <summary>
    /// Draw text at a whole multiple of the base size. 1x takes ImGui's own path; larger sizes
    /// name the font explicitly, which is what re-bakes the glyphs at that size rather than
    /// stretching the 13px ones.
    /// </summary>
    private static void ScaledText(ImDrawListPtr dl, Vector2 pos, uint col, int multiple, string text)
    {
        if (multiple <= 1) { dl.AddText(pos, col, text); return; }
        Span<byte> buf = stackalloc byte[192];
        int n = System.Text.Encoding.UTF8.GetBytes(text.AsSpan(0, Math.Min(text.Length, 180)), buf);
        fixed (byte* p = buf)
            dl.AddText(ImGui.GetFont(), UiFontSize(multiple), pos, col, p, p + n);
    }

    /// <summary>
    /// Draw text that must not leave a box: cut to <paramref name="maxW"/> and finished with
    /// an ellipsis, so a value too long for its column reads as truncated rather than as
    /// spilling over whatever is drawn beside it.
    /// </summary>
    private static void ClipText(ImDrawListPtr dl, Vector2 at, float maxW, uint col, string text)
    {
        if (text.Length == 0 || maxW <= 2f) return;
        if (ImGui.CalcTextSize(text).X <= maxW) { dl.AddText(at, col, text); return; }

        const string cut = "...";
        float cutW = ImGui.CalcTextSize(cut).X;
        if (cutW > maxW) return;
        // Longest prefix that still leaves room for the ellipsis.
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid]).X + cutW <= maxW) lo = mid; else hi = mid - 1;
        }
        if (lo > 0) dl.AddText(at, col, text[..lo].TrimEnd() + cut);
    }

    /// <summary>The <see cref="ScaledText"/> form of <see cref="ClipText"/>, for headings.</summary>
    private static void ClipScaled(ImDrawListPtr dl, Vector2 at, float maxW, uint col, int multiple, string text)
    {
        if (text.Length == 0 || maxW <= 2f) return;
        if (multiple <= 1) { ClipText(dl, at, maxW, col, text); return; }
        if (ImGui.CalcTextSize(text).X * multiple <= maxW) { ScaledText(dl, at, col, multiple, text); return; }

        const string cut = "...";
        float cutW = ImGui.CalcTextSize(cut).X * multiple;
        if (cutW > maxW) return;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid]).X * multiple + cutW <= maxW) lo = mid; else hi = mid - 1;
        }
        if (lo > 0) ScaledText(dl, at, col, multiple, text[..lo].TrimEnd() + cut);
    }

    /// <summary>An inset well: where a list, a grid or a body of text lives.</summary>
    private static void Well(ImDrawListPtr dl, Vector2 mn, Vector2 mx, uint accent, float round = 7f)
    {
        dl.AddRectFilled(mn, mx, UiSunken, round);
        dl.AddRect(mn, mx, Mix(UiLineSoft, accent, 0.22f), round);
    }

    /// <summary>
    /// How far inside a frame its scrolling contents must sit.
    ///
    /// A child window clips its contents to its own rectangle, and a child laid over a frame
    /// of the same rectangle therefore clips exactly ON the frame -- so a row scrolling past
    /// the top edge paints over the border, and the corners, where the rounded fill is absent,
    /// show content floating outside the shape. Three pixels is enough for both: it clears the
    /// 1px border, and it clears the deepest point of a corner arc, which for radius r sits
    /// r(1 - 1/sqrt2) ~= 0.29r from the corner, i.e. ~2px at the radii used here.
    /// </summary>
    private const float WellInset = 3f;

    /// <summary>The rect a frame's scrolling contents may occupy, given the frame's own.</summary>
    private static (Vector2 Pos, Vector2 Size) WellInner(Vector2 pos, Vector2 size) =>
        (pos + new Vector2(WellInset, WellInset),
         new Vector2(Math.Max(8f, size.X - WellInset * 2f), Math.Max(8f, size.Y - WellInset * 2f)));

    /// <summary>
    /// A well with a scrolling child inside it. The child's own background is transparent (the
    /// window style sets it so), which is what lets the well show through; the padding is
    /// tighter than the window's, because a list column can be 190px wide and cannot spare the
    /// window's own 12px gutters on both sides.
    /// </summary>
    /// <summary>Outer rects of the frames currently open, so each End can put the layout
    /// cursor back where a full-size child would have left it.</summary>
    private static readonly List<(Vector2 Pos, Vector2 Size)> _frameStack = new();

    private static void WellBegin(string id, Vector2 size, uint accent, float padX = 8f, float padY = 7f,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        // A pane whose header ran taller than the pane leaves nothing behind it; floor the
        // size rather than hand ImGui a zero (which means "fill") or a negative one.
        size = new Vector2(Math.Max(24f, size.X), Math.Max(24f, size.Y));
        var p = ImGui.GetCursorScreenPos();
        Well(ImGui.GetWindowDrawList(), p, p + size, accent);

        var (ip, isz) = WellInner(p, size);
        _frameStack.Add((p, size));
        ImGui.SetCursorScreenPos(ip);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
            new Vector2(Math.Max(2f, padX - WellInset), Math.Max(2f, padY - WellInset)));
        ImGui.BeginChild(id, isz, ImGuiChildFlags.AlwaysUseWindowPadding, flags);
    }

    private static void WellEnd()
    {
        ImGui.EndChild();
        ImGui.PopStyleVar();
        PopFrameLayout();
    }

    /// <summary>
    /// Close an inset frame: re-consume the frame's whole outer rect so that whatever follows
    /// -- or SameLines beside it -- lands exactly where it would have if the child had filled
    /// the frame. Without this the 3px inset walks every pane beside a well out of alignment.
    /// </summary>
    private static void PopFrameLayout()
    {
        if (_frameStack.Count == 0) return;
        var (p, size) = _frameStack[^1];
        _frameStack.RemoveAt(_frameStack.Count - 1);
        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(size);
    }

    /// <summary>A raised card: where a self-contained block of facts lives.</summary>
    private static void Card(ImDrawListPtr dl, Vector2 mn, Vector2 mx, uint accent, float lit = 0.07f)
    {
        FlatRect(dl, mn, mx, Mix(UiPanel, accent, lit), Mix(UiPanelHi, accent, lit + 0.12f), 6f);
        dl.AddRect(mn, mx, Mix(UiLineSoft, accent, 0.25f), 6f);
    }

    /// <summary>A horizontal meter: the fraction filled in the accent over a sunken track.</summary>
    private static void MeterBar(ImDrawListPtr dl, Vector2 mn, Vector2 mx, float frac, uint accent,
        uint? trackCol = null)
    {
        float h = mx.Y - mn.Y;
        float r = Math.Min(3f, h * 0.5f);
        dl.AddRectFilled(mn, mx, trackCol ?? Gfx.Rgba(28, 31, 40), r);
        frac = Math.Clamp(frac, 0f, 1f);
        if (frac <= 0.0001f) return;
        float w = Math.Max(2f, (mx.X - mn.X) * frac);
        var fill = new Vector2(mn.X + w, mx.Y);
        dl.AddRectFilled(mn, fill, Shade(accent, 0.72f, 245), r);
        // A brighter lip at the leading edge, so a short bar still reads as a bar.
        dl.AddRectFilled(new Vector2(Math.Max(mn.X, fill.X - 2.5f), mn.Y), fill, Shade(accent, 1.1f), r);
    }

    /// <summary>Draw-list badge: a tiny outlined chip used inside rows and on canvas nodes.</summary>
    private static Vector2 BadgeAt(ImDrawListPtr dl, Vector2 at, string text, uint accent, float alpha = 1f)
    {
        var pad = new Vector2(4.5f, 1.5f);
        var size = ImGui.CalcTextSize(text) + pad * 2f;
        byte a = (byte)Math.Clamp(alpha * 255f, 0f, 255f);
        dl.AddRectFilled(at, at + size, Shade(accent, 0.28f, (byte)(a * 0.92f)), 3f);
        dl.AddRect(at, at + size, Shade(accent, 0.70f, (byte)(a * 0.75f)), 3f);
        dl.AddText(at + pad, Shade(accent, 1.12f, a), text);
        return size;
    }

    /// <summary>The inline form of <see cref="BadgeAt"/>, laid out by the cursor.</summary>
    private static void Badge(string text, uint accent)
    {
        var size = BadgeAt(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), text, accent);
        ImGui.Dummy(size);
    }

    // =====================================================================
    // Headings
    // =====================================================================

    /// <summary>
    /// A section rule: a small accent tick, the label in the accent, and a hairline running
    /// out to the right edge. Replaces ImGui's SeparatorText, which centres its label and
    /// makes a column of sections read as a ladder rather than as a list of headings.
    /// </summary>
    private static void UiSection(string text, uint accent, string right = "")
    {
        ImGui.Dummy(new Vector2(0, 2f));
        var p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        var dl = ImGui.GetWindowDrawList();
        float lh = ImGui.GetTextLineHeight();

        dl.AddRectFilled(new Vector2(p.X, p.Y + 2f), new Vector2(p.X + 2.5f, p.Y + lh - 1f),
            Shade(accent, 1f, 230), 1f);
        var sz = ImGui.CalcTextSize(text);
        dl.AddText(new Vector2(p.X + 8f, p.Y), Shade(accent, 1.05f), text);

        float ruleX = p.X + 8f + sz.X + 8f;
        float ruleEnd = p.X + w;
        if (right.Length > 0)
        {
            var rsz = ImGui.CalcTextSize(right);
            dl.AddText(new Vector2(p.X + w - rsz.X, p.Y), UiFaint, right);
            ruleEnd -= rsz.X + 8f;
        }
        if (ruleEnd > ruleX)
            dl.AddRectFilled(new Vector2(ruleX, p.Y + lh * 0.5f),
                new Vector2(ruleEnd, p.Y + lh * 0.5f + 1f), UiLineSoft);

        ImGui.Dummy(new Vector2(w, lh + 3f));
    }

    /// <summary>
    /// The title block a detail pane opens with: name in the accent over a rule, with an
    /// optional line of particulars under it. <paramref name="multiple"/> is a whole multiple
    /// of the base font size -- see <see cref="UiFontSize"/> for why it is not a free scale.
    /// </summary>
    private static void UiTitle(string text, uint accent, string sub = "", int multiple = 2,
        float maxW = 0f)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        // maxW matters inside a tooltip: an auto-resizing window has no meaningful content
        // width until it has been laid out once, so the caller states the column instead.
        float avail = maxW > 0f ? maxW : Math.Max(24f, ImGui.GetContentRegionAvail().X);
        var size = Measure(text, multiple);
        float drawn = Math.Min(size.X, avail);

        ClipScaled(dl, p, avail, Shade(accent, 1.08f), multiple, text);
        dl.AddRectFilled(new Vector2(p.X, p.Y + size.Y + 2f),
            new Vector2(p.X + Math.Max(drawn, 48f), p.Y + size.Y + 4f), Shade(accent, 1f, 165), 1f);
        ImGui.Dummy(new Vector2(drawn, size.Y + 6f));
        if (sub.Length > 0) ImGui.TextDisabled(sub);
    }

    // =====================================================================
    // Controls
    // =====================================================================

    /// <summary>
    /// A segmented switch: one slab, one lit segment. Replaces the runs of radio buttons the
    /// browsers used for their two- and three-way modes, which spent a lot of width saying
    /// very little and never made it obvious the options were alternatives.
    /// </summary>
    private static bool SegBar(string id, ref int value, uint accent, float width,
        params (string Label, string Tip)[] segs)
    {
        if (segs.Length == 0) return false;
        float h = ImGui.GetFrameHeight();
        if (width <= 0f)
        {
            width = 0f;
            foreach (var s in segs) width += ImGui.CalcTextSize(s.Label).X + 22f;
            width += 6f;
        }
        float seg = (width - 4f) / segs.Length;

        var p = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(width, h), Gfx.Rgba(19, 22, 29), 5f);
        dl.AddRect(p, p + new Vector2(width, h), UiLineSoft, 5f);

        bool changed = false;
        int hot = -1;
        for (int i = 0; i < segs.Length; i++)
        {
            ImGui.SetCursorScreenPos(new Vector2(p.X + 2f + seg * i, p.Y));
            if (ImGui.InvisibleButton($"{id}s{i}", new Vector2(seg, h))) { value = i; changed = true; }
            if (ImGui.IsItemHovered())
            {
                hot = i;
                if (segs[i].Tip.Length > 0) ImGui.SetTooltip(segs[i].Tip);
            }
        }

        for (int i = 0; i < segs.Length; i++)
        {
            var a = new Vector2(p.X + 2f + seg * i, p.Y + 2f);
            var b = new Vector2(a.X + seg, p.Y + h - 2f);
            bool on = i == value;
            if (on) GradRect(dl, a, b, Shade(accent, 0.52f, 245), Shade(accent, 0.38f, 238), 4f);
            else if (i == hot) dl.AddRectFilled(a, b, Gfx.Rgba(255, 255, 255, 16), 4f);
            var sz = ImGui.CalcTextSize(segs[i].Label);
            ClipText(dl, new Vector2(a.X + Math.Max(3f, (seg - sz.X) * 0.5f),
                    a.Y + (b.Y - a.Y - sz.Y) * 0.5f), seg - 6f,
                on ? Gfx.Rgba(250, 252, 255) : i == hot ? UiText : UiDim, segs[i].Label);
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(width, h));
        return changed;
    }

    /// <summary>
    /// A compact on/off pill for a band. Same idea as the playback HUD's <see cref="Chip"/>,
    /// but sized to the label so a row of them packs tightly, and with a lit dot rather than
    /// a lit edge because band chips sit shoulder to shoulder.
    /// </summary>
    private static bool UiToggle(string label, ref bool on, uint accent, string tip = "", float width = 0f,
        bool disabled = false)
    {
        float h = ImGui.GetFrameHeight();
        var sz = ImGui.CalcTextSize(label);
        float w = width > 0f ? width : sz.X + 30f;

        var p = ImGui.GetCursorScreenPos();
        // Not ImGui.BeginDisabled: that also swallows the hover, and the tooltip on a chip you
        // cannot press is exactly where it says WHY you cannot press it.
        bool hit = ImGui.InvisibleButton($"##tg{label}", new Vector2(w, h)) && !disabled;
        bool hot = ImGui.IsItemHovered();
        if (hit) on = !on;
        if (hot && tip.Length > 0) ImGui.SetTooltip(tip);

        var dl = ImGui.GetWindowDrawList();
        var q = p + new Vector2(w, h);
        bool lit = on && !disabled;
        // One of the two places a gradient survives: on something this small it reads as a
        // key that has been pressed in, which is exactly the message.
        if (lit) GradRect(dl, p, q, Shade(accent, 0.46f, 240), Shade(accent, 0.34f, 232), 4f);
        else dl.AddRectFilled(p, q, disabled ? Gfx.Rgba(26, 29, 37)
            : hot ? Gfx.Rgba(48, 54, 68) : Gfx.Rgba(34, 38, 49), 4f);
        dl.AddRect(p, q, lit ? Shade(accent, 0.85f, 200) : disabled ? Gfx.Rgba(36, 40, 51)
            : hot ? UiLine : UiLineSoft, 4f);

        float cy = (p.Y + q.Y) * 0.5f;
        dl.AddCircleFilled(new Vector2(p.X + 10f, cy), 3.2f,
            lit ? Shade(accent, 1.15f) : disabled ? Gfx.Rgba(50, 55, 68) : Gfx.Rgba(70, 77, 94));
        if (lit) dl.AddCircle(new Vector2(p.X + 10f, cy), 5.2f, Shade(accent, 1f, 90));
        dl.AddText(new Vector2(p.X + 19f, cy - sz.Y * 0.5f),
            disabled ? Gfx.Rgba(82, 88, 104) : lit ? Gfx.Rgba(248, 250, 255) : hot ? UiText : UiDim, label);
        return hit;
    }

    /// <summary>
    /// A flat action button that matches the toggles beside it. It draws its own disabled
    /// state rather than leaning on BeginDisabled, so a button that cannot be pressed also
    /// looks like one.
    /// </summary>
    private static bool UiButton(string label, uint accent, string tip = "", float width = 0f,
        bool disabled = false)
    {
        float h = ImGui.GetFrameHeight();
        var sz = ImGui.CalcTextSize(label);
        float w = width > 0f ? width : sz.X + 20f;
        var p = ImGui.GetCursorScreenPos();
        bool hit = ImGui.InvisibleButton($"##bt{label}", new Vector2(w, h)) && !disabled;
        bool hot = !disabled && ImGui.IsItemHovered();
        bool held = !disabled && ImGui.IsItemActive();
        if (hot && tip.Length > 0) ImGui.SetTooltip(tip);

        var dl = ImGui.GetWindowDrawList();
        var q = p + new Vector2(w, h);
        dl.AddRectFilled(p, q, disabled ? Gfx.Rgba(28, 31, 40) : held ? Shade(accent, 0.55f, 240)
            : hot ? Gfx.Rgba(52, 59, 74) : Gfx.Rgba(36, 41, 52), 4f);
        dl.AddRect(p, q, disabled ? Gfx.Rgba(38, 42, 54) : hot ? Shade(accent, 0.75f, 190) : UiLineSoft, 4f);
        dl.AddText(new Vector2(p.X + (w - sz.X) * 0.5f, (p.Y + q.Y) * 0.5f - sz.Y * 0.5f),
            disabled ? Gfx.Rgba(84, 90, 106) : hot ? Gfx.Rgba(246, 249, 255) : UiText, label);
        return hit;
    }

    /// <summary>
    /// The filter box, dressed: a magnifier tick in the accent and a clear button that only
    /// appears once there is something to clear.
    /// </summary>
    private bool UiFilter(string id, string hint, byte[] buf, float width, uint accent, bool focus = false)
    {
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetFrameHeight();
        var dl = ImGui.GetWindowDrawList();
        bool any = buf[0] != 0;

        // The lens sits in the frame's own left padding, so the caret still starts after it.
        float cy = p.Y + h * 0.5f;
        dl.AddCircle(new Vector2(p.X + 10f, cy - 1f), 3.4f, Shade(accent, any ? 1.05f : 0.75f, 210), 0, 1.5f);
        dl.AddLine(new Vector2(p.X + 12.4f, cy + 1.4f), new Vector2(p.X + 15f, cy + 4f),
            Shade(accent, any ? 1.05f : 0.75f, 210), 1.5f);

        ImGui.Dummy(new Vector2(18f, h));
        ImGui.SameLine(0, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2f, 3f));
        // Immediately before the input itself: SetKeyboardFocusHere aims at the NEXT item, and
        // the lens above already spent one.
        if (focus) ImGui.SetKeyboardFocusHere();
        bool changed = FilterBox(id, hint, buf, Math.Max(40f, width - 18f - (any ? 22f : 0f)));
        ImGui.PopStyleVar();

        if (!any) return changed;
        ImGui.SameLine(0, 2f);
        var cp = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton($"{id}clr", new Vector2(20f, h))) { buf[0] = 0; changed = true; }
        bool hot = ImGui.IsItemHovered();
        var c = new Vector2(cp.X + 10f, cp.Y + h * 0.5f);
        uint xc = hot ? Gfx.Rgba(255, 170, 170) : UiDim;
        dl.AddLine(c - new Vector2(3.5f, 3.5f), c + new Vector2(3.5f, 3.5f), xc, 1.6f);
        dl.AddLine(c - new Vector2(-3.5f, 3.5f), c + new Vector2(-3.5f, 3.5f), xc, 1.6f);
        if (hot) ImGui.SetTooltip("clear the filter");
        return changed;
    }

    /// <summary>
    /// Lay a run of <see cref="UiToggle"/> chips out across the width they have, and say how
    /// many rows that takes. The caller needs the row count BEFORE it opens the band -- a band
    /// is a fixed-height child, so a row that did not fit would simply be clipped away, which
    /// is what the tree legend used to do to its last few branch kinds on a narrow window.
    /// Returns, per chip, whether it starts a new row.
    /// </summary>
    private static bool[] PackChips(IReadOnlyList<string> labels, float avail, out int rows, float gap = 4f)
    {
        var newRow = new bool[labels.Count];
        rows = labels.Count > 0 ? 1 : 0;
        float x = 0f;
        for (int i = 0; i < labels.Count; i++)
        {
            float w = ImGui.CalcTextSize(labels[i]).X + 30f;   // must match UiToggle's own width
            if (i > 0 && x + gap + w > avail) { newRow[i] = true; rows++; x = 0f; }
            x += (x > 0f ? gap : 0f) + w;
        }
        return newRow;
    }

    /// <summary>The width a band's contents get, measured before the band is opened.</summary>
    private static float BandInnerWidth() => Math.Max(40f, ImGui.GetContentRegionAvail().X - 24f);

    /// <summary>A label in front of a control inside a band, aligned to the frame.</summary>
    private static void BandLabel(string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ColorOf(UiFaint), text);
        ImGui.SameLine(0, 6f);
    }

    /// <summary>
    /// The word a band ends on: what device is open, how many banks were found, what the
    /// numbers behind it mean. A note is not a control, so it takes whatever room the controls
    /// have left and is cut with an ellipsis there -- ImGui does not wrap a band, and left to
    /// run a long device name would either be clipped off the right-hand edge or, now that a
    /// band's measured width is the window's minimum (see <see cref="BandEnd"/>), drag the
    /// whole window wider to carry a sentence.
    /// </summary>
    private static void BandNote(string text, uint col)
    {
        ImGui.AlignTextToFramePadding();
        float room = Math.Max(0f, ImGui.GetContentRegionAvail().X);
        var at = ImGui.GetCursorScreenPos();
        ClipText(ImGui.GetWindowDrawList(),
            new Vector2(at.X, at.Y + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) * 0.5f),
            room, col, text);
        // The item laid out is what the note asks the window for, which is not what it drew:
        // a fixed allowance, so a sentence is cut here instead of dragging the window wider,
        // but a note is never squeezed out of existence either. The text is drawn to `room`
        // regardless -- given more, it fills it.
        ImGui.Dummy(new Vector2(Math.Min(ImGui.CalcTextSize(text).X, BandNoteRoom),
            ImGui.GetTextLineHeight()));
    }

    /// <summary>What a <see cref="BandNote"/> is worth widening a window for.</summary>
    private const float BandNoteRoom = 120f;

    // =====================================================================
    // List rows
    // =====================================================================

    /// <summary>What a two-line row hands back so the caller can draw into it.</summary>
    private readonly record struct RowBox(Vector2 Min, Vector2 Max, bool Clicked, bool Hovered, bool Selected);

    /// <summary>
    /// One list row: an accent edge, a hover wash and a selected slab, all drawn by hand so
    /// every browser's list looks the same and a row can carry a sprite thumbnail. The caller
    /// fills the returned box with a thumbnail and two lines of text.
    /// </summary>
    private static RowBox UiRow(string id, bool selected, uint accent, float height, float indent = 0f)
    {
        var mn = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        if (indent > 0f) ImGui.Indent(indent);
        // The row consumes exactly `height` -- no trailing item spacing -- so a virtualised
        // list can compute "row k starts at k * height" and actually be right. The visible gap
        // between rows is the 3px the drawn box is inset by, not layout spacing.
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
        bool clicked = ImGui.Selectable(id, false, ImGuiSelectableFlags.None, new Vector2(0, height));
        ImGui.PopStyleVar();
        if (indent > 0f) ImGui.Unindent(indent);
        bool hovered = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        var a = new Vector2(mn.X + indent, mn.Y);
        var b = new Vector2(mn.X + w, mn.Y + height - 3f);

        if (selected)
        {
            dl.AddRectFilled(a, b, Shade(accent, 0.26f, 140), 5f);
            dl.AddRect(a, b, Shade(accent, 0.80f, 165), 5f);
        }
        else if (hovered) dl.AddRectFilled(a, b, Gfx.Rgba(255, 255, 255, 14), 5f);

        dl.AddRectFilled(new Vector2(a.X + 1.5f, a.Y + 3f), new Vector2(a.X + 4f, b.Y - 3f),
            Shade(accent, selected ? 1.15f : 0.95f, selected ? (byte)255 : (byte)205), 2f);
        return new RowBox(a, b, clicked, hovered, selected);
    }

    /// <summary>
    /// The two lines a row carries: a bright title over a dim, accent-tinted note, both cut to
    /// the room actually left between <paramref name="x"/> and the row's right edge.
    /// <paramref name="reserve"/> is what a <see cref="RowTrail"/> on the same row will want --
    /// without it a long level name runs straight under the count at the end.
    /// </summary>
    private static void RowText(in RowBox box, float x, string title, string sub, uint accent,
        bool selected = false, float reserve = 12f)
    {
        var dl = ImGui.GetWindowDrawList();
        float h = box.Max.Y - box.Min.Y;
        float lh = ImGui.GetTextLineHeight();
        float top = box.Min.Y + (h - (sub.Length > 0 ? lh * 2f + 1f : lh)) * 0.5f;
        float room = box.Max.X - (box.Min.X + x) - reserve;
        ClipText(dl, new Vector2(box.Min.X + x, top), room,
            selected ? Gfx.Rgba(250, 252, 255) : UiText, title);
        if (sub.Length > 0)
            ClipText(dl, new Vector2(box.Min.X + x, top + lh + 1f), room, Shade(accent, 1f, 195), sub);
    }

    /// <summary>Right-aligned trailing text on a row -- counts, prices, times. The width it
    /// takes is what a matching <see cref="RowText"/> should reserve.</summary>
    private static void RowTrail(in RowBox box, string text, uint col)
    {
        var sz = ImGui.CalcTextSize(text);
        ImGui.GetWindowDrawList().AddText(
            new Vector2(box.Max.X - sz.X - 8f, (box.Min.Y + box.Max.Y) * 0.5f - sz.Y * 0.5f), col, text);
    }

    /// <summary>What a <see cref="RowTrail"/> of this text will occupy, for RowText's reserve.</summary>
    private static float TrailRoom(string text) => ImGui.CalcTextSize(text).X + 18f;

    // =====================================================================
    // Stat tiles
    // =====================================================================

    /// <summary>
    /// A number worth looking at: the label small and dim, the value large in the accent, and
    /// an optional meter under it giving the value a scale. Sized by the caller so a row of
    /// them can be laid out in a table cell or by SameLine.
    /// </summary>
    private static void StatTile(string label, string value, uint accent, float w, float h,
        string tip = "", float frac = -1f, string foot = "")
    {
        var p = ImGui.GetCursorScreenPos();
        var q = p + new Vector2(w, h);
        var dl = ImGui.GetWindowDrawList();
        float lh = ImGui.GetTextLineHeight();
        Card(dl, p, q, accent, 0.06f);
        dl.AddRectFilled(new Vector2(p.X, p.Y + 5f), new Vector2(p.X + 2.5f, q.Y - 5f),
            Shade(accent, 1f, 200), 2f);

        ClipText(dl, new Vector2(p.X + 10f, p.Y + 6f), w - 20f, UiFaint, label);
        // 2x the base size exactly: the value is the one thing on the tile meant to be read
        // from across the room, and a fractional scale would only make it soft.
        ClipScaled(dl, new Vector2(p.X + 9f, p.Y + 7f + lh), w - 18f,
            Shade(accent, 1.12f), 2, value);

        if (frac >= 0f)
            MeterBar(dl, new Vector2(p.X + 10f, q.Y - 11f), new Vector2(q.X - 10f, q.Y - 7f),
                frac, accent);
        else if (foot.Length > 0)
            ClipText(dl, new Vector2(p.X + 10f, q.Y - lh - 5f), w - 20f, UiFaint, foot);

        ImGui.Dummy(new Vector2(w, h));
        if (tip.Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    /// <summary>The height <see cref="StatTile"/> wants: 6 top pad, the label, the 2x value,
    /// then the meter and 7 bottom pad.</summary>
    private static float StatTileH() => ImGui.GetTextLineHeight() * 3f + 24f;

    /// <summary>
    /// A labelled bar for a detail pane: the name, a meter, and the raw number, on one line
    /// so a column of them compares at a glance.
    /// </summary>
    private static void UiStatBar(string label, float value, float max, uint accent,
        string? shown = null, float labelW = 92f, float barW = 168f)
    {
        float lh = ImGui.GetTextLineHeight();
        var p = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(new Vector2(p.X, p.Y), UiDim, label);
        var bar = new Vector2(p.X + labelW, p.Y + lh * 0.5f - 4f);
        MeterBar(dl, bar, bar + new Vector2(barW, 8f), max > 0 ? value / max : 0f, accent);
        dl.AddText(new Vector2(bar.X + barW + 8f, p.Y), Shade(accent, 1.05f), shown ?? $"{value:0.##}");
        ImGui.Dummy(new Vector2(labelW + barW + 8f + 44f, lh + 2f));
    }

    /// <summary>A key/value line: dim key, bright value, aligned in a column.</summary>
    private static void KV(string key, string value, uint valueCol = 0, float keyW = 108f)
    {
        var p = ImGui.GetCursorScreenPos();
        float avail = Math.Max(24f, ImGui.GetContentRegionAvail().X);
        var dl = ImGui.GetWindowDrawList();
        ClipText(dl, p, keyW - 6f, UiFaint, key);
        ClipText(dl, new Vector2(p.X + keyW, p.Y), avail - keyW - 4f,
            valueCol == 0 ? UiText : valueCol, value);
        ImGui.Dummy(new Vector2(Math.Min(avail, keyW + ImGui.CalcTextSize(value).X),
            ImGui.GetTextLineHeight()));
    }

    /// <summary>
    /// A hint line at the bottom of a canvas or list: the controls that are not visible as
    /// widgets. Dim enough to ignore, present enough to find.
    /// </summary>
    private static void UiHint(ImDrawListPtr dl, Vector2 at, string text, uint accent)
    {
        var sz = ImGui.CalcTextSize(text);
        var pad = new Vector2(7f, 3f);
        dl.AddRectFilled(at, at + sz + pad * 2f, Gfx.Rgba(14, 16, 21, 205), 5f);
        dl.AddRect(at, at + sz + pad * 2f, Shade(accent, 0.5f, 90), 5f);
        dl.AddText(at + pad, UiDim, text);
    }

    /// <summary>The empty state a list or pane shows instead of a blank rectangle.</summary>
    private static void UiEmpty(string title, string detail, uint accent)
    {
        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 40f || avail.Y < 30f) { ImGui.TextDisabled(title); return; }
        var p = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        // Base size exactly. This asked for 1.12x, which bought a point and a half of height
        // and cost every glyph its hard edges -- see UiFontSize. The accent against the faint
        // detail line under it is what separates the two, not the size.
        var tsz = ImGui.CalcTextSize(title);
        float cy = p.Y + Math.Min(avail.Y * 0.38f, 120f);
        ClipText(dl, new Vector2(p.X + Math.Max(0f, (avail.X - tsz.X) * 0.5f), cy),
            avail.X, Shade(accent, 0.95f, 210), title);
        if (detail.Length > 0)
        {
            var dsz = ImGui.CalcTextSize(detail);
            ClipText(dl, new Vector2(p.X + Math.Max(0f, (avail.X - dsz.X) * 0.5f), cy + tsz.Y + 6f),
                avail.X, UiFaint, detail);
        }
        ImGui.Dummy(avail);
    }
}
