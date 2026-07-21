namespace T2LV.Tyrian;

/// <summary>Why one node leads to another. Drives the tree view's colours and labels.</summary>
public enum EdgeKind
{
    Continue,      // the ]L's own nextLevel: what happens when you just finish the level
    MapChoice,     // a destination the player picks on the outpost's galaxy map (]G + ]I)
    Secret,        // a secret-orb pickup inside the level overrode nextLevel
    TimerFail,     // ]t  - the level timer ran out (the boss survived its countdown)
    PlayerDied,    // ]l  - not every player came out alive
    Difficulty,    // ]H / ]h - the route depends on the difficulty the game was started on
    TwoPlayer,     // ]2  - two-player (or one-player-action) route
    SpecialShip,   // ]w  - only with the Stalker 21.126 equipped
    TimedBattle,   // ]T  - Timed Battle mode picks its arena straight off the title screen
    Start,         // the episode's entry point
}

public enum GraphNodeKind { Level, Start, NextEpisode, TimedBattleOver, DeadEnd }

/// <summary>One outpost's datacube shelf, as its ']?' command set it up.</summary>
public sealed class CubeStop
{
    /// <summary>The 1-based cubetxt indices the engine can put in its four reader slots.</summary>
    public List<int> Cubes = new();
    /// <summary>Indices the ']?' line names but the engine never reads — past the line's own
    /// count field, or past cubeList's four slots. Both happen in the shipped data.</summary>
    public List<int> Dropped = new();
    /// <summary>How many of Cubes the script alone unlocks (cubeMax with no pickups
    /// assumed); the rest need datacubes collected in earlier levels.</summary>
    public int Free = int.MaxValue;

    public bool IsFree(int cube)
    {
        int slot = Cubes.IndexOf(cube);
        return slot >= 0 && slot < Math.Min(Cubes.Count, Free);
    }
}

/// <summary>
/// One outpost's shop stock, exactly as its ']I' item-availability lines set it up
/// (tyrian2.c:4340). Nine rows are read — the engine reads nine — of which seven are ever
/// sold from; which row is which upgrade slot is fixed (game_menu.c:182 itemAvailMap).
///
/// The rows are stored verbatim, WITHOUT the widescreen fork's re-added Charge-Laser Cannon:
/// that injection depends on which build you play, so the item browser applies it itself and
/// one fork-agnostic graph serves both. <see cref="Section"/> is the ']I''s own section
/// (the engine's mainLevel), which the injection keys on (tyrian2.c:4360).
/// </summary>
public sealed class ShopStop
{
    /// <summary>How many availability rows a ']I' carries.</summary>
    public const int RowCount = 9;

    public int Section;
    public readonly List<int>[] Rows;

    public ShopStop()
    {
        Rows = new List<int>[RowCount];
        for (int i = 0; i < RowCount; i++) Rows[i] = new List<int>();
    }

    /// <summary>Same section and the same ids in every row: two routes that stock an outpost
    /// identically are one shelf, not two.</summary>
    public bool SameAs(ShopStop o)
    {
        if (Section != o.Section) return false;
        for (int i = 0; i < RowCount; i++)
            if (!Rows[i].SequenceEqual(o.Rows[i])) return false;
        return true;
    }
}

public sealed class GraphNode
{
    public int Id;
    public GraphNodeKind Kind;
    public int Line = -1;          // script line of the ]L (identity: a section can hold two)
    public int Section;            // section the ]L was found in
    public string Name = "";       // levelName from the ]L
    public int LvlFileNum;         // 1-based section in tyrian%d.lvl
    public int Song;
    public bool Bonus;             // ']L ...$' — deaths don't kick you back to the last save
    public bool Galaga, Engage, Extra;
    public bool SavePoint;         // a ]s / ]b ran on the way in
    public bool Shop;              // an outpost (]I) sits between the previous level and this one
    /// <summary>What the outposts on the way in stock. One entry per distinct ']?' list:
    /// a level reachable by two routes sits behind two different outposts, and in Episode 1
    /// the asteroid levels really are stocked differently coming from Tyrian than from
    /// Bubbles — keeping only the first list would lose half the readings.</summary>
    public readonly List<CubeStop> CubeStops = new();
    /// <summary>What the outposts on the way in sell. One entry per distinct ']I' shelf, same
    /// reasoning as <see cref="CubeStops"/>: a level reached two ways sits behind two outposts,
    /// and in Episode 1 the asteroid levels are stocked differently from Tyrian than from
    /// Bubbles. Raw rows; the fork's Charge-Laser re-add is applied by the reader.</summary>
    public readonly List<ShopStop> ShopStops = new();
    public readonly List<int> Out = new();   // edge indices
    public readonly List<int> In = new();

    // Layered layout, filled in by EpisodeGraph.Layout().
    public int Depth;
    public float X, Y, W;

    public string Title => Kind switch
    {
        GraphNodeKind.Start => "EPISODE START",
        GraphNodeKind.NextEpisode => "EPISODE COMPLETE",
        GraphNodeKind.TimedBattleOver => "BATTLE OVER",
        GraphNodeKind.DeadEnd => "(script ends)",
        _ => Name.Trim().Length > 0 ? Name.Trim() : "(unnamed)",
    };

    /// <summary>The level file, which is what tells two same-named cuts apart.</summary>
    public string Subtitle => Kind == GraphNodeKind.Level ? $"#{LvlFileNum}" : "";

    /// <summary>Every reading obtainable at any outpost on the way in.</summary>
    public IEnumerable<int> AllCubes => CubeStops.SelectMany(s => s.Cubes).Distinct();

    /// <summary>Of those, the ones no route puts on the shelf for free: the engine reveals
    /// one more of an outpost's list per datacube you are carrying.</summary>
    public int CubesLocked => AllCubes.Count(c => !CubeStops.Any(s => s.IsFree(c)));

    public float CX => X + W * 0.5f;
}

public sealed class GraphEdge
{
    public int From, To;
    public EdgeKind Kind;
    public string Label = "";      // short text drawn along the edge
    public string Detail = "";     // the full condition chain, for the tooltip
    public bool Back;              // closes a cycle (an ending that restarts the episode)
    /// <summary>Waypoints for an edge that spans more than one row, so it routes
    /// between the boxes in between instead of straight over them.</summary>
    public readonly List<System.Numerics.Vector2> Bends = new();
}

/// <summary>
/// The level-flow graph of one episode, resolved the way JE_loadMap resolves it
/// (tyrian2.c:4093-4690): from a section, run the ']' commands until a ]L loads a level
/// or a jump moves to another section. Conditional jumps fork the walk, so every branch
/// the script can take becomes its own edge; ]G + ]I becomes one edge per destination the
/// outpost's galaxy map offers. Secret levels are not in the script at all — they come
/// from a pickup inside the level whose enemyDat value is nextLevel + 10000
/// (mainint.c:7999-8018), so the level data is scanned for those too.
/// </summary>
public sealed class EpisodeGraph
{
    public readonly List<GraphNode> Nodes = new();
    public readonly List<GraphEdge> Edges = new();
    public int Width, Height;      // layout extents

    private const int MaxMapDest = 8;     // mapPlanet[]/mapSection[] are [5] in the engine
    private const int MaxCubes = 4;       // cubeList[4] — the outpost's four reader slots
    private const int MaxSteps = 4000;    // runaway guard for a malformed script
    private const int MaxExpansions = 20000;  // ... and for the cube-state search

    private readonly EpisodeScriptFile _f;
    private readonly List<string> _planets;
    private readonly Dictionary<int, GraphNode> _byLine = new();
    private readonly HashSet<string> _edgeSeen = new();

    private EpisodeGraph(EpisodeScriptFile f, List<string> planets)
    {
        _f = f;
        _planets = planets;
    }

    /// <summary>
    /// Resolve one episode. <paramref name="secretsOf"/> maps a level file number to the
    /// sections its secret pickups warp to (see <see cref="FindSecretTargets"/>).
    /// </summary>
    public static EpisodeGraph Build(EpisodeScriptFile f, List<string> planets,
        Func<int, IEnumerable<int>>? secretsOf = null)
    {
        var g = new EpisodeGraph(f, planets);
        g.Walk(secretsOf);
        g.Layout();
        return g;
    }

    // =====================================================================
    // Resolution
    // =====================================================================

    /// <summary>Engine state that survives across sections within one walk.</summary>
    private sealed class WalkState
    {
        public int MainLevel;                       // the section the current scan started at
        public int MapCount;
        public readonly int[] MapPlanet = new int[MaxMapDest];
        public readonly int[] MapSection = new int[MaxMapDest];
        // The engine's cubeList[4] verbatim, stale entries and all. ']?' overwrites only its
        // first `count` slots and nothing ever clears the rest, so a later ']+' that pushes
        // cubeMax past that count makes an older outpost's entry readable again — which is
        // exactly what Episode 4's Desert Run outpost does.
        public readonly int[] Slots = new int[MaxCubes];
        // cubeMax bounds. JE_main zeroes cubeMax at the start of every level
        // (tyrian2.c:2082, after JE_loadMap has already run the outpost), so an outpost sees
        // only the datacubes found in the level immediately before it. Low = none found,
        // High = as many as that level actually carries.
        public int MaxLow, MaxHigh;
        public List<int> CubeDropped = new();
        public bool Galaga, Engage, Extra, Save, Shop;
        /// <summary>The stock of the outpost the walk last passed through, set at its ']I' and
        /// attached to the level(s) it leads into. A fresh <see cref="WalkState"/> after each
        /// level (see Carry) clears it, so it only ever reaches the level right after the shop.
        /// The reference is shared read-only across the map-choice forks, so copying it is cheap.</summary>
        public ShopStop? ShopStock;
        public List<(EdgeKind Kind, string Label)> Conds = new();

        /// <summary>What the outpost can put on its shelf, and how much of that is free.</summary>
        public List<int> Readable => Slots.Take(Math.Clamp(MaxHigh, 0, MaxCubes)).ToList();

        public WalkState Fork(EdgeKind kind = EdgeKind.Continue, string label = "")
        {
            var s = new WalkState
            {
                MainLevel = MainLevel, MapCount = MapCount,
                MaxLow = MaxLow, MaxHigh = MaxHigh,
                CubeDropped = new List<int>(CubeDropped),
                Galaga = Galaga, Engage = Engage, Extra = Extra, Save = Save, Shop = Shop,
                ShopStock = ShopStock,
                Conds = new List<(EdgeKind, string)>(Conds),
            };
            Array.Copy(MapPlanet, s.MapPlanet, MaxMapDest);
            Array.Copy(MapSection, s.MapSection, MaxMapDest);
            Array.Copy(Slots, s.Slots, MaxCubes);
            if (label.Length > 0) s.Conds.Add((kind, label));
            return s;
        }

        /// <summary>A key for "have I already walked on from here in this cube state".</summary>
        public long CubeKey => ((long)Slots[0] << 36) | ((long)Slots[1] << 24)
            | ((long)Slots[2] << 12) | (long)Slots[3];
    }

    /// <summary>Where one walk came to rest, and under which conditions.</summary>
    private readonly record struct Dest(int Line, WalkState State);

    private void Walk(Func<int, IEnumerable<int>>? secretsOf)
    {
        var start = NewNode(GraphNodeKind.Start, -1);
        var queue = new Queue<(GraphNode From, int Section, WalkState State, EdgeKind Kind, string Label)>();
        queue.Enqueue((start, 1, new WalkState(), EdgeKind.Start, ""));
        // Expanded once per distinct cubeList state, because that state is what decides
        // which readings the outposts downstream can show. The four slots only ever hold
        // values a ']?' put there, so the state space stays small; MaxExpansions is a
        // backstop in case some episode's routing makes it blow up anyway.
        var expanded = new HashSet<(int Node, long Cubes)>();

        while (queue.Count > 0 && expanded.Count < MaxExpansions)
        {
            var (from, section, state, kind, label) = queue.Dequeue();
            foreach (var dest in Resolve(section, state))
            {
                var node = NodeFor(dest);
                AddEdge(from, node, kind, label, dest.State);
                if (node.Kind != GraphNodeKind.Level) continue;
                if (!expanded.Add((node.Id, dest.State.CubeKey))) continue;

                // Finishing the level advances mainLevel to the ]L's nextLevel; 0 means
                // "the section after the one that loaded me" (tyrian2.c:4374-4379).
                var entry = EpisodeScript.ParseLevelLine(_f.Lines[node.Line], node.Section);
                int next = entry.NextLevel != 0 ? entry.NextLevel : dest.State.MainLevel + 1;

                // cubeList survives the level; cubeMax is zeroed as the level starts and then
                // counts the datacubes picked up in it, so the next outpost sees 0..that many.
                // cubeList survives the level; cubeMax restarts at 0 and counts the datacubes
                // found in it. MaxHigh is the engine's cap rather than this level's supply:
                // a warp ball or an enemy's enemydie chain can drop cubes that no static
                // count sees, and over-reporting what an outpost *can* show is far safer
                // than hiding a reading the player has actually read.
                WalkState Carry()
                {
                    var c = new WalkState { MaxLow = 0, MaxHigh = MaxCubes };
                    Array.Copy(dest.State.Slots, c.Slots, MaxCubes);
                    return c;
                }
                if (_f.StartOf(next) >= 0)
                    queue.Enqueue((node, next, Carry(), EdgeKind.Continue, ""));

                if (secretsOf == null) continue;
                foreach (int target in secretsOf(node.LvlFileNum).Distinct())
                {
                    if (_f.StartOf(target) < 0 || target == next) continue;
                    queue.Enqueue((node, target, Carry(), EdgeKind.Secret, "secret orb"));
                }
            }
        }

        // A ]L the campaign can never arrive at (a leftover or debug-only cut) still belongs in
        // the picture: hang it off the start node rather than dropping it silently.
        for (int line = 0; line < _f.Lines.Count; line++)
        {
            string s = _f.Lines[line];
            if (s.Length < 2 || s[0] != ']' || s[1] != 'L' || _byLine.ContainsKey(line)) continue;
            var st = new WalkState { MainLevel = _f.SectionAt(line) };
            AddEdge(start, NodeFor(new Dest(line, st)), EdgeKind.Start, "no route in", st);
        }
    }

    /// <summary>
    /// Run the script from a section the way JE_loadMap's inner loop does, forking at every
    /// conditional jump. Returns each place the walk can come to rest.
    /// </summary>
    private List<Dest> Resolve(int section, WalkState start)
    {
        var results = new List<Dest>();
        if (_f.StartOf(section) < 0) return results;
        start.MainLevel = section;

        var pending = new Stack<(int Cursor, WalkState State)>();
        pending.Push((_f.StartOf(section), start));
        int steps = 0;
        while (pending.Count > 0 && steps++ < MaxSteps)
        {
            var (cursor, st) = pending.Pop();
            var end = RunPath(cursor, st, pending);
            if (end.HasValue) results.Add(end.Value);
        }
        return results;
    }

    /// <summary>
    /// One path through the command stream. Conditional commands push the branch they did
    /// not take onto <paramref name="pending"/> and carry on down the one they did.
    /// Returns where the path stopped, or null if it only spawned branches (]I, ]T) or ran out.
    /// </summary>
    private Dest? RunPath(int cursor, WalkState st, Stack<(int, WalkState)> pending)
    {
        var seen = new HashSet<int>();
        for (int i = cursor; i >= 0 && i < _f.Lines.Count; i++)
        {
            if (!seen.Add(i)) return null;        // a ]J ring; this path reaches nothing new
            string s = _f.Lines[i];
            // A '*' marker is just another string to the reader: a section that neither
            // loads a level nor jumps runs straight on into the next one.
            if (s.Length < 2 || s[0] != ']') continue;

            int Target(int off) => EpisodeScript.AtoiAt(s, off);
            // Take a jump: park the not-taken continuation, then move the cursor. Returns
            // the line before the target so the loop's i++ lands on it; -1 ends the path.
            int Jump(int to)
            {
                st.MainLevel = to;
                int line = _f.StartOf(to);
                return line < 0 ? int.MaxValue : line - 1;
            }
            // `elseLabel` is for the two-way splits: a difficulty test says something about
            // both sides, where "no player died" or "only one player" on every other edge
            // would just be noise.
            int Branch(int off, EdgeKind kind, string label, string elseLabel = "")
            {
                pending.Push((i + 1, elseLabel.Length > 0 ? st.Fork(kind, elseLabel) : st.Fork()));
                int to = Target(off);
                st = st.Fork(kind, label);
                return Jump(to);
            }

            switch (s[1])
            {
                case 'L': return new Dest(i, st);                 // load level: the path ends here

                case 'J': i = Jump(Target(3)); break;
                case '2': i = Branch(3, EdgeKind.TwoPlayer, "2-player"); break;
                case 'w': i = Branch(3, EdgeKind.SpecialShip, "Stalker 21.126"); break;
                case 't': i = Branch(3, EdgeKind.TimerFail, "boss timer ran out"); break;
                case 'l': i = Branch(3, EdgeKind.PlayerDied, "a player died"); break;
                case 'H': i = Branch(4, EdgeKind.Difficulty, DiffBelow, DiffHard); break;

                // ]h eats the line below it on Hard and up, which in Episode 1 section 3
                // means the second ]L (the harder cut of TYRIAN) is what loads.
                case 'h':
                    pending.Push((i + 2, st.Fork(EdgeKind.Difficulty, DiffHard)));
                    st = st.Fork(EdgeKind.Difficulty, DiffBelow);
                    break;

                case 'T':                                         // Timed Battle arena select
                    for (int b = 5; b >= 1; b--)
                    {
                        int to = Target(b * 3);
                        if (_f.StartOf(to) < 0) continue;
                        var bs = st.Fork(EdgeKind.TimedBattle, $"battle {b}");
                        bs.MainLevel = to;
                        pending.Push((_f.StartOf(to), bs));
                    }
                    break;

                case 'G':                                         // galaxy map destinations
                    st.MapCount = Math.Clamp(Target(7), 0, MaxMapDest);
                    for (int k = 0; k < st.MapCount; k++)
                    {
                        st.MapPlanet[k] = Target(1 + (k + 1) * 8);
                        st.MapSection[k] = Target(4 + (k + 1) * 8);
                    }
                    break;

                // The outpost. Its 9 item-availability lines are data, not commands, and
                // launching from its map menu is what sets the next section.
                case 'I':
                    st.ShopStock = ParseShop(i, st.MainLevel);
                    i += 9;
                    st.Shop = true;
                    if (st.MapCount == 0) break;
                    for (int k = 0; k < st.MapCount; k++)
                    {
                        int to = st.MapSection[k];
                        if (_f.StartOf(to) < 0) continue;
                        var ms = st.Fork(EdgeKind.MapChoice, PlanetLabel(st.MapPlanet[k]));
                        ms.MainLevel = to;
                        pending.Push((_f.StartOf(to), ms));
                    }
                    return null;

                case 'Q': return new Dest(-2, st);                // JE_nextEpisode
                case 'q': return new Dest(-3, st);                // Timed Battle over

                case 'W':                                         // warning text, until a '#'
                    while (i + 1 < _f.Lines.Count && !_f.Lines[++i].StartsWith('#')) { }
                    break;

                // Datacube availability. ]? names the readings this outpost carries and
                // clamps cubeMax to how many there are; ]! sets it outright and ]+ tops it
                // up, both capped at the four slots the engine has.
                //
                // Two ways a name on the line never reaches the player: the engine writes
                // only the first `count` of them, and cubeList has four slots while the
                // reader loop is bounded by cubeMax <= 4. Episode 3's Savara Y line lists
                // five, Episode 5's Stage 4 line writes two under a count of one -- both
                // are kept separately rather than silently shown as stocked.
                case '?':
                    st.CubeDropped.Clear();
                    int count = Target(4);
                    for (int k = 0; k < 8; k++)
                    {
                        int off = 3 + (k + 1) * 4;
                        if (off >= s.Length) break;
                        int cube = Target(off);
                        if (cube == 0) break;
                        // Only the first `count` are written, and only four slots exist;
                        // the engine leaves anything above that holding its old value.
                        if (k < Math.Min(count, MaxCubes)) st.Slots[k] = cube;
                        else st.CubeDropped.Add(cube);
                    }
                    st.MaxLow = Math.Min(st.MaxLow, count);
                    st.MaxHigh = Math.Min(st.MaxHigh, count);
                    break;
                case '!':
                    st.MaxLow = st.MaxHigh = Math.Clamp(Target(4), 0, MaxCubes);
                    break;
                case '+':
                    st.MaxLow = Math.Min(MaxCubes, st.MaxLow + Target(4));
                    st.MaxHigh = Math.Min(MaxCubes, st.MaxHigh + Target(4));
                    break;

                case 'g': st.Galaga = true; break;
                case 'e': st.Engage = true; break;
                case 'x': st.Extra = true; break;
                case 's': case 'b': st.Save = true; break;
            }
            if (i == int.MaxValue) return null;                   // jumped to a missing section
        }
        return null;
    }

    private string PlanetLabel(int planet)
    {
        string n = PlanetNames.Get(_planets, planet);
        return n.Length > 0 ? n : $"planet {planet}";
    }

    // =====================================================================
    // Node / edge construction
    // =====================================================================

    private GraphNode NewNode(GraphNodeKind kind, int line)
    {
        var n = new GraphNode { Id = Nodes.Count, Kind = kind, Line = line };
        Nodes.Add(n);
        if (line >= 0) _byLine[line] = n;
        return n;
    }

    private GraphNode NodeFor(Dest d)
    {
        if (d.Line == -2) return TerminalFor(GraphNodeKind.NextEpisode, d.State.MainLevel);
        if (d.Line == -3) return TerminalFor(GraphNodeKind.TimedBattleOver, d.State.MainLevel);
        if (d.Line < 0) return TerminalFor(GraphNodeKind.DeadEnd, d.State.MainLevel);
        if (_byLine.TryGetValue(d.Line, out var existing))
        {
            existing.Shop |= d.State.Shop;
            existing.SavePoint |= d.State.Save;
            NoteCubes(existing, d.State);
            NoteShop(existing, d.State);
            return existing;
        }

        int section = _f.SectionAt(d.Line);
        var e = EpisodeScript.ParseLevelLine(_f.Lines[d.Line], section);
        var n = NewNode(GraphNodeKind.Level, d.Line);
        n.Section = section;
        n.Name = e.Name;
        n.LvlFileNum = e.LvlFileNum;
        n.Song = e.Song;
        n.Bonus = e.BonusLevel || e.NormalBonus;
        n.Galaga = d.State.Galaga;
        n.Engage = d.State.Engage;
        n.Extra = d.State.Extra;
        n.SavePoint = d.State.Save;
        n.Shop = d.State.Shop;
        NoteCubes(n, d.State);
        NoteShop(n, d.State);
        return n;
    }

    /// <summary>Record the outpost this route came through, one entry per distinct shelf.</summary>
    private static void NoteShop(GraphNode n, WalkState st)
    {
        if (st.ShopStock == null) return;
        if (!n.ShopStops.Any(s => s.SameAs(st.ShopStock))) n.ShopStops.Add(st.ShopStock);
    }

    /// <summary>
    /// Read the nine item-availability lines that follow a ']I' at <paramref name="iLine"/>
    /// (tyrian2.c:4340-4354): each line's first eight characters are a fixed label field the
    /// engine skips, and the rest is a run of whitespace-separated item ids. <see cref="Section"/>
    /// is filled from the outpost's own section so the fork's Charge-Laser re-add can be
    /// reproduced without re-walking.
    /// </summary>
    private ShopStop ParseShop(int iLine, int section)
    {
        var stop = new ShopStop { Section = section };
        for (int r = 0; r < ShopStop.RowCount; r++)
        {
            int ln = iLine + 1 + r;
            if (ln >= _f.Lines.Count) break;
            string s = _f.Lines[ln];
            // strncpy(buf, (strlen(s) > 8) ? s + 8 : "", ...): only past the 8-char label.
            if (s.Length > 8) PopInts(s, 8, stop.Rows[r]);
        }
        return stop;
    }

    /// <summary>
    /// str_pop_int in a loop (mainint.c:5229): repeatedly strtol, which skips leading
    /// whitespace, reads an optional sign and digits, and stops at the first character that is
    /// neither — so the ids are whitespace-separated and anything else ends the line, exactly
    /// as the engine reads it.
    /// </summary>
    private static void PopInts(string s, int start, List<int> into)
    {
        int i = start;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;   // strtol skips whitespace
            int tokenStart = i;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            int digits = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i == digits) break;                                // strtol found no number: stop
            if (int.TryParse(s.AsSpan(tokenStart, i - tokenStart), out int v)) into.Add(v);
        }
    }

    /// <summary>Record the outpost this route came through, keeping one entry per distinct
    /// shelf and, for each, the fewest free slots any route arrives with.</summary>
    private static void NoteCubes(GraphNode n, WalkState st)
    {
        var readable = st.Readable;
        if (readable.Count == 0) return;
        var stop = n.CubeStops.FirstOrDefault(s => s.Cubes.SequenceEqual(readable));
        if (stop == null)
        {
            stop = new CubeStop { Cubes = readable, Dropped = new List<int>(st.CubeDropped) };
            n.CubeStops.Add(stop);
        }
        stop.Free = Math.Min(stop.Free, st.MaxLow);
    }

    private readonly Dictionary<(GraphNodeKind, int), GraphNode> _terminals = new();

    private GraphNode TerminalFor(GraphNodeKind kind, int section)
    {
        // One terminal per kind: every ending in an episode means the same thing, and
        // merging them keeps the ending from fanning out into a row of identical boxes.
        if (_terminals.TryGetValue((kind, 0), out var n)) return n;
        n = NewNode(kind, -1);
        n.Section = section;
        _terminals[(kind, 0)] = n;
        return n;
    }

    /// <summary>
    /// How much a condition says about a route. A galaxy-map pick sits on nearly every edge,
    /// so when something rarer (a timer, a difficulty, a secret) is also on the path, that is
    /// what the edge should be coloured and captioned by; the rest goes in the tooltip.
    /// </summary>
    private static int Rank(EdgeKind k) => k switch
    {
        EdgeKind.Secret => 8,
        EdgeKind.TimerFail => 7,
        EdgeKind.PlayerDied => 6,
        EdgeKind.SpecialShip => 5,
        EdgeKind.Difficulty => 4,
        EdgeKind.TwoPlayer => 3,
        EdgeKind.TimedBattle => 2,
        EdgeKind.MapChoice => 1,
        _ => 0,
    };

    private void AddEdge(GraphNode from, GraphNode to, EdgeKind kind, string label, WalkState state)
    {
        // The reason we started walking, then every condition the walk passed through.
        var conds = new List<(EdgeKind Kind, string Label)>();
        if (label.Length > 0) conds.Add((kind, label));
        foreach (var c in state.Conds)
            if (!conds.Contains(c)) conds.Add(c);

        var lead = conds.Count > 0 ? conds.MaxBy(c => Rank(c.Kind)) : (kind, "");
        string detail = string.Join("  ·  ", conds.Select(c => c.Label));

        string key = $"{from.Id}>{to.Id}:{lead.Item1}:{detail}";
        if (!_edgeSeen.Add(key)) return;

        from.Out.Add(Edges.Count);
        to.In.Add(Edges.Count);
        Edges.Add(new GraphEdge
        {
            From = from.Id, To = to.Id,
            Kind = conds.Count > 0 ? lead.Item1 : kind,
            Label = lead.Item2,
            Detail = detail,
        });
    }

    /// <summary>The two sides of a ']H'/']h' split. DIFFICULTY_HARD is 3, so "below" is
    /// Wimp, Easy or Normal (config.h:40-46).</summary>
    public const string DiffHard = "Hard+", DiffBelow = "below Hard";

    /// <summary>
    /// Is every route to this level file through a warp ball? Reachability rather than a
    /// look at the level's own incoming edges, because a secret level can lead on to more
    /// of them by ordinary means — Episode 2's Gem Warfare hands over to Markers and then
    /// Mistakes through plain outpost choices, and all three are secret levels.
    /// </summary>
    public bool IsSecretOnly(int lvlFileNum) => GatedBy(lvlFileNum, _openly ??= Reachable(e => e.Kind != EdgeKind.Secret));

    /// <summary>
    /// Which difficulty a level file is locked behind (<see cref="DiffHard"/> /
    /// <see cref="DiffBelow"/>), or "" when it can be reached whatever you started on.
    /// Same reasoning as the secret check: what matters is whether any route avoids the
    /// split, not whether the level's own arrows happen to be difficulty ones.
    /// </summary>
    public string DifficultyGate(int lvlFileNum)
    {
        _eitherDiff ??= Reachable(e => e.Kind != EdgeKind.Difficulty);
        if (!GatedBy(lvlFileNum, _eitherDiff)) return "";

        _diffReach ??= new[] { DiffHard, DiffBelow }.ToDictionary(l => l,
            l => Reachable(e => e.Kind != EdgeKind.Difficulty || e.Label == l));
        var opens = _diffReach.Where(kv => !GatedBy(lvlFileNum, kv.Value)).Select(kv => kv.Key).ToList();
        return opens.Count == 1 ? opens[0] : "";
    }

    private HashSet<int>? _openly, _eitherDiff;
    private Dictionary<string, HashSet<int>>? _diffReach;

    /// <summary>True when no cut of this level file is in the given reachable set.</summary>
    private bool GatedBy(int lvlFileNum, HashSet<int> reached)
    {
        var cuts = Nodes.Where(n => n.Kind == GraphNodeKind.Level && n.LvlFileNum == lvlFileNum).ToList();
        return cuts.Count > 0 && cuts.All(n => !reached.Contains(n.Id));
    }

    /// <summary>Everything the campaign reaches using only the edges the filter allows.</summary>
    private HashSet<int> Reachable(Func<GraphEdge, bool> allow)
    {
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (var n in Nodes)
            if (n.In.Count == 0 && seen.Add(n.Id)) queue.Enqueue(n.Id);
        while (queue.Count > 0)
            foreach (int ei in Nodes[queue.Dequeue()].Out)
            {
                if (!allow(Edges[ei]) || !seen.Add(Edges[ei].To)) continue;
                queue.Enqueue(Edges[ei].To);
            }
        return seen;
    }

    // =====================================================================
    // Secret levels (level data, not the script)
    // =====================================================================

    /// <summary>
    /// The sections a level's secret warps lead to. Flying into an item whose enemyDat value
    /// is over 10000 sets nextLevel to value - 10000 (mainint.c:7999-8018); 20000 and up are
    /// the armour and special pickups instead, so the window is 10001..20000.
    ///
    /// The warp ball is rarely placed by the level itself: it is usually what a boss or a
    /// guarded turret leaves behind, so this follows every way one enemy leads to another —
    /// enemyDat's enemydie successor, and event 33, which retargets a live formation's
    /// enemydie — until the set stops growing.
    /// </summary>
    public static List<int> FindSecretTargets(Level lv, EnemyData ed)
    {
        var reachable = new HashSet<int>();
        var pending = new Queue<int>();
        void Consider(int type)
        {
            if (type > 0 && reachable.Add(type)) pending.Enqueue(type);
        }

        foreach (var e in lv.Events)
        {
            // Event 33 hands a whole linked formation a new enemydie: that is how a boss
            // hands over its warp ball.
            if (e.Type == 33) { Consider(e.Dat); continue; }
            if (!IsEnemySpawn(e.Type)) continue;
            // Event 12 places four consecutive types as one 4x4 object.
            for (int k = 0; k < (e.Type == 12 ? 4 : 1); k++) Consider(e.Dat + k);
        }
        while (pending.Count > 0)
            Consider(ed.Get(pending.Dequeue()).EEnemyDie);

        var found = new List<int>();
        foreach (int type in reachable)
        {
            var dat = ed.Get(type);
            if (!dat.Loaded || dat.Value <= 10000 || dat.Value > 20000) continue;
            // Collectible either straight away (no armour -> enemyAvail 2) or once shot down
            // (dlevel -1 turns the wreck into the pickup instead of killing it).
            if (dat.Armor != 0 && dat.DLevel != -1) continue;
            int target = dat.Value - 10000;
            if (!found.Contains(target)) found.Add(target);
        }
        found.Sort();
        return found;
    }

    // The spawn events that take an enemyDat index in dat (ObjectPlacer.IsSpawn, minus the
    // 49..52 inline forms, which carry their own graphics and never a pickup value).
    private static bool IsEnemySpawn(byte type) =>
        type is 6 or 7 or 10 or 12 or 15 or 17 or 18 or 23 or 32 or 56;

    // =====================================================================
    // Layout
    // =====================================================================

    public const float NodeH = 38f, RowGap = 58f, ColGap = 30f, MinNodeW = 104f;
    private const float BendW = 16f;      // lane a routed edge reserves in the rows it crosses
    public int MaxDepth { get; private set; }

    /// <summary>
    /// A slot in the layered layout: either a real node, or a waypoint standing in for an
    /// edge that crosses this row. Giving the crossings a place in the ordering is what
    /// keeps a long route (a secret warp four rows down) from cutting across the boxes
    /// between its ends.
    /// </summary>
    private struct Slot { public int Node; public int Edge; public float X, W; }

    /// <summary>
    /// Layered top-to-bottom placement (the classic Sugiyama three steps): longest-path
    /// depth with back edges removed, waypoints inserted for edges that span more than one
    /// row, then barycentre sweeps to untangle the rows.
    /// </summary>
    private void Layout()
    {
        int n = Nodes.Count;
        if (n == 0) return;
        AssignDepths(n, out int maxDepth);
        MaxDepth = maxDepth;

        // --- Slots: every node, plus a waypoint per crossed row for the long edges. ---
        var slots = new List<Slot>();
        var rows = new List<List<int>>();
        for (int d = 0; d <= maxDepth; d++) rows.Add(new List<int>());
        var nodeSlot = new int[n];
        for (int id = 0; id < n; id++)
        {
            nodeSlot[id] = slots.Count;
            slots.Add(new Slot { Node = id, Edge = -1, W = Math.Max(MinNodeW, NodeLabelWidth(Nodes[id])) });
            rows[Nodes[id].Depth].Add(nodeSlot[id]);
        }

        // Arcs link consecutive rows: a short edge is one arc, a long one is a chain
        // through its waypoints. All the ordering below works on arcs, not on edges.
        var arcs = new List<(int From, int To)>();
        var inArcs = new List<List<int>>();
        var outArcs = new List<List<int>>();
        void Grow() { while (inArcs.Count < slots.Count) { inArcs.Add(new List<int>()); outArcs.Add(new List<int>()); } }
        void Link(int a, int b)
        {
            Grow();
            outArcs[a].Add(arcs.Count);
            inArcs[b].Add(arcs.Count);
            arcs.Add((a, b));
        }

        for (int e = 0; e < Edges.Count; e++)
        {
            var edge = Edges[e];
            if (edge.Back) continue;
            int d0 = Nodes[edge.From].Depth, d1 = Nodes[edge.To].Depth;
            if (d1 <= d0) { edge.Back = true; continue; }
            int prev = nodeSlot[edge.From];
            for (int d = d0 + 1; d < d1; d++)
            {
                int s = slots.Count;
                slots.Add(new Slot { Node = -1, Edge = e, W = BendW });
                rows[d].Add(s);
                Link(prev, s);
                prev = s;
            }
            Link(prev, nodeSlot[edge.To]);
        }
        Grow();

        // --- Ordering: alternate down and up barycentre sweeps. ---
        var pos = new float[slots.Count];
        for (int d = 0; d <= maxDepth; d++)
            for (int k = 0; k < rows[d].Count; k++) pos[rows[d][k]] = k;

        var bary = new Dictionary<int, float>();
        for (int pass = 0; pass < 12; pass++)
        {
            bool down = pass % 2 == 0;
            for (int step = 0; step <= maxDepth; step++)
            {
                var row = rows[down ? step : maxDepth - step];
                if (row.Count < 2) continue;
                bary.Clear();
                foreach (int s in row)
                {
                    float sum = 0; int count = 0;
                    foreach (int a in down ? inArcs[s] : outArcs[s])
                    {
                        sum += pos[down ? arcs[a].From : arcs[a].To];
                        count++;
                    }
                    bary[s] = count > 0 ? sum / count : pos[s];
                }
                row.Sort((a, b) => bary[a].CompareTo(bary[b]));
                for (int k = 0; k < row.Count; k++) pos[row[k]] = k;
            }
        }

        // --- Placement. Pack each row, then pull every slot towards the average of its
        // neighbours so straight runs stay straight and the branches splay out. ---
        float widest = 0;
        var rowW = new float[maxDepth + 1];
        for (int d = 0; d <= maxDepth; d++)
        {
            float w = 0;
            foreach (int s in rows[d]) w += slots[s].W + ColGap;
            rowW[d] = Math.Max(0, w - ColGap);
            widest = Math.Max(widest, rowW[d]);
        }
        for (int d = 0; d <= maxDepth; d++)
        {
            float x = (widest - rowW[d]) * 0.5f;
            foreach (int s in rows[d])
            {
                var slot = slots[s];
                slot.X = x;
                slots[s] = slot;
                x += slot.W + ColGap;
            }
        }

        for (int pass = 0; pass < 6; pass++)
        {
            bool down = pass % 2 == 0;
            for (int step = 0; step <= maxDepth; step++)
            {
                var row = rows[down ? step : maxDepth - step];
                foreach (int s in row)
                {
                    var links = down ? inArcs[s] : outArcs[s];
                    if (links.Count == 0) continue;
                    float sum = 0;
                    foreach (int a in links)
                    {
                        int other = down ? arcs[a].From : arcs[a].To;
                        sum += slots[other].X + slots[other].W * 0.5f;
                    }
                    var slot = slots[s];
                    slot.X = sum / links.Count - slot.W * 0.5f;
                    slots[s] = slot;
                }
                // Restore the row's minimum spacing without changing its order.
                for (int k = 1; k < row.Count; k++)
                {
                    float min = slots[row[k - 1]].X + slots[row[k - 1]].W + ColGap;
                    if (slots[row[k]].X >= min) continue;
                    var slot = slots[row[k]];
                    slot.X = min;
                    slots[row[k]] = slot;
                }
                for (int k = row.Count - 2; k >= 0; k--)
                {
                    float max = slots[row[k + 1]].X - ColGap - slots[row[k]].W;
                    if (slots[row[k]].X <= max) continue;
                    var slot = slots[row[k]];
                    slot.X = max;
                    slots[row[k]] = slot;
                }
            }
        }

        // --- Commit: node boxes, then each long edge's waypoints in row order. ---
        float minX = float.MaxValue, maxX = float.MinValue;
        foreach (var slot in slots)
        {
            minX = Math.Min(minX, slot.X);
            maxX = Math.Max(maxX, slot.X + slot.W);
        }
        float shift = -minX + 20f;

        for (int id = 0; id < n; id++)
        {
            var slot = slots[nodeSlot[id]];
            Nodes[id].X = slot.X + shift;
            Nodes[id].W = slot.W;
            Nodes[id].Y = Nodes[id].Depth * (NodeH + RowGap);
        }
        foreach (var edge in Edges) edge.Bends.Clear();
        for (int d = 0; d <= maxDepth; d++)
            foreach (int s in rows[d])
            {
                if (slots[s].Node >= 0) continue;
                Edges[slots[s].Edge].Bends.Add(new System.Numerics.Vector2(
                    slots[s].X + shift + slots[s].W * 0.5f, d * (NodeH + RowGap) + NodeH * 0.5f));
            }

        Width = (int)(maxX - minX + 40f);
        Height = (int)(maxDepth * (NodeH + RowGap) + NodeH + 20f);
    }

    /// <summary>Longest-path depth over the graph with its cycles broken.</summary>
    private void AssignDepths(int n, out int maxDepth)
    {
        // Depth-first pass to mark back edges: an ending that jumps to level 1 makes a cycle.
        var state = new byte[n];          // 0 unvisited, 1 on stack, 2 done
        var stack = new Stack<(int Id, int Next)>();
        for (int root = 0; root < n; root++)
        {
            if (state[root] != 0) continue;
            state[root] = 1;
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (id, k) = stack.Pop();
                if (k >= Nodes[id].Out.Count) { state[id] = 2; continue; }
                stack.Push((id, k + 1));
                int ei = Nodes[id].Out[k];
                int to = Edges[ei].To;
                if (state[to] == 1) { Edges[ei].Back = true; continue; }
                if (state[to] != 0) continue;
                state[to] = 1;
                stack.Push((to, 0));
            }
        }

        var indeg = new int[n];
        foreach (var e in Edges) if (!e.Back) indeg[e.To]++;
        var q = new Queue<int>();
        for (int i = 0; i < n; i++) if (indeg[i] == 0) q.Enqueue(i);
        while (q.Count > 0)
        {
            int id = q.Dequeue();
            foreach (int ei in Nodes[id].Out)
            {
                if (Edges[ei].Back) continue;
                int to = Edges[ei].To;
                Nodes[to].Depth = Math.Max(Nodes[to].Depth, Nodes[id].Depth + 1);
                if (--indeg[to] == 0) q.Enqueue(to);
            }
        }

        // The episode's ending reads better pinned to the bottom row than floating beside
        // whichever level happens to reach it first. Timed Battle's ending is deliberately
        // left where it lands: dragging it down would stretch five arena routes over the
        // whole campaign and bury it in crossings.
        maxDepth = 0;
        foreach (var node in Nodes) maxDepth = Math.Max(maxDepth, node.Depth);
        foreach (var node in Nodes)
            if (node.Kind == GraphNodeKind.NextEpisode)
                node.Depth = maxDepth;
    }

    // Rough text metrics: the layout runs without a font, so approximate the box width at
    // the default ImGui glyph advance. The view draws the text centred in whatever comes out.
    private static float NodeLabelWidth(GraphNode n) =>
        (n.Title.Length + n.Subtitle.Length) * 7.0f + 34f;
}
