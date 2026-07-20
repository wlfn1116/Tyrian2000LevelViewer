using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;

namespace T2LV;

/// <summary>
/// The shop's side of the data set: ships, weapon ports, sidekicks, shields, generators and
/// special weapons, each with the icon the game itself draws for it. The viewer never
/// simulates a player, so none of this feeds the sim -- it is here because the tables sit in
/// the same block as enemyDat and nothing else reads them. See <see cref="ItemData"/>.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showItems;
    private int _itemTab;                 // index into ItemTabs
    private int _itemTabPending = -1;     // a tab the CLI asked for, applied once
    private int _itemRowPending = -1;     // ... and a row within it
    private bool _itemScrollToSelection;  // keep a row set from elsewhere in view
    private double _itemClock;            // ticks, for the sidekick animation
    private int _itemCharge;
    private bool _itemChargeAuto = true;

    /// <summary>Ticks per charge stage while the fire button is held (mainint.c:7565).</summary>
    private const int SidekickChargeTicks = 20;
    private int _itemSelected;
    private bool _itemOpened;             // first frame has chosen a row rather than "None"
    private float _itemListW = 250f;
    private readonly byte[] _itemFilter = new byte[64];

    private static readonly string[] ItemTabs =
        { "Ships", "Weapon ports", "Sidekicks", "Shields", "Generators", "Specials" };

    private static readonly uint AcItem = Gfx.Rgba(255, 210, 120);

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

        ImGui.SetNextWindowSize(new Vector2(980, 660), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(600, 360), new Vector2(float.MaxValue, float.MaxValue));
        bool open = _showItems;
        if (!ImGui.Begin("Ships & items###items", ref open)) { ImGui.End(); _showItems = open; return; }
        _showItems = open;

        var items = _gd.GetItems(CurEpisode);
        if (!_itemOpened && items.Loaded) { _itemSelected = FirstRealItem(items); _itemOpened = true; }
        _itemClock += ImGui.GetIO().DeltaTime * 35.0;   // the engine's fixed tick

        ImGui.SetNextItemWidth(130);
        EpisodeCombo("##itemepisode");
        ImGui.SameLine(0, 12);
        FilterBox("##itemfilter", "filter by name", _itemFilter, 180f);
        ImGui.SameLine(0, 12);
        ImGui.TextDisabled("(?) why the episode matters");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Episodes 4 and 5 carry their own copy of this whole block, and the engine\n" +
                "rewrites parts of it per episode on top of that. Real differences you can see\n" +
                "here: the Xega Ball is a six-bolt spread in 1-3 and one heavy bolt in 4-5, the\n" +
                "MicroSol's fifth option fires eight weak bolts against two, and the Beno Wallop\n" +
                "Beam gains a second bolt in 4-5. Shot patterns and sounds differ too.");

        if (!items.Loaded)
        {
            ImGui.Separator();
            ImGui.TextWrapped("The item tables could not be located in this episode's data. " +
                "They sit between the weapon table and enemyDat; if enemyDat itself loaded, " +
                "the block is laid out differently from the one this parser expects.");
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("itemtabs"))
        {
            for (int t = 0; t < ItemTabs.Length; t++)
                if (TabItem(ItemTabs[t], _itemTabPending == t))
                {
                    // Slot 0 of most tables is the shop's empty "None" row. It belongs in the
                    // list, but opening the detail pane on it shows nothing at all.
                    if (_itemTab != t) { _itemTab = t; _itemSelected = FirstRealItem(items); }
                    ImGui.EndTabItem();
                }
            ImGui.EndTabBar();
            // ImGui applies SetSelected on the frame AFTER the one that asks, so the request
            // has to outlive that frame -- clear it only once the tab it named is really up,
            // and apply the row then, over the tab switch's own default.
            if (_itemTabPending >= 0 && _itemTab == _itemTabPending)
            {
                if (_itemRowPending >= 0) _itemSelected = _itemRowPending;
                _itemTabPending = -1;
                _itemRowPending = -1;
            }
        }

        float maxList = Math.Max(180f, ImGui.GetContentRegionAvail().X - 320f);
        _itemListW = Math.Clamp(_itemListW, 180f, maxList);

        ImGui.BeginChild("itemlist", new Vector2(_itemListW, 0), ImGuiChildFlags.Borders);
        DrawItemList(items);
        ImGui.EndChild();
        ImGui.SameLine(0, 2);
        VSplitter("##itemsplit", ref _itemListW, 180f, maxList);
        ImGui.SameLine(0, 2);
        ImGui.BeginChild("itemdetail", new Vector2(0, 0));
        DrawItemDetail(items);
        ImGui.EndChild();

        ImGui.End();
    }

    /// <summary>Name, icon and price for a row in the current tab. Slot 0 of most tables is
    /// the empty "None" entry, which the shop shows too.</summary>
    private (string Name, int Cost, SpriteSource Src, int Sprite, bool Big) ItemRow(ItemData d, int i)
    {
        switch (_itemTab)
        {
            case 0:
            {
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
    private void DrawItemIcon(ImDrawListPtr dl, ItemData d, int index, Vector2 boxMin, Vector2 boxMax, float scale)
    {
        if (_itemTab == 0 && d.Ships[index].ShipGraphic == 1)
        {
            var shipSheet = Atlas(SpriteSource.MainSheet(8), AppSettings.GamePalette);
            if (shipSheet == null) return;
            // Two 2x2 halves side by side: 48x28 in all, so centring means backing off by
            // half of that, not by half of one block.
            var tl = new Vector2(
                MathF.Round((boxMin.X + boxMax.X) * 0.5f - 24f * scale),
                MathF.Round((boxMin.Y + boxMax.Y) * 0.5f - 14f * scale));
            Draw2x2(dl, shipSheet, 220, tl, scale);
            Draw2x2(dl, shipSheet, 222, tl + new Vector2(24f * scale, 0), scale);
            return;
        }

        var (_, _, src, sprite, big) = ItemRow(d, index);
        var atlas = Atlas(src, AppSettings.GamePalette);
        if (atlas != null) DrawEnemyFrameCentered(dl, atlas, sprite, big, boxMin, boxMax, scale);
    }

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
        0 => d.Ships.Length,
        1 => d.Ports.Length,
        2 => d.Options.Length,
        3 => d.Shields.Length,
        4 => d.Powers.Length,
        _ => d.Specials.Length,
    };

    private void DrawItemList(ItemData d)
    {
        string filter = BufText(_itemFilter).Trim();
        var dl = ImGui.GetWindowDrawList();
        int n = ItemCount(d);
        bool anyShown = false;

        for (int i = 0; i < n; i++)
        {
            var (name, cost, src, sprite, big) = ItemRow(d, i);
            if (!Matches(filter, name, i.ToString())) continue;
            anyShown = true;

            var mn = ImGui.GetCursorScreenPos();
            bool sel = i == _itemSelected;
            if (ImGui.Selectable($"##it{i}", sel, ImGuiSelectableFlags.None, new Vector2(0, 32f)))
                _itemSelected = i;
            if (sel && _itemScrollToSelection) { ImGui.SetScrollHereY(0.4f); _itemScrollToSelection = false; }

            DrawItemIcon(dl, d, i, mn, new Vector2(mn.X + 36f, mn.Y + 30f), 1f);
            dl.AddText(new Vector2(mn.X + 40f, mn.Y + 1f), Gfx.Rgba(228, 232, 242),
                name.Length > 0 ? name : "(unnamed)");

            bool restored = _itemTab == 2 && i == d.ChargeLaserSlot && d.ChargeLaserSlot > 0;
            dl.AddText(new Vector2(mn.X + 40f, mn.Y + 15f), restored ? AcGo : Gfx.Rgba(140, 146, 162),
                (cost > 0 ? $"{cost:n0} credits" : $"#{i}") + (restored ? "   ·   restored cut content" : ""));
        }
        if (!anyShown) ImGui.TextDisabled("Nothing matches.");
    }

    private void DrawItemDetail(ItemData d)
    {
        int n = ItemCount(d);
        _itemSelected = Math.Clamp(_itemSelected, 0, Math.Max(0, n - 1));
        if (n == 0) return;

        var (name, cost, src, sprite, _) = ItemRow(d, _itemSelected);
        DrawGameHeader(name.Length > 0 ? name : "(unnamed)", AcItem);

        // --- For a ship, the shop's large illustration leads and the in-flight icon sits
        //     beside it; for everything else the icon is all there is, so it gets the room. ---
        var dl = ImGui.GetWindowDrawList();
        var big2 = _itemTab == 0 ? Atlas(SpriteSource.MainBank(5), AppSettings.GamePalette) : null;
        int bigIdx = _itemTab == 0 ? d.Ships[_itemSelected].BigShipGraphic - 1 : -1;
        bool hasBig = big2 != null && bigIdx >= 0 && big2.Has(bigIdx);

        if (hasBig)
        {
            var (bw, bh) = big2!.SizeOf(bigIdx);
            // Integer scale so the pixels stay crisp, as large as the panel allows.
            float bs = Math.Max(1f, MathF.Floor(Math.Min(320f / Math.Max(1, bw), 200f / Math.Max(1, bh))));
            var box = new Vector2(bw * bs + 20f, bh * bs + 16f);
            var p = ImGui.GetCursorScreenPos();
            dl.AddRectFilled(p, p + box, Gfx.Rgba(14, 16, 22), 4f);
            big2.DrawCentered(dl, bigIdx, p, p + box, bs);
            dl.AddRect(p, p + box, Gfx.Rgba(80, 90, 118, 190), 4f);
            ImGui.Dummy(box);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"the shop's large illustration (bigshipgraphic {bigIdx + 1}), at {bs:0}x");
            ImGui.SameLine(0, 10);
        }

        float iconBox = hasBig ? 84f : 110f;
        var at = ImGui.GetCursorScreenPos();
        dl.AddRectFilled(at, at + new Vector2(iconBox, iconBox), Gfx.Rgba(14, 16, 22), 4f);
        DrawItemIcon(dl, d, _itemSelected, at, at + new Vector2(iconBox, iconBox), hasBig ? 2f : 3f);
        dl.AddRect(at, at + new Vector2(iconBox, iconBox), Gfx.Rgba(80, 90, 118, 190), 4f);
        ImGui.Dummy(new Vector2(iconBox, iconBox));
        if (ImGui.IsItemHovered() && _itemTab == 0) ImGui.SetTooltip("the ship as it flies");

        ImGui.Dummy(new Vector2(0, 4));
        if (cost > 0) ImGui.TextDisabled($"{cost:n0} credits");
        if (ImGui.SmallButton("open the sprite##itemspr")) OpenSprite(src, sprite);
        ImGui.Separator();

        switch (_itemTab)
        {
            case 0: DrawShipDetail(d.Ships[_itemSelected]); break;
            case 1: DrawPortDetail(d.Ports[_itemSelected]); break;
            case 2: DrawSidekickDetail(d.Options[_itemSelected]); break;
            case 3: DrawShieldDetail(d.Shields[_itemSelected]); break;
            case 4: DrawGeneratorDetail(d.Powers[_itemSelected]); break;
            default: DrawSpecialDetail(d.Specials[_itemSelected]); break;
        }
    }

    private static void DrawShipDetail(ShipItem s)
    {
        StatBar("armor", s.Dmg, 30, AcBoss);
        ImGui.TextDisabled($"speed modifier {s.Spd:+0;-0;0}   ·   {s.Ani} animation frames");
        ImGui.TextDisabled(s.ShipGraphic == 1
            ? "graphic 1 is a sentinel: the Nort Ship, drawn as two halves"
            : $"ship sprite {s.ShipGraphic}" + (s.ShipGraphic > 500 ? "  (Tyrian 2000 sheet)" : ""));
    }

    private void DrawPortDetail(PortItem p)
    {
        ImGui.TextDisabled($"{p.OpNum} fire mode{(p.OpNum == 1 ? "" : "s")}   ·   {p.PowerUse} power per shot");
        var wd = _gd != null && CurEpisode != null ? _gd.GetItems(CurEpisode).Weapons : null;

        for (int mode = 0; mode < Math.Max(1, (int)p.OpNum); mode++)
        {
            ImGui.SeparatorText(p.OpNum > 1 ? $"mode {mode + 1}" : "power levels");
            if (!ImGui.BeginTable($"pw{mode}", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) continue;
            ImGui.TableSetupColumn("lvl", ImGuiTableColumnFlags.WidthFixed, 34f);
            ImGui.TableSetupColumn("weapon", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("shots");
            ImGui.TableSetupColumn("damage");
            ImGui.TableSetupColumn("rate");
            ImGui.TableHeadersRow();

            for (int lvl = 0; lvl < 11; lvl++)
            {
                int id = p.Op[mode, lvl];
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text($"{lvl + 1}");
                ImGui.TableNextColumn(); ImGui.TextDisabled($"#{id}");
                var w = wd?.Get(id) ?? default;
                if (!w.Loaded) { ImGui.TableNextColumn(); ImGui.TextDisabled("-"); continue; }
                ImGui.TableNextColumn(); ImGui.Text($"{w.Max}");
                ImGui.TableNextColumn(); ImGui.Text($"{w.Attack.Take(Math.Clamp((int)w.Max, 1, 8)).Sum(a => (int)a)}");
                ImGui.TableNextColumn(); ImGui.Text(w.ShotRepeat > 0 ? $"every {w.ShotRepeat}" : "-");
            }
            ImGui.EndTable();
        }
    }

    private void DrawSidekickDetail(OptionItem o)
    {
        var items = _gd != null && CurEpisode != null ? _gd.GetItems(CurEpisode) : null;
        if (items != null && items.ChargeLaserSlot == _itemSelected)
            ImGui.TextColored(ColorOf(AcGo),
                "Restored cut content: Tyrian 2000 dropped this sidekick and reused its slot for\n" +
                "the Mint-O-Ship. The widescreen build puts it back from the DOS data, into the\n" +
                "first free sidekick slot and six otherwise-unused weapon records.");

        ImGui.TextDisabled($"mount: {MountName(o.Tr)}   ·   speed {o.OpSpd:+0;-0;0}");
        ImGui.TextDisabled(o.Ammo > 0 ? $"{o.Ammo} rounds before it reloads" : "no ammo limit");
        ImGui.TextDisabled($"{o.Pwr} charge stage{(o.Pwr == 1 ? "" : "s")}   ·   fires weapon #{o.WpNum} through port {o.WPort}");
        ImGui.TextDisabled(o.Option switch
        {
            0 => "not drawn in flight",
            1 => "always animating",
            _ => "animates while firing",
        } + (o.Stop ? "   ·   stops with the ship" : ""));

        // A charge sidekick fires wpnum + charge, so its stages are consecutive weapon records.
        if (o.Pwr > 0 && items != null)
        {
            ImGui.SeparatorText("charge stages");
            if (ImGui.BeginTable("chg", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("charge", ImGuiTableColumnFlags.WidthFixed, 54f);
                ImGui.TableSetupColumn("weapon", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableSetupColumn("damage");
                ImGui.TableSetupColumn("rate");
                ImGui.TableHeadersRow();
                for (int c = 0; c <= o.Pwr; c++)
                {
                    var w = items.Weapons.Get(o.WpNum + c);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.Text($"{c}");
                    ImGui.TableNextColumn(); ImGui.TextDisabled($"#{o.WpNum + c}");
                    if (!w.Loaded) { ImGui.TableNextColumn(); ImGui.TextDisabled("-"); continue; }
                    ImGui.TableNextColumn();
                    ImGui.Text($"{w.Attack.Take(Math.Clamp((int)w.Max, 1, 8)).Sum(a => (int)a)}");
                    ImGui.TableNextColumn();
                    ImGui.Text(w.ShotRepeat > 0 ? $"every {w.ShotRepeat}" : "-");
                }
                ImGui.EndTable();
            }
        }

        if (o.Ani == 0) return;
        // tr 1 and 2 draw the body as a 2x2 out of the powerup sheet, everything else as a
        // single 12x14 sprite out of the ship sheet (mainint.c:7549).
        var src = SpriteSource.MainSheet(o.DrawsFromPowerupSheet ? 9 : 8);
        var atlas = Atlas(src, AppSettings.GamePalette);
        if (atlas == null) { ImGui.TextDisabled("(sheet unavailable)"); return; }

        ImGui.SeparatorText("in flight");
        DrawSidekickStage(o, atlas);
        DrawSidekickFrames(o, atlas);
    }

    /// <summary>
    /// The sidekick animating the way the engine animates it: one frame per tick through
    /// gr[0..ani-1], with the charge level ADDED to the sprite number (mainint.c:7540), which
    /// is why a charge sidekick's frames are spaced apart -- charging walks into the
    /// neighbouring sprites and gives each stage its own look.
    /// </summary>
    private void DrawSidekickStage(OptionItem o, SpriteAtlas atlas)
    {
        int frame = o.Ani > 0 ? (int)(((long)_itemClock) % Math.Min((int)o.Ani, 20)) : 0;
        int charge = o.Pwr == 0 ? 0
            : _itemChargeAuto ? (int)(((long)(_itemClock / SidekickChargeTicks)) % (o.Pwr + 1))
            : Math.Clamp(_itemCharge, 0, o.Pwr);

        const float stageH = 120f;
        var mn = ImGui.GetCursorScreenPos();
        var mx = new Vector2(mn.X + Math.Min(300f, ImGui.GetContentRegionAvail().X), mn.Y + stageH);
        var dl = ImGui.GetWindowDrawList();
        DrawStarStage(dl, mn, mx);
        DrawEnemyFrameCentered(dl, atlas, o.Gr[frame] + charge, o.DrawsFromPowerupSheet, mn, mx, 3f);
        ImGui.Dummy(new Vector2(mx.X - mn.X, stageH));

        ImGui.SameLine(0, 12);
        ImGui.BeginGroup();
        ImGui.TextDisabled($"frame {frame + 1} of {Math.Min((int)o.Ani, 20)}   ·   sprite {o.Gr[frame] + charge}");
        if (o.Pwr > 0)
        {
            ImGui.Checkbox("ramp the charge", ref _itemChargeAuto);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"In game the charge climbs one stage every {SidekickChargeTicks}\n" +
                    "ticks while you hold fire, and resets when it lets go.");
            ImGui.BeginDisabled(_itemChargeAuto);
            ImGui.SetNextItemWidth(150);
            ImGui.SliderInt("charge", ref _itemCharge, 0, o.Pwr);
            ImGui.EndDisabled();
            ImGui.TextDisabled($"showing charge {charge} of {o.Pwr}");
        }
        else ImGui.TextDisabled("no charge stages");
        ImGui.EndGroup();
    }

    /// <summary>Every frame at every charge: the rows are the charge stages, so a five-stage
    /// sidekick shows all six of its bodies at once.</summary>
    private void DrawSidekickFrames(OptionItem o, SpriteAtlas atlas)
    {
        ImGui.SeparatorText("body frames");
        int frames = Math.Min((int)o.Ani, 20);
        var dl = ImGui.GetWindowDrawList();
        for (int c = 0; c <= o.Pwr; c++)
        {
            if (o.Pwr > 0)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled($"+{c}");
                ImGui.SameLine(0, 6);
            }
            for (int k = 0; k < frames; k++)
            {
                if (o.Gr[k] == 0) continue;
                if (k > 0) ImGui.SameLine(0, 3);
                var mn = ImGui.GetCursorScreenPos();
                var mx = mn + new Vector2(34f, 34f);
                ImGui.Dummy(new Vector2(34f, 34f));
                dl.AddRectFilled(mn, mx, Gfx.Rgba(18, 20, 28), 2f);
                DrawEnemyFrameCentered(dl, atlas, o.Gr[k] + c, o.DrawsFromPowerupSheet, mn, mx, 1f);
                dl.AddRect(mn, mx, Gfx.Rgba(60, 66, 84), 2f);
            }
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
        StatBar("capacity", s.TPwr, 30, AcPlayer);
        StatBar("recharge", s.MPwr, 30, AcGo);
    }

    private static void DrawGeneratorDetail(PowerItem p)
    {
        StatBar("power", p.Power, 30, AcBuild);
        ImGui.TextDisabled($"recharge rate {p.Speed}");
    }

    private void DrawSpecialDetail(SpecialItem s)
    {
        ImGui.TextDisabled($"strength {s.Pwr}   ·   effect type {s.SType}" +
            (s.SType is >= 1 and <= 18 ? "" : "  (outside the engine's handled range)"));
        ImGui.TextDisabled(s.Wpn > 0 ? $"fires weapon #{s.Wpn}" : "fires no weapon of its own");
    }

    /// <summary>A labelled bar, so two items compare at a glance rather than by reading numbers.</summary>
    private static void StatBar(string label, int value, int max, uint accent)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"{label,-9}");
        ImGui.SameLine(0, 6);
        var mn = ImGui.GetCursorScreenPos();
        var size = new Vector2(170f, ImGui.GetTextLineHeight());
        ImGui.Dummy(size);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(mn, mn + size, Gfx.Rgba(30, 33, 42), 2f);
        float f = Math.Clamp(value / (float)max, 0f, 1f);
        if (f > 0) dl.AddRectFilled(mn, new Vector2(mn.X + size.X * f, mn.Y + size.Y), Shade(accent, 0.85f), 2f);
        ImGui.SameLine(0, 8);
        ImGui.Text($"{value}");
    }
}
