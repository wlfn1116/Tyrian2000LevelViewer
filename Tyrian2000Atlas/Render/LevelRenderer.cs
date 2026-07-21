using System.Numerics;
using T2A.Tyrian;

namespace T2A.Render;

public struct EventMarker
{
    public float X, Y;       // canvas coordinates
    public uint Color;
    public int EnemyId;
    public byte Type;
    public ushort Time;
    public string Band;
    public bool ApproxX;     // X was -99/-200 (default/random)
}

/// <summary>
/// Composites a parsed Level into a CompositeImage from a user-orderable stack of
/// layers (starfield, the three background grids, and one layer per object category),
/// each with its own visibility and opacity, and derives enemy-spawn markers from the
/// event list. The stack is front-to-back; the renderer draws it in reverse.
/// </summary>
public static class LevelRenderer
{
    public const int CanvasW = Level.Bg3Cols * ShapeTable.TileW;   // 360
    public const int CanvasH = Level.Bg2Rows * ShapeTable.TileH;   // 16800

    public static int LayerYOffset(int layer)
        => CanvasH - Level.RowsFor(layer) * ShapeTable.TileH;

    // "Fit to level": canvas height = the bg1 gameplay length (300 rows). All layers
    // bottom-aligned 1:1 (bg2/bg3 cropped), so there's no phantom empty region.
    public const int ParallaxH = Level.Bg1Rows * ShapeTable.TileH;       // 8400
    public const int ParallaxYOffset = CanvasH - ParallaxH;             // 8400

    /// <summary>Canvas height of the layer-anchored 1:1 view: the full grids, extended
    /// upward when a late-introduced layer's seen content reaches past the map top.</summary>
    public static int RawHeight(ObjectPlacer.LayerScroll? scroll)
    {
        int h = CanvasH;
        if (scroll == null) return h;
        for (int k = 0; k < 3; k++)
            if (scroll.Late[k])
                h = Math.Max(h, (int)Math.Ceiling(scroll.Anchor[k] + scroll.Seen[k]) + LevelTimeline.ViewBottom);
        return h;
    }

    public static int HeightFor(LevelTimeline? timeline, bool fitToLevel,
        bool uniformTextureScale = false, bool rawGrids = false)
    {
        int normal = fitToLevel ? ParallaxH : CanvasH;
        if (rawGrids || timeline?.IsUnrolled != true) return normal;
        // A continuous route is its own map; its height is the recorded route length.
        // The 1:1 texture view spans recorded content only — layers and anchors never
        // extend past UniformExtent, and inventing map rows to fill a taller canvas
        // would show terrain the game never scrolls in.
        int distance = uniformTextureScale ? timeline.UniformExtent : timeline.Distance;
        return Math.Max(LevelTimeline.ViewBottom, distance + LevelTimeline.ViewBottom);
    }

    /// <summary>
    /// Straight-alpha "over": composite <paramref name="color"/> at coverage
    /// <paramref name="a"/> onto the running (non-premultiplied) accumulator. The result
    /// keeps non-premultiplied RGBA so the SDL Blend texture draws it correctly over the
    /// dark canvas (no double-darkening of the base layer).
    /// </summary>
    private static void Plot(CompositeImage img, int dst, uint color, byte paletteIndex, int a)
    {
        var px = img.Pixels;
        if (a >= 255)
        {
            px[dst] = color;
            img.PaletteIndices[dst] = paletteIndex;
            return;
        }
        if (a <= 0) return;
        uint d = px[dst];
        int da = (int)((d >> 24) & 0xFF);
        if (da == 0)
        {
            px[dst] = (color & 0x00FFFFFFu) | ((uint)a << 24);
            return;
        }
        int wd = da * (255 - a) / 255;        // dst contribution
        int outA = a + wd;
        if (outA <= 0) { px[dst] = 0; return; }
        int nr = (((int)(color & 0xFF)) * a + ((int)(d & 0xFF)) * wd) / outA;
        int ng = (((int)((color >> 8) & 0xFF)) * a + ((int)((d >> 8) & 0xFF)) * wd) / outA;
        int nb = (((int)((color >> 16) & 0xFF)) * a + ((int)((d >> 16) & 0xFF)) * wd) / outA;
        px[dst] = (uint)(nr | (ng << 8) | (nb << 16) | (outA << 24));
    }

    private static void PlotBackground(CompositeImage img, int dst, byte sourceIndex,
        uint[] palette, int alpha, bool indexedBlend)
    {
        if (!indexedBlend)
        {
            Plot(img, dst, palette[sourceIndex], sourceIndex, alpha);
            return;
        }

        byte destinationIndex = img.PaletteIndices[dst];
        byte blendedIndex = (byte)((sourceIndex & 0xF0) |
            (((destinationIndex & 0x0F) + (sourceIndex & 0x0F)) / 2));
        Plot(img, dst, palette[blendedIndex], blendedIndex, alpha);
    }

    /// <summary>
    /// Draw one background layer 1:1 from its authored map, bottom-aligned in a canvas of
    /// height H. A layer introduced mid-level (BRAINIAC's escape BG2) is lifted by its
    /// anchor so its content sits where the game first scrolls it in, and only the rows
    /// the game actually shows are drawn for such layers. Tiles are never resampled.
    /// </summary>
    private static void DrawBgLayer(CompositeImage img, Level lv, ShapeTable shapes, uint[] palette,
        int layer, int H, int alpha, LevelTimeline? timeline, bool uniformTextureScale,
        int clipY0 = 0, int clipY1 = int.MaxValue, bool indexedBlend = false, bool rawGrids = false,
        ObjectPlacer.LayerScroll? layerScroll = null)
    {
        if (alpha <= 0) return;
        if (timeline?.IsUnrolled == true && !rawGrids)
        {
            DrawTimelineBgLayer(img, lv, shapes, palette, layer, H, alpha, timeline,
                uniformTextureScale, clipY0, clipY1, indexedBlend);
            return;
        }
        var px = img.Pixels;
        int cols = Level.ColsFor(layer), rows = Level.RowsFor(layer);
        byte[] cells = lv.CellsFor(layer);
        int anchorShift = (int)Math.Round(layerScroll?.Anchor[layer] ?? 0);
        bool late = layerScroll?.Late[layer] == true;
        // Rows above what the game ever scrolls in stay hidden for late layers; the
        // start row (rows-8) is the one at the screen top on the layer's first frame.
        int seenTopPx = late
            ? (rows - 8) * ShapeTable.TileH - (int)Math.Ceiling(layerScroll!.Seen[layer])
            : 0;
        int yOff = H - rows * ShapeTable.TileH - anchorShift;
        for (int cy = 0; cy < rows; cy++)
        {
            if (late && (cy + 1) * ShapeTable.TileH <= seenTopPx) continue;
            int rowBase = cy * cols;
            for (int cx = 0; cx < cols; cx++)
            {
                int sid = lv.ResolveShapeId(layer, cells[rowBase + cx]);
                if (sid == 0) continue;
                byte[]? tile = shapes.GetById(sid);
                if (tile == null) continue;
                int baseX = cx * ShapeTable.TileW, baseY = yOff + cy * ShapeTable.TileH, ti = 0;
                for (int ty = 0; ty < ShapeTable.TileH; ty++)
                {
                    int dy = baseY + ty;
                    if (dy < clipY0 || dy >= clipY1 || dy < 0 || dy >= H)
                    { ti += ShapeTable.TileW; continue; }
                    int dst = dy * CanvasW + baseX;
                    for (int tx = 0; tx < ShapeTable.TileW; tx++, ti++, dst++)
                    {
                        byte v = tile[ti];
                        if (v != 0) PlotBackground(img, dst, v, palette, alpha, indexedBlend);
                    }
                }
            }
        }
    }

    private static void DrawTimelineBgLayer(CompositeImage img, Level lv, ShapeTable shapes,
        uint[] palette, int layer, int H, int alpha, LevelTimeline timeline,
        bool uniformTextureScale, int clipY0, int clipY1, bool indexedBlend)
    {
        var px = img.Pixels;
        int cols = Level.ColsFor(layer), rows = Level.RowsFor(layer);
        byte[] cells = lv.CellsFor(layer);
        int baseY = H - LevelTimeline.ViewBottom;
        // In the 1:1 texture view a layer ends where its recorded scroll history ends;
        // the rows above it hold content the game never shows and stay empty.
        int maxDistance = uniformTextureScale ? timeline.UniformLength(layer) : timeline.Distance;
        int firstY = Math.Max(Math.Max(0, clipY0), baseY - maxDistance);
        int initialSource = timeline.SourceY(layer, 0, uniformTextureScale);

        for (int y = firstY; y < Math.Min(H, clipY1); y++)
        {
            int distance = baseY - y;
            int sourceY = distance < 0
                ? initialSource - distance
                : timeline.SourceY(layer, distance, uniformTextureScale);
            int row = Math.DivRem(sourceY, ShapeTable.TileH, out int tileY);
            if (tileY < 0) { tileY += ShapeTable.TileH; row--; }
            if (row < 0 || row >= rows) continue;

            int rowBase = row * cols;
            int dstRow = y * CanvasW;
            for (int cx = 0; cx < cols; cx++)
            {
                int sid = lv.ResolveShapeId(layer, cells[rowBase + cx]);
                if (sid == 0) continue;
                byte[]? tile = shapes.GetById(sid);
                if (tile == null) continue;
                int src = tileY * ShapeTable.TileW;
                int dst = dstRow + cx * ShapeTable.TileW;
                for (int tx = 0; tx < ShapeTable.TileW; tx++, src++, dst++)
                {
                    byte v = tile[src];
                    if (v != 0) PlotBackground(img, dst, v, palette, alpha, indexedBlend);
                }
            }
        }
    }

    /// <summary>
    /// Full composite, bottom-aligned (every grid shown once at 1:1). Layers are drawn
    /// in the user-defined stack order (back to front).
    /// </summary>
    public static void Compose(CompositeImage img, Level lv, ShapeTable shapes, uint[] palette,
        IReadOnlyList<LayerDef> layers, List<PlacedObject>? objects, bool drawObjectSprites,
        LevelTimeline? timeline = null, bool uniformTextureScale = false,
        bool gameLayerOrder = false, bool rawGrids = false,
        ObjectPlacer.LayerScroll? layerScroll = null)
    {
        int h = rawGrids && layerScroll != null
            ? RawHeight(layerScroll)
            : HeightFor(timeline, false, uniformTextureScale, rawGrids);
        img.Resize(CanvasW, h);
        img.Clear(0);
        DrawStack(img, lv, shapes, palette, layers, h, 0, objects, drawObjectSprites,
            timeline, uniformTextureScale, gameLayerOrder, rawGrids, layerScroll);
        // Screen filters (event 44 hue/brightness) are transient full-screen effects;
        // baking them would tint map regions the game only colours for moments.
        FlattenToBlack(img, palette);
        img.MarkDirty();
    }

    /// <summary>"Fit to level": canvas trimmed to the bg1 length; bg2/bg3 cropped, 1:1.</summary>
    public static void ComposeParallax(CompositeImage img, Level lv, ShapeTable shapes, uint[] palette,
        IReadOnlyList<LayerDef> layers, List<PlacedObject>? objects, bool drawObjectSprites,
        LevelTimeline? timeline = null, bool uniformTextureScale = false,
        bool gameLayerOrder = false, bool rawGrids = false,
        ObjectPlacer.LayerScroll? layerScroll = null)
    {
        int h = HeightFor(timeline, true, uniformTextureScale, rawGrids);
        img.Resize(CanvasW, h);
        img.Clear(0);
        DrawStack(img, lv, shapes, palette, layers, h, ParallaxYOffset, objects, drawObjectSprites,
            timeline, uniformTextureScale, gameLayerOrder, rawGrids, layerScroll);
        FlattenToBlack(img, palette);
        img.MarkDirty();
    }

    /// <summary>
    /// Composite the finished stack onto the game's backdrop: in-game every frame starts
    /// from SDL_FillRect(0) (backgrnd.c draw_background_1), i.e. palette colour 0.
    /// Run after the stack walk, so the starfield's only-onto-black rule can still test
    /// for untouched pixels while compositing.
    /// </summary>
    private static void FlattenToBlack(CompositeImage img, uint[] palette)
    {
        uint bg = palette[0] | 0xFF000000u;
        var px = img.Pixels;
        for (int i = 0; i < px.Length; i++)
        {
            uint p = px[i];
            uint a = p >> 24;
            if (a == 255) continue;
            if (a == 0) { px[i] = bg; continue; }
            // straight-alpha over the opaque backdrop
            uint ia = 255 - a;
            uint r = ((p & 0xFF) * a + (bg & 0xFF) * ia) / 255;
            uint g = (((p >> 8) & 0xFF) * a + ((bg >> 8) & 0xFF) * ia) / 255;
            uint b = (((p >> 16) & 0xFF) * a + ((bg >> 16) & 0xFF) * ia) / 255;
            px[i] = r | (g << 8) | (b << 16) | 0xFF000000u;
        }
    }

    private static void ApplyScreenFilters(CompositeImage img, uint[] palette,
        LevelTimeline? timeline, bool uniformTextureScale, bool rawGrids = false)
    {
        if (timeline == null) return;
        bool stateUniform = uniformTextureScale || rawGrids || !timeline.IsUnrolled;
        var px = img.Pixels;
        var indices = img.PaletteIndices;
        int H = img.Height;
        for (int y = 0; y < H; y++)
        {
            int distance = H - LevelTimeline.ViewBottom - y;
            ScreenFilterState filter = timeline.ScreenFilter(distance, stateUniform);
            if (!filter.Active || (filter.Hue == -99 && filter.Brightness == -99))
                continue;

            int row = y * CanvasW;
            for (int x = 0; x < CanvasW; x++)
            {
                int dst = row + x;
                int index = indices[dst];
                if (filter.Hue != -99)
                    index = ((filter.Hue << 4) & 0xF0) | (index & 0x0F);
                if (filter.Brightness != -99)
                {
                    int value = (index & 0x0F) + filter.Brightness;
                    int adjusted = value < 0 || value >= 0x1F ? 0
                        : value >= 0x0F ? 0x0F : value;
                    index = (index & 0xF0) | adjusted;
                }
                indices[dst] = (byte)index;
                px[dst] = palette[index];
            }
        }
    }

    /// <summary>
    /// Walk the layer stack back-to-front (so index 0 ends up on top) and draw each
    /// visible layer with its own opacity: starfield, a bg grid, or one object category.
    /// </summary>
    private static void DrawStack(CompositeImage img, Level lv, ShapeTable shapes, uint[] palette,
        IReadOnlyList<LayerDef> layers, int H, int objYOff, List<PlacedObject>? objects,
        bool drawObjectSprites, LevelTimeline? timeline, bool uniformTextureScale,
        bool gameLayerOrder, bool rawGrids = false, ObjectPlacer.LayerScroll? layerScroll = null)
    {
        if (gameLayerOrder)
        {
            if (timeline != null && !rawGrids)
                DrawDynamicGameStack(img, lv, shapes, palette, layers, H, objYOff,
                    objects, drawObjectSprites, timeline, uniformTextureScale);
            else
                DrawStaticGameStack(img, lv, shapes, palette, layers, H, objYOff,
                    objects, drawObjectSprites, timeline, rawGrids, layerScroll);
            return;
        }

        bool stars = (timeline != null && !rawGrids) || WantsStars(lv);
        bool stateUniform = timeline != null &&
            (uniformTextureScale || rawGrids || !timeline.IsUnrolled);
        DrawStackRange(0, H);

        void DrawStackRange(int clipY0, int clipY1)
        {
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            var ly = layers[i];
            if (!ly.Visible || ly.Alpha <= 0) continue;
            switch (ly.Kind)
            {
                case LayerKind.Starfield:
                    if (stars) DrawStarfield(img, palette, H, ly.Alpha, timeline,
                        stateUniform, clipY0, clipY1);
                    break;
                case LayerKind.Background:
                    DrawBgLayer(img, lv, shapes, palette, ly.Slot, H, ly.Alpha,
                        timeline, uniformTextureScale, clipY0, clipY1,
                        rawGrids: rawGrids, layerScroll: layerScroll);
                    break;
                case LayerKind.Objects:
                    if (drawObjectSprites && objects != null)
                        DrawObjectCategory(img, objects, palette, ly.Slot, objYOff, ly.Alpha,
                            timeline, uniformTextureScale, clipY0, clipY1);
                    break;
            }
        }
        }
    }

    private static void DrawStaticGameStack(CompositeImage img, Level lv, ShapeTable shapes,
        uint[] palette, IReadOnlyList<LayerDef> layers, int H, int objYOff,
        List<PlacedObject>? objects, bool drawObjectSprites, LevelTimeline? timeline,
        bool rawGrids = false, ObjectPlacer.LayerScroll? layerScroll = null)
    {
        var backgrounds = layers.Where(l => l.Kind == LayerKind.Background)
            .ToDictionary(l => l.Slot);
        var objectLayers = layers.Where(l => l.Kind == LayerKind.Objects)
            .ToDictionary(l => l.Slot);
        LayerDef? stars = layers.FirstOrDefault(l => l.Kind == LayerKind.Starfield);
        LevelStartFlags flags = lv.ComputeStartFlags();

        if (WantsStars(lv) && stars is { Visible: true, Alpha: > 0 })
            DrawStarfield(img, palette, H, stars.Alpha, timeline, true, 0, H);

        DrawBackground(0);
        if (flags.Background2Over is 0 or 3) DrawBackground(1);
        DrawBand(25);
        DrawBand(75);
        if (flags.Background2Over == 1) DrawBackground(1);
        if (flags.Background3Over == 2) DrawBackground(2);
        if (!flags.SkyEnemyOverAll) DrawBand(0);
        if (flags.Background3Over == 0) DrawBackground(2);
        if (!flags.TopEnemyOver) DrawBand(50);
        if (flags.Background3Over == 1) DrawBackground(2);
        if (flags.TopEnemyOver) DrawBand(50);
        if (flags.SkyEnemyOverAll) DrawBand(0);
        if (flags.Background2Over == 2) DrawBackground(1);

        void DrawBackground(int slot)
        {
            if (!backgrounds.TryGetValue(slot, out var layer) ||
                !layer.Visible || layer.Alpha <= 0) return;
            DrawBgLayer(img, lv, shapes, palette, slot, H, layer.Alpha,
                timeline, false, 0, H,
                slot == 1 && flags.Background2Over != 3 &&
                !flags.Background2NotTransparent, rawGrids, layerScroll);
        }

        void DrawBand(int band)
        {
            if (!drawObjectSprites || objects == null) return;
            DrawObjectBand(img, objects, palette, band, objYOff,
                objectLayers, timeline, false, 0, H);
        }
    }

    private static void DrawDynamicGameStack(CompositeImage img, Level lv, ShapeTable shapes,
        uint[] palette, IReadOnlyList<LayerDef> layers, int H, int objYOff,
        List<PlacedObject>? objects, bool drawObjectSprites, LevelTimeline timeline,
        bool uniformTextureScale)
    {
        var backgrounds = layers.Where(l => l.Kind == LayerKind.Background)
            .ToDictionary(l => l.Slot);
        var objectLayers = layers.Where(l => l.Kind == LayerKind.Objects)
            .ToDictionary(l => l.Slot);
        LayerDef? stars = layers.FirstOrDefault(l => l.Kind == LayerKind.Starfield);
        bool stateUniform = uniformTextureScale || !timeline.IsUnrolled;

        static bool Same(in LevelStartFlags a, in LevelStartFlags b)
            => a.Background2Over == b.Background2Over &&
               a.Background3Over == b.Background3Over &&
               a.TopEnemyOver == b.TopEnemyOver &&
               a.SkyEnemyOverAll == b.SkyEnemyOverAll &&
               a.Background2NotTransparent == b.Background2NotTransparent;

        int y0 = 0;
        while (y0 < H)
        {
            int distance = H - LevelTimeline.ViewBottom - y0;
            LevelStartFlags flags = timeline.RenderFlags(distance, stateUniform);
            int y1 = y0 + 1;
            while (y1 < H && Same(flags,
                timeline.RenderFlags(H - LevelTimeline.ViewBottom - y1, stateUniform)))
                y1++;

            if (stars is { Visible: true, Alpha: > 0 })
                DrawStarfield(img, palette, H, stars.Alpha, timeline,
                    stateUniform, y0, y1);

            DrawBackground(0);
            if (flags.Background2Over is 0 or 3) DrawBackground(1);
            DrawBand(25);
            DrawBand(75);
            if (flags.Background2Over == 1) DrawBackground(1);
            if (flags.Background3Over == 2) DrawBackground(2);
            if (!flags.SkyEnemyOverAll) DrawBand(0);
            if (flags.Background3Over == 0) DrawBackground(2);
            if (!flags.TopEnemyOver) DrawBand(50);
            if (flags.Background3Over == 1) DrawBackground(2);
            if (flags.TopEnemyOver) DrawBand(50);
            if (flags.SkyEnemyOverAll) DrawBand(0);
            if (flags.Background2Over == 2) DrawBackground(1);

            y0 = y1;

            void DrawBackground(int slot)
            {
                if (!backgrounds.TryGetValue(slot, out var layer) ||
                    !layer.Visible || layer.Alpha <= 0) return;
                DrawBgLayer(img, lv, shapes, palette, slot, H, layer.Alpha,
                    timeline, uniformTextureScale, y0, y1,
                    slot == 1 && flags.Background2Over != 3 &&
                    !flags.Background2NotTransparent);
            }

            void DrawBand(int band)
            {
                if (!drawObjectSprites || objects == null) return;
                DrawObjectBand(img, objects, palette, band, objYOff,
                    objectLayers, timeline, uniformTextureScale, y0, y1);
            }
        }
    }

    /// <summary>Initial star state after the engine drains the time-zero event batch.</summary>
    public static bool WantsStars(Level lv)
    {
        bool active = true;
        foreach (var e in lv.Events)
        {
            if (e.Time != 0) break;
            if (e.Type == 8) active = false;
            else if (e.Type == 9) active = true;
        }
        return active;
    }

    // In-game starfield (backgrnd.c): 330 stars on the 356x184 viewport, colour
    // 0x90 + rand%16, and a star pixel is written only where the screen is still
    // black. Bright stars (colour-4 still in the hue bank) get a 4-neighbour halo.
    private const int StarHue = 0x90;
    private const int StarCountRef = 330, StarRefW = 356, StarRefH = 184;

    private static void DrawStarfield(CompositeImage img, uint[] palette, int H, int alpha,
        LevelTimeline? timeline, bool uniformTextureScale, int clipY0, int clipY1)
    {
        var px = img.Pixels;
        var rng = new Random(1234);
        int count = (int)((long)StarCountRef * CanvasW * H / (StarRefW * StarRefH));

        // engine rule: stars never overdraw anything already on screen
        void Star(int x, int y, byte paletteIndex)
        {
            if (x < 0 || x >= CanvasW || y < clipY0 || y >= clipY1 || y < 0 || y >= H) return;
            int dst = y * CanvasW + x;
            if ((px[dst] >> 24) == 0)
                Plot(img, dst, palette[paletteIndex], paletteIndex, alpha);
        }

        for (int i = 0; i < count; i++)
        {
            int x = rng.Next(CanvasW);
            int y = rng.Next(H);
            if (y < clipY0 || y >= clipY1) continue;
            if (timeline != null)
            {
                int distance = H - LevelTimeline.ViewBottom - y;
                if (!timeline.StarActive(distance, uniformTextureScale)) continue;
            }
            int shade = rng.Next(16);
            Star(x, y, (byte)(StarHue + shade));
            if (shade >= 4)
            {
                byte halo = (byte)(StarHue + shade - 4);
                Star(x + 1, y, halo);
                Star(x - 1, y, halo);
                Star(x, y + 1, halo);
                Star(x, y - 1, halo);
            }
        }
    }

    /// <summary>
    /// Draw one object category, sorted by band (engine order ground 25, ground2 75,
    /// sky 0, top 50) so objects within the category occlude one another as in-game.
    /// </summary>
    private static void DrawObjectCategory(CompositeImage img, List<PlacedObject> objs, uint[] palette,
        int category, int yOffset, int alpha, LevelTimeline? timeline, bool uniformTextureScale,
        int clipY0, int clipY1)
    {
        if (alpha <= 0) return;
        foreach (var o in objs.Where(o => (int)o.Cat == category).OrderBy(o => BandDrawOrder(o.Band)))
            DrawObject(img, o, palette, yOffset, alpha, timeline, uniformTextureScale,
                clipY0, clipY1);
    }

    private static void DrawObjectBand(CompositeImage img, List<PlacedObject> objs, uint[] palette,
        int band, int yOffset, IReadOnlyDictionary<int, LayerDef> objectLayers,
        LevelTimeline? timeline, bool uniformTextureScale, int clipY0, int clipY1)
    {
        foreach (var o in objs.Where(o => o.Band == band))
        {
            if (!objectLayers.TryGetValue((int)o.Cat, out var layer) ||
                !layer.Visible || layer.Alpha <= 0) continue;
            DrawObject(img, o, palette, yOffset, layer.Alpha, timeline,
                uniformTextureScale, clipY0, clipY1);
        }
    }

    private static void DrawObject(CompositeImage img, in PlacedObject o, uint[] palette,
        int yOffset, int alpha, LevelTimeline? timeline, bool uniformTextureScale,
        int clipY0, int clipY1)
    {
        if (o.Sheet == null || o.SpriteIndex <= 0 || o.SpriteIndex == 999) return;
        int H = img.Height;
        int x = (int)o.X;
        int y = (int)ObjectCanvasY(o, timeline, H, yOffset, uniformTextureScale);
        if (y < clipY0 - 32 || y >= clipY1 + 32) return;
        if (o.Esize == 1)
        {
            Blit(img, H, o.Sheet, o.SpriteIndex,      x - 6, y - 7,
                palette, alpha, clipY0, clipY1);
            Blit(img, H, o.Sheet, o.SpriteIndex + 1,  x + 6, y - 7,
                palette, alpha, clipY0, clipY1);
            Blit(img, H, o.Sheet, o.SpriteIndex + 19, x - 6, y + 7,
                palette, alpha, clipY0, clipY1);
            Blit(img, H, o.Sheet, o.SpriteIndex + 20, x + 6, y + 7,
                palette, alpha, clipY0, clipY1);
        }
        else
        {
            Blit(img, H, o.Sheet, o.SpriteIndex, x, y,
                palette, alpha, clipY0, clipY1);
        }
    }

    public static float ObjectCanvasY(PlacedObject o, LevelTimeline? timeline, int height,
        int normalYOffset, bool uniformTextureScale = false)
    {
        if (timeline?.IsUnrolled != true || o.PathDistance < 0) return o.Y - normalYOffset;
        int distance = uniformTextureScale && o.UniformPathDistance >= 0
            ? o.UniformPathDistance
            : o.PathDistance;
        return height - LevelTimeline.ViewBottom - distance + o.ScreenY;
    }

    // Engine within-layer draw order (tyrian2.c): ground(25), ground2(75), sky(0), top(50).
    private static int BandDrawOrder(int band) => band switch { 25 => 0, 75 => 1, 0 => 2, 50 => 3, _ => 4 };

    private static void Blit(CompositeImage img, int H, CompShapes sheet, int index, int x, int y,
        uint[] palette, int alpha, int clipY0, int clipY1)
    {
        Sprite? spr = sheet.Decode(index);
        if (spr == null) return;
        for (int sy = 0; sy < spr.H; sy++)
        {
            int dy = y + sy;
            if (dy < clipY0 || dy >= clipY1 || dy < 0 || dy >= H) continue;
            int rowBase = dy * CanvasW;
            int sRow = sy * spr.W;
            for (int sx = 0; sx < spr.W; sx++)
            {
                byte v = spr.Pixels[sRow + sx];
                if (v == 0) continue;
                int dx = x + sx;
                if (dx < 0 || dx >= CanvasW) continue;
                Plot(img, rowBase + dx, palette[v], v, alpha);
            }
        }
    }

    // --- Enemy spawn markers ---------------------------------------------

    public static readonly uint ColSky    = Gfx.Rgba(80, 200, 255);
    public static readonly uint ColGround  = Gfx.Rgba(80, 255, 110);
    public static readonly uint ColTop     = Gfx.Rgba(255, 230, 70);
    public static readonly uint ColGround2 = Gfx.Rgba(255, 160, 60);
    public static readonly uint ColInline  = Gfx.Rgba(255, 110, 230);

    private static bool ClassifySpawn(byte type, out string band, out uint color)
    {
        switch (type)
        {
            case 15: case 18: band = "Sky";     color = ColSky;     return true;
            case 6:  case 17: band = "Ground";  color = ColGround;  return true;
            case 7:  case 23: case 32: band = "Top"; color = ColTop; return true;
            case 10: case 56: band = "Ground2"; color = ColGround2; return true;
            case 12: band = "Cluster4x4"; color = ColInline; return true;
            case 49: case 50: case 51: case 52: band = "Inline"; color = ColInline; return true;
            default: band = ""; color = 0; return false;
        }
    }

    public static List<EventMarker> ComputeEnemyMarkers(Level lv)
    {
        var list = new List<EventMarker>();
        foreach (var e in lv.Events)
        {
            if (!ClassifySpawn(e.Type, out string band, out uint color)) continue;

            bool approxX = e.Dat2 == -99 || e.Dat2 == -200;
            float x = approxX ? CanvasW * 0.5f : Math.Clamp((int)e.Dat2, 0, CanvasW);
            float y = Math.Clamp(CanvasH - e.Time, 0, CanvasH);

            list.Add(new EventMarker
            {
                X = x, Y = y, Color = color,
                EnemyId = e.Dat, Type = e.Type, Time = e.Time,
                Band = band, ApproxX = approxX,
            });
        }
        return list;
    }
}
