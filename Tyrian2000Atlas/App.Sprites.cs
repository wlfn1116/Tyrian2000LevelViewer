using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The sprite browser: every bank in the data set laid out as an atlas -- the 36 enemy
/// banks (newsh%c.shp), the tyrian.shp sub-tables the menus and shop draw from, and the
/// terrain tile sets. Picking a sprite shows it blown up with the enemyDat entries that
/// actually name it, so a shape can be traced back to the thing that spawns it.
///
/// The controls are split by what they belong to: the top band picks the bank and the palette
/// it decodes through, and the strip over the grid says how the grid itself is drawn. Mixing
/// the two -- which is what one long toolbar did -- made a row of eight sliders none of which
/// obviously belonged to anything.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showSprites;
    private SpriteSource _sprSource = SpriteSource.Newsh(1);
    private int _sprSelected = -1;
    private float _sprZoom = 2f;
    private int _sprPalette = AppSettings.GamePalette;
    private bool _sprCheckerboard = true;
    /// <summary>Butt the cells together with no padding or backing, so a tile set reads as the
    /// one continuous sheet it is drawn from rather than as a grid of separate pictures.</summary>
    private bool _sprGapless;
    /// <summary>Number every cell with its own sprite index. Off by default: at a tight zoom
    /// the numbers are all you can see, and the tooltip already says which one is under the
    /// cursor.</summary>
    private bool _sprNumbers;
    /// <summary>Columns in the grid; 0 fits as many as the panel is wide. Fixing it is what
    /// makes a bank's own row stride visible -- 19 lines a 2x2 sheet up into whole icons.</summary>
    private int _sprCols;
    private float _sprListW = 210f;
    private readonly byte[] _sprFilter = new byte[64];
    // Two flags, not one: the bank list draws before the grid and would otherwise clear the
    // request before the grid ever saw it, so jumping to a sprite never scrolled to it.
    private bool _sprScrollBankList;
    private bool _sprScrollGrid;

    private static readonly uint AcSprite = Gfx.Rgba(150, 200, 255);

    /// <summary>The bank list's own sections, in the order it lists them.</summary>
    private static readonly (string Label, Func<SpriteSource, bool> Match)[] SpriteGroups =
    {
        ("Enemy banks", s => s.Store == SpriteStore.Newsh),
        ("Shop sheet", s => s.Store == SpriteStore.NewshFile),
        ("tyrian.shp sheets", s => s.Store == SpriteStore.MainSheet && !s.Xmas),
        ("tyrian.shp banks", s => s.Store == SpriteStore.MainBank && !s.Xmas),
        ("Christmas (tyrianc.shp)", s => s.Xmas),
        ("Terrain tiles", s => s.Store == SpriteStore.Tiles),
    };

    /// <summary>Open the browser on a bank by its position in the list -- the "--showsprites N"
    /// entry point, which is how one particular bank gets framed for a screenshot.</summary>
    public void ShowSpriteBank(int listIndex)
    {
        var all = AllSpriteSources();
        if (listIndex < 0 || listIndex >= all.Count) return;
        OpenSprite(all[listIndex], -1);
    }

    /// <summary>Open the browser on a particular sprite (the enemy browser links through here).</summary>
    private void OpenSprite(SpriteSource src, int index)
    {
        _showSprites = true;
        _sprSource = src;
        _sprSelected = index;
        _sprScrollBankList = true;
        _sprScrollGrid = index >= 0;
    }

    private void DrawSpriteWindow()
    {
        if (!_showSprites || _gd == null) return;

        if (!RefBegin("Sprites", "sprites", ref _showSprites, AcSprite,
                new Vector2(1060, 720), new Vector2(620, 380))) return;

        DrawSpriteBand();

        float maxList = Math.Max(160f, ImGui.GetContentRegionAvail().X - 340f);
        _sprListW = Math.Clamp(_sprListW, 160f, maxList);

        WellBegin("sprbanks", new Vector2(_sprListW, ImGui.GetContentRegionAvail().Y), AcSprite);
        DrawSpriteBankList();
        WellEnd();

        ImGui.SameLine(0, 3);
        VSplitter("##sprsplit", ref _sprListW, 160f, maxList);
        ImGui.SameLine(0, 3);

        ImGui.BeginChild("sprmain", new Vector2(0, 0));
        DrawSpriteSheet();
        ImGui.EndChild();

        RefEnd(AcSprite);
    }

    /// <summary>The top band: which bank, and the palette every bank decodes through.</summary>
    private void DrawSpriteBand()
    {
        BandBegin("sprband", AcSprite);

        UiFilter("##sprfilter", "filter banks", _sprFilter, 210f, AcSprite);

        BandDivider();
        BandLabel("palette");
        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt("##sprpal", ref _sprPalette, 0, _gd!.Palettes.Count - 1);
        SliderReset(ref _sprPalette, AppSettings.GamePalette,
            "Which of the game's palettes to decode through.\nGameplay always runs in palette 5.");
        if (_sprPalette != AppSettings.GamePalette)
        {
            ImGui.SameLine(0, 5);
            if (UiButton("in-game", AcSprite, "Back to palette 5, the one gameplay runs in"))
                _sprPalette = AppSettings.GamePalette;
        }

        BandDivider();
        int banks = AllSpriteSources().Count;
        bool windows = OperatingSystem.IsWindows();
        // The row stride is the one the grid strip sets, so a file out of here is the same file
        // "export sheet PNG" would have written for that bank.
        int allCols = _sprCols > 0 ? _sprCols : SheetDefaultCols;
        if (UiButton("export all sprites", AcSprite,
                $"Write all {banks} banks as PNG sheets at 1:1, {allCols} sprites to a row, into\n" +
                "one folder -- each named for the bank it came out of, replacing a file\n" +
                "already there by that name. Decoded through the palette selected here.\n" +
                "Takes about a second; the status line counts them off.",
                0f, SpriteExportBusy || !windows) && windows)
            StartSpriteExportAll(_sprPalette, allCols);

        BandDivider();
        BandNote($"{banks} banks in this data folder", UiFaint);

        BandEnd();
    }

    private void DrawSpriteBankList()
    {
        string filter = BufText(_sprFilter).Trim();
        var all = AllSpriteSources();
        bool any = false;

        foreach (var (label, match) in SpriteGroups)
        {
            var items = all.Where(match)
                .Where(s => filter.Length == 0 || s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (items.Count == 0) continue;
            any = true;
            UiSection(label, AcSprite, items.Count.ToString());

            foreach (var src in items)
            {
                bool sel = src == _sprSource;
                var box = UiRow($"##b{(int)src.Store}_{src.Index}_{src.Xmas}", sel, AcSprite, 30f);
                if (box.Clicked) { _sprSource = src; _sprSelected = -1; }
                if (box.Hovered) ImGui.SetTooltip(src.Title);
                RowText(box, 11f, src.ListTitle, src.ListNote, AcSprite, sel);
                if (sel && _sprScrollBankList) ImGui.SetScrollHereY(0.5f);
            }
        }
        if (!any) ImGui.TextDisabled("No bank matches that filter.");
        _sprScrollBankList = false;
    }

    private void DrawSpriteSheet()
    {
        var atlas = Atlas(_sprSource, _sprPalette);
        if (atlas == null || atlas.Count == 0)
        {
            UiTitle(_sprSource.ListTitle.ToUpperInvariant(), AcSprite, _sprSource.Title);
            UiEmpty("This bank could not be read", "Not present in the current data folder.", AcSprite);
            return;
        }

        int first = _sprSource.FirstIndex;
        int usable = 0;
        for (int i = first; i < atlas.Count; i++) if (atlas.Has(i)) usable++;

        UiTitle(_sprSource.ListTitle.ToUpperInvariant(), AcSprite, _sprSource.Title);
        Badge($"{usable} sprites", AcSprite);
        ImGui.SameLine(0, 5f);
        Badge($"cell {atlas.CellW}x{atlas.CellH}", Gfx.Rgba(150, 162, 185));
        ImGui.SameLine(0, 5f);
        Badge(first == 1 ? "numbered from 1" : "numbered from 0", Gfx.Rgba(150, 162, 185));

        ImGui.Dummy(new Vector2(0, 4f));
        DrawSpriteGridControls();

        // --- The detail strip is pinned to the bottom, so the grid never pushes it away. ---
        const float detailH = 148f;
        float gridH = Math.Max(90f, ImGui.GetContentRegionAvail().Y - detailH - 8f);

        // A fixed column count can be wider than the panel, so the grid needs to pan sideways.
        WellBegin("sprgrid", new Vector2(ImGui.GetContentRegionAvail().X, gridH), AcSprite,
            7f, 7f, ImGuiWindowFlags.HorizontalScrollbar);
        DrawSpriteGrid(atlas, first);
        WellEnd();

        WellBegin("sprdetail", ImGui.GetContentRegionAvail(), AcSprite, 10f, 8f);
        DrawSpriteDetail(atlas);
        WellEnd();
    }

    /// <summary>How the grid itself is drawn -- kept next to the grid rather than up in the
    /// window's own toolbar, where it read as if it applied to the bank list.</summary>
    private void DrawSpriteGridControls()
    {
        BandBegin("sprgridband", AcSprite);
        BandLabel("zoom");
        ImGui.SetNextItemWidth(112);
        ImGui.SliderFloat("##sprzoom", ref _sprZoom, 1f, 8f, "%.0fx");
        SliderReset(ref _sprZoom, 2f, "How far the grid's cells are blown up.", "2x");

        BandDivider(9f);
        BandLabel("columns");
        ImGui.SetNextItemWidth(112);
        ImGui.SliderInt("##sprcols", ref _sprCols, 0, 40, _sprCols == 0 ? "fit" : "%d");
        SliderReset(ref _sprCols, 0,
            "How many sprites per row. 0 fits as many as the panel is wide.\n" +
            "The sheets are laid out 19 to a row, so 19 (or 38) lines the\n" +
            "2x2 icons up into whole pictures.", "fit");
        ImGui.SameLine(0, 5);
        if (UiButton(_sprCols == 19 ? "fit" : "19", AcSprite,
                _sprCols == 19 ? "back to fitting the panel width" : "the sheets' own row stride"))
            _sprCols = _sprCols == 19 ? 0 : 19;

        BandDivider(9f);
        UiToggle("gapless", ref _sprGapless, AcSprite,
            "Butt the cells together with no gaps, so a tile set reads as one\n" +
            "continuous sheet. Each sprite still sits at its own cell's top-left,\n" +
            "which is where the game blits it.");
        ImGui.SameLine(0, 5);
        UiToggle("checker", ref _sprCheckerboard, AcSprite,
            _sprGapless ? "Off in gapless mode - a per-cell backing would put the gaps back."
                : "Show what is transparent rather than black.", 0f, _sprGapless);
        ImGui.SameLine(0, 5);
        UiToggle("numbers", ref _sprNumbers, AcSprite, "Print each cell's sprite index on it.");

        // Last cluster of its own: everything before it says how the grid is DRAWN, this one
        // writes a file. It sits here rather than up by the bank name because what comes out
        // is laid on "columns", which is two chips to the left.
        BandDivider(9f);
        bool windows = OperatingSystem.IsWindows();
        int sheetCols = _sprCols > 0 ? _sprCols : SheetDefaultCols;
        if (UiButton("export sheet PNG", AcSprite,
                $"Write the bank as one PNG at 1:1 -- {sheetCols} sprites to a row, cells\n" +
                "butted together and empty slots left transparent, so the file is a\n" +
                "sheet you can index rather than a picture of the grid. \"columns\"\n" +
                "sets the row stride; the palette is the one the band above selects.",
                0f, SpriteExportBusy || !windows) && windows)
            ExportSpriteSheet(_sprSource, _sprPalette, sheetCols);
        BandEnd();
    }

    private void DrawSpriteGrid(SpriteAtlas atlas, int first)
    {
        float scale = MathF.Round(_sprZoom);
        float pad = _sprGapless ? 0f : 12f;
        float cw = atlas.CellW * scale + pad;
        float ch = atlas.CellH * scale + pad;
        int cols = _sprCols > 0 ? _sprCols : Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / cw));
        int count = Math.Max(0, atlas.Count - first);
        int rows = (count + cols - 1) / cols;

        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(cols * cw, rows * ch));

        var dl = ImGui.GetWindowDrawList();
        float scrollY = ImGui.GetScrollY();
        float viewH = ImGui.GetWindowSize().Y;

        // Scroll to a sprite opened from elsewhere. Computed here rather than inside the
        // row loop below: that loop only visits rows already on screen, which is precisely
        // the case where no scrolling is needed -- a jump to a sprite further down the bank
        // never scrolled at all, and left the request pending forever.
        if (_sprScrollGrid && _sprSelected >= first && _sprSelected < atlas.Count)
        {
            int selRow = (_sprSelected - first) / cols;
            ImGui.SetScrollY(Math.Max(0f, selRow * ch - viewH * 0.4f));
            _sprScrollGrid = false;
        }

        int rowFrom = Math.Max(0, (int)(scrollY / ch) - 1);
        int rowTo = Math.Min(rows - 1, (int)((scrollY + viewH) / ch) + 1);

        // Which cell the cursor is over -- one hit test for the whole grid rather than an
        // invisible button per sprite, which would cost an ImGui id for every one of 600.
        int hover = -1;
        if (ImGui.IsWindowHovered())
        {
            var m = ImGui.GetIO().MousePos - origin;
            int c = (int)(m.X / cw), r = (int)(m.Y / ch);
            if (c >= 0 && c < cols && r >= 0 && r < rows && m.X >= 0 && m.Y >= 0)
            {
                int idx = first + r * cols + c;
                if (idx < atlas.Count) hover = idx;
            }
        }

        bool numbers = _sprNumbers && cw >= 22f;

        // Selection and hover outlines are captured in the loop and stroked once it finishes,
        // never inline. In gapless mode the cells butt together, so a cell's right and bottom
        // edges land exactly where the next cell's flat backing is drawn a few iterations
        // later -- stroked inline, those two sides were painted straight back over.
        Vector2 selMn = default, selMx = default; bool haveSel = false;
        Vector2 hovMn = default, hovMx = default; bool haveHov = false;

        for (int r = rowFrom; r <= rowTo; r++)
            for (int c = 0; c < cols; c++)
            {
                int idx = first + r * cols + c;
                if (idx >= atlas.Count) break;
                var mn = new Vector2(origin.X + c * cw, origin.Y + r * ch);
                var mx = mn + new Vector2(cw - (_sprGapless ? 0f : 3f), ch - (_sprGapless ? 0f : 3f));

                bool has = atlas.Has(idx);
                if (_sprGapless)
                {
                    // One flat backing for the whole sheet, drawn per cell only so the loop
                    // stays simple; no rounding and no inset, so the cells cannot show seams.
                    dl.AddRectFilled(mn, mx, Gfx.Rgba(16, 18, 24));
                    // Top-left, not centred: that is where the game blits, and it is what makes
                    // neighbouring tiles line up into a continuous picture.
                    if (has) atlas.Draw(dl, idx, mn, scale);
                }
                else
                {
                    if (has)
                    {
                        if (_sprCheckerboard) Checkerboard(dl, mn, mx);
                        else dl.AddRectFilled(mn, mx, Gfx.Rgba(16, 18, 24), 3f);
                    }
                    else EmptyCell(dl, mn, mx);
                    if (has) atlas.DrawCentered(dl, idx, mn, mx, scale);
                }

                // Number sits on both layouts. In gapless it lands over the tile's top-left --
                // the one corner every cell shares -- rather than in a margin there is none of.
                if (numbers && has)
                    dl.AddText(new Vector2(mn.X + 2.5f, mn.Y + 1f), Gfx.Rgba(255, 255, 255, 95),
                        idx.ToString());

                if (idx == _sprSelected) { selMn = mn; selMx = mx; haveSel = true; }
                else if (idx == hover) { hovMn = mn; hovMx = mx; haveHov = true; }
            }

        if (haveHov)
            dl.AddRect(hovMn, hovMx, Gfx.Rgba(255, 255, 255, 130), _sprGapless ? 0f : 3f);
        if (haveSel)
        {
            // One sharp 1px ring: no rounding, no corner ticks. The cell's corners fall on
            // fractional pixels (cw/ch are floats), and ImGui strokes lines half a pixel off
            // the path, so a 2px box straddled the grid and left two of its four sides a pixel
            // fatter than the others. Snapping to whole pixels and stroking a single-pixel
            // line lands it crisp and even.
            var a = new Vector2(MathF.Round(selMn.X) - 1f, MathF.Round(selMn.Y) - 1f);
            var b = new Vector2(MathF.Round(selMx.X) + 1f, MathF.Round(selMx.Y) + 1f);
            dl.AddRect(a, b, Shade(AcSprite, 1.15f));
        }

        if (hover < 0) return;
        var (w, h) = atlas.SizeOf(hover);
        ImGui.SetTooltip(w == 0 ? $"#{hover}  (empty slot)" : $"#{hover}  ·  {w}x{h} px  ·  click to inspect");
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _sprSelected = hover;
    }

    /// <summary>A slot the bank leaves empty: hatched, so it reads as "nothing here" rather
    /// than as a black sprite.</summary>
    private static void EmptyCell(ImDrawListPtr dl, Vector2 mn, Vector2 mx)
    {
        dl.AddRectFilled(mn, mx, Gfx.Rgba(21, 22, 28), 3f);
        float w = mx.X - mn.X, h = mx.Y - mn.Y;
        for (float o = -h; o < w; o += 7f)
        {
            float x0 = Math.Clamp(mn.X + o, mn.X, mx.X);
            float y0 = mn.Y + (x0 - (mn.X + o));
            float x1 = Math.Clamp(mn.X + o + h, mn.X, mx.X);
            float y1 = mn.Y + (x1 - (mn.X + o));
            if (x1 > x0) dl.AddLine(new Vector2(x0, y0), new Vector2(x1, y1), Gfx.Rgba(34, 36, 45));
        }
    }

    /// <summary>The transparency checker, in the window's own greys so it never reads as art.</summary>
    private static void Checkerboard(ImDrawListPtr dl, Vector2 mn, Vector2 mx)
    {
        dl.AddRectFilled(mn, mx, Gfx.Rgba(30, 32, 40), 3f);
        const float s = 6f;
        for (float y = mn.Y; y < mx.Y; y += s)
            for (float x = mn.X; x < mx.X; x += s)
            {
                if ((((int)((x - mn.X) / s) + (int)((y - mn.Y) / s)) & 1) == 0) continue;
                dl.AddRectFilled(new Vector2(x, y),
                    new Vector2(Math.Min(x + s, mx.X), Math.Min(y + s, mx.Y)), Gfx.Rgba(41, 44, 54));
            }
    }

    private void DrawSpriteDetail(SpriteAtlas atlas)
    {
        if (_sprSelected < 0 || !atlas.Has(_sprSelected))
        {
            UiEmpty("Click a sprite to inspect it", "Its size, and every enemy entry that names it.", AcSprite);
            return;
        }
        var (w, h) = atlas.SizeOf(_sprSelected);

        // Blown up as large as the strip allows, on integer steps so it stays pixel-crisp.
        float avail = ImGui.GetContentRegionAvail().Y;
        const float boxW = 128f;
        float big = Math.Max(1f, MathF.Floor(Math.Min(boxW / Math.Max(1, w), avail / Math.Max(1, h))));
        var at = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var boxMax = at + new Vector2(boxW, avail);
        Checkerboard(dl, at, boxMax);
        atlas.DrawCentered(dl, _sprSelected, at, boxMax, big);
        dl.AddRect(at, boxMax, Shade(AcSprite, 0.6f, 170), 3f);
        ImGui.Dummy(new Vector2(boxW, avail));
        ImGui.SameLine(0, 14);

        ImGui.BeginGroup();
        // The particulars and the export share a line: the strip is 148px tall and a row of
        // its own for one button would come straight out of the users list below.
        UiTitle($"sprite #{_sprSelected}", AcSprite, "", 1);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"{w} x {h} px  ·  shown at {big:0}x");
        ImGui.SameLine(0, 10f);
        bool windows = OperatingSystem.IsWindows();
        if (UiButton("export PNG", AcSprite,
                $"Save this sprite on its own as a {w}x{h} PNG at 1:1, in the palette\n" +
                "selected above and transparent everywhere the sprite is.",
                0f, SpriteExportBusy || !windows) && windows)
            ExportOneSprite(_sprSource, _sprPalette, _sprSelected);

        var users = SpriteUsers(_sprSource, _sprSelected);
        if (users.Count == 0)
        {
            ImGui.TextColored(ColorOf(UiFaint), _sprSource.Store == SpriteStore.Newsh
                ? "No enemyDat entry in this episode names it."
                : "Drawn by the menus rather than by an enemyDat entry.");
            ImGui.EndGroup();
            return;
        }

        UiSection($"used by {users.Count} enemy entr{(users.Count == 1 ? "y" : "ies")}", AcSprite);
        var ed = TryEnemyData();
        ImGui.BeginChild("sprusers", new Vector2(0, 0));
        float rowW = ImGui.GetContentRegionAvail().X;
        float x = 0f;
        foreach (int id in users)
        {
            const float cw2 = 78f, chh = 26f;
            // Flow layout: an item that still fits joins the row, one that does not simply
            // does not SameLine, and so starts a new one. NewLine() would add a blank row --
            // every item already ends its own line.
            if (x > 0f && x + cw2 <= rowW) ImGui.SameLine(0, 4f);
            else x = 0f;
            x += cw2 + 4f;

            var mn = ImGui.GetCursorScreenPos();
            bool hit = ImGui.InvisibleButton($"##spru{id}", new Vector2(cw2, chh));
            bool hot = ImGui.IsItemHovered();
            var mx = mn + new Vector2(cw2, chh);
            var dl2 = ImGui.GetWindowDrawList();
            dl2.AddRectFilled(mn, mx, hot ? Gfx.Rgba(46, 52, 66) : Gfx.Rgba(31, 35, 45), 4f);
            dl2.AddRect(mn, mx, hot ? Shade(AcEnemy, 0.85f, 200) : UiLineSoft, 4f);
            if (ed != null)
            {
                var d = ed.Get(id);
                var a2 = d.Loaded ? Atlas(EnemySpriteSource(d.ShapeBank), AppSettings.GamePalette) : null;
                if (a2 != null)
                    DrawEnemyFrameCentered(dl2, a2, d.EGraphic[0], d.Esize == 1,
                        mn + new Vector2(2f, 1f), new Vector2(mn.X + 30f, mx.Y - 1f), 1f);
            }
            dl2.AddText(new Vector2(mn.X + 34f, (mn.Y + mx.Y) * 0.5f - ImGui.GetTextLineHeight() * 0.5f),
                hot ? Gfx.Rgba(248, 250, 255) : UiText, $"#{id}");
            if (hot) ImGui.SetTooltip("open this enemy in the enemy browser");
            if (hit) OpenEnemy(_episodeIdx, id);
        }
        ImGui.EndChild();
        ImGui.EndGroup();
    }

    /// <summary>enemyDat entries in the shown episode whose shape bank is this one and whose
    /// frame table names this sprite -- the reverse of the enemy browser's sprite link.</summary>
    private List<int> SpriteUsers(SpriteSource src, int sprite)
    {
        var users = new List<int>();
        if (_gd == null || src.Store != SpriteStore.Newsh || CurEpisode == null) return users;
        EnemyData ed;
        try { ed = _gd.GetEnemyData(CurEpisode); } catch { return users; }

        for (int i = 0; i < ed.Enemies.Length; i++)
        {
            var e = ed.Enemies[i];
            if (!e.Loaded || e.ShapeBank != src.Index || e.EGraphic == null) continue;
            bool uses = e.Dgr == sprite;
            for (int g = 0; !uses && g < e.EGraphic.Length; g++) uses = e.EGraphic[g] == sprite;
            if (uses) users.Add(i);
            if (users.Count >= 60) break;    // a shared frame can have hundreds; the list is a pointer
        }
        return users;
    }
}
