using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The shop's side of the data set: ships, weapon ports, sidekicks, shields, generators and
/// special weapons, each with the icon the game itself draws for it. The atlas never
/// simulates a player, so none of this feeds the sim -- it is here because the tables sit in
/// the same block as enemyDat and nothing else reads them. See <see cref="ItemData"/>.
///
/// Numbers that come in a series -- a port's eleven power levels, a sidekick's charge stages --
/// are drawn as bars against the series' own maximum as well as printed, because the thing you
/// actually want from those tables is the shape of the progression, and eleven rows of digits
/// hide it completely.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showItems;
    private int _itemTab;                 // index into ItemTabs
    private int _itemTabPending = -1;     // a tab the CLI asked for, applied once
    private int _itemRowPending = -1;     // ... and a row within it
    private bool _itemScrollToSelection;  // keep a row set from elsewhere in view
    private double _itemClock;            // ticks, for the sidekick animation
    private bool _itemAnimate = true;
    private float _itemAnimSpeed = 1f;
    /// <summary>Whether the trigger is held. Only <c>option 2</c> sidekicks care: they animate
    /// on a shot and rest between them, so with this off they simply sit on their first frame.</summary>
    private bool _itemFiring = true;
    private int _itemCharge;
    private bool _itemChargeAuto = true;
    /// <summary>Which build's shop to show: the tables as Tyrian 2000 shipped them, or with
    /// the Engaged fork's post-load pass over them (<see cref="ForkRestoration"/>).</summary>
    private bool _itemFork = true;

    /// <summary>Ticks per charge stage while the fire button is held (mainint.c:7565).</summary>
    private const int SidekickChargeTicks = 20;
    private int _itemSelected;
    private bool _itemOpened;             // first frame has chosen a row rather than "None"
    private float _itemListW = 250f;
    private readonly byte[] _itemFilter = new byte[64];

    private static readonly string[] ItemTabs =
        { "Ships", "Weapon ports", "Sidekicks", "Shields", "Generators", "Specials", "Arcade", "Other", "Outposts" };

    /// <summary>The tabs that are not one of the six shop item tables: "Arcade" lays out the
    /// super-arcade ships and their ball loadouts, "Other" the field pickups (powerups,
    /// datacubes, money...) and "Outposts" the shops by level.</summary>
    private const int TabArcade = 6, TabOther = 7, TabOutposts = 8;

    private int _arcadeSel;                 // row within the Arcade tab
    private int _otherSel;                 // row within the Other tab
    private int _outpostSel;               // row within the Outposts tab
    private bool _arcadeScrollToSelection, _otherScrollToSelection, _outpostScrollToSelection;

    /// <summary>The window's own colour, shared with its launcher chip. See AcAnalysis.</summary>
    private static uint AcShop => AcBuild;
    /// <summary>Money, wherever it turns up.</summary>
    private static readonly uint AcItem = Gfx.Rgba(255, 210, 120);

    // ---- The two-player craft the shop's ship table never lists --------------------------------
    // In two-player mode (arcade and network alike) Player 1 always flies the Silver Ship and
    // Player 2 the Dragonwing (tyrian2.c:5420 / :4971). The Silver Ship is one of the 19 shop
    // hulls already, but the Dragonwing has no shop record at all: fixed 10 armour (varz.c:437),
    // drawn as two 2x2 halves out of the player-ship sheet rather than a single block
    // (mainint.c:7099), and folded onto shipCombos row 0 for its twiddles. The Ships tab appends
    // it after the real hulls; everything ship-shaped keys off IsSynthShip.

    /// <summary>How many made-up hulls the Ships tab shows past the 19 the table names.</summary>
    private const int SynthShipCount = 1;
    /// <summary>ships[] index of the hull Player 1 always flies in two players: the Silver Ship.</summary>
    private const int TwoPlayerP1Ship = 11;
    /// <summary>Player-ship sheet (tyrian.shp #8) 2x2 block ids for the Dragonwing's left and right
    /// halves at rest -- <c>ship_sprite + 13</c> and <c>+ 51</c> with no banking (mainint.c:7099).</summary>
    private const int DragonwingLeftGr = 13, DragonwingRightGr = 51;

    /// <summary>Rows in the Ships tab: the 19 shop hulls plus the appended two-player craft.</summary>
    private static int ShipRowCount(ItemData d) => d.Ships.Length + SynthShipCount;
    /// <summary>Whether a Ships-tab row is one of the appended craft rather than a shop hull.</summary>
    private static bool IsSynthShip(ItemData d, int i) => i >= d.Ships.Length;

    /// <summary>The "--showitems &lt;tab&gt; [row]" entry point: frame one shop tab, and optionally
    /// one row, for a screenshot. ImGui owns which tab is active, so the tab is a request the
    /// next frame hands it via SetSelected.</summary>
    public void ShowItemTab(int tab, int row = -1)
    {
        _showItems = true;
        _itemTabPending = Math.Clamp(tab, 0, ItemTabs.Length - 1);
        if (row < 0) return;
        // Held separately: switching tab otherwise resets the row to the first named item.
        _itemRowPending = row;
        _itemOpened = true;
    }

    private void DrawItemWindow()
    {
        if (!_showItems || _gd == null || CurEpisode == null) return;

        if (!RefBegin("Ships & items", "items", ref _showItems, AcShop,
                new Vector2(1020, 700), new Vector2(640, 400))) return;

        var items = _gd.GetItems(CurEpisode, _itemFork);
        if (!_itemOpened && items.Loaded) { _itemSelected = FirstRealItem(items); _itemOpened = true; }
        if (_itemAnimate) _itemClock += ImGui.GetIO().DeltaTime * 35.0 * _itemAnimSpeed;

        BandBegin("itemband", AcShop);
        BandLabel("episode");
        ImGui.SetNextItemWidth(126);
        EpisodeCombo("##itemepisode");

        BandDivider();
        int build = _itemFork ? 1 : 0;
        if (SegBar("##itembuild", ref build, AcShop, 208f,
                ("Vanilla", "The item tables exactly as Tyrian 2000 shipped them."),
                ("Engaged", "The Engaged fork's post-load pass over the same tables:\n" +
                               "the restored Charge-Laser Cannon, Wobbley's stray first frame\n" +
                               "snapped to its rest frame, placeholder icons for named items\n" +
                               "that shipped without one, magazine sizes spelled out in the\n" +
                               "names, and the per-episode weapon rewrites.")))
        {
            _itemFork = build == 1;
            // The two tables are different lengths in what they name, so a row index from one
            // means nothing in the other.
            _itemOpened = false;
            _itemSelected = 0;
        }

        BandDivider();
        UiFilter("##itemfilter", "filter by name", _itemFilter, 200f, AcShop);
        BandDivider();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ColorOf(UiFaint), "(?)  why the episode matters");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Episodes 4 and 5 carry their own copy of this whole block, and the engine\n" +
                "rewrites parts of it per episode on top of that. Real differences you can see\n" +
                "here: the Xega Ball is a six-bolt spread in 1-3 and one heavy bolt in 4-5, the\n" +
                "MicroSol's fifth option fires eight weak bolts against two, and the Beno Wallop\n" +
                "Beam gains a second bolt in 4-5. Shot patterns and sounds differ too.");
        BandEnd();

        if (!items.Loaded)
        {
            UiEmpty("The item tables are not where this parser expects them",
                "They sit between the weapon table and enemyDat.", AcShop);
            RefEnd(AcShop);
            return;
        }

        if (ImGui.BeginTabBar("itemtabs", ImGuiTabBarFlags.FittingPolicyScroll))
        {
            for (int t = 0; t < ItemTabs.Length; t++)
                if (TabItem(ItemTabs[t], _itemTabPending == t))
                {
                    // Slot 0 of most tables is the shop's empty "None" row. It belongs in the
                    // list, but opening the detail pane on it shows nothing at all. The two
                    // non-table tabs carry their own selection, untouched by the switch.
                    if (_itemTab != t)
                    {
                        _itemTab = t;
                        if (t < TabArcade) _itemSelected = FirstRealItem(items);
                    }
                    ImGui.EndTabItem();
                }
            ImGui.EndTabBar();
            // ImGui applies SetSelected on the frame AFTER the one that asks, so the request
            // has to outlive that frame -- clear it only once the tab it named is really up,
            // and apply the row then, over the tab switch's own default.
            if (_itemTabPending >= 0 && _itemTab == _itemTabPending)
            {
                if (_itemRowPending >= 0)
                {
                    if (_itemTab == TabArcade) { _arcadeSel = _itemRowPending; _arcadeScrollToSelection = true; }
                    else if (_itemTab == TabOther) { _otherSel = _itemRowPending; _otherScrollToSelection = true; }
                    else if (_itemTab == TabOutposts) { _outpostSel = _itemRowPending; _outpostScrollToSelection = true; }
                    else { _itemSelected = _itemRowPending; _itemScrollToSelection = true; }
                }
                _itemTabPending = -1;
                _itemRowPending = -1;
            }
        }

        float maxList = Math.Max(190f, ImGui.GetContentRegionAvail().X - 340f);
        _itemListW = Math.Clamp(_itemListW, 190f, maxList);

        WellBegin("itemlist", new Vector2(_itemListW, ImGui.GetContentRegionAvail().Y), AcShop);
        if (_itemTab == TabOutposts) DrawOutpostList();
        else if (_itemTab == TabOther) DrawOtherList();
        else if (_itemTab == TabArcade) DrawArcadeList();
        else DrawItemList(items);
        WellEnd();

        ImGui.SameLine(0, 3);
        VSplitter("##itemsplit", ref _itemListW, 190f, maxList);
        ImGui.SameLine(0, 3);
        ImGui.BeginChild("itemdetail", new Vector2(0, 0));
        if (_itemTab == TabOutposts) DrawOutpostDetail();
        else if (_itemTab == TabOther) DrawOtherDetail();
        else if (_itemTab == TabArcade) DrawArcadeDetail();
        else DrawItemDetail(items);
        ImGui.EndChild();

        RefEnd(AcShop);
    }

    /// <summary>Name, icon and price for a row in the current tab. Slot 0 of most tables is
    /// the empty "None" entry, which the shop shows too.</summary>
    private (string Name, int Cost, SpriteSource Src, int Sprite, bool Big) ItemRow(ItemData d, int i) =>
        ItemRowFor(d, _itemTab, i);

    /// <summary>The same for an explicit tab, so the Outposts inventory can draw ships, ports,
    /// sidekicks and the rest side by side without touching <see cref="_itemTab"/>.</summary>
    private (string Name, int Cost, SpriteSource Src, int Sprite, bool Big) ItemRowFor(ItemData d, int tab, int i)
    {
        switch (tab)
        {
            case 0:
            {
                // The appended Dragonwing has no shop record; name it, price it at nothing, and
                // hand back the player-ship sheet block its left half comes from so "open the
                // sprite" lands somewhere sensible.
                if (IsSynthShip(d, i))
                    return ("Dragonwing", 0, SpriteSource.MainSheet(8), DragonwingLeftGr, true);
                var s = d.Ships[i];
                // Over 500 the index means the Tyrian 2000 sheet. Graphic 1 is the Nort Ship
                // sentinel and is drawn separately -- see DrawShipIcon.
                bool t2k = s.ShipGraphic > 500;
                return (s.Name, s.Cost, SpriteSource.MainSheet(t2k ? 12 : 8),
                    t2k ? s.ShipGraphic - 500 : s.ShipGraphic, true);
            }
            case 1:
            {
                var p = d.Ports[i];
                return (p.Name, p.Cost, SpriteSource.Shop, p.ItemGraphic, true);
            }
            case 2:
            {
                var o = d.Options[i];
                return (o.Name, o.Cost, SpriteSource.Shop, o.ItemGraphic, true);
            }
            case 3:
            {
                var s = d.Shields[i];
                return (s.Name, s.Cost, SpriteSource.Shop, s.ItemGraphic, true);
            }
            case 4:
            {
                var p = d.Powers[i];
                return (p.Name, p.Cost, SpriteSource.Shop, p.ItemGraphic, true);
            }
            default:
            {
                var s = d.Specials[i];
                return (s.Name, 0, SpriteSource.MainSheet(9), s.ItemGraphic, true);
            }
        }
    }

    /// <summary>
    /// Draw an item's icon centred in a box. Ships need a special case: the Nort Ship's
    /// shipgraphic is 1, which is a sentinel rather than a sprite index -- blitting sprite 1
    /// shows garbage. The engine draws it as two 2x2 halves, sprites 220 and 222 either side
    /// of the anchor (game_menu.c:3032), and so do we.
    /// </summary>
    private void DrawItemIcon(ImDrawListPtr dl, ItemData d, int index, Vector2 boxMin, Vector2 boxMax, float scale) =>
        DrawItemIconFor(dl, d, _itemTab, index, boxMin, boxMax, scale);

    private void DrawItemIconFor(ImDrawListPtr dl, ItemData d, int tab, int index, Vector2 boxMin, Vector2 boxMax, float scale)
    {
        // The Dragonwing flies as two 2x2 halves exactly like the Nort Ship, just out of a
        // different pair of blocks -- take that path before touching d.Ships[index], which the
        // appended row has no entry in.
        if (tab == 0 && IsSynthShip(d, index))
        {
            DrawShipHalves(dl, DragonwingLeftGr, DragonwingRightGr, boxMin, boxMax, scale);
            return;
        }
        if (tab == 0 && d.Ships[index].ShipGraphic == 1)
        {
            // The Nort Ship: blocks 220 and 222 either side of the anchor (game_menu.c:3032).
            DrawShipHalves(dl, 220, 222, boxMin, boxMax, scale);
            return;
        }

        var (_, _, src, sprite, big) = ItemRowFor(d, tab, index);
        var atlas = Atlas(src, AppSettings.GamePalette);
        if (atlas != null) DrawEnemyFrameCentered(dl, atlas, sprite, big, boxMin, boxMax, scale);
    }

    /// <summary>Draw a two-block hull centred in a box. The Nort Ship and the Dragonwing are both
    /// drawn not as one 2x2 block but as a left and a right one side by side, 48x28 in all, out of
    /// the player-ship sheet -- so centring backs off by half of the pair, not half of a block.
    /// They are twice a normal hull's width, so their callers hand them a wider box; the scale is
    /// kept whole (<paramref name="maxScale"/> capped to what fits) so the pixels never warp.</summary>
    private void DrawShipHalves(ImDrawListPtr dl, int leftGr, int rightGr, Vector2 boxMin, Vector2 boxMax, float maxScale)
    {
        var shipSheet = Atlas(SpriteSource.MainSheet(8), AppSettings.GamePalette);
        if (shipSheet == null) return;
        float scale = IntFitScale(48f, 28f, boxMin, boxMax, maxScale);
        var tl = new Vector2(
            MathF.Round((boxMin.X + boxMax.X) * 0.5f - 24f * scale),
            MathF.Round((boxMin.Y + boxMax.Y) * 0.5f - 14f * scale));
        Draw2x2(dl, shipSheet, leftGr, tl, scale);
        Draw2x2(dl, shipSheet, rightGr, tl + new Vector2(24f * scale, 0), scale);
    }

    /// <summary>The largest WHOLE scale up to <paramref name="maxScale"/> at which a
    /// <paramref name="w"/>x<paramref name="h"/> game-pixel footprint fits the box. Pixel art wants
    /// integer scales -- a fractional one warps the sprite -- so the two-block hulls are given a box
    /// big enough for a good whole scale rather than squeezed into a normal hull's box.</summary>
    private static float IntFitScale(float w, float h, Vector2 boxMin, Vector2 boxMax, float maxScale)
    {
        for (int k = (int)MathF.Floor(maxScale); k >= 2; k--)
            if (k * w <= boxMax.X - boxMin.X && k * h <= boxMax.Y - boxMin.Y) return k;
        return 1f;
    }

    /// <summary>The Nort Ship and the appended Dragonwing both fly as a 48px-wide pair of blocks
    /// rather than a single 24px hull, so their icon needs a wider box to sit in at full scale.</summary>
    private static bool IsTwoHalfShip(ItemData d, int tab, int i) =>
        tab == 0 && (IsSynthShip(d, i) || (i >= 0 && i < d.Ships.Length && d.Ships[i]?.ShipGraphic == 1));

    /// <summary>The first row that actually names something, so a tab never opens on "None".</summary>
    private int FirstRealItem(ItemData d)
    {
        for (int i = 0; i < ItemCount(d); i++)
        {
            string name = ItemRow(d, i).Name;
            if (name.Length > 0 && !name.Equals("None", StringComparison.OrdinalIgnoreCase)) return i;
        }
        return 0;
    }

    private int ItemCount(ItemData d) => _itemTab switch
    {
        0 => ShipRowCount(d),
        1 => d.Ports.Length,
        2 => d.Options.Length,
        3 => d.Shields.Length,
        4 => d.Powers.Length,
        _ => d.Specials.Length,
    };

    private void DrawItemList(ItemData d)
    {
        string filter = BufText(_itemFilter).Trim();
        int n = ItemCount(d);
        bool anyShown = false;
        const float rowH = 36f;

        for (int i = 0; i < n; i++)
        {
            var (name, cost, _, _, _) = ItemRow(d, i);
            if (!Matches(filter, name, i.ToString())) continue;
            anyShown = true;

            bool sel = i == _itemSelected;
            bool restored = _itemTab == 2 && i == d.ChargeLaserSlot && d.ChargeLaserSlot > 0;
            var box = UiRow($"##it{i}", sel, restored ? AcGo : AcShop, rowH);
            if (box.Clicked) _itemSelected = i;
            if (sel && _itemScrollToSelection) { ImGui.SetScrollHereY(0.4f); _itemScrollToSelection = false; }

            // A two-block hull is 48px wide against a normal hull's 24, so it gets a wider icon box
            // and its text is pushed right to clear it -- shrinking it into the normal gutter would
            // warp the pixels. Everything else keeps the original spacing.
            bool wide = IsTwoHalfShip(d, _itemTab, i);
            float iconRight = wide ? 60f : 45f;
            float textX = wide ? 66f : 50f;

            var dl = ImGui.GetWindowDrawList();
            DrawItemIcon(dl, d, i, new Vector2(box.Min.X + (wide ? 6f : 7f), box.Min.Y + 2f),
                new Vector2(box.Min.X + iconRight, box.Max.Y - 2f), 1f);

            float lh = ImGui.GetTextLineHeight();
            float top = box.Min.Y + (rowH - 3f - lh * 2f - 1f) * 0.5f;
            float room = box.Max.X - box.Min.X - textX - 10f;
            ClipText(dl, new Vector2(box.Min.X + textX, top), room,
                sel ? Gfx.Rgba(250, 252, 255) : UiText, name.Length > 0 ? name : "(unnamed)");
            // Ships-tab synthetic rows carry no price and no table slot, so the row index would
            // read as a bogus ship id -- label them by role instead.
            string sub = _itemTab == 0 && IsSynthShip(d, i) ? "two-player · Player 2"
                : cost > 0 ? $"{cost:n0} credits" : $"#{i}";
            ClipText(dl, new Vector2(box.Min.X + textX, top + lh + 1f), room,
                restored ? AcGo : cost > 0 ? Shade(AcItem, 1f, 205) : UiFaint,
                sub + (restored ? "   ·   restored cut content" : ""));
        }
        if (!anyShown) ImGui.TextDisabled("Nothing matches.");
    }

    // =====================================================================
    // Detail
    // =====================================================================

    private void DrawItemDetail(ItemData d)
    {
        int n = ItemCount(d);
        _itemSelected = Math.Clamp(_itemSelected, 0, Math.Max(0, n - 1));
        if (n == 0) return;

        var (name, cost, src, sprite, _) = ItemRow(d, _itemSelected);
        DrawItemHero(d, name, cost, src, sprite);

        ImGui.Dummy(new Vector2(0, 4f));

        // Ports and sidekicks carry long detail -- a port's two eleven-row tables, the
        // Charge-Laser's charge stages -- so their "where you get it" is pinned in its own well
        // at the foot of the pane, stats scrolling above it, and never scrolls off the bottom.
        // The short tabs let it flow straight under the stats, which fills the pane without a gap.
        if (_itemTab is 1 or 2)
        {
            var avail = ImGui.GetContentRegionAvail();
            float whereH = Math.Clamp(avail.Y * 0.44f, 150f, 330f);
            whereH = Math.Min(whereH, Math.Max(120f, avail.Y - 130f));
            float bodyH = Math.Max(90f, avail.Y - whereH - 6f);

            WellBegin("itembody", new Vector2(avail.X, bodyH), AcShop, 12f, 9f);
            DrawItemBody(d);
            WellEnd();

            ImGui.Dummy(new Vector2(0, 6f));
            WellBegin("itemwhere", new Vector2(avail.X, whereH), AcShop, 12f, 9f);
            DrawItemAppearances(d);
            WellEnd();
        }
        else
        {
            WellBegin("itembody", ImGui.GetContentRegionAvail(), AcShop, 12f, 9f);
            DrawItemBody(d);
            ImGui.Dummy(new Vector2(0, 6f));
            DrawItemAppearances(d);
            WellEnd();
        }
    }

    private void DrawItemBody(ItemData d)
    {
        switch (_itemTab)
        {
            case 0: DrawShipDetail(d, _itemSelected); break;
            case 1: DrawPortDetail(d.Ports[_itemSelected]); break;
            case 2: DrawSidekickDetail(d.Options[_itemSelected]); break;
            case 3: DrawShieldDetail(d.Shields[_itemSelected]); break;
            case 4: DrawGeneratorDetail(d.Powers[_itemSelected]); break;
            default: DrawSpecialDetail(d.Specials[_itemSelected]); break;
        }
    }

    /// <summary>
    /// The art and the headline facts: for a ship the shop's large illustration leads and the
    /// in-flight icon sits beside it; everything else has only the icon, so the icon gets the
    /// room instead.
    /// </summary>
    private void DrawItemHero(ItemData d, string name, int cost, SpriteSource src, int sprite)
    {
        var dl = ImGui.GetWindowDrawList();
        bool synthShip = _itemTab == 0 && IsSynthShip(d, _itemSelected);
        // The Nort Ship and the Dragonwing fly as a 48px-wide pair of blocks, so the icon well is
        // widened for them and the hull drawn at full 2x rather than squeezed to fit an 88px well.
        bool twoHalf = IsTwoHalfShip(d, _itemTab, _itemSelected);
        // The shop's large illustration is a real hull's; the Dragonwing has none.
        var big2 = _itemTab == 0 && !synthShip ? Atlas(SpriteSource.MainBank(5), AppSettings.GamePalette) : null;
        int bigIdx = big2 != null ? d.Ships[_itemSelected].BigShipGraphic - 1 : -1;
        bool hasBig = big2 != null && bigIdx >= 0 && big2.Has(bigIdx);

        if (hasBig)
        {
            var (bw, bh) = big2!.SizeOf(bigIdx);
            // Integer scale so the pixels stay crisp, as large as the panel allows.
            float bs = Math.Max(1f, MathF.Floor(Math.Min(300f / Math.Max(1, bw), 190f / Math.Max(1, bh))));
            var box = new Vector2(bw * bs + 22f, bh * bs + 18f);
            var p = ImGui.GetCursorScreenPos();
            Well(dl, p, p + box, AcShop, 6f);
            big2.DrawCentered(dl, bigIdx, p, p + box, bs);
            ImGui.Dummy(box);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"the shop's large illustration (bigshipgraphic {bigIdx + 1}), at {bs:0}x");
            ImGui.SameLine(0, 10);
        }
        else if (synthShip)
        {
            // No shop art exists for the Dragonwing, so the lead slot shows the craft it forms with
            // Player 1's Silver Ship once the two link (mainint.c:6942) instead. The slot is sized so
            // the 48x36 pair sits at a whole 3x.
            var box = new Vector2(160f, 136f);
            var p = ImGui.GetCursorScreenPos();
            Well(dl, p, p + box, AcShop, 6f);
            DrawLinkedCraft(dl, d, p, p + box, 3f);
            ImGui.Dummy(box);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Player 1's Silver Ship with the Dragonwing linked below it, at 3x");
            ImGui.SameLine(0, 10);
        }

        // The wide hulls get a roomier square well so their 96px 2x form is not clipped.
        float iconBox = twoHalf ? 108f : hasBig || synthShip ? 88f : 116f;
        var at = ImGui.GetCursorScreenPos();
        Well(dl, at, at + new Vector2(iconBox, iconBox), AcShop, 6f);
        DrawItemIcon(dl, d, _itemSelected, at, at + new Vector2(iconBox, iconBox), hasBig || synthShip ? 2f : 3f);
        ImGui.Dummy(new Vector2(iconBox, iconBox));
        if (ImGui.IsItemHovered() && _itemTab == 0)
            ImGui.SetTooltip(synthShip ? "the Dragonwing on its own, as it flies" : "the ship as it flies");
        ImGui.SameLine(0, 12);

        ImGui.BeginGroup();
        UiTitle(name.Length > 0 ? name : "(unnamed)", AcShop);
        // A real hull is a "Ship" numbered by its table slot; the appended craft is a two-player
        // fixture, so it is badged by its role rather than a meaningless row index.
        if (synthShip)
        {
            Badge("2-player", AcShop);
            ImGui.SameLine(0, 5f);
            Badge("Player 2", Gfx.Rgba(150, 162, 185));
        }
        else
        {
            Badge(ItemTabs[_itemTab].TrimEnd('s'), AcShop);
            ImGui.SameLine(0, 5f);
            Badge($"#{_itemSelected}", Gfx.Rgba(150, 162, 185));
        }
        if (cost > 0)
        {
            ImGui.SameLine(0, 5f);
            Badge($"{cost:n0} credits", AcItem);
        }
        ImGui.Dummy(new Vector2(0, 4f));
        if (UiButton("open the sprite", AcShop, "show this icon in the sprite browser"))
            OpenSprite(src, sprite);
        ImGui.EndGroup();
    }

    /// <summary>The two-player craft the Silver Ship (Player 1) and the Dragonwing (Player 2) make
    /// when they link: the Dragonwing rides one pixel left and eight below the Silver Ship
    /// (mainint.c:6944). The engine draws player 1 first and player 2 over it (mainint.c:7587-7594),
    /// so the Silver Ship goes down first and the Dragonwing sits on top, as in the game. The pair's
    /// footprint is 48x36 game pixels, fitted to the box.</summary>
    private void DrawLinkedCraft(ImDrawListPtr dl, ItemData d, Vector2 boxMin, Vector2 boxMax, float maxScale)
    {
        var sheet = Atlas(SpriteSource.MainSheet(8), AppSettings.GamePalette);
        if (sheet == null) return;
        float scale = IntFitScale(48f, 36f, boxMin, boxMax, maxScale);
        var origin = new Vector2(
            MathF.Round((boxMin.X + boxMax.X) * 0.5f - 24f * scale),
            MathF.Round((boxMin.Y + boxMax.Y) * 0.5f - 18f * scale));

        // The Silver Ship first, under, centred over the join. It may live on either the classic or
        // the Tyrian 2000 sheet, so resolve its block the same way the row does.
        var s = d.Ships[TwoPlayerP1Ship];
        bool t2k = s.ShipGraphic > 500;
        var p1Sheet = t2k ? Atlas(SpriteSource.MainSheet(12), AppSettings.GamePalette) : sheet;
        int p1Gr = t2k ? s.ShipGraphic - 500 : s.ShipGraphic;
        if (p1Sheet != null && p1Gr > 1)
            Draw2x2(dl, p1Sheet, p1Gr, origin + new Vector2(13f * scale, 0f), scale);

        // The Dragonwing over it, eight pixels down, both halves.
        Draw2x2(dl, sheet, DragonwingLeftGr, origin + new Vector2(0f, 8f * scale), scale);
        Draw2x2(dl, sheet, DragonwingRightGr, origin + new Vector2(24f * scale, 8f * scale), scale);
    }

    /// <summary>The hull's own numbers, then the twiddles it can fly -- what the ship is, then
    /// what it can do, before the pane goes on to where you get it.</summary>
    private void DrawShipDetail(ItemData d, int shipId)
    {
        if (IsSynthShip(d, shipId)) { DrawDragonwingDetail(d); return; }
        var s = d.Ships[shipId];
        UiSection("hull", AcShop);
        UiStatBar("damage taken", s.Dmg, 30, AcBoss, $"{s.Dmg}");
        UiStatBar("speed", s.Spd + 15, 30, AcPlayer, $"{s.Spd:+0;-0;0}");
        ImGui.Dummy(new Vector2(0, 4f));
        KV("animation", $"{s.Ani} frames");
        KV("ship sprite", s.ShipGraphic == 1
            ? "1 - a sentinel: the Nort Ship, drawn as two halves"
            : $"{s.ShipGraphic}" + (s.ShipGraphic > 500 ? "  (Tyrian 2000 sheet)" : ""));

        DrawShipTwiddleBlock(d, shipId);
    }

    /// <summary>The appended Dragonwing: the craft Player 2 flies in two players. It has no shop
    /// record, so its numbers come from the engine rather than the item table -- fixed 10 armour
    /// (varz.c:437), no speed of its own, powering the rear gun and folded onto the 2nd-player
    /// twiddle row. Player 1's half of the pair, the Silver Ship, is a click away.</summary>
    private void DrawDragonwingDetail(ItemData d)
    {
        UiSection("hull", AcShop);
        UiStatBar("damage taken", 10, 30, AcBoss, "10");
        ImGui.Dummy(new Vector2(0, 4f));
        KV("role", "Player 2 in two-player mode");
        KV("armour", "10, fixed  (not the item table's)");
        KV("ship sprite", $"two halves: blocks {DragonwingLeftGr} and {DragonwingRightGr} of the player-ship sheet");

        ImGui.Dummy(new Vector2(0, 4f));
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(ColorOf(UiFaint),
            "Two-player mode always pairs the Dragonwing with Player 1's Silver Ship. It is not sold " +
            "or found -- it is simply the hull Player 2 gets. Unlike a bought ship it powers the rear " +
            "weapon rather than the front, and when the two ships overlap they link: the Dragonwing " +
            "rides just below the Silver Ship and becomes an aimable gun turret.");
        ImGui.PopTextWrapPos();

        ImGui.Dummy(new Vector2(0, 6f));
        if (UiButton("Player 1's Silver Ship", AcShop, "open the hull Player 1 flies in two players"))
            ShowItemTab(0, TwoPlayerP1Ship);

        // Player 2's twiddles are shipCombos row 0 whatever hull they fly (varz.c:158); the twiddle
        // block already knows that row as the 2nd-player one, so hand it straight over.
        DrawShipTwiddleBlock(d, PlayerTwoCombos);
    }

    private void DrawPortDetail(PortItem p)
    {
        UiSection("port", AcShop);
        KV("fire modes", $"{p.OpNum}");
        KV("power per shot", $"{p.PowerUse}");

        var wd = _gd != null && CurEpisode != null ? _gd.GetItems(CurEpisode, _itemFork).Weapons : null;
        // opnum is a raw byte off the record and the table only holds two modes: a junk value
        // in an unused slot, or a block resolved a record or two off, would otherwise index
        // past Op's first dimension and throw while the pane is being drawn.
        int modes = Math.Clamp((int)p.OpNum, 1, 2);
        for (int mode = 0; mode < modes; mode++)
        {
            // Every mode restarts its power levels at 1, so without this the two modes' rows
            // collide on ImGui ids wherever they name the same weapon record.
            ImGui.PushID(mode);
            // Everything in the mode is scaled against the mode's own heaviest level, so the
            // eleven bars show how the gun ramps rather than how it compares to another gun.
            int maxDmg = 1;
            for (int lvl = 0; lvl < 11; lvl++)
            {
                var w0 = wd?.Get(p.Op[mode, lvl]) ?? default;
                if (w0.Loaded) maxDmg = Math.Max(maxDmg, VolleyDamage(w0));
            }

            UiSection(modes > 1 ? $"mode {mode + 1}" : "power levels", AcShop,
                $"peak volley {maxDmg}");
            WeaponHeader("power");
            for (int lvl = 0; lvl < 11; lvl++)
            {
                int id = p.Op[mode, lvl];
                WeaponRow($"{lvl + 1}", id, wd?.Get(id) ?? default, maxDmg);
            }
            ImGui.PopID();
        }
    }

    private static int VolleyDamage(in WeaponDat w) =>
        w.Attack == null ? 0 : w.Attack.Take(Math.Clamp((int)w.Max, 1, 8)).Sum(a => (int)a);

    /// <summary>
    /// Where each part of a weapon row sits, relative to the row's left edge. One function so
    /// the heading and the rows under it cannot drift apart -- which is exactly what went
    /// wrong when two sets of hand-laid widths were expected to agree.
    ///
    /// Every column is MEASURED rather than hand-laid. The tail used to be handed a fixed
    /// 150px whatever the window was doing -- the bar ate every pixel a wider window added --
    /// so "8 shots   ·   every 10 ticks" was cut to "8 shots   ·   every..." at every size,
    /// and at a larger font the other columns went the same way. The bar is now the elastic
    /// one and the text columns take exactly what their widest possible content needs.
    /// </summary>
    private static (float Step, float Id, float BarX, float BarW, float Tail) WeaponCols(float wide)
    {
        const float stepX = 6f;
        float idX = stepX + Widest("charge", "+00", "11") + 10f;
        float barX = idX + Widest("record", "#8888") + 10f;
        float tailW = WeaponTailW();
        float barW = Math.Max(50f, wide - barX - tailW - 18f);
        return (stepX, idX, barX, barW, barX + barW + 12f);
    }

    private static float Widest(params string[] samples)
    {
        float w = 0f;
        foreach (string s in samples) w = Math.Max(w, ImGui.CalcTextSize(s).X);
        return w;
    }

    /// <summary>The room the last column needs: the widest tail a row can print (both fields
    /// are bytes, so 255 is the true ceiling), never narrower than its own heading.</summary>
    private static float WeaponTailW() => Widest(WeaponTail(255, 255), "shots / reload");

    /// <summary>How many bolts a volley is and how long the port is out of action after it.</summary>
    private static string WeaponTail(int shots, int repeat) =>
        $"{shots} shot{(shots == 1 ? "" : "s")}" +
        (repeat > 0 ? $"   ·   every {repeat} ticks" : "");

    /// <summary>The heading over a run of weapon rows, on the same offsets they use.</summary>
    private static void WeaponHeader(string stepLabel)
    {
        float wide = ImGui.GetContentRegionAvail().X;
        var (stepX, idX, barX, barW, tailX) = WeaponCols(wide);
        var p = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        float lh = ImGui.GetTextLineHeight();

        ClipText(dl, new Vector2(p.X + stepX, p.Y), idX - stepX - 4f, UiFaint, stepLabel);
        ClipText(dl, new Vector2(p.X + idX, p.Y), barX - idX - 6f, UiFaint, "record");
        ClipText(dl, new Vector2(p.X + barX, p.Y), barW - 4f, UiFaint, "volley damage");
        ClipText(dl, new Vector2(p.X + tailX, p.Y), wide - tailX - 6f, UiFaint, "shots / reload");
        dl.AddRectFilled(new Vector2(p.X + stepX, p.Y + lh + 2f),
            new Vector2(p.X + wide - 6f, p.Y + lh + 3f), UiLineSoft);
        ImGui.Dummy(new Vector2(wide, lh + 5f));
    }

    /// <summary>
    /// One weapon record as a row: the step it belongs to, its record number, a damage bar
    /// against the series' peak, and the shots and reload it comes from. Drawn by hand rather
    /// than as a table so the bar can sit behind the number instead of in a column of its own.
    /// </summary>
    private static void WeaponRow(string step, int id, in WeaponDat w, int maxDmg)
    {
        float lh = ImGui.GetTextLineHeight();
        float h = lh + 6f;
        var p = ImGui.GetCursorScreenPos();
        float wide = ImGui.GetContentRegionAvail().X;
        var (stepX, idX, barXOfs, barWidth, tailXOfs) = WeaponCols(wide);
        ImGui.InvisibleButton($"##wr{step}_{id}", new Vector2(wide, h));
        bool hot = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        var q = new Vector2(p.X + wide, p.Y + h);
        if (hot) dl.AddRectFilled(p, q, Gfx.Rgba(255, 255, 255, 12), 4f);

        dl.AddText(new Vector2(p.X + stepX, p.Y + 3f), UiDim, step);
        dl.AddText(new Vector2(p.X + idX, p.Y + 3f), UiFaint, $"#{id}");

        float barX = p.X + barXOfs;
        if (!w.Loaded)
        {
            ClipText(dl, new Vector2(barX, p.Y + 3f), wide - barXOfs - 6f, UiFaint,
                "no such weapon record");
            return;
        }

        int dmg = VolleyDamage(w);
        var bar = new Vector2(barX, p.Y + h * 0.5f - 5f);
        MeterBar(dl, bar, bar + new Vector2(barWidth, 10f), maxDmg > 0 ? dmg / (float)maxDmg : 0f, AcItem);
        var dsz = ImGui.CalcTextSize($"{dmg}");
        // A dark copy one pixel down-right of the light one: the number sits on the bar, which
        // is bright where the bar is filled and dark where it is not.
        dl.AddText(new Vector2(barX + barWidth - dsz.X - 6f, p.Y + 3f), Gfx.Rgba(20, 22, 28), $"{dmg}");
        dl.AddText(new Vector2(barX + barWidth - dsz.X - 7f, p.Y + 2f), Gfx.Rgba(250, 250, 252), $"{dmg}");

        string tail = WeaponTail(w.Max, w.ShotRepeat);
        ClipText(dl, new Vector2(p.X + tailXOfs, p.Y + 3f), wide - tailXOfs - 6f, UiDim, tail);
        if (hot)
            ImGui.SetTooltip($"weapon record #{id}\nvolley {dmg} damage over {w.Max} shot(s)" +
                (w.ShotRepeat > 0 ? $"\nreloads every {w.ShotRepeat} ticks" : ""));
    }

    private void DrawSidekickDetail(OptionItem o)
    {
        var items = _gd != null && CurEpisode != null ? _gd.GetItems(CurEpisode, _itemFork) : null;
        if (items != null && items.ChargeLaserSlot == _itemSelected)
        {
            var p = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            float h = ImGui.GetTextLineHeight() * 3f + 12f;
            var dl = ImGui.GetWindowDrawList();
            Card(dl, p, p + new Vector2(w, h), AcGo, 0.14f);
            dl.AddRectFilled(p, new Vector2(p.X + 3f, p.Y + h), AcGo, 2f);
            ImGui.Dummy(new Vector2(0, 6f));
            ImGui.Indent(11f);
            ImGui.TextColored(ColorOf(Shade(AcGo, 1.1f)),
                "Restored cut content: Tyrian 2000 dropped this sidekick and reused its slot for\n" +
                "the Mint-O-Ship. The Engaged build puts it back from the DOS data, into the\n" +
                "first free sidekick slot and six otherwise-unused weapon records.");
            ImGui.Unindent(11f);
            ImGui.Dummy(new Vector2(0, 4f));
        }

        UiSection("sidekick", AcShop);
        KV("mount", MountName(o.Tr));
        KV("speed", $"{o.OpSpd:+0;-0;0}");
        KV("ammo", o.Ammo > 0 ? $"{o.Ammo} rounds before it reloads" : "no limit");
        KV("charge", $"{o.Pwr} stage{(o.Pwr == 1 ? "" : "s")}");
        KV("fires", $"weapon #{o.WpNum} through port {o.WPort}");
        int firePeriod = SidekickFirePeriod(items, o);
        KV("in flight", o.Option switch
        {
            0 => "not drawn",
            1 => $"always animating   ·   {SidekickFrames(o)} frames, one per tick",
            // Spelled out because it is the difference between what the engine draws and a
            // strobe: the table is a per-shot flash, not a loop.
            _ => $"animates while firing   ·   {SidekickFrames(o)} frames per shot" +
                 (firePeriod > 0 ? $", a shot every {firePeriod} tick{(firePeriod == 1 ? "" : "s")}" : ""),
        } + (o.Stop ? "   ·   stops with the ship" : ""));

        // A charge sidekick fires wpnum + charge, so its stages are consecutive weapon records.
        if (o.Pwr > 0 && items != null)
        {
            int maxDmg = 1;
            for (int c = 0; c <= o.Pwr; c++)
            {
                var w0 = items.Weapons.Get(o.WpNum + c);
                if (w0.Loaded) maxDmg = Math.Max(maxDmg, VolleyDamage(w0));
            }
            UiSection("charge stages", AcShop, $"peak volley {maxDmg}");
            WeaponHeader("charge");
            for (int c = 0; c <= o.Pwr; c++)
                WeaponRow($"+{c}", o.WpNum + c, items.Weapons.Get(o.WpNum + c), maxDmg);
        }

        if (o.Ani == 0) return;
        // tr 1 and 2 draw the body as a 2x2 out of the powerup sheet, everything else as a
        // single 12x14 sprite out of the ship sheet (mainint.c:7549).
        var src = SpriteSource.MainSheet(o.DrawsFromPowerupSheet ? 9 : 8);
        var atlas = Atlas(src, AppSettings.GamePalette);
        if (atlas == null) { ImGui.TextDisabled("(sheet unavailable)"); return; }

        UiSection("in flight", AcShop);
        DrawSidekickStage(o, atlas, firePeriod);
        DrawSidekickFrames(o, atlas);
    }

    /// <summary>How many frames of <c>gr</c> the entry actually cycles. The array is 20 long
    /// and the engine indexes it with <c>ani</c> unchecked, so the clamp is the atlas's.</summary>
    private static int SidekickFrames(OptionItem o) => Math.Max(1, Math.Min((int)o.Ani, 20));

    /// <summary>
    /// Ticks between a sidekick's shots. The engine counts its weapon's shotrepeat down one
    /// per tick and fires when it reaches zero (mainint.c:7418, shots.c:631), so the period is
    /// shotrepeat + 1. A sidekick mounted on port 0 never fires at all.
    /// </summary>
    private static int SidekickFirePeriod(ItemData? items, OptionItem o)
    {
        if (o.WPort == 0) return 0;
        var w = items?.Weapons.Get(o.WpNum) ?? default;
        return w.Loaded ? w.ShotRepeat + 1 : 1;
    }

    /// <summary>
    /// Which frame the engine would be showing on this tick.
    ///
    /// The frame table is stepped once per tick, but only while the sidekick's animation is
    /// ARMED, and that is the part the atlas used to leave out. <c>option 1</c> is armed
    /// forever; <c>option 2</c> is armed by firing a shot and disarms itself the moment the
    /// table wraps, so one shot buys exactly one pass and the pod then sits on gr[0] until the
    /// next one (mainint.c:7529 -- the wrap sets animation_enabled back to <c>option == 1</c>).
    ///
    /// Free-running an option-2 table is what made the Single Shot Option look nothing like
    /// the game: its gr[] is 168, 187, 206, 187 -- a muzzle flash, four ticks long, not an idle
    /// loop -- and repeating it forever at 35Hz is a strobe.
    /// </summary>
    private static int SidekickFrame(OptionItem o, int frames, int firePeriod, long tick, bool firing)
    {
        if (frames <= 1 || o.Option == 0) return 0;
        if (o.Option == 1) return (int)(tick % frames);
        if (!firing || firePeriod <= 0) return 0;      // trigger up: the rest frame
        // Fires on tick 0 of each period, then walks gr[1..frames-1] and stops back on gr[0].
        // A weapon that reloads faster than the table is long simply never gets to rest.
        long phase = tick % Math.Max(firePeriod, frames);
        return phase < frames - 1 ? (int)phase + 1 : 0;
    }

    /// <summary>
    /// The sidekick animating the way the engine animates it: through gr[0..ani-1] on the
    /// engine's own gate (see <see cref="SidekickFrame"/>), with the charge level ADDED to the
    /// sprite number (mainint.c:7540), which is why a charge sidekick's frames are spaced
    /// apart -- charging walks into the neighbouring sprites and gives each stage its own look.
    /// </summary>
    private void DrawSidekickStage(OptionItem o, SpriteAtlas atlas, int firePeriod)
    {
        int frames = SidekickFrames(o);
        bool gated = o.Option == 2;                    // only animates while it is shooting
        bool firing = !gated || _itemFiring;
        int frame = SidekickFrame(o, frames, firePeriod, (long)_itemClock, firing);
        int charge = o.Pwr == 0 ? 0
            : _itemChargeAuto ? (int)(((long)(_itemClock / SidekickChargeTicks)) % (o.Pwr + 1))
            : Math.Clamp(_itemCharge, 0, o.Pwr);

        const float stageH = 124f;
        var mn = ImGui.GetCursorScreenPos();
        var mx = new Vector2(mn.X + Math.Min(300f, ImGui.GetContentRegionAvail().X), mn.Y + stageH);
        var dl = ImGui.GetWindowDrawList();
        StageBegin(dl, mn, mx);
        DrawEnemyFrameCentered(dl, atlas, o.Gr[frame] + charge, o.DrawsFromPowerupSheet, mn, mx, 3f);
        StageEnd(dl);
        ImGui.Dummy(new Vector2(mx.X - mn.X, stageH));

        ImGui.SameLine(0, 12);
        ImGui.BeginGroup();
        bool resting = gated && frame == 0;
        KV("frame", resting ? $"1 of {frames}   ·   at rest" : $"{frame + 1} of {frames}", 0, 66f);
        KV("sprite", $"{o.Gr[frame] + charge}", 0, 66f);

        // Same controls as the enemy browser's stage, and for the same reason: at the engine's
        // real rate a twelve-frame loop is too fast to see what any one frame is.
        ImGui.Dummy(new Vector2(0, 3f));
        UiToggle("animate", ref _itemAnimate, AcShop,
            "Run the clock at the engine's 35Hz. Whether the body steps on every one\n" +
            "of those ticks is the entry's own business -- see \"in flight\" above.");
        ImGui.SameLine(0, 5);
        ImGui.SetNextItemWidth(96);
        ImGui.SliderFloat("##itemspeed", ref _itemAnimSpeed, 0.1f, 3f, "x%.2f");
        SliderReset(ref _itemAnimSpeed, 1f,
            "The engine runs at 35 ticks a second; this scales that.", "x1");

        // An option-2 sidekick is idle unless it is shooting, so the trigger is the control
        // that decides whether it animates at all -- without it the stage showed a four-frame
        // muzzle flash looping forever, which is not a thing the game ever draws.
        if (gated)
        {
            ImGui.Dummy(new Vector2(0, 3f));
            UiToggle("holding fire", ref _itemFiring, AcShop, firePeriod > 0
                ? $"This one only animates while it is shooting. Trigger down it fires\n" +
                  $"every {firePeriod} tick{(firePeriod == 1 ? "" : "s")} (weapon #{o.WpNum}, shotrepeat " +
                  $"{firePeriod - 1}), and each shot plays\nits {frames} frames once; let go and it rests on frame 1."
                : "Mounted on port 0, so it never fires -- and an option-2 body\nonly animates on a shot. In game it holds frame 1 forever.",
                0f, firePeriod <= 0);
        }

        if (o.Pwr > 0)
        {
            ImGui.Dummy(new Vector2(0, 3f));
            UiToggle("ramp the charge", ref _itemChargeAuto, AcShop,
                $"In game the charge climbs one stage every {SidekickChargeTicks}\n" +
                "ticks while you hold fire, and resets when it lets go.");
            ImGui.BeginDisabled(_itemChargeAuto);
            ImGui.SetNextItemWidth(150);
            ImGui.SliderInt("##itemcharge", ref _itemCharge, 0, o.Pwr, "charge %d");
            SliderReset(ref _itemCharge, 0, "Which charge stage the body is drawn at.");
            ImGui.EndDisabled();
            ImGui.TextColored(ColorOf(UiFaint), $"showing charge {charge} of {o.Pwr}");
        }
        else ImGui.TextColored(ColorOf(UiFaint), "no charge stages");
        ImGui.EndGroup();
    }

    /// <summary>Every frame at every charge: the rows are the charge stages, so a five-stage
    /// sidekick shows all six of its bodies at once.</summary>
    private void DrawSidekickFrames(OptionItem o, SpriteAtlas atlas)
    {
        UiSection("body frames", AcShop, o.Pwr > 0 ? "one row per charge stage" : "");
        int frames = SidekickFrames(o);
        var dl = ImGui.GetWindowDrawList();
        for (int c = 0; c <= o.Pwr; c++)
        {
            if (o.Pwr > 0)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(ColorOf(UiFaint), $"+{c}");
                ImGui.SameLine(0, 6);
            }
            // "First on this row", not "k > 0": a bank whose frame 0 is empty would otherwise
            // SameLine its frame 1 onto the previous charge stage's row.
            bool firstOnRow = true;
            for (int k = 0; k < frames; k++)
            {
                if (o.Gr[k] == 0) continue;
                if (!firstOnRow) ImGui.SameLine(0, 3);
                firstOnRow = false;
                var mn = ImGui.GetCursorScreenPos();
                var mx = mn + new Vector2(34f, 34f);
                ImGui.Dummy(new Vector2(34f, 34f));
                dl.AddRectFilled(mn, mx, Gfx.Rgba(18, 20, 28), 3f);
                DrawEnemyFrameCentered(dl, atlas, o.Gr[k] + c, o.DrawsFromPowerupSheet, mn, mx, 1f);
                dl.AddRect(mn, mx, UiLineSoft, 3f);
            }
            // A stage whose frames were all empty drew nothing and left the label dangling.
            if (firstOnRow) ImGui.NewLine();
        }
    }

    private static string MountName(byte tr) => tr switch
    {
        0 => "side pod",
        1 => "trailing",
        2 => "front",
        3 => "trailing (single)",
        4 => "orbiting",
        _ => $"style {tr}",
    };

    private static void DrawShieldDetail(ShieldItem s)
    {
        UiSection("shield", AcShop);
        UiStatBar("capacity", s.TPwr, 30, AcPlayer, $"{s.TPwr}");
        UiStatBar("recharge", s.MPwr, 30, AcGo, $"{s.MPwr}");
    }

    private static void DrawGeneratorDetail(PowerItem p)
    {
        UiSection("generator", AcShop);
        UiStatBar("power", p.Power, 30, AcShop, $"{p.Power}");
        UiStatBar("recharge rate", p.Speed, 30, AcGo, $"{p.Speed}");
    }

    private static void DrawSpecialDetail(SpecialItem s)
    {
        UiSection("special weapon", AcShop);
        UiStatBar("strength", s.Pwr, 30, AcBoss, $"{s.Pwr}");
        KV("effect type", $"{s.SType}" +
            (s.SType is >= 1 and <= 18 ? "" : "   (outside the engine's handled range)"));
        KV("weapon", s.Wpn > 0 ? $"fires weapon #{s.Wpn}" : "fires no weapon of its own");
    }
}
