using T2LV.Tyrian;

namespace T2LV.Render;

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

    /// <summary>
    /// Rearrange <paramref name="layers"/> (same instances, so visibility/opacity are
    /// kept) into the engine's draw order for the given level flags. Order per the
    /// tyrian2.c gameplay loop: bg1, [bg2 if over==0/3], ground bands, [bg2 if over==1],
    /// [bg3 if over==2], sky band, [bg3 if over==0], top band, [bg3 if over==1], then
    /// the topEnemyOver/skyEnemyOverAll bands over everything, and finally bg2 when
    /// background2over==2.
    /// </summary>
    public static List<LayerDef> GameOrder(List<LayerDef> layers, in Tyrian.LevelStartFlags f)
    {
        var backToFront = new List<string> { "star" };
        backToFront.Add("bg1");
        if (f.Background2Over == 0 || f.Background2Over == 3) backToFront.Add("bg2");
        backToFront.Add("obj" + (int)ObjCategory.Decor);
        backToFront.Add("obj" + (int)ObjCategory.EnemyGround);
        backToFront.Add("obj" + (int)ObjCategory.Datacube);
        backToFront.Add("obj" + (int)ObjCategory.Money);
        backToFront.Add("obj" + (int)ObjCategory.Powerup);
        if (f.Background2Over == 1) backToFront.Add("bg2");
        if (f.Background3Over == 2) backToFront.Add("bg3");
        if (!f.SkyEnemyOverAll) backToFront.Add("obj" + (int)ObjCategory.EnemyAir);
        if (f.Background3Over == 0) backToFront.Add("bg3");
        if (!f.TopEnemyOver) backToFront.Add("obj" + (int)ObjCategory.EnemyForeground);
        if (f.Background3Over == 1) backToFront.Add("bg3");
        if (f.TopEnemyOver) backToFront.Add("obj" + (int)ObjCategory.EnemyForeground);
        if (f.SkyEnemyOverAll) backToFront.Add("obj" + (int)ObjCategory.EnemyAir);
        if (f.Background2Over == 2) backToFront.Add("bg2");

        var rank = new Dictionary<string, int>();
        for (int i = 0; i < backToFront.Count; i++)
            rank[backToFront[i]] = i;   // higher = closer to the front
        // stack is front-to-back: front-most (highest rank) first; unknown ids sink to the end
        return layers.OrderByDescending(l => rank.TryGetValue(l.Id, out var r) ? r : -1).ToList();
    }
}
