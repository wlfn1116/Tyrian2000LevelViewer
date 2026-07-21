namespace T2LV.Tyrian;

/// <summary>
/// One piece of an assembly, at the spot the level spawns it, in game pixels relative to the
/// assembly's own top-left. It carries what to draw rather than just an enemyDat id, because
/// events 49-52 do not name an entry at all -- they carry the sprite and bank themselves.
/// </summary>
public readonly record struct AssemblyPart(
    int EnemyId, float X, float Y, int Band, int LinkNum,
    int Sprite, int ShapeBank, bool Big, int Armor);

/// <summary>
/// A multi-part enemy as a level actually builds one. The engine has no boss object: a boss
/// is N ordinary enemy slots spawned by N events at one instant, tiled on the 24x28 sprite
/// grid and glued together by a shared <c>linknum</c> so that killing one kills the rest
/// (tyrian2.c's kill cascade). Reconstructing that grouping from the event list is the only
/// way to see the thing whole, because enemyDat itself holds nothing but the parts.
///
/// Grouping key is the linknum where there is one, since that is the engine's own notion of
/// "these belong together"; unlinked spawns fall back to their event time, which is what
/// holds an ordinary formation together.
/// </summary>
public sealed class EnemyAssembly
{
    /// <summary>
    /// The parts of one object are spawned together -- usually at a single instant, sometimes
    /// as two or three bursts a few ticks apart (FLEET's carrier lays its rows down over 28
    /// ticks). Anything further apart than this is a later wave that happens to reuse the same
    /// link number, not more of the same body.
    /// </summary>
    private const int SameObjectTicks = 60;

    /// <summary>
    /// Link 0 means "dies alone" -- the engine ties nothing to these, so a run of them is a
    /// row of scenery, not an object. They are still grouped when they land on the very same
    /// tick, which is what an authored formation looks like, but only up to this many; beyond
    /// it the burst is terrain being laid down and belongs in the level view, not here.
    /// </summary>
    private const int MaxUnlinkedParts = 8;

    /// <summary>Index into GameData.Episodes, so a list spanning every episode can still say
    /// where each group came from and load the right level back.</summary>
    public int EpisodeIdx;
    public int LevelFileNum;
    public string LevelName = "";
    public ushort Time;
    public int LinkNum { get; private set; }
    /// <summary>Every link number the group's parts carry. A boss can be spawned under
    /// several that the kill cascade ties together, and the health bar names only one of
    /// them, so the bar has to be matched against the whole set.</summary>
    public readonly HashSet<int> Links = new();
    /// <summary>True once <see cref="ResolveFromSim"/> has replaced the authored offsets with
    /// the positions the engine actually produces.</summary>
    public bool FromSim { get; private set; }
    private bool _simTried;
    public readonly List<AssemblyPart> Parts = new();
    /// <summary>An event 79 registered this link group as a boss health bar.</summary>
    public bool HasBossBar;
    /// <summary>Assembled from link numbers the kill cascade does not relate: one body on the
    /// screen, but each link dies on its own. See <see cref="MergeAdjacentBodies"/>.</summary>
    public bool SplitLinks { get; private set; }
    /// <summary>One place the game spawns a body: which level, and where along it.</summary>
    public readonly record struct SpawnSite(
        int EpisodeIdx, int LevelFileNum, string LevelName, ushort Time);

    /// <summary>The earlier group this one is a repeat of, when the same body is spawned again
    /// — null on the one that stands for the run. See <see cref="MarkRepeats"/>.</summary>
    public EnemyAssembly? RepeatOf;
    /// <summary>On a representative, every spawn in the run including its own, in level and
    /// then map order; empty on a repeat.</summary>
    public readonly List<SpawnSite> Sites = new();
    /// <summary>How many distinct levels <see cref="Sites"/> covers; 1 on a repeat.</summary>
    public int LevelCount { get; private set; } = 1;
    public int RepeatCount => Math.Max(1, Sites.Count);
    /// <summary>linknum 254: killing any part clears the screen and can jump the script —
    /// how the engine spells "this is the end-of-level boss".</summary>
    public bool KillsEverything => LinkNum == 254;
    public int TotalArmor;
    public float Width, Height;

    /// <summary>Distinct sprites in the body -- how much of it is drawn rather than repeated.
    /// Set from the authored parts and left alone afterwards, so opening a group and resolving
    /// it against the sim cannot reclassify it.</summary>
    public int TileCount { get; private set; }

    /// <summary>Distinct sprites above which a body is a boss on size alone. Counting sprites
    /// rather than parts is what separates the two: STARGATE's 50-part wall is two tiles laid
    /// down twenty-five times and APPROACH's 18-part rank is one three-tile segment six times
    /// over, while every hand-drawn body in the game -- DREAD-NOT 34, TIME WAR 24, STATION 27,
    /// CAMANIS 23 -- is almost all distinct.</summary>
    private const int BossTileCount = 16;

    /// <summary>
    /// A boss is a thing with a health bar -- or one drawn from enough distinct tiles that the
    /// level plainly built it as the set piece. Armour does not qualify one on its own -- an
    /// asteroid built from twenty 50-armour rocks outweighs several real bosses, and 154 of
    /// episode 4's 849 entries carry the invulnerable 255 -- and neither does the 254 cascade,
    /// which some levels use just to clear the screen at the end.
    ///
    /// The size clause exists because a bar is declared, not inherent: TIME WAR hangs a 30-part
    /// machine over the whole level and never arms one (no event 79 anywhere in ep4 #17), so a
    /// bar-only rule files the biggest object in the game under "structure".
    /// </summary>
    public bool IsBoss => HasBossBar || TileCount >= BossTileCount;

    /// <summary>Ranked so the list can group by it and put the interesting things first.</summary>
    public int Rank => IsBoss ? 0 : Parts.Count >= 8 ? 1 : Parts.Count >= 4 ? 2 : 3;

    public string Kind => Rank switch { 0 => "Boss", 1 => "Structure", 2 => "Formation", _ => "Group" };
    public string KindPlural => Rank switch { 0 => "bosses", 1 => "structures", 2 => "formations", _ => "groups" };

    public string Title => $"{Kind} · {Parts.Count} part{(Parts.Count == 1 ? "" : "s")}";

    /// <summary>Last spawn time added, so a group only accepts parts that arrive with it.</summary>
    private int _lastTime;

    /// <summary>The group was authored as a picture -- every part a different sprite -- rather
    /// than one enemy repeated. Fixed before any merging; see <see cref="MergeAdjacentBodies"/>.
    /// </summary>
    private bool _tiled;

    /// <summary>
    /// Every multi-part group a level spawns. Single-part spawns are kept only when the level
    /// hangs a health bar on them, so an ordinary lone enemy does not flood the list.
    /// </summary>
    public static List<EnemyAssembly> Find(Level lv, EnemyData ed, string levelName, int episodeIdx = 0)
    {
        var open = new Dictionary<int, EnemyAssembly>();   // by link, the group still accepting parts
        var done = new List<EnemyAssembly>();

        void Add(EventRec ev, in ObjectPlacer.SpawnInfo info, float x, float y, int band)
        {
            if (info.Sprite <= 0) return;
            int link = ev.Dat4;

            if (open.TryGetValue(link, out var asm) && ev.Time - asm._lastTime > (link == 0 ? 0 : SameObjectTicks))
            {
                done.Add(asm);
                open.Remove(link);
                asm = null;
            }
            asm ??= open[link] = new EnemyAssembly
            {
                EpisodeIdx = episodeIdx, LevelFileNum = lv.FileNum, LevelName = levelName,
                Time = ev.Time, LinkNum = link,
            };
            asm.Parts.Add(new AssemblyPart(info.EnemyId, x, y, band, link,
                info.Sprite, info.ShapeBank, info.Big, info.Armor));
            asm.TotalArmor += info.Armor;
            asm.Links.Add(link);
            asm._lastTime = ev.Time;
        }

        // Event 65 locks the top band to bg1's cursor, which moves every top-band spawn 54px
        // (see BandXOffset). TIME WAR turns it on at t=0, and its machine has rows in both
        // bands, so without tracking it the two halves assemble a couple of tiles apart.
        bool bg3x1 = false;
        foreach (var ev in lv.Events)
        {
            if (ev.Type == 65) { bg3x1 = ev.Dat == 0; continue; }
            if (ev.Type == 12)
            {
                // "Custom 4x4 ground enemy": four consecutive entries as one 2x2 block of
                // 2x2 metasprites, 48x56 in all (tyrian2.c:6396).
                float bx = SpawnX(ev, ed, ev.Dat) + BandXOffset(lv, 25, bg3x1);
                for (int k = 0; k < 4; k++)
                    Add(ev, ObjectPlacer.Describe(ev.Dat + k, ed),
                        bx + (k % 2) * 24f, (k < 2 ? 0f : -28f) + ev.Dat5, 25);
                continue;
            }
            if (!ObjectPlacer.IsSpawn(ev.Type, out int band, out int baseEy)) continue;
            var info = ObjectPlacer.ResolveSpawn(ev, ed);
            // -99 / -200 fall back to the entry's own start X, which the scratch entry the
            // 49-52 events use does not have -- those always carry a real X.
            float x = ev.Dat2 is -99 or -200 && info.EnemyId != 0 ? ed.Get(info.EnemyId).StartX : ev.Dat2;
            Add(ev, info, x + BandXOffset(lv, band, bg3x1), baseEy + ev.Dat5, band);
        }
        done.AddRange(open.Values);

        // Before any merging: whether a group is a picture is a property of how it was
        // authored, and folding two mirrored halves together would destroy the evidence.
        foreach (var asm in done)
            asm._tiled = asm.Parts.Count >= 2 &&
                         asm.Parts.Select(p => p.Sprite).Distinct().Count() == asm.Parts.Count;

        MergeCascadeLinked(done);
        MergeAdjacentBodies(done);
        AttachBossBars(lv, done);

        var kept = new List<EnemyAssembly>();
        foreach (var asm in done)
        {
            if (asm.Parts.Count < 2 && !asm.HasBossBar) continue;
            if (asm.LinkNum == 0 && asm.Parts.Count > MaxUnlinkedParts) continue;
            asm.TileCount = asm.Parts.Select(p => p.Sprite).Distinct().Count();
            asm.Normalize();
            kept.Add(asm);
        }
        kept.Sort((a, b) => a.Time.CompareTo(b.Time));
        // Self-consistent for a caller that only wants one level; a caller gathering several
        // re-runs this over the whole set, which resets and recomputes from scratch.
        MarkRepeats(kept, acrossLevels: false);
        return kept;
    }

    /// <summary>
    /// The game reuses a body: GYGES hangs the same eight-part chain thirteen times down the
    /// level, and the same rock formations turn up in every asteroid level in the episode. Those
    /// are one design, so tag every repeat against the first one and let the browser show the
    /// whole run as a single row with the list of places it is used.
    ///
    /// Sameness is the body, not the placement: the same pieces in the same arrangement. Where
    /// on the map it lands and which link numbers it carries are both ignored, because a script
    /// that re-spawns a body always moves it and always renumbers it.
    ///
    /// <paramref name="acrossLevels"/> sets the scope, which is whatever is being browsed: one
    /// episode folds within that episode, "All episodes" folds over the whole game. False keeps
    /// each level's runs to itself.
    ///
    /// Run before <see cref="ResolveFromSim"/> ever touches a group, so the comparison is over
    /// authored offsets throughout: the sim only ever resolves the group the detail pane is
    /// showing, and a signature that shifted under selection would reshuffle the list.
    /// </summary>
    public static void MarkRepeats(List<EnemyAssembly> groups, bool acrossLevels)
    {
        foreach (var a in groups) { a.RepeatOf = null; a.Sites.Clear(); a.LevelCount = 1; }

        var buckets = new Dictionary<string, List<EnemyAssembly>>();
        // Level then map order, so the run's representative is its first appearance in the game
        // and the site list below it reads in play order whatever the display sort ends up being.
        foreach (var asm in groups
                     .OrderBy(a => a.EpisodeIdx).ThenBy(a => a.LevelFileNum).ThenBy(a => a.Time))
        {
            // Cheap key first: only groups made of the very same pieces can be the same body,
            // which keeps the pairwise layout compare to a handful of candidates. The band is
            // deliberately not part of it -- IXMUCANE lays the same 7x3 machine down twice, once
            // on layer 3 and once on ground 2, and which layer carries it is invisible in a
            // preview that shows the body standing still.
            var ids = asm.Parts
                .Select(p => $"{p.EnemyId}:{p.Sprite}:{p.ShapeBank}:{(p.Big ? 1 : 0)}")
                .Order(StringComparer.Ordinal);
            string key = (acrossLevels ? "" : $"{asm.EpisodeIdx}/{asm.LevelFileNum}|") +
                $"{asm.HasBossBar}|{asm.Parts.Count}|{string.Join(',', ids)}";
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new List<EnemyAssembly>();

            var first = bucket.FirstOrDefault(b => SameLayout(b, asm));
            if (first == null) bucket.Add(first = asm);
            else asm.RepeatOf = first;
            first.Sites.Add(new SpawnSite(asm.EpisodeIdx, asm.LevelFileNum, asm.LevelName, asm.Time));
        }

        foreach (var a in groups)
            if (a.RepeatOf == null)
                a.LevelCount = a.Sites.Select(s => (s.EpisodeIdx, s.LevelFileNum)).Distinct().Count();
    }

    /// <summary>
    /// Do two groups put the same pieces in the same places? Both are already normalised to
    /// their own bounding box, so this compares them as sets -- the parts arrive in whatever
    /// order the events are written in, which differs between copies of one body -- and allows
    /// a couple of pixels, because a swaying structure is sampled at a different phase each
    /// time it is spawned.
    /// </summary>
    private static bool SameLayout(EnemyAssembly a, EnemyAssembly b)
    {
        const float Tol = 2f;
        if (a.Parts.Count != b.Parts.Count) return false;
        var used = new bool[b.Parts.Count];
        foreach (var p in a.Parts)
        {
            int at = -1;
            for (int j = 0; j < b.Parts.Count; j++)
            {
                var q = b.Parts[j];
                if (used[j] || q.EnemyId != p.EnemyId || q.Sprite != p.Sprite || q.Big != p.Big ||
                    Math.Abs(q.X - p.X) > Tol || Math.Abs(q.Y - p.Y) > Tol) continue;
                at = j;
                break;
            }
            if (at < 0) return false;
            used[at] = true;
        }
        return true;
    }

    /// <summary>
    /// A boss can be spawned under several link numbers that the kill cascade ties together --
    /// wings on 41 and 42 with the core on 50, say. Those are one body, so fold groups that
    /// spawned together and whose links kill each other into a single assembly.
    /// </summary>
    private static void MergeCascadeLinked(List<EnemyAssembly> groups)
    {
        // Repeated to a fixed point, because the relation is transitive but not something a
        // single pass can close: GYGES spawns its boss on links 10, 109 and 110, where 10 and
        // 109 are not directly related and only meet through 110. In the wrong order a single
        // pass compares 10 against 109 first, rejects it, and never looks again.
        for (bool merged = true; merged;)
        {
            merged = false;
            for (int i = 0; i < groups.Count && !merged; i++)
                for (int j = groups.Count - 1; j > i; j--)
                {
                    var (a, b) = (groups[i], groups[j]);
                    if (Math.Abs(a.Time - b.Time) > SameObjectTicks || !CascadeLinked(a, b)) continue;
                    Absorb(a, b);
                    groups.RemoveAt(j);
                    merged = true;
                    break;
                }
        }
    }

    /// <summary>Fold the second group's parts into the first, which keeps its own spawn time --
    /// the earlier of the two, since the list is in event order.</summary>
    private static void Absorb(EnemyAssembly a, EnemyAssembly b)
    {
        a.Parts.AddRange(b.Parts);
        a.TotalArmor += b.TotalArmor;
        a.LinkNum = Math.Max(a.LinkNum, b.LinkNum);   // the core, which kills the rest
        a.Links.UnionWith(b.Links);                   // ... but the bar may name any of them
        a._lastTime = Math.Max(a._lastTime, b._lastTime);
        a._tiled &= b._tiled;
    }

    /// <summary>
    /// Fuse groups that are one body the kill cascade knows nothing about. TIME WAR builds its
    /// overhead machine out of five link numbers -- 25 through 29, every one at or below 40 and
    /// none a 100-offset pair, so nothing in tyrian2.c:3030 relates them -- laid down as one
    /// contiguous 30-part mass in a twenty-tick burst. Grouped by link alone it reads as a
    /// structure plus four formations, which is not a thing the level contains.
    ///
    /// Four signals together, because each alone over-merges -- measured over all 70 levels, the
    /// first three on their own fold FLEET's 200-fighter wave and SQUADRON's forty into single
    /// "bodies". The same spawn burst; link numbers that adjoin, since a designer numbers the
    /// parts of one object in sequence and a wave reusing those numbers arrives spread over
    /// hundreds of ticks; bodies that touch on the sprite grid; and both sides authored as
    /// pictures rather than repeats (<see cref="_tiled"/>), which is what tells a machine from
    /// a rank of identical fighters. Unlinked groups are excluded outright -- those are already
    /// the "landed on one tick" fallback, and adjacency would glue terrain rows into slabs.
    /// </summary>
    private static void MergeAdjacentBodies(List<EnemyAssembly> groups)
    {
        for (bool merged = true; merged;)
        {
            merged = false;
            for (int i = 0; i < groups.Count && !merged; i++)
                for (int j = groups.Count - 1; j > i; j--)
                {
                    var (a, b) = (groups[i], groups[j]);
                    if (Math.Abs(a.Time - b.Time) > SameObjectTicks || !a._tiled || !b._tiled ||
                        !LinksAdjoin(a, b) || !Touching(a, b)) continue;
                    Absorb(a, b);
                    a.SplitLinks = true;
                    groups.RemoveAt(j);
                    merged = true;
                    break;
                }
        }
    }

    /// <summary>Does either group carry a link number one off one of the other's?</summary>
    private static bool LinksAdjoin(EnemyAssembly a, EnemyAssembly b)
    {
        if (a.Links.Contains(0) || b.Links.Contains(0)) return false;
        foreach (int x in a.Links)
            foreach (int y in b.Links)
                if (Math.Abs(x - y) <= 1) return true;
        return false;
    }

    /// <summary>
    /// Do the two bodies touch? The slack is deliberately loose -- two tiles across, one down.
    /// A body with rows in more than one band is only ever approximately placed here: the bands
    /// register horizontally against different map cursors, and the authored offsets say nothing
    /// about what the scroll does to the rows between the bursts. <see cref="ResolveFromSim"/>
    /// reads the real positions back once a group is opened; this only has to tell a body apart
    /// from the next thing along.
    /// </summary>
    private static bool Touching(EnemyAssembly a, EnemyAssembly b)
    {
        var (ax0, ay0, ax1, ay1) = a.Bounds();
        var (bx0, by0, bx1, by1) = b.Bounds();
        float gapX = Math.Max(bx0 - ax1, ax0 - bx1);
        float gapY = Math.Max(by0 - ay1, ay0 - by1);
        return gapX <= 48f && gapY <= 28f;
    }

    /// <summary>The group's bounding box in its own (still un-normalised) coordinates.
    /// esize 1 anchors at the block's centre, esize 0 at its top-left.</summary>
    private (float X0, float Y0, float X1, float Y1) Bounds()
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var p in Parts)
        {
            float px = p.X + (p.Big ? -6f : 0f), py = p.Y + (p.Big ? -7f : 0f);
            x0 = Math.Min(x0, px); y0 = Math.Min(y0, py);
            x1 = Math.Max(x1, px + (p.Big ? 24f : 12f));
            y1 = Math.Max(y1, py + (p.Big ? 28f : 14f));
        }
        return (x0, y0, x1, y1);
    }

    /// <summary>Whether any link in one group's set kills any link in the other's.</summary>
    private static bool CascadeLinked(EnemyAssembly a, EnemyAssembly b)
    {
        foreach (int x in a.Links)
            foreach (int y in b.Links)
                if (CascadeLinked(x, y)) return true;
        return false;
    }

    /// <summary>Does killing one of these links kill the other? tyrian2.c:3030 --
    /// the 100-offset pair, or the shared bucket of 20 above link 40.</summary>
    private static bool CascadeLinked(int a, int b)
    {
        if (a == 0 || b == 0 || a == b) return false;
        if (a - 100 == b || b - 100 == a) return true;
        return a > 40 && b > 40 && a / 20 == b / 20;
    }

    /// <summary>
    /// Hang each declared health bar on ONE group. Event 79 names a link number, and link
    /// numbers get reused all level long -- New Deli arms a bar on link 10 at t=4814, but
    /// ordinary enemies have been spawning on link 10 since t=610.
    ///
    /// The owner is the last group to have spawned when the bar arms, because that is the one
    /// on screen. Nothing later can own it: the engine scans the live enemies for the link on
    /// the very next frame and drops a bar that matches none of them (tyrian2.c's
    /// draw_boss_bar), so a body still to come never gets one. DREAD-NOT is the case that
    /// nearest-in-time gets wrong -- its link 14 bar arms at t=4000 for the eight-part bow laid
    /// down at t=68, and the stray fighter that reuses link 14 at t=5440 is merely closer.
    ///
    /// Event 39 renumbers everything already on the field, so the number a bar names need not
    /// be the one its body spawned under: LAVA RUN moves its machine from link 19 to 254 at
    /// t=3000 and arms the 254 bar sixty ticks later. Replaying those renames in order is what
    /// makes such a boss findable at all.
    /// </summary>
    private static void AttachBossBars(Level lv, List<EnemyAssembly> groups)
    {
        // Which groups answer to each link number as of the point the walk has reached, which
        // is not what they spawned under once event 39 has had its say.
        var byLink = new Dictionary<int, List<EnemyAssembly>>();
        List<EnemyAssembly> Under(int link) =>
            byLink.TryGetValue(link, out var list) ? list : byLink[link] = new List<EnemyAssembly>();

        foreach (var g in groups)
            foreach (int link in g.Links)
                Under(link).Add(g);

        foreach (var ev in lv.Events)
        {
            if (ev.Type == 39)
            {
                // "Enemy Global Linknum Change" (tyrian2.c:6787): everything already spawned
                // moves to the new number, and anything spawned later keeps the old one.
                if (ev.Dat <= 0 || ev.Dat == ev.Dat2 || !byLink.TryGetValue(ev.Dat, out var from))
                    continue;
                var moved = from.Where(g => g.Time <= ev.Time).ToList();
                if (moved.Count == 0) continue;
                from.RemoveAll(g => g.Time <= ev.Time);
                // A merged group can arrive from two numbers at once -- NOSE DRIP folds six
                // links into 5 on one tick -- and should still be listed under the target once.
                var to = Under(ev.Dat2);
                foreach (var g in moved)
                    if (!to.Contains(g)) to.Add(g);
                continue;
            }
            if (ev.Type != 79) continue;
            foreach (int link in new[] { (int)ev.Dat, (int)ev.Dat2 })
            {
                if (link <= 0 || !byLink.TryGetValue(link, out var candidates)) continue;
                // Every bar the game ships names something already spawned, since one that did
                // not would be dropped again immediately. A level that armed a bar a tick early
                // would lose its boss here entirely, so fall back to the first body still to come.
                var best = candidates.Where(g => g.Time <= ev.Time).MaxBy(g => g.Time)
                           ?? candidates.MinBy(g => g.Time);
                if (best != null) best.HasBossBar = true;
            }
        }
    }

    /// <summary>Where the event puts the enemy horizontally. -99 leaves the enemyDat's own
    /// start X in place and -200 is "roll one at spawn", so both fall back to the entry.</summary>
    private static float SpawnX(EventRec ev, EnemyData ed, int enemyId)
        => ev.Dat2 is -99 or -200 ? ed.Get(enemyId).StartX : ev.Dat2;

    /// <summary>
    /// The engine turns an event's map-relative X into a screen X differently per band
    /// (tyrian2.c:6174-6193): the ground bands sit 12px left of the sky band, and the top band
    /// is measured against its own map cursor entirely -- unless event 65 has locked it to
    /// bg1's, when it registers like a ground band. Only differences matter inside one group,
    /// so this gives each band's offset relative to the sky band -- without it, a boss whose
    /// hull is in one band and whose guns are in another assembles misaligned.
    /// </summary>
    private static float BandXOffset(Level lv, int band, bool bg3x1) => band switch
    {
        0 => 0f,
        50 => bg3x1 ? -12f : (lv.MapX - 1) * 24f - lv.MapX3 * 24f + 6f - 48f,
        _ => -12f,      // 25 and 75, the two ground bands
    };

    /// <summary>
    /// Replace the authored offsets with where the engine actually puts the parts.
    ///
    /// The event data is not enough on its own. A boss whose rows are staggered by the scroll
    /// rather than by explicit offsets -- CAMANIS spawns its rows 14 map-pixels apart and lets
    /// the terrain space them out -- lands entirely on one row if you only read dat5, and the
    /// parts' own velocities (dat3, dat6) move them further still. The only thing that knows
    /// where they end up is the simulation, so run the level to the moment the group is
    /// complete and read the live enemies back.
    ///
    /// Returns false if the sim could not be built or found none of the group, leaving the
    /// authored layout in place.
    /// </summary>
    public bool ResolveFromSim(GameData gd, EpisodeInfo ep, Level lv)
    {
        if (_simTried) return FromSim;
        // Once only, whether or not it works: this runs the level, and a caller that retries
        // on failure would run it again every frame the group is on screen.
        _simTried = true;
        const int MaxTicks = 200_000;
        /// How long to watch after the group first appears, in ticks.
        const int SampleTicks = 1200;

        GameSim sim;
        try
        {
            sim = new GameSim(gd, ep, lv, gd.GetShapeTable(lv.ShapeChar)) { ExtendedDraw = true };
            sim.Reset();
        }
        catch { return false; }

        for (int guard = 0; sim.CurLoc < Time && !sim.Finished && guard < MaxTicks; guard++)
            sim.Tick(draw: false);

        // Then watch, and keep the frame where the most of the group is on screen at once.
        // A boss that scrolls in row by row is only ever whole for a moment: GYGES lays down
        // six rows over 70 map-pixels and the first has descended most of the screen by the
        // time the last arrives, so sampling at any fixed instant catches a fragment.
        var live = new List<GameSim.EnemyView>();
        List<AssemblyPart>? best = null;
        bool seen = false;
        for (int t = 0; t < SampleTicks && !sim.Finished; t++)
        {
            sim.Tick(draw: false);
            sim.CollectEnemies(live);

            var mine = new List<AssemblyPart>();
            foreach (var v in live)
            {
                if (!Links.Contains(v.LinkNum)) continue;
                mine.Add(new AssemblyPart(v.EnemyId, v.ScreenX, v.ScreenY, v.Band, v.LinkNum,
                    v.SpriteIndex, v.SheetId, v.Size == 1, v.ArmorLeft));
            }
            if (mine.Count > 0) seen = true;
            else if (seen) break;          // the group has come and gone; a later reuse of the
                                           // same link number is a different object entirely
            if (best == null || mine.Count > best.Count) best = mine;
        }
        if (best == null || best.Count == 0) return false;

        Parts.Clear();
        Parts.AddRange(best);
        FromSim = true;
        Normalize();
        return true;
    }

    /// <summary>Shift the parts so the assembly's own bounding box starts at (0,0), which is
    /// what a preview can lay out without knowing anything about the map.</summary>
    private void Normalize()
    {
        var (minX, minY, maxX, maxY) = Bounds();
        if (minX > maxX) return;

        for (int i = 0; i < Parts.Count; i++)
            Parts[i] = Parts[i] with { X = Parts[i].X - minX, Y = Parts[i].Y - minY };
        Width = maxX - minX;
        Height = maxY - minY;
    }
}
