using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;

namespace T2LV;

/// <summary>
/// The Outposts tab: the shops the other way round. The left column is every level whose flow
/// puts an outpost on the way in (a ']I', <see cref="GraphNode.ShopStops"/>), in campaign order;
/// the right pane is that outpost's whole shelf -- ships, front and rear weapons, generator, the
/// two sidekick slots and the shield -- each a click through to the item's own page. The
/// widescreen fork's Charge-Laser re-add is folded in exactly where <see cref="ChargeLaserOutpost"/>
/// puts it, so the shelf matches the build the browser is set to.
/// </summary>
public sealed unsafe partial class App
{
    private readonly record struct OutpostRow(int EpisodeIdx, int Episode, int FileNum, string Level,
        int Depth, GraphNode Node);

    /// <summary>Every outpost in the shown episodes, in the order the campaign reaches them.</summary>
    private List<OutpostRow> OutpostRows()
    {
        var rows = new List<OutpostRow>();
        if (_gd == null) return rows;
        foreach (int e in ShownEpisodes())
        {
            var ep = _gd.Episodes[e];
            var g = _gd.GetGraph(ep);
            if (g == null) continue;
            foreach (var n in g.Nodes)
                if (n.Kind == GraphNodeKind.Level && n.ShopStops.Count > 0)
                    rows.Add(new OutpostRow(e, ep.Number, n.LvlFileNum, n.Title.Trim(), n.Depth, n));
        }
        return rows.OrderBy(r => r.Episode).ThenBy(r => r.Depth).ThenBy(r => r.FileNum).ToList();
    }

    private void DrawOutpostList()
    {
        var rows = OutpostRows();
        string filter = BufText(_itemFilter).Trim();
        _outpostSel = Math.Clamp(_outpostSel, 0, Math.Max(0, rows.Count - 1));

        bool any = false;
        int lastEp = -1;
        const float rowH = 34f;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!Matches(filter, r.Level, r.FileNum.ToString(), $"episode {r.Episode}")) continue;
            any = true;
            if (_allEpisodes && r.Episode != lastEp)
            {
                UiSection($"Episode {r.Episode}", AcShop);
                lastEp = r.Episode;
            }

            bool sel = i == _outpostSel;
            var box = UiRow($"##oplist{i}", sel, AcShop, rowH);
            if (box.Clicked) _outpostSel = i;
            if (sel && _outpostScrollToSelection) { ImGui.SetScrollHereY(0.4f); _outpostScrollToSelection = false; }

            var dl = ImGui.GetWindowDrawList();
            int shelf = r.Node.ShopStops.Sum(s => s.Rows.Sum(row => row.Count(id => id > 0)));
            float lh = ImGui.GetTextLineHeight();
            float top = box.Min.Y + (rowH - 3f - lh * 2f - 1f) * 0.5f;
            float room = box.Max.X - box.Min.X - 20f;
            ClipText(dl, new Vector2(box.Min.X + 11f, top), room,
                sel ? Gfx.Rgba(250, 252, 255) : UiText, $"before {r.Level}  #{r.FileNum:00}");
            ClipText(dl, new Vector2(box.Min.X + 11f, top + lh + 1f), room, Shade(AcShop, 1f, 190),
                _allEpisodes ? $"Ep {r.Episode}  ·  outpost" : "outpost");
        }
        if (!any) ImGui.TextDisabled(rows.Count == 0 ? "No outpost in this episode." : "Nothing matches.");
    }

    private void DrawOutpostDetail()
    {
        var rows = OutpostRows();
        if (rows.Count == 0)
        {
            UiEmpty("No outposts here", "Pick \"All episodes\", or this episode has no shops.", AcShop);
            return;
        }
        _outpostSel = Math.Clamp(_outpostSel, 0, rows.Count - 1);
        var o = rows[_outpostSel];
        var ep = _gd!.Episodes[o.EpisodeIdx];
        var items = _gd.GetItems(ep, _itemFork);
        int clSlot = _itemFork ? items.ChargeLaserSlot : 0;

        UiTitle($"before {o.Level}", AcShop, "");
        Badge($"Episode {o.Episode}", Gfx.Rgba(150, 162, 185));
        ImGui.SameLine(0, 5f);
        Badge($"#{o.FileNum}", Gfx.Rgba(150, 162, 185));
        ImGui.SameLine(0, 5f);
        Badge("outpost", AcShop);
        ImGui.Dummy(new Vector2(0, 4f));
        if (UiButton("open the level", AcShop, "load the level this outpost sits before"))
            SelectLevelFile(o.EpisodeIdx, o.FileNum);
        if (o.Node.ShopStops.Count > 1)
        {
            ImGui.SameLine(0, 8f);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ColorOf(UiFaint), $"reached {o.Node.ShopStops.Count} ways -- shelves merged");
        }

        ImGui.Dummy(new Vector2(0, 4f));
        WellBegin("outpostbody", ImGui.GetContentRegionAvail(), AcShop, 12f, 9f);
        bool anySection = false;
        foreach (var (row, tab, cat) in ShopRowCats)
        {
            var ids = new List<int>();
            foreach (var stop in o.Node.ShopStops)
                foreach (int id in ShopRowIds(stop, row, ep.Number, clSlot))
                    if (ItemExists(items, tab, id) && !ids.Contains(id)) ids.Add(id);
            if (ids.Count == 0) continue;
            anySection = true;
            UiSection(ShopCatSection(cat), AcShop, $"{ids.Count}");
            foreach (int id in ids) DrawOutpostItemRow(items, tab, id, cat, clSlot);
        }
        if (!anySection) ImGui.TextDisabled("This outpost's list names nothing sellable.");
        WellEnd();
    }

    private void DrawOutpostItemRow(ItemData d, int tab, int id, ShopCat cat, int clSlot)
    {
        var (name, cost, _, _, _) = ItemRowFor(d, tab, id);
        bool charge = tab == 2 && id == clSlot && clSlot > 0;
        const float rowH = 36f;
        var box = UiRow($"##opitem{tab}_{id}_{(int)cat}", false, charge ? AcGo : AcShop, rowH);
        if (box.Clicked) ShowItemTab(tab, id);
        if (box.Hovered) ImGui.SetTooltip($"open {(name.Length > 0 ? name : "this item")} in its own tab");

        var dl = ImGui.GetWindowDrawList();
        DrawItemIconFor(dl, d, tab, id, new Vector2(box.Min.X + 8f, box.Min.Y + 2f),
            new Vector2(box.Min.X + 46f, box.Max.Y - 2f), 1f);

        float lh = ImGui.GetTextLineHeight();
        float top = box.Min.Y + (rowH - 3f - lh * 2f - 1f) * 0.5f;
        float room = box.Max.X - box.Min.X - 62f;
        ClipText(dl, new Vector2(box.Min.X + 52f, top), room, UiText, name.Length > 0 ? name : "(unnamed)");
        ClipText(dl, new Vector2(box.Min.X + 52f, top + lh + 1f), room,
            charge ? AcGo : cost > 0 ? Shade(AcItem, 1f, 205) : UiFaint,
            (cost > 0 ? $"{cost:n0} credits" : $"#{id}") + (charge ? "   ·   restored" : ""));
    }
}
