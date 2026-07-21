using T2A.Tyrian;

namespace T2A.Render;

public enum LayerKind { Background, Objects, Starfield }

/// <summary>
/// One entry in the user-orderable layer stack. The stack is kept front-to-back
/// (index 0 = drawn last = visually on top); the renderer walks it in reverse.
/// </summary>
public sealed class LayerDef
{
    public readonly LayerKind Kind;
    public readonly int Slot;      // Background: 0/1/2 ; Objects: (int)ObjCategory
    public readonly string Id;     // stable ImGui id, survives reordering
    public readonly string Name;
    public bool Visible = true;
    public int Alpha = 255;        // 0..255

    public LayerDef(LayerKind kind, int slot, string id, string name)
    { Kind = kind; Slot = slot; Id = id; Name = name; }

    public uint Swatch => Kind == LayerKind.Objects
        ? ObjectPlacer.CategoryColor((ObjCategory)Slot) : 0;
}

public static class LayerStack
{
    /// <summary>
    /// Default stack, front-to-back: the engine's draw order under its default flags
    /// (tyrian2.c gameplay loop, background2over=1 / background3over=0): terrain, then
    /// the ground bands, the bg2 overlay above them, sky enemies, bg3 clouds, and the
    /// top band in front of everything. Stars fill only black pixels in-game, so the
    /// starfield sits at the very back. The user can drag any layer to change this.
    /// </summary>
    public static List<LayerDef> CreateDefault()
        => GameOrder(CreateLayers(), Tyrian.LevelStartFlags.Defaults);

    private static List<LayerDef> CreateLayers()
    {
        LayerDef Obj(ObjCategory c) => new(LayerKind.Objects, (int)c, "obj" + (int)c, ObjectPlacer.CategoryName(c));
        return new()
        {
            Obj(ObjCategory.EnemyForeground),
            new(LayerKind.Background, 2, "bg3", "BG3 sky / clouds"),
            Obj(ObjCategory.EnemyAir),
            new(LayerKind.Background, 1, "bg2", "BG2 overlay"),
            Obj(ObjCategory.Powerup),
            Obj(ObjCategory.Money),
            Obj(ObjCategory.Datacube),
            Obj(ObjCategory.EnemyGround),
            Obj(ObjCategory.Decor),
            new(LayerKind.Background, 0, "bg1", "BG1 terrain"),
            new(LayerKind.Starfield, 0, "star", "Starfield"),
        };
    }

    /// <summary>The layer id an object category owns in the stack.</summary>
    public static string ObjId(ObjCategory c) => "obj" + (int)c;

    /// <summary>
    /// The categories the engine draws as one band, in the order it draws them. Nothing in the
    /// level data can split them, so the layer list moves them as a block during playback.
    /// </summary>
    public static readonly ObjCategory[] GroundBand =
    {
        ObjCategory.Decor, ObjCategory.EnemyGround, ObjCategory.Datacube,
        ObjCategory.Money, ObjCategory.Powerup,
    };

    /// <summary>
    /// The ids the engine's four order flags can actually move relative to one another. bg1 and
    /// the starfield are not among them: the terrain is always the first thing drawn and the
    /// stars only fill black pixels behind it, whatever the level says.
    /// </summary>
    public static readonly string[] Movable =
    {
        "bg2", "bg3", "objAir", "objTop", "objGround",
    };

    /// <summary>The engine's draw order for one set of flags, back to front. The tyrian2.c
    /// gameplay loop, in the sequence it calls things: bg1, [bg2 if over==0/3], the ground
    /// band, [bg2 if over==1], [bg3 if over==2], the sky band, [bg3 if over==0], the top band,
    /// [bg3 if over==1], then the topEnemyOver/skyEnemyOverAll bands over everything, and
    /// finally bg2 when background2over==2.</summary>
    private static List<string> BackToFront(in Tyrian.LevelStartFlags f)
    {
        var o = new List<string> { "star", "bg1" };
        if (f.Background2Over == 0 || f.Background2Over == 3) o.Add("bg2");
        o.Add("objGround");
        if (f.Background2Over == 1) o.Add("bg2");
        if (f.Background3Over == 2) o.Add("bg3");
        if (!f.SkyEnemyOverAll) o.Add("objAir");
        if (f.Background3Over == 0) o.Add("bg3");
        if (!f.TopEnemyOver) o.Add("objTop");
        if (f.Background3Over == 1) o.Add("bg3");
        if (f.TopEnemyOver) o.Add("objTop");
        if (f.SkyEnemyOverAll) o.Add("objAir");
        if (f.Background2Over == 2) o.Add("bg2");
        return o;
    }

    /// <summary>Which of <see cref="BackToFront"/>'s slots a stack layer belongs to. The five
    /// ground categories all answer "objGround" -- they are one band to the engine.</summary>
    public static string SlotOf(LayerDef l) => l.Id switch
    {
        "bg1" or "bg2" or "bg3" or "star" => l.Id,
        _ when l.Slot == (int)ObjCategory.EnemyAir => "objAir",
        _ when l.Slot == (int)ObjCategory.EnemyForeground => "objTop",
        _ => "objGround",
    };

    /// <summary>
    /// Rearrange <paramref name="layers"/> (same instances, so visibility/opacity are
    /// kept) into the engine's draw order for the given level flags.
    /// </summary>
    public static List<LayerDef> GameOrder(List<LayerDef> layers, in Tyrian.LevelStartFlags f)
    {
        var order = BackToFront(f);
        var rank = new Dictionary<string, int>();
        for (int i = 0; i < order.Count; i++)
            rank[order[i]] = i;   // higher = closer to the front

        // Within the ground band the engine's own sequence decides, so it is applied on top of
        // the band's shared rank rather than left to whatever the list happened to hold.
        var within = new Dictionary<string, int>();
        for (int i = 0; i < GroundBand.Length; i++) within[ObjId(GroundBand[i])] = i;

        // stack is front-to-back: front-most (highest rank) first; unknown ids sink to the end
        return layers
            .OrderByDescending(l => rank.TryGetValue(SlotOf(l), out var r) ? r : -1)
            .ThenByDescending(l => within.TryGetValue(l.Id, out var w) ? w : -1)
            .ToList();
    }

    /// <summary>
    /// The engine flags whose own draw order comes closest to <paramref name="stack"/>:
    /// <see cref="GameOrder"/> run backwards, as far as it can be run backwards.
    ///
    /// It is not an inverse. The stack is a free permutation of eleven layers; the engine has
    /// four flags and thirty-six combinations between them, so most orderings are simply not
    /// something the engine can be asked for. Rather than refuse those, every combination is
    /// generated and scored on how many of the pairs the user ordered it gets right, and the
    /// best one wins -- ties going to the flags already in force, so a drag that changes
    /// nothing expressible changes nothing at all. The caller then re-runs
    /// <see cref="GameOrder"/> with the answer, which is what makes the list visibly snap to
    /// the arrangement the engine is really going to draw.
    /// </summary>
    public static Tyrian.LevelStartFlags FlagsFor(IReadOnlyList<LayerDef> stack,
        in Tyrian.LevelStartFlags current)
    {
        // Where the user put each engine slot, front-to-back. A band's several rows collapse to
        // the frontmost of them, which is the same thing while the list keeps them contiguous.
        var want = new Dictionary<string, int>();
        for (int i = 0; i < stack.Count; i++)
        {
            string slot = SlotOf(stack[i]);
            if (!want.ContainsKey(slot)) want[slot] = i;
        }

        var best = current;
        int bestScore = -1;
        for (int b2 = 0; b2 <= 3; b2++)
        for (int b3 = 0; b3 <= 2; b3++)
        for (int top = 0; top <= 1; top++)
        for (int sky = 0; sky <= 1; sky++)
        {
            var f = new Tyrian.LevelStartFlags
            {
                Background2Over = b2,
                Background3Over = b3,
                TopEnemyOver = top != 0,
                SkyEnemyOverAll = sky != 0,
                Background2NotTransparent = current.Background2NotTransparent,
            };
            var order = BackToFront(f);
            int score = 0;
            for (int i = 0; i < Movable.Length; i++)
            for (int j = i + 1; j < Movable.Length; j++)
            {
                if (!want.TryGetValue(Movable[i], out int wi)) continue;
                if (!want.TryGetValue(Movable[j], out int wj)) continue;
                int oi = order.IndexOf(Movable[i]), oj = order.IndexOf(Movable[j]);
                if (oi < 0 || oj < 0 || wi == wj) continue;
                // `order` runs back to front and `want` front to back, so the two agree about a
                // pair exactly when their comparisons come out opposite.
                if (wi < wj == oi > oj) score++;
            }
            // Doubled so the stability bit can only break an exact tie, never outvote a pair.
            score = score * 2 + (b2 == current.Background2Over && b3 == current.Background3Over &&
                                 (top != 0) == current.TopEnemyOver &&
                                 (sky != 0) == current.SkyEnemyOverAll ? 1 : 0);
            if (score > bestScore) { bestScore = score; best = f; }
        }
        return best;
    }
}
