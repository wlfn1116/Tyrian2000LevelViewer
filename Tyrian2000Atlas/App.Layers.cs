using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The layer stack: which of the level's eleven layers are drawn, in what order, and at what
/// opacity.
///
/// It used to be a row of default ImGui widgets per layer -- checkbox, Selectable, two arrow
/// buttons, a 52px slider -- inside a bordered child, and it had two problems. It looked like
/// nothing else in the app, and during playback it half worked: visibility reached the running
/// simulation, order did not, and the panel said so in a line of small grey text rather than in
/// anything you could see.
///
/// Both are fixed here, and the second is the interesting one. The engine has no layer list to
/// sort: its draw order IS the gameplay loop, and a background band is drawn wherever the loop
/// calls it. What the level data can move is four flags (background2over, background3over,
/// topEnemyOver, skyEnemyOverAll), thirty-six combinations in all, and those are exactly what
/// <see cref="GameSim.DrawOrder"/> now takes as an override. So a drag here is answered by
/// <see cref="LayerStack.FlagsFor"/> -- the closest arrangement the engine can actually be asked
/// for -- and the list then snaps to it, so what you see in the panel is what the simulation is
/// going to draw rather than a wish it will quietly ignore.
///
/// That also means some rows cannot move while a simulation is running, and some move together:
/// the terrain is always first and the stars always behind it, and the five object categories
/// are one band the engine draws in one place. The panel shows that -- a locked row has no grip,
/// and a band is bracketed and dragged whole -- instead of accepting a drag it is going to undo.
/// </summary>
public sealed unsafe partial class App
{
    private const float LayerRowH = 21f;
    private static readonly uint AcLayer = Gfx.Rgba(140, 186, 255);

    /// <summary>Id of the row being dragged, "" while nothing is.</summary>
    private string _layDrag = "";
    /// <summary>Where the drag would drop it: an index into <see cref="LayerGroups"/>, i.e. a
    /// gap between two groups, 0 = in front of everything.</summary>
    private int _layDropAt = -1;

    /// <summary>
    /// The engine flags the current stack order asks for, cached between drags. Null means
    /// "work it out again": every reorder clears it, and <see cref="LayerOrderFlags"/> derives
    /// it once from the stack rather than on every frame the HUD draws.
    /// </summary>
    private LevelStartFlags? _layerOrderFlags;

    /// <summary>The order the running simulation was last seen using, so the panel can re-sort
    /// itself when an event moves a band and only when one does.</summary>
    private LevelStartFlags _layerLiveSeen;
    private bool _layerLiveSeenValid;

    // =====================================================================
    //  Grouping
    // =====================================================================

    /// <summary>
    /// Which rows move as one. Off playback every layer is its own group and the stack is a
    /// free permutation; during playback the five object categories collapse into the single
    /// band the engine draws, because nothing in the level data can separate them.
    /// </summary>
    private string LayerGroupKey(LayerDef l) => HudLive ? LayerStack.SlotOf(l) : l.Id;

    /// <summary>Can this row be dragged at all? During playback, only the layers the engine's
    /// own four flags are able to move.</summary>
    private bool LayerMovable(LayerDef l) =>
        !HudLive || Array.IndexOf(LayerStack.Movable, LayerStack.SlotOf(l)) >= 0;

    /// <summary>The stack as runs of adjacent rows that move together, front to back.</summary>
    private List<(string Key, int First, int Count)> LayerGroups()
    {
        var groups = new List<(string, int, int)>();
        for (int i = 0; i < _layers.Count;)
        {
            string k = LayerGroupKey(_layers[i]);
            int n = 1;
            while (i + n < _layers.Count && LayerGroupKey(_layers[i + n]) == k) n++;
            groups.Add((k, i, n));
            i += n;
        }
        return groups;
    }

    // =====================================================================
    //  Order <-> engine
    // =====================================================================

    /// <summary>Do two draw orders put everything in the same place?</summary>
    private static bool SameOrder(in LevelStartFlags a, in LevelStartFlags b) =>
        a.Background2Over == b.Background2Over && a.Background3Over == b.Background3Over &&
        a.TopEnemyOver == b.TopEnemyOver && a.SkyEnemyOverAll == b.SkyEnemyOverAll;

    /// <summary>The flags the stack is asking the simulation for, derived once per reorder.</summary>
    private LevelStartFlags LayerOrderFlags(GameSim sim)
    {
        _layerOrderFlags ??= LayerStack.FlagsFor(_layers, sim.LiveDrawOrder);
        return _layerOrderFlags.Value;
    }

    /// <summary>
    /// Put the stack into the arrangement the engine will actually draw for it: ask
    /// <see cref="LayerStack.FlagsFor"/> what the order comes closest to, then re-sort the list
    /// with that answer. This is what makes a drag that the engine cannot honour visibly spring
    /// back rather than sit there being ignored.
    /// </summary>
    private void SnapLayersToEngine()
    {
        var sim = _playback?.Sim;
        if (sim == null) return;
        _layerOrderFlags = LayerStack.FlagsFor(_layers, sim.LiveDrawOrder);
        _layers = LayerStack.GameOrder(_layers, _layerOrderFlags.Value);
    }

    /// <summary>The engine-order switch was thrown, or a level was loaded under it.</summary>
    private void ApplyEngineOrder()
    {
        _layerOrderFlags = null;
        if (!_gameLayerOrder)
        {
            // Turning it off keeps the stack exactly where the engine had just left it -- which
            // is the point: you take over from the order you were watching.
            if (HudLive) SnapLayersToEngine();
            return;
        }
        _layerLiveSeenValid = false;
        if (_level != null) _layers = LayerStack.GameOrder(_layers, _level.ComputeStartFlags());
        _composeDirty = true;
    }

    /// <summary>
    /// Mirror the stack's order onto the running simulation, and -- while the engine is the one
    /// in charge -- mirror the simulation's order back into the stack, so the panel re-sorts
    /// itself as the level's own events move a band. Returns true if the frame has to be drawn
    /// again. Called from <see cref="SyncPlaybackVisibility"/>, beside the visibility half.
    /// </summary>
    private bool SyncPlaybackLayerOrder(GameSim sim)
    {
        if (_gameLayerOrder)
        {
            bool changed = sim.DrawOrder != null;
            sim.DrawOrder = null;
            var live = sim.LiveDrawOrder;
            if (!_layerLiveSeenValid || !SameOrder(_layerLiveSeen, live))
            {
                _layerLiveSeen = live;
                _layerLiveSeenValid = true;
                _layers = LayerStack.GameOrder(_layers, live);
            }
            return changed;
        }

        var want = LayerOrderFlags(sim);
        if (sim.DrawOrder is { } had && SameOrder(had, want)) return false;
        sim.DrawOrder = want;
        _layerLiveSeenValid = false;
        return true;
    }

    // =====================================================================
    //  Moving
    // =====================================================================

    /// <summary>Move group <paramref name="from"/> so that it lands in the gap
    /// <paramref name="to"/> (0 = in front of everything, groups.Count = right at the back).</summary>
    private void MoveLayerGroup(int from, int to)
    {
        var groups = LayerGroups();
        if (from < 0 || from >= groups.Count || to < 0 || to > groups.Count) return;
        if (to == from || to == from + 1) return;   // dropped back where it came from

        var moving = _layers.GetRange(groups[from].First, groups[from].Count);
        // The row the group lands in front of, identified before the list is disturbed.
        LayerDef? anchor = to < groups.Count ? _layers[groups[to].First] : null;
        foreach (var m in moving) _layers.Remove(m);
        int at = anchor == null ? _layers.Count : _layers.IndexOf(anchor);
        _layers.InsertRange(at < 0 ? _layers.Count : at, moving);

        _gameLayerOrder = false;   // a manual order takes over until engine order is re-armed
        _layerOrderFlags = null;
        _layerLiveSeenValid = false;
        if (HudLive) SnapLayersToEngine();
        _composeDirty = true;
    }

    /// <summary>A move the row loop asked for, applied once the loop is over: the rows are
    /// drawn from a snapshot of the grouping and index straight into the stack, so reordering
    /// it half way through would repaint the rest of the list as somebody else.</summary>
    private (int From, int To) _layPending = (-1, -1);

    /// <summary>The arrows: one step towards the front (-1) or the back (+1).</summary>
    private void StepLayerGroup(int group, int dir) =>
        _layPending = (group, dir < 0 ? group - 1 : group + 2);

    // =====================================================================
    //  The panel
    // =====================================================================

    /// <summary>
    /// The whole section: the engine-order switch, a line saying what a drag will do here, the
    /// list itself and the splitter that sizes it.
    /// </summary>
    private void DrawLayersPanel()
    {
        bool live = HudLive;
        int on = _layers.Count(l => l.Visible && l.Alpha > 0);
        UiSection("Layers", AcLayer, $"{on}/{_layers.Count} shown");

        float avail = ImGui.GetContentRegionAvail().X;
        bool engine = _gameLayerOrder;
        if (UiToggle("engine order", ref engine, AcLayer, live
                ? "Follow the running level's own draw order, live: the list below re-sorts\n" +
                  "itself the moment an event moves a background band. Turn it off -- or just\n" +
                  "drag a row -- to force an order of your own onto the simulation instead."
                : "Order each continuous section the way the level draws it in-game\n" +
                  "(background2over / background3over / topEnemyOver events).\n" +
                  "Dragging a layer switches back to manual order.",
                Math.Max(96f, avail * 0.52f)))
        {
            _gameLayerOrder = engine;
            ApplyEngineOrder();
        }
        ImGui.SameLine(0, 5);
        if (UiButton("reset", AcLayer,
                "Every layer visible at full opacity, in the level's own draw order.",
                Math.Max(48f, avail * 0.48f - 5f)))
        {
            foreach (var l in _layers) { l.Visible = true; l.Alpha = 255; }
            _gameLayerOrder = true;
            ApplyEngineOrder();
        }

        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(ColorOf(UiFaint), live
            ? _gameLayerOrder
                ? "the engine's own stack, live - drag to take it over"
                : "forced on the simulation - drags snap to it"
            : "drag a row - top = drawn in front");
        ImGui.PopTextWrapPos();

        if (_layersHeight <= 30)   // first run: size the list to fit every row
            _layersHeight = _layers.Count * LayerRowH + 10f;
        DrawLayerList();
        HSplitter("##laysplit", ref _layersHeight, 60f, 700f);
    }

    /// <summary>
    /// The list itself: a well of rows and a drag that shows, as a line between two rows,
    /// exactly where the thing being dragged is going to land. The old list used ImGui's
    /// drag-and-drop payloads, where the target is whichever row you happen to be over and
    /// nothing says which side of it you are dropping on -- so a drag was a guess, and the
    /// arrows beside it were how the reordering actually got done.
    /// </summary>
    private void DrawLayerList()
    {
        var groups = LayerGroups();
        float w = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(w, Math.Max(LayerRowH + 8f, _layersHeight));

        // The well is drawn under a transparent child so the rows can scroll inside the shape
        // rather than on top of its border -- the same arrangement the reference browsers use.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Gfx.Rgba(0, 0, 0, 0));
        WellBegin("layerlist", size, AcLayer, 4f, 4f);

        var dl = ImGui.GetWindowDrawList();
        float inner = ImGui.GetContentRegionAvail().X;
        // Taken once and passed down: every row places its own hit targets with
        // SetCursorScreenPos, so the layout cursor is no use as a row origin after the first.
        var listTop = ImGui.GetCursorScreenPos();

        for (int g = 0; g < groups.Count; g++)
            DrawLayerGroup(dl, listTop, groups, g, inner);

        // --- the drag ---
        if (_layDrag.Length > 0)
        {
            int from = groups.FindIndex(x => _layers.GetRange(x.First, x.Count)
                .Any(l => l.Id == _layDrag));
            float mouseY = ImGui.GetMousePos().Y;
            int row = Math.Clamp((int)MathF.Round((mouseY - listTop.Y) / LayerRowH),
                0, _layers.Count);
            // Snap to a group boundary: a gap between two groups is the only place a drop can
            // land, since a group is indivisible.
            _layDropAt = 0;
            int bestGap = int.MaxValue;
            for (int g = 0; g <= groups.Count; g++)
            {
                int at = g < groups.Count ? groups[g].First : _layers.Count;
                int gap = Math.Abs(at - row);
                if (gap < bestGap) { bestGap = gap; _layDropAt = g; }
            }

            if (from >= 0 && _layDropAt != from && _layDropAt != from + 1)
            {
                float y = listTop.Y + (_layDropAt < groups.Count
                    ? groups[_layDropAt].First : _layers.Count) * LayerRowH - 1f;
                dl.AddRectFilled(new Vector2(listTop.X, y - 1f),
                    new Vector2(listTop.X + inner, y + 1f), Shade(AcLayer, 1.15f), 1f);
                dl.AddCircleFilled(new Vector2(listTop.X + 3f, y), 3f, Shade(AcLayer, 1.15f));
            }

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (from >= 0) _layPending = (from, _layDropAt);
                _layDrag = "";
                _layDropAt = -1;
            }
        }

        if (_layPending.From >= 0)
        {
            MoveLayerGroup(_layPending.From, _layPending.To);
            _layPending = (-1, -1);
        }

        // The scrollable extent, which the rows themselves never told the child about: they all
        // draw at absolute positions off listTop and leave the layout cursor wherever their last
        // hit target happened to end.
        ImGui.SetCursorScreenPos(listTop);
        ImGui.Dummy(new Vector2(inner, _layers.Count * LayerRowH));
        WellEnd();
        ImGui.PopStyleColor();
    }

    /// <summary>One group of rows: its bracket, if it has more than one, and the rows.</summary>
    private void DrawLayerGroup(ImDrawListPtr dl, Vector2 origin,
        List<(string Key, int First, int Count)> groups, int g, float w)
    {
        var (_, first, count) = groups[g];
        float y0 = origin.Y + first * LayerRowH;

        // A band the engine draws in one place: bracketed down its left edge so it reads as one
        // thing that moves as one thing.
        if (count > 1)
            dl.AddRectFilled(new Vector2(origin.X + 1f, y0 + 2f),
                new Vector2(origin.X + 2.5f, y0 + count * LayerRowH - 3f),
                Alpha(AcLayer, 90), 1f);

        for (int r = 0; r < count; r++)
            DrawLayerRow(dl, new Vector2(origin.X, y0 + r * LayerRowH), w,
                _layers[first + r], g, groups.Count, r == 0, count > 1);
    }

    /// <summary>
    /// One row: grip, eye, swatch, name, opacity and the two step arrows. Everything is laid out
    /// by hand against <paramref name="pos"/> -- the row has six hit targets in it and ImGui's
    /// own cursor flow cannot place them and still leave the row a single drag handle.
    /// </summary>
    private void DrawLayerRow(ImDrawListPtr dl, Vector2 pos, float w, LayerDef ly,
        int group, int groupCount, bool groupHead, bool banded)
    {
        bool live = HudLive;
        bool movable = LayerMovable(ly);
        bool dragging = _layDrag == ly.Id;
        uint tint = ly.Kind == LayerKind.Objects ? ly.Swatch
            : ly.Kind == LayerKind.Starfield ? Gfx.Rgba(210, 214, 235)
            : Shade(AcLayer, 1f);

        float x = pos.X + (banded ? 6f : 2f);
        float mid = pos.Y + LayerRowH * 0.5f;
        float arrowW = 13f, alphaW = live ? 0f : 42f;
        float rightEnd = pos.X + w - 2f;

        // --- hit targets ---
        // Order matters and is not arbitrary: the drag handle spans the whole row, the eye and
        // the arrows sit inside it, and ImGui gives an overlapped point to whichever item
        // claimed it FIRST (ItemHoverable bails once HoveredId is taken). So the small targets
        // are submitted before the big one they sit on, and the handle picks up only what is
        // left over.
        ImGui.PushID(ly.Id);

        // eye
        ImGui.SetCursorScreenPos(new Vector2(x + 10f, pos.Y + 2f));
        bool eyeHit = ImGui.InvisibleButton("##vis", new Vector2(17f, LayerRowH - 4f));
        bool eyeHot = ImGui.IsItemHovered();
        if (eyeHit) { ly.Visible = !ly.Visible; _composeDirty = true; }
        if (eyeHot) ImGui.SetTooltip(ly.Visible ? "Hide this layer" : "Show this layer");

        // arrows
        float ax = rightEnd - arrowW * 2f - 2f;
        bool upHot = false, dnHot = false;
        if (groupHead && movable)
        {
            ImGui.SetCursorScreenPos(new Vector2(ax, pos.Y + 2f));
            if (ImGui.InvisibleButton("##up", new Vector2(arrowW, LayerRowH - 4f)))
                StepLayerGroup(group, -1);
            upHot = ImGui.IsItemHovered();
            if (upHot) ImGui.SetTooltip("One step towards the front");
            ImGui.SetCursorScreenPos(new Vector2(ax + arrowW + 2f, pos.Y + 2f));
            if (ImGui.InvisibleButton("##dn", new Vector2(arrowW, LayerRowH - 4f)))
                StepLayerGroup(group, 1);
            dnHot = ImGui.IsItemHovered();
            if (dnHot) ImGui.SetTooltip("One step towards the back");
        }

        // opacity
        float alphaX = ax - alphaW - 6f;
        if (alphaW > 0f)
        {
            ImGui.SetCursorScreenPos(new Vector2(alphaX, pos.Y + 2f));
            if (AlphaBar("##op", ref ly.Alpha, tint, alphaW, LayerRowH - 4f)) _composeDirty = true;
        }

        // the rest of the row is the drag handle
        float handleW = Math.Max(24f, (alphaW > 0f ? alphaX : ax) - x - 8f);
        ImGui.SetCursorScreenPos(new Vector2(x, pos.Y));
        ImGui.InvisibleButton("##grab", new Vector2(handleW, LayerRowH));
        bool rowHot = ImGui.IsItemHovered();
        if (rowHot && movable && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (movable && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3f))
            _layDrag = ly.Id;
        if (rowHot && !dragging)
            ImGui.SetTooltip(LayerRowTip(ly, live, movable, banded));

        ImGui.PopID();

        // --- paint ---
        bool hot = rowHot || eyeHot || upHot || dnHot;
        var mn = new Vector2(pos.X + 1f, pos.Y + 1f);
        var mx = new Vector2(pos.X + w - 1f, pos.Y + LayerRowH - 2f);
        if (dragging) dl.AddRectFilled(mn, mx, Shade(AcLayer, 0.42f, 130), 4f);
        else if (hot) dl.AddRectFilled(mn, mx, Gfx.Rgba(255, 255, 255, 14), 4f);

        // grip: two columns of dots, and nothing at all on a row that cannot move -- an absent
        // handle says "not draggable" without a word of explanation.
        if (movable)
            for (int c = 0; c < 2; c++)
            for (int r = 0; r < 3; r++)
                dl.AddRectFilled(new Vector2(x + 1f + c * 3f, mid - 4f + r * 3f),
                    new Vector2(x + 2.5f + c * 3f, mid - 2.5f + r * 3f),
                    Alpha(UiFaint, hot || dragging ? (byte)225 : (byte)110));

        // The eye is the panel's own colour, not the layer's: the swatch beside it already says
        // which category this is, and two coloured marks a millimetre apart said it twice.
        var ec = new Vector2(x + 18.5f, mid);
        bool shown = ly.Visible && ly.Alpha > 0;
        if (shown)
        {
            dl.AddCircleFilled(ec, 4.6f, Shade(AcLayer, 1f, 245));
            dl.AddCircle(ec, 6.4f, Alpha(AcLayer, eyeHot ? (byte)150 : (byte)70));
        }
        else
        {
            dl.AddCircle(ec, 4.6f, Gfx.Rgba(84, 90, 106, eyeHot ? (byte)235 : (byte)175), 0, 1.4f);
            dl.AddLine(ec + new Vector2(-4f, 4f), ec + new Vector2(4f, -4f),
                Gfx.Rgba(96, 102, 120, 220), 1.4f);
        }

        // swatch: the object categories' marker colour, so the row and the dot on the map agree
        float nx = x + 28f;
        if (ly.Kind == LayerKind.Objects)
        {
            dl.AddRectFilled(new Vector2(nx, mid - 4.5f), new Vector2(nx + 8f, mid + 4.5f),
                Alpha(ly.Swatch, shown ? (byte)255 : (byte)90), 2f);
            nx += 12f;
        }

        float nameEnd = alphaW > 0f ? alphaX : ax;
        ClipText(dl, new Vector2(nx, mid - ImGui.GetTextLineHeight() * 0.5f),
            Math.Max(8f, nameEnd - nx - 6f),
            shown ? hot || dragging ? Gfx.Rgba(248, 250, 255) : UiText : Gfx.Rgba(112, 118, 136),
            ly.Name);

        if (!groupHead || !movable) return;
        Arrow(dl, new Vector2(ax + arrowW * 0.5f, mid), true, upHot, group > 0);
        Arrow(dl, new Vector2(ax + arrowW * 1.5f + 2f, mid), false, dnHot, group < groupCount - 1);
    }

    /// <summary>What a row says when you hover it -- which differs by rather more than the
    /// name, once the engine has an opinion about whether it may move.</summary>
    private string LayerRowTip(LayerDef ly, bool live, bool movable, bool banded)
    {
        string what = ly.Kind switch
        {
            LayerKind.Background => ly.Slot switch
            {
                0 => "The terrain the level scrolls over.",
                1 => "The overlay band: the layer the level moves around the stack.",
                _ => "The sky/cloud band, on its own faster parallax.",
            },
            LayerKind.Starfield => "The star field, which only ever fills black pixels.",
            _ => "One of the object categories the map places.",
        };
        if (!live) return what + "\n\nDrag to reorder, or use the arrows. Top = drawn in front.";
        if (!movable)
            return what + "\n\nFixed while the simulation runs: the engine always draws the\n" +
                   "terrain first and the stars behind it, whatever the level says.";
        return what + (banded
            ? "\n\nThe five object categories are one band to the engine, so they\n" +
              "drag together. Visibility is still per category."
            : "\n\nDrag it: the simulation is told to draw it there, as far as the\n" +
              "engine's own four order flags can express it.");
    }

    /// <summary>A small step arrow, drawn rather than ImGui's own so it fits the row.</summary>
    private static void Arrow(ImDrawListPtr dl, Vector2 c, bool up, bool hot, bool enabled)
    {
        uint col = !enabled ? Gfx.Rgba(58, 63, 78) : hot ? Gfx.Rgba(246, 249, 255) : UiDim;
        float r = 3.6f;
        float dy = up ? -r : r;
        dl.AddTriangleFilled(new Vector2(c.X - r * 1.25f, c.Y - dy * 0.6f),
            new Vector2(c.X + r * 1.25f, c.Y - dy * 0.6f),
            new Vector2(c.X, c.Y + dy * 0.9f), col);
    }

    /// <summary>
    /// The opacity control: a bar you drag rather than a 52px ImGui slider with a grab handle
    /// wider than the value it shows. Right-click puts it back to full, the way every other
    /// slider in the app now does.
    /// </summary>
    private static bool AlphaBar(string id, ref int alpha, uint tint, float w, float h)
    {
        var p = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(w, h));
        bool hot = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
        bool changed = false;

        if (active)
        {
            float f = Math.Clamp((ImGui.GetMousePos().X - p.X) / Math.Max(1f, w), 0f, 1f);
            int v = (int)MathF.Round(f * 255f);
            if (v != alpha) { alpha = v; changed = true; }
        }
        if (hot)
        {
            ImGui.SetTooltip("Opacity. Drag it; right-click for full.");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && alpha != 255)
            { alpha = 255; changed = true; }
        }

        // A thin track rather than a filled cell: at full opacity -- which is where ten of the
        // eleven rows sit -- a cell-sized block of the layer's own colour shouted louder than
        // the swatch it sits beside, and the column read as a bar chart of nothing.
        var dl = ImGui.GetWindowDrawList();
        float track = hot || active ? 8f : 5f;
        var a = new Vector2(p.X, p.Y + (h - track) * 0.5f);
        var b = new Vector2(p.X + w, a.Y + track);
        dl.AddRectFilled(a, b, Gfx.Rgba(16, 18, 24), 2.5f);
        float fill = w * (alpha / 255f);
        if (fill > 1f)
            dl.AddRectFilled(a, new Vector2(a.X + fill, b.Y),
                Shade(tint, hot || active ? 0.95f : 0.66f, 235), 2.5f);
        dl.AddRect(a, b, hot || active ? Shade(tint, 0.8f, 200) : UiLineSoft, 2.5f);

        // Only a value worth reading is printed. A column of eleven bars all saying 100% is a
        // column of noise; a full bar already says full, and the number appears the moment it
        // stops being one.
        if (alpha == 255) return changed;
        string t = $"{(int)MathF.Round(alpha / 255f * 100f)}%";
        var sz = ImGui.CalcTextSize(t);
        var at = new Vector2(p.X + (w - sz.X) * 0.5f, p.Y + (h - sz.Y) * 0.5f);
        dl.AddRectFilled(at - new Vector2(2f, 0f), at + sz + new Vector2(2f, 0f),
            Gfx.Rgba(14, 16, 21, 225), 2f);
        dl.AddText(at, alpha > 0 ? Gfx.Rgba(240, 244, 255) : Gfx.Rgba(120, 126, 144), t);
        return changed;
    }
}
