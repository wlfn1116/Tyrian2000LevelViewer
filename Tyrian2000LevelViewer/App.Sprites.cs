using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;

namespace T2LV;

/// <summary>
/// The sprite browser: every bank in the data set laid out as an atlas -- the 36 enemy
/// banks (newsh%c.shp), the tyrian.shp sub-tables the menus and shop draw from, and the
/// terrain tile sets. Picking a sprite shows it blown up with the enemyDat entries that
/// actually name it, so a shape can be traced back to the thing that spawns it.
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

        ImGui.SetNextWindowSize(new Vector2(1000, 680), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(560, 340), new Vector2(float.MaxValue, float.MaxValue));
        bool open = _showSprites;
        if (!ImGui.Begin("Sprites###sprites", ref open)) { ImGui.End(); _showSprites = open; return; }
        _showSprites = open;

        DrawSpriteToolbar();
        ImGui.Separator();

        float maxList = Math.Max(150f, ImGui.GetContentRegionAvail().X - 320f);
        _sprListW = Math.Clamp(_sprListW, 150f, maxList);

        ImGui.BeginChild("sprbanks", new Vector2(_sprListW, 0), ImGuiChildFlags.Borders);
        DrawSpriteBankList();
        ImGui.EndChild();
        ImGui.SameLine(0, 2);
        VSplitter("##sprsplit", ref _sprListW, 150f, maxList);
        ImGui.SameLine(0, 2);

        ImGui.BeginChild("sprmain", new Vector2(0, 0));
        DrawSpriteSheet();
        ImGui.EndChild();

        ImGui.End();
    }

    private void DrawSpriteToolbar()
    {
        ImGui.SetNextItemWidth(120);
        ImGui.SliderFloat("zoom", ref _sprZoom, 1f, 8f, "%.0fx");
        ImGui.SameLine(0, 12);
        ImGui.SetNextItemWidth(110);
        ImGui.SliderInt("palette", ref _sprPalette, 0, _gd!.Palettes.Count - 1);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Which of the game's palettes to decode through.\nGameplay always runs in palette 5.");
        if (_sprPalette != AppSettings.GamePalette)
        {
            ImGui.SameLine(0, 4);
            if (ImGui.SmallButton("in-game##sprpal")) _sprPalette = AppSettings.GamePalette;
        }
        ImGui.SameLine(0, 12);
        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt("columns", ref _sprCols, 0, 40, _sprCols == 0 ? "fit" : "%d");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How many sprites per row. 0 fits as many as the panel is wide.\n" +
                "The sheets are laid out 19 to a row, so 19 (or 38) lines the\n" +
                "2x2 icons up into whole pictures.");
        if (_sprCols != 0)
        {
            ImGui.SameLine(0, 4);
            if (ImGui.SmallButton("19##sprcols")) _sprCols = 19;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("the sheets' own row stride");
        }

        ImGui.SameLine(0, 12);
        ImGui.Checkbox("gapless", ref _sprGapless);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Butt the cells together with no gaps, so a tile set reads as one\n" +
                "continuous sheet. Each sprite still sits at its own cell's top-left,\n" +
                "which is where the game blits it.");

        ImGui.SameLine(0, 12);
        ImGui.BeginDisabled(_sprGapless);
        ImGui.Checkbox("checkerboard", ref _sprCheckerboard);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_sprGapless
                ? "Off in gapless mode - a per-cell backing would put the gaps back."
                : "Show what is transparent rather than black.");

        ImGui.SameLine(0, 12);
        FilterBox("##sprfilter", "filter banks", _sprFilter, 160f);
    }

    private void DrawSpriteBankList()
    {
        string filter = BufText(_sprFilter).Trim();
        var groups = new (string Label, Func<SpriteSource, bool> Match)[]
        {
            ("Enemy banks", s => s.Store == SpriteStore.Newsh),
            ("Shop sheet", s => s.Store == SpriteStore.NewshFile),
            ("tyrian.shp sheets", s => s.Store == SpriteStore.MainSheet && !s.Xmas),
            ("tyrian.shp banks", s => s.Store == SpriteStore.MainBank && !s.Xmas),
            ("Christmas (tyrianc.shp)", s => s.Xmas),
            ("Terrain tiles", s => s.Store == SpriteStore.Tiles),
        };

        var all = AllSpriteSources();
        foreach (var (label, match) in groups)
        {
            var items = all.Where(match)
                .Where(s => filter.Length == 0 || s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (items.Count == 0) continue;
            ImGui.SeparatorText(label);
            foreach (var src in items)
            {
                bool sel = src == _sprSource;
                if (ImGui.Selectable($"{src.ShortName}##b{(int)src.Store}_{src.Index}_{src.Xmas}", sel))
                {
                    _sprSource = src;
                    _sprSelected = -1;
                }
                if (sel && _sprScrollBankList) ImGui.SetScrollHereY(0.5f);
            }
        }
        _sprScrollBankList = false;
    }

    private void DrawSpriteSheet()
    {
        var atlas = Atlas(_sprSource, _sprPalette);
        if (atlas == null || atlas.Count == 0)
        {
            ImGui.TextDisabled($"{_sprSource.Title}\n\nThis bank could not be read from the data folder.");
            return;
        }

        // --- Header, in the game's own font when tyrian.shp gave us one. ---
        DrawGameHeader(_sprSource.ShortName.ToUpperInvariant(), AcSprite);
        ImGui.TextDisabled(_sprSource.Title);
        int first = _sprSource.FirstIndex;
        int usable = 0;
        for (int i = first; i < atlas.Count; i++) if (atlas.Has(i)) usable++;
        ImGui.TextDisabled($"{usable} sprites  ·  cell {atlas.CellW}x{atlas.CellH}  ·  " +
            (first == 1 ? "numbered from 1" : "numbered from 0"));

        // --- The detail strip is pinned to the bottom, so the grid never pushes it away. ---
        const float detailH = 132f;
        float gridH = Math.Max(80f, ImGui.GetContentRegionAvail().Y - detailH - 6f);

        // A fixed column count can be wider than the panel, so the grid needs to pan sideways.
        ImGui.BeginChild("sprgrid", new Vector2(0, gridH), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.HorizontalScrollbar);
        DrawSpriteGrid(atlas, first);
        ImGui.EndChild();

        ImGui.BeginChild("sprdetail", new Vector2(0, 0), ImGuiChildFlags.Borders);
        DrawSpriteDetail(atlas);
        ImGui.EndChild();
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
                    if (_sprCheckerboard && has) Checkerboard(dl, mn, mx);
                    else dl.AddRectFilled(mn, mx, has ? Gfx.Rgba(16, 18, 24) : Gfx.Rgba(24, 20, 20, 140), 2f);
                    if (has) atlas.DrawCentered(dl, idx, mn, mx, scale);
                }

                if (idx == _sprSelected)
                {
                    dl.AddRect(mn, mx, AcSprite, 2f, ImDrawFlags.None, 2f);
                    if (_sprScrollGrid)
                    {
                        ImGui.SetScrollY(Math.Max(0, r * ch - viewH * 0.4f));
                        _sprScrollGrid = false;
                    }
                }
                else if (idx == hover) dl.AddRect(mn, mx, Gfx.Rgba(255, 255, 255, 120), 2f);
            }

        if (hover < 0) return;
        var (w, h) = atlas.SizeOf(hover);
        ImGui.SetTooltip(w == 0 ? $"#{hover}  (empty)" : $"#{hover}  ·  {w}x{h}");
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _sprSelected = hover;
    }

    /// <summary>The transparency checker, in the window's own greys so it never reads as art.</summary>
    private static void Checkerboard(ImDrawListPtr dl, Vector2 mn, Vector2 mx)
    {
        dl.AddRectFilled(mn, mx, Gfx.Rgba(30, 32, 40), 2f);
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
            ImGui.TextDisabled("Click a sprite to inspect it.");
            return;
        }
        var (w, h) = atlas.SizeOf(_sprSelected);

        // Blown up as large as the strip allows, on integer steps so it stays pixel-crisp.
        const float boxW = 120f;
        float avail = ImGui.GetContentRegionAvail().Y - 8f;
        float big = Math.Max(1f, MathF.Floor(Math.Min(boxW / Math.Max(1, w), avail / Math.Max(1, h))));
        var at = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var boxMax = at + new Vector2(boxW, avail);
        Checkerboard(dl, at, boxMax);
        atlas.DrawCentered(dl, _sprSelected, at, boxMax, big);
        dl.AddRect(at, boxMax, Gfx.Rgba(90, 100, 130, 190), 2f);
        ImGui.Dummy(new Vector2(boxW, avail));
        ImGui.SameLine(0, 12);

        ImGui.BeginGroup();
        ImGui.TextColored(ColorOf(AcSprite), $"sprite #{_sprSelected}");
        ImGui.TextDisabled($"{w} x {h} px  ·  shown at {big:0}x");
        ImGui.TextDisabled(_sprSource.Title);

        var users = SpriteUsers(_sprSource, _sprSelected);
        if (users.Count == 0)
        {
            ImGui.TextDisabled(_sprSource.Store == SpriteStore.Newsh
                ? "No enemyDat entry in this episode names it."
                : "Drawn by the menus rather than by an enemyDat entry.");
        }
        else
        {
            ImGui.TextDisabled($"used by {users.Count} enemy entr{(users.Count == 1 ? "y" : "ies")}:");
            ImGui.BeginChild("sprusers", new Vector2(0, 0));
            foreach (int id in users)
                if (ImGui.SmallButton($"#{id}##spru{id}")) OpenEnemy(_episodeIdx, id);
                else if (ImGui.IsItemHovered()) ImGui.SetTooltip("open this enemy in the enemy browser");
            ImGui.EndChild();
        }
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

    /// <summary>A window heading: the title in the window's accent, over a rule in the same
    /// colour so the panels below it read as belonging to it.</summary>
    private static void DrawGameHeader(string text, uint accent)
    {
        var at = ImGui.GetCursorScreenPos();
        ImGui.TextColored(ColorOf(accent), text);
        var mx = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(new Vector2(at.X, mx.Y + 3f),
            new Vector2(Math.Max(mx.X, at.X + 60f), mx.Y + 4.5f), Shade(accent, 1f, 150));
        ImGui.Dummy(new Vector2(0, 5f));
    }
}
