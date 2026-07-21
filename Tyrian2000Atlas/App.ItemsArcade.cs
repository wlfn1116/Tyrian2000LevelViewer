using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The Arcade tab: Super-Arcade mode laid out ship by ship. In that mode you fly one of nine fixed
/// ships (<see cref="SAShip"/>), and the front weapon is not bought -- it is delivered by balls. A
/// purple ball (which most enemies drop; tyrian2.c:3062-3068) turns into one of five coloured balls
/// in turn -- Red, Blue, Black, Green, Purple (sprite 5 + n·2) -- and each colour fits THIS ship's
/// weapon at that step (<see cref="SAWeapon"/>, mainint.c:7811-7816). So the five balls share their
/// sprites across every ship but do something different on each -- the Nort ship's Green ball is not
/// the Dragon's Green ball. The special comes fitted too, and upgrades on the second special ball
/// (<see cref="SASpecialWeapon"/> → <see cref="SASpecialWeaponB"/>).
///
/// Below the ships sit the raw ball pickups -- the whole front / rear / sidekick pool, placed or not
/// -- each a click through to the weapon or sidekick it swaps in. Special balls stay in the Other
/// tab, since those turn up in the normal game.
/// </summary>
public sealed unsafe partial class App
{
    /// <summary>The nine Super-Arcade ships, named as varz.c labels them (SAWeapon's comments).</summary>
    private static readonly string[] ArcadeShipNames =
        { "Stealth Ship", "StormWind", "Techno", "Enemy", "Weird", "Unknown", "NortShip Z", "Dragon", "Pretzel Pete" };

    /// <summary>The five ball colours a front weapon cycles through (SAWeapon's column header).</summary>
    private static readonly string[] ArcadeBallColors = { "Red", "Blue", "Black", "Green", "Purple" };

    /// <summary>Ball n (0-based) wears powerup-sheet sprite 5 + (n+1)·2 (tyrian2.c:3067).</summary>
    private static int ArcadeBallSprite(int n) => 5 + (n + 1) * 2;

    /// <summary>How many list rows the nine ships take before the ball pool starts.</summary>
    private const int ArcadeShipCount = 9;

    private void DrawArcadeList()
    {
        var items = _gd!.GetItems(CurEpisode!, _itemFork);
        var balls = ArcadeBallPickups();
        string filter = BufText(_itemFilter).Trim();
        _arcadeSel = Math.Clamp(_arcadeSel, 0, ArcadeShipCount + balls.Count - 1);
        const float rowH = 36f;

        UiSection("super-arcade ships", AcArcade, $"{ArcadeShipCount}");
        for (int i = 0; i < ArcadeShipCount; i++)
        {
            int hullId = SAShip[i];
            string hull = hullId < items.Ships.Length ? items.Ships[hullId].Name.Trim() : "";
            if (!Matches(filter, ArcadeShipNames[i], hull)) continue;

            bool sel = _arcadeSel == i;
            var box = UiRow($"##arcs{i}", sel, AcArcade, rowH);
            if (box.Clicked) _arcadeSel = i;
            if (sel && _arcadeScrollToSelection) { ImGui.SetScrollHereY(0.4f); _arcadeScrollToSelection = false; }

            var dl = ImGui.GetWindowDrawList();
            DrawItemIconFor(dl, items, 0, hullId, new Vector2(box.Min.X + 7f, box.Min.Y + 2f),
                new Vector2(box.Min.X + 45f, box.Max.Y - 2f), 1f);
            float lh = ImGui.GetTextLineHeight();
            float top = box.Min.Y + (rowH - 3f - lh * 2f - 1f) * 0.5f;
            float room = box.Max.X - box.Min.X - 60f;
            ClipText(dl, new Vector2(box.Min.X + 50f, top), room, sel ? Gfx.Rgba(250, 252, 255) : UiText, ArcadeShipNames[i]);
            ClipText(dl, new Vector2(box.Min.X + 50f, top + lh + 1f), room, Shade(AcArcade, 1f, 190),
                hull.Length > 0 ? $"hull: {hull}" : $"ship #{hullId}");
        }

        OtherKind? lastKind = null;
        for (int k = 0; k < balls.Count; k++)
        {
            var p = balls[k];
            string title = OtherRowTitle(items, p);
            if (!Matches(filter, title, OtherKindName(p.Kind))) continue;
            if (lastKind != p.Kind) { UiSection(OtherKindName(p.Kind), AcArcade); lastKind = p.Kind; }

            int idx = ArcadeShipCount + k;
            bool sel = _arcadeSel == idx;
            var box = UiRow($"##arcb{k}", sel, AcArcade, rowH);
            if (box.Clicked) _arcadeSel = idx;
            if (sel && _arcadeScrollToSelection) { ImGui.SetScrollHereY(0.4f); _arcadeScrollToSelection = false; }

            var dl = ImGui.GetWindowDrawList();
            var atlas = Atlas(EnemySpriteSource(p.Rep.ShapeBank), AppSettings.GamePalette);
            if (atlas != null)
                DrawEnemyFrameCentered(dl, atlas, p.Sprite, p.Rep.Esize == 1,
                    new Vector2(box.Min.X + 7f, box.Min.Y + 2f), new Vector2(box.Min.X + 45f, box.Max.Y - 2f), 1f);
            float lh = ImGui.GetTextLineHeight();
            float top = box.Min.Y + (rowH - 3f - lh * 2f - 1f) * 0.5f;
            float room = box.Max.X - box.Min.X - 60f;
            ClipText(dl, new Vector2(box.Min.X + 50f, top), room, sel ? Gfx.Rgba(250, 252, 255) : UiText, title);
            ClipText(dl, new Vector2(box.Min.X + 50f, top + lh + 1f), room, Shade(AcArcade, 1f, 190),
                p.Appears ? "placed / dropped" : "arcade pool");
        }
    }

    private void DrawArcadeDetail()
    {
        var items = _gd!.GetItems(CurEpisode!, _itemFork);
        var balls = ArcadeBallPickups();
        _arcadeSel = Math.Clamp(_arcadeSel, 0, ArcadeShipCount + balls.Count - 1);
        if (_arcadeSel < ArcadeShipCount) DrawArcadeShipDetail(items, _arcadeSel);
        else DrawArcadeBallDetail(items, balls[_arcadeSel - ArcadeShipCount]);
    }

    private void DrawArcadeShipDetail(ItemData items, int i)
    {
        int hullId = SAShip[i];
        var dl = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        const float box = 116f;
        Well(dl, at, at + new Vector2(box, box), AcArcade, 6f);
        // Integer scale only: a 2x2 ship metasprite drawn at 2.5x seams between its four quads.
        // The Nort Ship is two 2x2 halves (48px wide), so it takes the smaller scale to fit.
        bool nort = hullId < items.Ships.Length && items.Ships[hullId].ShipGraphic == 1;
        DrawItemIconFor(dl, items, 0, hullId, at, at + new Vector2(box, box), nort ? 2f : 3f);
        ImGui.Dummy(new Vector2(box, box));
        ImGui.SameLine(0, 12);

        ImGui.BeginGroup();
        UiTitle(ArcadeShipNames[i], AcArcade);
        Badge("arcade ship", AcArcade);
        ImGui.SameLine(0, 5f);
        Badge($"ship {i + 1} of {ArcadeShipCount}", Gfx.Rgba(150, 162, 185));
        ImGui.Dummy(new Vector2(0, 4f));
        string hull = hullId < items.Ships.Length ? items.Ships[hullId].Name.Trim() : $"ship #{hullId}";
        if (UiButton($"hull: {hull}", AcArcade, "open this ship's hull in the Ships tab"))
            ShowItemTab(0, hullId);
        ImGui.EndGroup();

        ImGui.Dummy(new Vector2(0, 4f));
        WellBegin("arcadebody", ImGui.GetContentRegionAvail(), AcArcade, 12f, 9f);

        UiSection("front weapon balls", AcArcade, "Red -> Purple");
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(ColorOf(UiFaint),
            "The purple balls most enemies drop become these five coloured balls in turn. Each colour " +
            "fits THIS ship's weapon at that step -- the same five sprites do something different on " +
            "every arcade ship.");
        ImGui.PopTextWrapPos();

        const float rowH = 34f;
        for (int n = 0; n < 5; n++)
        {
            int wid = SAWeapon[i, n];
            string wname = wid > 0 && wid < items.Ports.Length ? items.Ports[wid].Name.Trim() : $"weapon #{wid}";
            var b = UiRow($"##arcw{i}_{n}", false, AcArcade, rowH);
            if (b.Clicked && wid > 0) ShowItemTab(1, wid);
            if (b.Hovered) ImGui.SetTooltip("open this weapon in the Weapon ports tab");
            var dl2 = ImGui.GetWindowDrawList();
            var atlas = Atlas(SpriteSource.MainSheet(9), AppSettings.GamePalette);   // bank 26, the powerups sheet
            if (atlas != null)
                DrawEnemyFrameCentered(dl2, atlas, ArcadeBallSprite(n), true,
                    new Vector2(b.Min.X + 6f, b.Min.Y + 2f), new Vector2(b.Min.X + 40f, b.Max.Y - 2f), 1f);
            RowText(b, 46f, $"{ArcadeBallColors[n]} ball  ->  {wname}", $"front weapon step {n + 1}", AcArcade, false, 14f);
        }

        ImGui.Dummy(new Vector2(0, 4f));
        UiSection("special", AcArcade);
        int sp = SASpecialWeapon[i], spB = SASpecialWeaponB[i];
        DrawArcadeSpecialRow(items, sp, "fitted from the start", $"##arcsp{i}a");
        if (spB != sp) DrawArcadeSpecialRow(items, spB, "on the second special ball", $"##arcsp{i}b");

        WellEnd();
    }

    private void DrawArcadeSpecialRow(ItemData items, int specialId, string note, string id)
    {
        string name = specialId > 0 && specialId < items.Specials.Length ? items.Specials[specialId].Name.Trim() : $"special #{specialId}";
        const float rowH = 30f;
        var b = UiRow(id, false, AcArcade, rowH);
        if (b.Clicked && specialId > 0) ShowItemTab(5, specialId);
        if (b.Hovered) ImGui.SetTooltip("open this special in the Specials tab");
        RowText(b, 10f, name, note, AcArcade, false, 14f);
    }

    private void DrawArcadeBallDetail(ItemData items, OtherPickup p)
    {
        var dl = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        const float box = 116f;
        Well(dl, at, at + new Vector2(box, box), AcArcade, 6f);
        var atlas = Atlas(EnemySpriteSource(p.Rep.ShapeBank), AppSettings.GamePalette);
        if (atlas != null)
            DrawEnemyFrameCentered(dl, atlas, PickupSprite(p.Rep), p.Rep.Esize == 1, at, at + new Vector2(box, box), 3f);
        ImGui.Dummy(new Vector2(box, box));
        ImGui.SameLine(0, 12);

        ImGui.BeginGroup();
        UiTitle(OtherRowTitle(items, p), AcArcade);
        Badge(OtherKindName(p.Kind), AcArcade);
        ImGui.SameLine(0, 5f);
        Badge($"value {p.Value}", AcItem);
        ImGui.Dummy(new Vector2(0, 4f));
        UiToggle("animate", ref _itemAnimate, AcArcade, "Run the ball's frames at the engine's 35Hz.");
        ImGui.SameLine(0, 5);
        ImGui.SetNextItemWidth(96);
        ImGui.SliderFloat("##arcspeed", ref _itemAnimSpeed, 0.1f, 3f, "x%.2f");
        SliderReset(ref _itemAnimSpeed, 1f,
            "The engine runs at 35 ticks a second; this scales that.", "x1");
        ImGui.EndGroup();

        ImGui.Dummy(new Vector2(0, 4f));
        WellBegin("arcadeballbody", ImGui.GetContentRegionAvail(), AcArcade, 12f, 9f);

        UiSection("what it does", AcArcade);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(ColorOf(UiText), OtherKindDesc(p.Kind));
        ImGui.PopTextWrapPos();
        if (p.GrantTab >= 0 && ItemExists(items, p.GrantTab, p.GrantId))
        {
            string g = OtherGrantName(items, p);
            if (UiButton($"grants: {g}", AcItem, "open the item this ball swaps in"))
                ShowItemTab(p.GrantTab, p.GrantId);
        }
        if (p.Kind == OtherKind.FrontBall)
            ImGui.TextColored(ColorOf(UiFaint),
                "In Super-Arcade this value is read against the current ship instead (see the ships above).");
        KV("sprite", $"bank {p.Rep.ShapeBank}, frame {p.Sprite}" + (p.Rep.Ani > 1 ? $"  ({p.Rep.Ani} frames)" : ""), 0, 84f);

        DrawOtherDroppers(p);
        DrawOtherDirect(p);
        WellEnd();
    }
}
