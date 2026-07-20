using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;
using T2LV.Tyrian.Audio;

namespace T2LV;

/// <summary>
/// The level tree: how an episode's levels lead into each other, including the branches
/// the level list can't show — the outpost's route choices, the secret warps hidden in the
/// level data, the boss-timer and difficulty forks. Built by <see cref="EpisodeGraph"/>;
/// this half only draws it.
///
/// Two things make a seventy-node graph usable rather than merely correct: hovering anything
/// dims everything it does not touch, and the search box dims everything it does not name. The
/// canvas is otherwise a wall of boxes in which the level you actually came to find is
/// indistinguishable from its sixty-nine neighbours.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showTree;
    private float _treeZoom = 1f;
    private Vector2 _treeScroll;
    private int _treeFitEpisode = -1;         // episode the view was last framed for
    private bool _treeFitAll;                 // next fit frames the whole tree, not just its width
    private int _treeEdgeMask = DefaultEdgeMask;
    private bool _treeLabels = true;
    private readonly byte[] _treeSearch = new byte[64];

    private const float TreeMinZoom = 0.25f, TreeMaxZoom = 2.5f;
    // Timed Battle is its own game mode picked off the title screen, not campaign
    // progression, so it starts hidden — the legend chip turns it back on.
    private const int DefaultEdgeMask = ~(1 << (int)EdgeKind.TimedBattle);

    private static uint EdgeColor(EdgeKind k) => k switch
    {
        EdgeKind.MapChoice   => Gfx.Rgba(105, 190, 235),   // the outpost's galaxy map
        EdgeKind.Secret      => Gfx.Rgba(255, 205,  90),
        EdgeKind.TimerFail   => Gfx.Rgba(255, 140,  70),
        EdgeKind.PlayerDied  => Gfx.Rgba(240,  95,  95),
        EdgeKind.Difficulty  => Gfx.Rgba(185, 150, 255),
        EdgeKind.TwoPlayer   => Gfx.Rgba(120, 220, 140),
        EdgeKind.SpecialShip => Gfx.Rgba(235, 125, 215),
        EdgeKind.TimedBattle => Gfx.Rgba(130, 140, 168),
        EdgeKind.Start       => Gfx.Rgba(120, 128, 150),
        _                    => Gfx.Rgba(160, 172, 196),   // plain progression
    };

    private static string EdgeName(EdgeKind k) => k switch
    {
        EdgeKind.Continue    => "finish the level",
        EdgeKind.MapChoice   => "outpost route choice",
        EdgeKind.Secret      => "secret warp",
        EdgeKind.TimerFail   => "boss timer ran out",
        EdgeKind.PlayerDied  => "a player died",
        EdgeKind.Difficulty  => "difficulty",
        EdgeKind.TwoPlayer   => "two players",
        EdgeKind.SpecialShip => "Stalker 21.126",
        EdgeKind.TimedBattle => "Timed Battle",
        _                    => "episode start",
    };

    // The kinds offered as filters, in legend order.
    private static readonly EdgeKind[] LegendKinds =
    {
        EdgeKind.Continue, EdgeKind.MapChoice, EdgeKind.Secret, EdgeKind.TimerFail,
        EdgeKind.Difficulty, EdgeKind.TwoPlayer, EdgeKind.SpecialShip, EdgeKind.PlayerDied,
        EdgeKind.TimedBattle,
    };

    private bool KindShown(EdgeKind k) => (_treeEdgeMask & (1 << (int)k)) != 0;

    // =====================================================================

    /// <summary>One episode's graph and where it sits on the shared canvas. With "All
    /// episodes" picked the trees stand side by side, each under its own caption.</summary>
    private sealed class TreePane
    {
        public EpisodeInfo Ep = null!;
        public int EpisodeIdx;
        public EpisodeGraph Graph = null!;
        public float X;                 // canvas offset of this pane's origin
    }

    private const float PaneGap = 70f, PaneCaptionH = 30f;

    private List<TreePane> TreePanes(out float width, out float height)
    {
        var panes = new List<TreePane>();
        width = 0; height = 0;
        if (_gd == null) return panes;

        for (int i = 0; i < _gd.Episodes.Count; i++)
        {
            if (!_allEpisodes && i != _episodeIdx) continue;
            EpisodeGraph? g;
            try { g = _gd.GetGraph(_gd.Episodes[i]); }
            catch (Exception ex) { _status = "Level tree failed: " + ex.Message; continue; }
            if (g == null || g.Nodes.Count == 0) continue;

            panes.Add(new TreePane { Ep = _gd.Episodes[i], EpisodeIdx = i, Graph = g, X = width });
            width += g.Width + PaneGap;
            height = Math.Max(height, g.Height);
        }
        width = Math.Max(0, width - PaneGap);
        return panes;
    }

    private void DrawTreeWindow()
    {
        if (!_showTree || _gd == null || CurEpisode == null) return;

        if (!RefBegin("Level tree", "leveltree", ref _showTree, AcRoutes,
                new Vector2(1100, 780), new Vector2(520, 360))) return;

        var panes = TreePanes(out float width, out float height);
        DrawTreeToolbar(panes);
        if (panes.Count == 0)
            UiEmpty("No tree to draw", "No episode here has a script to build one from.", AcRoutes);
        else DrawTreeCanvas(panes, width, height);
        RefEnd(AcRoutes);
    }

    /// <summary>The legend's short form. The full name is in the tooltip: nine chips each
    /// captioned "outpost route choice" will not fit any window anyone actually uses.</summary>
    private static string EdgeShort(EdgeKind k) => k switch
    {
        EdgeKind.Continue    => "finish",
        EdgeKind.MapChoice   => "route",
        EdgeKind.Secret      => "secret",
        EdgeKind.TimerFail   => "timer",
        EdgeKind.PlayerDied  => "died",
        EdgeKind.Difficulty  => "difficulty",
        EdgeKind.TwoPlayer   => "2 players",
        EdgeKind.SpecialShip => "Stalker",
        EdgeKind.TimedBattle => "Timed Battle",
        _                    => "start",
    };

    private void DrawTreeToolbar(List<TreePane> panes)
    {
        // The legend is packed before the band opens, because the band's height has to cover
        // however many rows it turns out to need.
        var kinds = new List<EdgeKind>();
        var labels = new List<string>();
        foreach (var kind in LegendKinds)
        {
            int count = panes.Sum(p => p.Graph.Edges.Count(e => e.Kind == kind));
            if (count == 0) continue;
            kinds.Add(kind);
            labels.Add($"{EdgeShort(kind)}  {count}");
        }
        // Whether the reset chip is part of this frame's layout has to be decided ONCE, here:
        // a legend toggle below flips the mask mid-frame, and re-reading it afterwards asked
        // PackChips's array for a row it was never sized for. Turning Timed Battle on is the
        // shortest route to that -- it is the one kind the default mask has off.
        bool resetChip = _treeEdgeMask != DefaultEdgeMask;
        if (resetChip) labels.Add("reset");
        var newRow = PackChips(labels, BandInnerWidth(), out int legendRows);

        BandBegin("treeband", AcRoutes, 1 + Math.Max(1, legendRows));

        BandLabel("episode");
        ImGui.SetNextItemWidth(126);
        EpisodeCombo("##treeepisode");

        BandDivider();
        if (UiButton("fit width", AcRoutes,
                "Frame the tree across the window and start at the top.\nDrag to pan, wheel to zoom."))
            { _treeFitAll = false; _treeFitEpisode = -1; }
        ImGui.SameLine(0, 4);
        if (UiButton("fit all", AcRoutes, "Squeeze everything shown into the window."))
            { _treeFitAll = true; _treeFitEpisode = -1; }
        ImGui.SameLine(0, 4);
        if (UiButton("1:1", AcRoutes, "Back to actual size."))
            { _treeZoom = 1f; _treeFitEpisode = TreeFitKey; }
        ImGui.SameLine(0, 8);
        ImGui.SetNextItemWidth(108);
        float z = _treeZoom;
        if (ImGui.SliderFloat("##treezoom", &z, TreeMinZoom, TreeMaxZoom, "%.2fx", ImGuiSliderFlags.Logarithmic))
            _treeZoom = Math.Clamp(z, TreeMinZoom, TreeMaxZoom);

        BandDivider();
        UiToggle("labels", ref _treeLabels, AcRoutes,
            "Caption each branch with what takes you down it.\n(Hidden automatically when zoomed far out.)");

        BandDivider();
        UiFilter("##treesearch", "find a level", _treeSearch, 190f, AcRoutes);

        BandDivider();
        ImGui.AlignTextToFramePadding();
        int levels = panes.Sum(p => p.Graph.Nodes.Count(n => n.Kind == GraphNodeKind.Level));
        int routes = panes.Sum(p => p.Graph.Edges.Count(e => KindShown(e.Kind)));
        ImGui.TextColored(ColorOf(UiFaint), $"{levels} levels  ·  {routes} routes shown");

        // --- Below: the legend, which doubles as the filter. ---
        for (int i = 0; i < kinds.Count; i++)
        {
            if (i > 0 && !newRow[i]) ImGui.SameLine(0, 4);
            bool on = KindShown(kinds[i]);
            if (UiToggle(labels[i], ref on, EdgeColor(kinds[i]),
                    $"{EdgeName(kinds[i])}\n" +
                    (on ? "Shown - click to hide these routes" : "Hidden - click to show")))
                _treeEdgeMask ^= 1 << (int)kinds[i];
        }
        if (resetChip)
        {
            if (kinds.Count > 0 && !newRow[kinds.Count]) ImGui.SameLine(0, 4);
            if (UiButton("reset", AcRoutes, "Back to the default set of branches",
                    ImGui.CalcTextSize("reset").X + 30f))
                _treeEdgeMask = DefaultEdgeMask;
        }
        BandEnd();
    }

    /// <summary>Which episodes are framed, so switching the picker re-frames the view.</summary>
    private int TreeFitKey => _allEpisodes ? 1000 : _episodeIdx;

    /// <summary>A node/edge geometry pass, then draw: edges under boxes, labels over both.</summary>
    private void DrawTreeCanvas(List<TreePane> panes, float width, float height)
    {
        var canvasPos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 32 || avail.Y < 32) return;
        height += PaneCaptionH;

        if (_treeFitEpisode != TreeFitKey)
        {
            // An episode is a long chain: fitting its height too would shrink the captions
            // away, so the default frames the width and leaves the depth to scroll.
            float byWidth = avail.X / Math.Max(1, width);
            // The 0.5 floor keeps the captions legible: with every episode side by side the
            // whole game will not fit at a readable size, so it starts at the left and pans.
            _treeZoom = _treeFitAll
                ? Math.Clamp(Math.Min(byWidth, avail.Y / Math.Max(1, height)), TreeMinZoom, TreeMaxZoom)
                : Math.Clamp(byWidth, 0.5f, 1f);
            float over = avail.X - width * _treeZoom;
            _treeScroll = new Vector2(over > 0 ? over * 0.5f : 8f,
                _treeFitAll ? Math.Max(8f, (avail.Y - height * _treeZoom) * 0.5f) : 8f);
            _treeFitEpisode = TreeFitKey;
        }

        ImGui.InvisibleButton("treecanvas", avail,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle |
            ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();

        if (hovered && io.MouseWheel != 0)
        {
            float newZoom = Math.Clamp(_treeZoom * MathF.Pow(1.15f, io.MouseWheel), TreeMinZoom, TreeMaxZoom);
            Vector2 rel = mouse - canvasPos - _treeScroll;
            _treeScroll = mouse - canvasPos - rel * (newZoom / _treeZoom);
            _treeZoom = newZoom;
        }
        if (ImGui.IsItemActive() &&
            (ImGui.IsMouseDragging(ImGuiMouseButton.Left) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle)))
            _treeScroll += io.MouseDelta;

        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(canvasPos, canvasPos + avail, true);
        DrawTreeBackdrop(dl, canvasPos, avail);

        Vector2 origin = canvasPos + _treeScroll + new Vector2(0, PaneCaptionH * _treeZoom);
        float zoom = _treeZoom;
        float nodeH = EpisodeGraph.NodeH * zoom;

        string query = BufText(_treeSearch).Trim();
        bool searching = query.Length > 0;

        TreePane? hitPane = null;
        int hitNode = -1, hitEdge = -1;
        float bestDist = 9f * 9f;
        var geometry = new List<(TreePane Pane, bool[] Shown, List<Vector2>[] Paths)>();

        // --- Geometry and hit test, pane by pane. Boxes win over lines. ---
        foreach (var pane in panes)
        {
            var graph = pane.Graph;
            Vector2 At(float x, float y) => origin + new Vector2(pane.X + x, y) * zoom;

            // Hiding a branch kind should take with it the levels only that branch reached —
            // and whatever hung off them. Re-walk from the entry points over the visible
            // routes only; the Timed Battle arenas and their ending vanish together.
            var shown = new bool[graph.Nodes.Count];
            var reach = new Queue<int>();
            foreach (var node in graph.Nodes)
                if (node.In.Count == 0) { shown[node.Id] = true; reach.Enqueue(node.Id); }
            while (reach.Count > 0)
                foreach (int ei in graph.Nodes[reach.Dequeue()].Out)
                {
                    var edge = graph.Edges[ei];
                    if (!KindShown(edge.Kind) || shown[edge.To]) continue;
                    shown[edge.To] = true;
                    reach.Enqueue(edge.To);
                }

            if (hovered && hitNode < 0)
                for (int i = 0; i < graph.Nodes.Count; i++)
                {
                    if (!shown[i]) continue;
                    var p = At(graph.Nodes[i].X, graph.Nodes[i].Y);
                    if (mouse.X >= p.X && mouse.X <= p.X + graph.Nodes[i].W * zoom &&
                        mouse.Y >= p.Y && mouse.Y <= p.Y + nodeH) { hitNode = i; hitPane = pane; break; }
                }

            // Fan the endpoints across each box so parallel routes leave and arrive at
            // distinct points instead of stacking on the centre.
            var paths = new List<Vector2>[graph.Edges.Count];
            var order = BuildEndpointOrder(graph);
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                var edge = graph.Edges[i];
                if (!KindShown(edge.Kind) || !shown[edge.From] || !shown[edge.To]) continue;
                var from = graph.Nodes[edge.From];
                var to = graph.Nodes[edge.To];

                var pts = new List<Vector2> { At(from.X + from.W * order.OutFrac[i], from.Y + EpisodeGraph.NodeH) };
                foreach (var b in edge.Bends) pts.Add(At(b.X, b.Y));
                pts.Add(At(to.X + to.W * order.InFrac[i], edge.Back ? to.Y + EpisodeGraph.NodeH : to.Y));
                paths[i] = Smooth(pts, edge.Back);

                if (!hovered || hitNode >= 0) continue;
                for (int k = 1; k < paths[i].Count; k++)
                {
                    float d = SegDistSq(mouse, paths[i][k - 1], paths[i][k]);
                    if (d < bestDist) { bestDist = d; hitEdge = i; hitPane = pane; }
                }
            }
            geometry.Add((pane, shown, paths));
        }

        // Boxes win over lines -- but the two hit tests run pane by pane, so an earlier pane
        // can have claimed an edge before a later pane claims a node, and hitPane then names
        // the later pane while hitEdge still indexes the earlier one's edge list. Side by side
        // that really happens: a back edge bows ~60px sideways into its neighbour's column and
        // the edge hit radius is 9px, so the cursor can be inside one pane's box and next to
        // another's curve. Left alone it lights the wrong route, and indexes past the end of
        // the shorter episode's edge list -- inside the pushed clip rect and before RefEnd, so
        // the throw would take the whole frame's ImGui state with it.
        if (hitNode >= 0) hitEdge = -1;

        // Read the clicks here, while the invisible button is still the last item: the
        // tooltips below open their own windows. A press that moved was a pan, not a click.
        bool moved = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).LengthSquared() >= 25f;
        bool clicked = hitNode >= 0 && ImGui.IsItemDeactivated() && !moved;
        bool rightClicked = hitNode >= 0 && hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Right);
        bool focus = hitNode >= 0 || hitEdge >= 0;
        int found = 0;

        foreach (var (pane, shown, paths) in geometry)
        {
            var graph = pane.Graph;
            bool inFocusPane = pane == hitPane;
            int localNode = inFocusPane ? hitNode : -1;
            int localEdge = inFocusPane ? hitEdge : -1;

            bool NodeFound(GraphNode n) =>
                searching && n.Kind == GraphNodeKind.Level &&
                Matches(query, n.Title, n.Subtitle, n.LvlFileNum.ToString());

            // --- Edges. When something is hovered, everything unrelated fades back; when the
            //     search box has something in it, so does everything it does not reach. ---
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                if (paths[i] == null) continue;
                var edge = graph.Edges[i];
                bool lit = !focus || i == localEdge || edge.From == localNode || edge.To == localNode;
                if (searching && !NodeFound(graph.Nodes[edge.From]) && !NodeFound(graph.Nodes[edge.To]))
                    lit = false;
                uint col = Shade(EdgeColor(edge.Kind), lit ? 1f : 0.55f, lit ? (byte)235 : (byte)45);

                for (int k = 1; k < paths[i].Count; k++) dl.PathLineTo(paths[i][k - 1]);
                dl.PathLineTo(paths[i][^1]);
                dl.PathStroke(col, ImDrawFlags.None, Math.Max(1f, (lit && (focus || searching) ? 2.6f : 1.7f) * zoom));
                Arrow(dl, paths[i][^2], paths[i][^1], col, Math.Max(4f, 7f * zoom));
            }

            // --- Nodes. ---
            foreach (var node in graph.Nodes)
            {
                if (!shown[node.Id]) continue;
                var p = origin + new Vector2(pane.X + node.X, node.Y) * zoom;
                var q = p + new Vector2(node.W * zoom, nodeH);
                bool lit = !focus || (inFocusPane && (node.Id == localNode ||
                    (localNode >= 0 && Touches(graph, localNode, node.Id)) ||
                    (localEdge >= 0 && (graph.Edges[localEdge].From == node.Id || graph.Edges[localEdge].To == node.Id))));
                bool hitSearch = NodeFound(node);
                if (searching && !hitSearch) lit = false;
                if (hitSearch) { lit = true; found++; }
                bool current = node.Kind == GraphNodeKind.Level &&
                    node.LvlFileNum == _levelFileNum && pane.EpisodeIdx == _episodeIdx;

                uint accent = NodeAccent(graph, node);
                byte alpha = lit ? (byte)255 : (byte)80;
                float round = node.Kind == GraphNodeKind.Level ? 5f * zoom : nodeH * 0.5f;

                FlatRect(dl, p, q, Shade(accent, current ? 0.40f : 0.20f, alpha),
                    Shade(accent, current ? 0.70f : 0.42f, alpha), round);
                dl.AddRect(p, q, Shade(accent, current ? 1.15f : 0.80f, alpha),
                    round, ImDrawFlags.None, (current ? 2.4f : 1.2f) * Math.Max(0.8f, zoom));

                // The level in the viewer gets a slow breathing ring, so it is findable in a
                // wall of boxes without hunting for a slightly brighter outline.
                if (current)
                {
                    float pulse = 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * 2.2f);
                    dl.AddRect(p - new Vector2(2.5f, 2.5f), q + new Vector2(2.5f, 2.5f),
                        Shade(accent, 1.1f, (byte)(70 + 120 * pulse)), round + 2f, ImDrawFlags.None,
                        1.6f * Math.Max(0.8f, zoom));
                }
                if (hitSearch)
                    dl.AddRect(p - new Vector2(3.5f, 3.5f), q + new Vector2(3.5f, 3.5f),
                        Gfx.Rgba(255, 240, 170, 230), round + 3f, ImDrawFlags.None,
                        2f * Math.Max(0.8f, zoom));

                if (node.Bonus)
                    dl.AddRectFilled(p, new Vector2(p.X + 3f * zoom, q.Y), Shade(Gfx.Rgba(255, 205, 90), 1f, alpha), round);
                if (zoom >= 0.55f)
                {
                    // The badge carries a caption at the base size whatever the zoom, so below
                    // this it would stand taller than the box it hangs off.
                    if (node.CubeStops.Count > 0) CubeBadge(dl, q, node, zoom, alpha);
                    NodeMarks(dl, p, q, node, zoom, alpha);
                }

                // Captions hold the base size however far out the tree is zoomed. They used to
                // shrink with it, which meant they were baked at a fractional size at every
                // step but 1.00x and read soft the whole way down -- see UiFontSize. Constant
                // size the way a map labels its towns: clipped to the box when the box gets
                // narrow, and dropped once the box is shorter than the line itself.
                float lh = ImGui.GetTextLineHeight();
                if (nodeH < lh + 4f) continue;
                float boxW = q.X - p.X - 6f;
                string sub = TreeSubtitle(node);
                bool twoLine = sub.Length > 0 && nodeH >= lh * 2f + 5f;
                float ty = p.Y + (nodeH - (twoLine ? lh * 2f : lh)) * 0.5f;

                var size = ImGui.CalcTextSize(node.Title);
                ClipText(dl, new Vector2(p.X + 3f + Math.Max(0f, (boxW - size.X) * 0.5f), ty), boxW,
                    Shade(Gfx.Rgba(240, 244, 252), 1f, alpha), node.Title);

                if (!twoLine) continue;
                var ssize = ImGui.CalcTextSize(sub);
                ClipText(dl, new Vector2(p.X + 3f + Math.Max(0f, (boxW - ssize.X) * 0.5f), ty + lh), boxW,
                    Shade(accent, 1.05f, (byte)(alpha * 0.85f)), sub);
            }

            // --- Branch captions, on top of everything so they stay readable. Rarer
            // branches are captioned first and a caption that would land on one already
            // placed is dropped, so a fan of outpost picks can't bury a secret warp. ---
            if (_treeLabels && zoom >= 0.55f && !searching)
            {
                var placed = new List<(Vector2 Min, Vector2 Max)>();
                foreach (int i in Enumerable.Range(0, graph.Edges.Count)
                             .Where(i => paths[i] != null && graph.Edges[i].Label.Length > 0 &&
                                         graph.Edges[i].Kind != EdgeKind.Continue)
                             .OrderByDescending(i => LabelRank(graph.Edges[i].Kind)))
                {
                    var edge = graph.Edges[i];
                    bool lit = !focus || (inFocusPane &&
                        (i == localEdge || edge.From == localNode || edge.To == localNode));
                    if (focus && !lit) continue;
                    // Stagger along the path by the edge's place in its source's fan-out so
                    // sibling branches don't caption at the same height, then slide along
                    // the route until a free spot turns up.
                    int slot = graph.Nodes[edge.From].Out.IndexOf(i);
                    float t0 = 0.22f + 0.14f * (slot < 0 ? 0 : slot % 4);
                    foreach (float t in new[] { t0, t0 + 0.16f, t0 - 0.12f, t0 + 0.34f, 0.5f })
                    {
                        var at = paths[i][Math.Clamp((int)(paths[i].Count * t), 0, paths[i].Count - 1)];
                        if (LabelChip(dl, at, edge.Label, EdgeColor(edge.Kind), placed)) break;
                    }
                }
            }

            // Episode caption over each tree, so a side-by-side view stays readable.
            if (panes.Count > 1 || _allEpisodes)
            {
                string cap = $"EPISODE {pane.Ep.Number}";
                // 2x once the reserved band is tall enough to hold it, 1x under that: the two
                // crisp sizes, in place of the 0.9x-1.8x ramp this used to ride.
                int cm = zoom >= 1f ? 2 : 1;
                var csz = Measure(cap, cm);
                ScaledText(dl, new Vector2(origin.X + (pane.X + pane.Graph.Width * 0.5f) * zoom - csz.X * 0.5f,
                        origin.Y - csz.Y - 4f * zoom),
                    Gfx.Rgba(150, 190, 245, 235), cm, cap);
            }
        }

        // Inside the clip rect: on a narrow window the hint is wider than the canvas, and
        // outside it the text would run over the window's own edge.
        var hintAt = new Vector2(canvasPos.X + 9f, canvasPos.Y + avail.Y - ImGui.GetTextLineHeight() - 13f);
        if (searching)
            UiHint(dl, hintAt, found > 0
                ? $"{found} level{(found == 1 ? "" : "s")} match \"{query}\""
                : $"nothing matches \"{query}\"", AcRoutes);
        else if (hovered && hitNode < 0 && hitEdge < 0)
            UiHint(dl, hintAt,
                "drag to pan  ·  wheel to zoom  ·  click a level to open it  ·  right-click for its datacubes",
                AcRoutes);

        dl.PopClipRect();

        // --- Tooltips and click-through to the level. ---
        if (hitPane != null && hitNode >= 0) TreeNodeTooltip(hitPane, hitPane.Graph.Nodes[hitNode]);
        else if (hitPane != null && hitEdge >= 0) TreeEdgeTooltip(hitPane.Graph, hitPane.Graph.Edges[hitEdge]);

        if (hitPane != null && hitNode >= 0 && hitPane.Graph.Nodes[hitNode].Kind == GraphNodeKind.Level)
        {
            var node = hitPane.Graph.Nodes[hitNode];
            if (clicked) SelectLevelFile(hitPane.EpisodeIdx, node.LvlFileNum);
            if (rightClicked && node.CubeStops.Count > 0)
                OpenCubes(hitPane.EpisodeIdx, node.CubeStops[0].Cubes[0]);
        }
    }

    /// <summary>
    /// The canvas floor: a faint grid that makes panning and zooming legible as motion, and a
    /// darkening at the edges so the graph reads as sitting in a space rather than being
    /// clipped by one.
    /// </summary>
    private void DrawTreeBackdrop(ImDrawListPtr dl, Vector2 at, Vector2 size)
    {
        var max = at + size;
        dl.AddRectFilled(at, max, Gfx.Rgba(15, 16, 21));

        float step = 48f * _treeZoom;
        if (step >= 14f)
        {
            uint grid = Gfx.Rgba(255, 255, 255, 8);
            for (float x = at.X + ((_treeScroll.X % step) + step) % step; x < max.X; x += step)
                dl.AddLine(new Vector2(x, at.Y), new Vector2(x, max.Y), grid);
            for (float y = at.Y + ((_treeScroll.Y % step) + step) % step; y < max.Y; y += step)
                dl.AddLine(new Vector2(at.X, y), new Vector2(max.X, y), grid);
        }

        // Four edge fades. Square-cornered, but they sit inside a clipped rectangle so the
        // corners never show.
        const float v = 46f;
        uint dark = Gfx.Rgba(8, 9, 12, 190), clear = Gfx.Rgba(8, 9, 12, 0);
        dl.AddRectFilledMultiColor(at, new Vector2(max.X, at.Y + v), dark, dark, clear, clear);
        dl.AddRectFilledMultiColor(new Vector2(at.X, max.Y - v), max, clear, clear, dark, dark);
        dl.AddRectFilledMultiColor(at, new Vector2(at.X + v, max.Y), dark, clear, clear, dark);
        dl.AddRectFilledMultiColor(new Vector2(max.X - v, at.Y), max, clear, dark, dark, clear);
    }

    /// <summary>
    /// The small facts a node carries that its caption has no room for: an outpost before it,
    /// a savepoint on the way in. Two dots at the top-left, which is the one corner nothing
    /// else uses (the cube tally sits bottom-right, the bonus stripe on the left edge).
    /// </summary>
    private static void NodeMarks(ImDrawListPtr dl, Vector2 p, Vector2 q, GraphNode node, float zoom, byte alpha)
    {
        if (node.Kind != GraphNodeKind.Level) return;
        float r = Math.Clamp(2.4f * zoom, 1.6f, 3.2f);
        float x = q.X - r - 3f * zoom;
        float y = p.Y + r + 3f * zoom;
        if (node.Shop)
        {
            dl.AddCircleFilled(new Vector2(x, y), r, Shade(Gfx.Rgba(150, 190, 245), 1f, alpha));
            x -= r * 2.6f;
        }
        if (node.SavePoint)
            dl.AddCircleFilled(new Vector2(x, y), r, Shade(Gfx.Rgba(120, 220, 140), 1f, alpha));
    }

    /// <summary>A small tally on the box: how many readings the outpost before this level
    /// carries. Colour says whether they are free; the "free/total" form only appears when
    /// the outpost is actually split, since most guarantee none and a bare "4" in the gated
    /// colour says that already.</summary>
    private static void CubeBadge(ImDrawListPtr dl, Vector2 boxMax, GraphNode node, float zoom, byte alpha)
    {
        int total = node.AllCubes.Count(), locked = node.CubesLocked;
        string text = locked > 0 && locked < total ? $"{total - locked}/{total}" : total.ToString();
        var size = ImGui.CalcTextSize(text);
        var pad = new Vector2(3f, 0f);
        var q = boxMax + new Vector2(2f * zoom, 1f * zoom);
        var p = q - size - pad * 2f;
        uint accent = locked == 0 ? Gfx.Rgba(150, 190, 245)      // all free
            : locked == total ? Gfx.Rgba(210, 170, 255)          // all need a datacube
            : Gfx.Rgba(190, 180, 230);                           // some of each
        dl.AddRectFilled(p, q, Shade(accent, 0.30f, (byte)(alpha * 0.92f)), 2f);
        dl.AddRect(p, q, Shade(accent, 0.75f, (byte)(alpha * 0.85f)), 2f);
        dl.AddText(p + pad, Shade(accent, 1.15f, alpha), text);
    }

    // =====================================================================
    // Pieces
    // =====================================================================

    /// <summary>Where each edge meets its two boxes, spread left-to-right by the other end's
    /// position so the lines around a box keep their order and don't cross needlessly.</summary>
    private readonly record struct EndpointOrder(float[] OutFrac, float[] InFrac);

    private static EndpointOrder BuildEndpointOrder(EpisodeGraph g)
    {
        var outFrac = new float[g.Edges.Count];
        var inFrac = new float[g.Edges.Count];
        foreach (var node in g.Nodes)
        {
            void Spread(List<int> list, float[] into, bool outgoing)
            {
                var sorted = list.OrderBy(ei =>
                {
                    var e = g.Edges[ei];
                    float x = e.Bends.Count > 0
                        ? (outgoing ? e.Bends[0].X : e.Bends[^1].X)
                        : g.Nodes[outgoing ? e.To : e.From].CX;
                    return x;
                }).ToList();
                for (int k = 0; k < sorted.Count; k++)
                    into[sorted[k]] = sorted.Count == 1 ? 0.5f : 0.22f + 0.56f * k / (sorted.Count - 1);
            }
            Spread(node.Out, outFrac, true);
            Spread(node.In, inFrac, false);
        }
        return new EndpointOrder(outFrac, inFrac);
    }

    private static bool Touches(EpisodeGraph g, int a, int b)
    {
        foreach (int ei in g.Nodes[a].Out) if (g.Edges[ei].To == b) return true;
        foreach (int ei in g.Nodes[a].In) if (g.Edges[ei].From == b) return true;
        return false;
    }

    private static uint NodeAccent(EpisodeGraph g, GraphNode node) => node.Kind switch
    {
        GraphNodeKind.Start => Gfx.Rgba(130, 200, 255),
        GraphNodeKind.NextEpisode => Gfx.Rgba(120, 220, 140),
        GraphNodeKind.TimedBattleOver => Gfx.Rgba(130, 140, 168),
        GraphNodeKind.DeadEnd => Gfx.Rgba(140, 140, 150),
        _ when node.Galaga || node.Engage => Gfx.Rgba(200, 150, 255),
        _ when node.Bonus => Gfx.Rgba(255, 205, 90),
        // A level you can only get to through a warp ball is worth spotting at a glance.
        _ when node.In.Count > 0 && node.In.All(ei => g.Edges[ei].Kind == EdgeKind.Secret)
            => Gfx.Rgba(245, 190, 110),
        _ => Gfx.Rgba(120, 165, 215),
    };

    private static string TreeSubtitle(GraphNode node)
    {
        if (node.Kind != GraphNodeKind.Level) return "";
        string s = node.Subtitle;
        if (node.Galaga) s += "  galaga";
        else if (node.Engage) s += "  engage";
        else if (node.Bonus) s += "  bonus";
        return s;
    }

    /// <summary>Round the polyline into a flowing curve. Vertical tangents on forward edges;
    /// a back edge loops out sideways so it reads as a return rather than a crossing.</summary>
    private static List<Vector2> Smooth(List<Vector2> pts, bool back)
    {
        var outPts = new List<Vector2> { pts[0] };
        if (back)
        {
            // One wide arc out to the side and up.
            var a = pts[0];
            var b = pts[^1];
            float bow = Math.Max(60f, Math.Abs(b.Y - a.Y) * 0.35f);
            var c1 = new Vector2(a.X + bow, a.Y + bow * 0.4f);
            var c2 = new Vector2(b.X + bow, b.Y + bow * 0.4f);
            for (int i = 1; i <= 24; i++) outPts.Add(Bezier(a, c1, c2, b, i / 24f));
            return outPts;
        }
        for (int i = 1; i < pts.Count; i++)
        {
            Vector2 a = pts[i - 1], b = pts[i];
            float t = (b.Y - a.Y) * 0.45f;
            var c1 = new Vector2(a.X, a.Y + t);
            var c2 = new Vector2(b.X, b.Y - t);
            int steps = Math.Clamp((int)(Vector2.Distance(a, b) / 9f), 6, 26);
            for (int k = 1; k <= steps; k++) outPts.Add(Bezier(a, c1, c2, b, k / (float)steps));
        }
        return outPts;
    }

    private static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
    {
        float u = 1 - t;
        return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
    }

    private static void Arrow(ImDrawListPtr dl, Vector2 from, Vector2 to, uint col, float size)
    {
        var dir = to - from;
        float len = dir.Length();
        if (len < 0.001f) return;
        dir /= len;
        var side = new Vector2(-dir.Y, dir.X) * size * 0.5f;
        dl.AddTriangleFilled(to, to - dir * size + side, to - dir * size - side, col);
    }

    /// <summary>How much a caption is worth keeping when captions collide.</summary>
    private static int LabelRank(EdgeKind k) => k switch
    {
        EdgeKind.TimerFail => 6, EdgeKind.PlayerDied => 5, EdgeKind.SpecialShip => 4,
        EdgeKind.Secret => 3, EdgeKind.Difficulty => 2, EdgeKind.TwoPlayer => 1, _ => 0,
    };

    /// <summary>Draw a caption unless it would land on one already placed. False if skipped.</summary>
    private static bool LabelChip(ImDrawListPtr dl, Vector2 at, string text, uint accent,
        List<(Vector2 Min, Vector2 Max)> placed)
    {
        var size = ImGui.CalcTextSize(text);
        var pad = new Vector2(4f, 1f);
        var p = at - new Vector2(size.X * 0.5f, size.Y * 0.5f) - pad;
        var q = p + size + pad * 2f;

        foreach (var (mn, mx) in placed)
            if (p.X < mx.X && q.X > mn.X && p.Y < mx.Y && q.Y > mn.Y) return false;
        placed.Add((p, q));

        dl.AddRectFilled(p, q, Gfx.Rgba(16, 17, 22, 232), 3f);
        dl.AddRect(p, q, Shade(accent, 0.75f, 190), 3f);
        dl.AddText(p + pad, Shade(accent, 1.1f), text);
        return true;
    }

    private static float SegDistSq(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 0.0001f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return (p - (a + ab * t)).LengthSquared();
    }

    private void TreeNodeTooltip(TreePane pane, GraphNode node)
    {
        var g = pane.Graph;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(430f);
        uint accent = NodeAccent(g, node);
        UiTitle(node.Title, accent, "", 1, 430f);
        if (node.Kind == GraphNodeKind.Level)
        {
            Badge($"file #{node.LvlFileNum:00}", accent);
            ImGui.SameLine(0, 4f);
            Badge($"section {node.Section}", Gfx.Rgba(150, 162, 185));
            ImGui.SameLine(0, 4f);
            // The number is what the script says; the title is what anyone would recognise.
            // (This is inside a tooltip, so it names the song rather than linking to it.)
            Badge(node.Song > 0
                ? $"song {node.Song}  {MusicBank.Titles[Math.Min(node.Song - 1, MusicBank.Titles.Length - 1)]}"
                : "no song", node.Song > 0 ? AcMusic : Gfx.Rgba(150, 162, 185));

            var tags = new List<string>();
            if (node.Bonus) tags.Add("bonus level (a death here doesn't cost the run)");
            if (node.Galaga) tags.Add("Galaga mode");
            if (node.Engage) tags.Add("ENGAGE mini-game loadout");
            if (node.Extra) tags.Add("extra game");
            if (node.SavePoint) tags.Add("savepoint on the way in");
            if (node.Shop) tags.Add("outpost before it");
            if (tags.Count > 0)
            {
                ImGui.Dummy(new Vector2(0, 2f));
                foreach (var t in tags) ImGui.BulletText(t);
            }

            if (node.CubeStops.Count > 0)
            {
                var cubes = _gd!.GetCubes(pane.Ep);
                for (int s = 0; s < node.CubeStops.Count; s++)
                {
                    UiSection(node.CubeStops.Count > 1
                        ? $"datacubes at that outpost (route {s + 1} of {node.CubeStops.Count})"
                        : "datacubes at that outpost", Gfx.Rgba(185, 150, 255));
                    var stop = node.CubeStops[s];
                    foreach (int idx in stop.Cubes)
                    {
                        bool free = stop.IsFree(idx);
                        ImGui.Bullet();
                        ImGui.TextColored(ColorOf(free ? Gfx.Rgba(150, 190, 245) : Gfx.Rgba(210, 170, 255)),
                            idx >= 1 && idx <= cubes.Count ? StripMarks(cubes[idx - 1].Title) : $"cube {idx}");
                        if (free) continue;
                        ImGui.SameLine();
                        ImGui.TextDisabled("- needs a datacube found in the level before");
                    }
                }
            }

            UiSection("gets here by", AcRoutes);
            if (node.In.Count == 0) ImGui.BulletText("nothing - unreachable in the campaign");
            foreach (int ei in node.In)
                RouteLine(g.Edges[ei], g.Nodes[g.Edges[ei].From].Title);
            UiSection("leads to", AcRoutes);
            if (node.Out.Count == 0) ImGui.BulletText("nothing - the route ends here");
            foreach (int ei in node.Out)
                RouteLine(g.Edges[ei], g.Nodes[g.Edges[ei].To].Title);

            ImGui.Dummy(new Vector2(0, 3f));
            ImGui.TextColored(ColorOf(UiFaint), node.CubeStops.Count > 0
                ? "click to open this level  ·  right-click to read its datacubes"
                : "click to open this level in the viewer");
        }
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>Cube titles carry the fonts' '~' highlight toggles; drop them for plain UI.</summary>
    private static string StripMarks(string s) => s.Replace("~", "");

    private static void RouteLine(GraphEdge edge, string other)
    {
        ImGui.Bullet();
        ImGui.TextColored(ColorOf(EdgeColor(edge.Kind)), other);
        string via = edge.Detail.Length > 0 ? edge.Detail : EdgeName(edge.Kind);
        ImGui.SameLine();
        ImGui.TextDisabled($"- {via}");
    }

    private void TreeEdgeTooltip(EpisodeGraph g, GraphEdge edge)
    {
        ImGui.BeginTooltip();
        UiTitle(EdgeName(edge.Kind), EdgeColor(edge.Kind), "", 1, 430f);
        ImGui.Text($"{g.Nodes[edge.From].Title}   ->   {g.Nodes[edge.To].Title}");
        if (edge.Detail.Length > 0) ImGui.TextColored(ColorOf(UiDim), edge.Detail);
        ImGui.EndTooltip();
    }

    private static Vector4 ColorOf(uint c) => new(
        (c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, ((c >> 24) & 0xFF) / 255f);
}
