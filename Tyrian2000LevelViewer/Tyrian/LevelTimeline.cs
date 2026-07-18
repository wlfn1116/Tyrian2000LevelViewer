namespace T2LV.Tyrian;

public readonly record struct EventOccurrence(
    int Index, EventRec Event, int PathDistance,
    int UniformDistance1, int UniformDistance2, int UniformDistance3,
    int Move1, int Move2, int Move3,
    int SourceY1, int SourceY2, int SourceY3,
    int UniformSourceY1, int UniformSourceY2, int UniformSourceY3);

/// <summary>
/// A finite, successful pass through a level whose script jumps backwards or wraps a
/// background. Path distance is one monotonic display axis. Every background keeps its
/// real engine cursor, sampled along that axis, so consecutive scroll phases cannot be
/// overlaid merely because different layers moved during them.
/// </summary>
public sealed class LevelTimeline
{
    public const int ViewBottom = 224;

    public bool IsUnrolled { get; private init; }
    public int Distance { get; private init; }
    /// <summary>Canvas extent (px) actually backed by recorded content in the 1:1 texture
    /// view: the longest layer history and every spawn's anchor fit inside it.</summary>
    public int UniformExtent { get; private init; }
    public IReadOnlyList<EventOccurrence> Occurrences { get; private init; } = Array.Empty<EventOccurrence>();

    private int[] _layerDistance = new int[3];
    private int[] _wrapCount = new int[3];
    private int[][] _sourceY = { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };
    private int[][] _uniformSourceY = { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };
    private bool[] _starActive = Array.Empty<bool>();
    private bool[] _uniformStarActive = Array.Empty<bool>();
    private LevelStartFlags[] _renderFlags = Array.Empty<LevelStartFlags>();
    private LevelStartFlags[] _uniformRenderFlags = Array.Empty<LevelStartFlags>();
    private ScreenFilterState[] _screenFilters = Array.Empty<ScreenFilterState>();
    private ScreenFilterState[] _uniformScreenFilters = Array.Empty<ScreenFilterState>();

    public int LayerDistance(int layer) => _layerDistance[layer];
    /// <summary>Recorded length of a layer's 1:1 texture track; rows beyond it carry no
    /// real content (the layer moved less than the shared path axis) and are not drawn.</summary>
    public int UniformLength(int layer) => _layerDistance[layer];
    public int WrapCount(int layer) => _wrapCount[layer];
    public bool StarActive(int distance, bool uniformTextureScale = false)
    {
        var states = uniformTextureScale ? _uniformStarActive : _starActive;
        return states.Length == 0 || states[Math.Clamp(distance, 0, states.Length - 1)];
    }
    public LevelStartFlags RenderFlags(int distance, bool uniformTextureScale = false)
    {
        var states = uniformTextureScale ? _uniformRenderFlags : _renderFlags;
        return states.Length == 0 ? LevelStartFlags.Defaults
            : states[Math.Clamp(distance, 0, states.Length - 1)];
    }
    public ScreenFilterState ScreenFilter(int distance, bool uniformTextureScale = false)
    {
        var states = uniformTextureScale ? _uniformScreenFilters : _screenFilters;
        return states.Length == 0 ? new ScreenFilterState(false, -99, -99)
            : states[Math.Clamp(distance, 0, states.Length - 1)];
    }

    public int SourceY(int layer, int distance, bool uniformTextureScale = false)
    {
        var a = uniformTextureScale ? _uniformSourceY[layer] : _sourceY[layer];
        if (a.Length == 0) return 0;
        return a[Math.Clamp(distance, 0, a.Length - 1)];
    }

    public bool HasSourceDiscontinuity(int layer, int fromDistance, int toDistance,
        bool uniformTextureScale = false)
    {
        var a = uniformTextureScale ? _uniformSourceY[layer] : _sourceY[layer];
        if (a.Length < 2) return false;
        int lo = Math.Clamp(Math.Min(fromDistance, toDistance), 0, a.Length - 1);
        int hi = Math.Clamp(Math.Max(fromDistance, toDistance), 0, a.Length - 1);
        for (int i = lo + 1; i <= hi; i++)
            if (Math.Abs(a[i] - a[i - 1]) > 1)
                return true;
        return false;
    }

    public static LevelTimeline Build(Level lv)
    {
        const int MaxTicks = 120_000;
        const int MaxDistance = 120_000;

        // All three source tracks are indexed by the same monotonic path distance.
        // LayerDistance below remains the physical pixel total for diagnostics only.
        var source = new[] { new List<int>(), new List<int>(), new List<int>() };
        var uniformSource = new[] { new List<int>(), new List<int>(), new List<int>() };
        var starActiveByDistance = new List<bool> { true };
        var renderFlagsByDistance = new List<LevelStartFlags> { LevelStartFlags.Defaults };
        var uniformStarChanges = new SortedDictionary<int, bool> { [0] = true };
        var uniformRenderChanges = new SortedDictionary<int, LevelStartFlags>
            { [0] = LevelStartFlags.Defaults };
        // The engine enters every level with a transient fade-in (tyrian2.c:870-875,
        // levelBrightness -14 rising to 0, then the filter idles). Baking that fade
        // would blacken the start band of every map, so the static view begins at the
        // idle state; authored event-44 filters still apply from their own values.
        var initialFilter = new ScreenFilterState(true, -99, -99);
        var filterByDistance = new List<ScreenFilterState> { initialFilter };
        var uniformFilterChanges = new SortedDictionary<int, ScreenFilterState>
            { [0] = initialFilter };
        var occurrences = new List<EventOccurrence>();
        var cursors = new[]
        {
            new MapCursor(Level.Bg1Cols, 292),
            new MapCursor(Level.Bg2Cols, 592),
            new MapCursor(Level.Bg3Cols, 592),
        };
        var uniformCursors = new[]
        {
            new MapCursor(Level.Bg1Cols, 292, loopAtTop: true),
            new MapCursor(Level.Bg2Cols, 592, loopAtTop: true),
            new MapCursor(Level.Bg3Cols, 592, loopAtTop: true),
        };
        for (int layer = 0; layer < 3; layer++)
        {
            source[layer].Add(cursors[layer].SourceY);
            uniformSource[layer].Add(uniformCursors[layer].SourceY);
        }

        int eventIndex = 0;
        int curLoc = 0;
        int move1 = 1, move2 = 2, move3 = 3;
        int delay1 = 1, delay1Max = 1, delay2 = 1, delay2Max = 1;
        bool forceEvents = false;
        bool timer = false;
        int timerLeft = 0, timerJump = 0;
        int returnLoc = 0;
        bool pendingReturn = false;
        int superJump = -1;
        bool readyToEnd = false;
        bool starActive = true;
        LevelStartFlags renderFlags = LevelStartFlags.Defaults;
        bool filterActive = true, filterFade = false, filterFadeStart = false;
        int levelFilter = -99, levelBrightness = -99, levelBrightnessChg = 1;
        int levelFilterNew = 0;
        var globalFlags = new bool[10];
        int pathDistance = 0;
        int spawnExtent = 0;
        var layerDistance = new int[3];
        var wrapCount = new int[3];
        bool ended = false;
        var visits = new Dictionary<int, int>();

        int LowerBound(int time)
        {
            int lo = 0, hi = lv.Events.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (lv.Events[mid].Time < time) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        void Jump(int target, bool rememberReturn = true)
        {
            if (rememberReturn) returnLoc = curLoc + 1;
            curLoc = target;
            eventIndex = LowerBound(target);
        }

        void RelocateTrack(int layer)
        {
            uniformCursors[layer].CopyPosition(cursors[layer]);
            source[layer][pathDistance] = cursors[layer].SourceY;
            uniformSource[layer][^1] = uniformCursors[layer].SourceY;
        }

        for (int tick = 0; tick < MaxTicks && pathDistance < MaxDistance && !ended; tick++)
        {
            int occurrenceBatchStart = occurrences.Count;
            int eventGuard = 0;
            while (eventIndex < lv.Events.Length && lv.Events[eventIndex].Time <= curLoc && !ended)
            {
                if (++eventGuard > lv.Events.Length * 3)
                {
                    ended = true;
                    break;
                }

                int thisIndex = eventIndex;
                EventRec e = lv.Events[thisIndex];
                occurrences.Add(new EventOccurrence(
                    thisIndex, e, pathDistance,
                    uniformSource[0].Count - 1, uniformSource[1].Count - 1,
                    uniformSource[2].Count - 1, move1, move2, move3,
                    cursors[0].SourceY, cursors[1].SourceY, cursors[2].SourceY,
                    uniformCursors[0].SourceY, uniformCursors[1].SourceY,
                    uniformCursors[2].SourceY));
                eventIndex++;

                // Whatever track a spawn ends up anchored to, its 1:1-view distance never
                // exceeds the path distance at the spawn tick; record the deepest one so
                // the uniform canvas can be sized to real content only.
                if (IsSpawnType(e.Type))
                    spawnExtent = Math.Max(spawnExtent, pathDistance);

                switch (e.Type)
                {
                    case 8:
                        starActive = false;
                        starActiveByDistance[pathDistance] = false;
                        uniformStarChanges[uniformSource[0].Count - 1] = false;
                        break;
                    case 9:
                        starActive = true;
                        starActiveByDistance[pathDistance] = true;
                        uniformStarChanges[uniformSource[0].Count - 1] = true;
                        break;
                    case 21:
                        renderFlags.Background3Over = 1;
                        renderFlagsByDistance[pathDistance] = renderFlags;
                        uniformRenderChanges[uniformSource[0].Count - 1] = renderFlags;
                        break;
                    case 22:
                        renderFlags.Background3Over = 0;
                        renderFlagsByDistance[pathDistance] = renderFlags;
                        uniformRenderChanges[uniformSource[0].Count - 1] = renderFlags;
                        break;
                    case 28:
                        renderFlags.TopEnemyOver = false;
                        renderFlagsByDistance[pathDistance] = renderFlags;
                        uniformRenderChanges[uniformSource[0].Count - 1] = renderFlags;
                        break;
                    case 29:
                        renderFlags.TopEnemyOver = true;
                        renderFlagsByDistance[pathDistance] = renderFlags;
                        uniformRenderChanges[uniformSource[0].Count - 1] = renderFlags;
                        break;
                    case 42:
                        renderFlags.Background3Over = 2;
                        renderFlagsByDistance[pathDistance] = renderFlags;
                        uniformRenderChanges[uniformSource[0].Count - 1] = renderFlags;
                        break;
                    case 43:
                        renderFlags.Background2Over = unchecked((byte)e.Dat);
                        renderFlagsByDistance[pathDistance] = renderFlags;
                        uniformRenderChanges[uniformSource[0].Count - 1] = renderFlags;
                        break;
                    case 44:
                        filterActive = e.Dat > 0;
                        filterFade = e.Dat == 2;
                        levelFilter = e.Dat2;
                        levelBrightness = e.Dat3;
                        levelFilterNew = e.Dat4;
                        levelBrightnessChg = e.Dat5;
                        filterFadeStart = e.Dat6 == 0;
                        var newFilter = new ScreenFilterState(
                            filterActive, levelFilter, levelBrightness);
                        filterByDistance[pathDistance] = newFilter;
                        uniformFilterChanges[uniformSource[0].Count - 1] = newFilter;
                        break;
                    case 48:
                        renderFlags.Background2NotTransparent = true;
                        renderFlagsByDistance[pathDistance] = renderFlags;
                        uniformRenderChanges[uniformSource[0].Count - 1] = renderFlags;
                        break;
                    case 73:
                        renderFlags.SkyEnemyOverAll = e.Dat == 1;
                        renderFlagsByDistance[pathDistance] = renderFlags;
                        uniformRenderChanges[uniformSource[0].Count - 1] = renderFlags;
                        break;
                    case 2:
                    case 30:
                        move1 = Math.Max(0, (int)e.Dat);
                        move2 = Math.Max(0, (int)e.Dat2);
                        move3 = Math.Max(0, (int)e.Dat3);
                        delay1 = delay1Max = delay2 = delay2Max = 1;
                        break;
                    case 3:
                        move1 = move2 = move3 = 1;
                        delay1 = delay1Max = 3;
                        delay2 = delay2Max = 2;
                        break;
                    case 4:
                    case 83:
                        // Map stops are released by the live enemy state, which a static
                        // viewer cannot simulate. Continue with the engine's resume speeds.
                        move1 = 1; move2 = 2; move3 = 3;
                        delay1 = delay1Max = delay2 = delay2Max = 1;
                        break;
                    case 11:
                        ended = true;
                        break;
                    case 36:
                        // Ready-to-end still allows later authored records to run while
                        // enemies remain. The pass ends at the script tail or loop exit.
                        readyToEnd = true;
                        break;
                    case 38:
                    {
                        int target = unchecked((ushort)e.Dat);
                        if (target < e.Time)
                        {
                            // CORAL/FRUIT use event 38 as a holding loop after the
                            // level has become ready to end. A successful static pass
                            // ends here; replaying the loop duplicates the level map.
                            if (readyToEnd) { ended = true; break; }
                            int count = visits.TryGetValue(thisIndex, out int old) ? old + 1 : 1;
                            visits[thisIndex] = count;
                            if (count >= 2) { ended = true; break; }
                        }
                        // Event 38 assigns curLoc directly; unlike JE_eventJump it does
                        // not alter the return address used by event 76.
                        Jump(target, rememberReturn: false);
                        break;
                    }
                    case 53:
                        forceEvents = e.Dat != 99;
                        break;
                    case 54:
                    {
                        int target = unchecked((ushort)e.Dat);
                        int count = visits.TryGetValue(thisIndex, out int old) ? old + 1 : 1;
                        visits[thisIndex] = count;
                        if (target < e.Time)
                        {
                            // A boss-254 exit is taken after the authored attack pass. Replaying
                            // that pass before exiting only duplicates stationary arena objects.
                            if (superJump > e.Time) target = superJump;
                            else if (count >= 2) { ended = true; break; }
                        }
                        Jump(target);
                        break;
                    }
                    case 57:
                        superJump = unchecked((ushort)e.Dat);
                        break;
                    case 60:
                        // A successful static pass assumes a specially tagged enemy
                        // is eventually destroyed before a later flag test.
                        if (e.Dat >= 1 && e.Dat <= globalFlags.Length)
                            globalFlags[e.Dat - 1] = e.Dat2 == 1;
                        break;
                    case 61:
                        if (e.Dat >= 1 && e.Dat <= globalFlags.Length &&
                            globalFlags[e.Dat - 1] == (e.Dat2 == 1))
                            eventIndex = Math.Min(lv.Events.Length,
                                eventIndex + Math.Max(0, (int)e.Dat3));
                        break;
                    case 63: // normal one-player path
                        eventIndex = Math.Min(lv.Events.Length, eventIndex + Math.Max(0, (int)e.Dat));
                        break;
                    case 66: // normal difficulty (2)
                        if (2 <= e.Dat)
                            eventIndex = Math.Min(lv.Events.Length, eventIndex + Math.Max(0, (int)e.Dat2));
                        break;
                    case 67:
                        timer = e.Dat == 1;
                        timerLeft = Math.Max(0, (int)e.Dat3) * 100;
                        timerJump = unchecked((ushort)e.Dat2);
                        break;
                    case 70:
                    {
                        // The condition depends on live enemies. Keep the first authored
                        // attack pass, then take the successful branch when it comes around.
                        int count = visits.TryGetValue(thisIndex, out int old) ? old + 1 : 1;
                        visits[thisIndex] = count;
                        if (count >= 2) Jump(unchecked((ushort)e.Dat));
                        break;
                    }
                    case 71:
                        if (cursors[0].ByteOffset <= unchecked((ushort)e.Dat2))
                            Jump(unchecked((ushort)e.Dat));
                        break;
                    case 76:
                        // The engine returns after the spawned wave has left the screen.
                        // A static successful pass takes that return after this tick;
                        // waiting until the distant selector records would fabricate a
                        // huge stationary section (notably SQUADRON's time-60000 tail).
                        pendingReturn = returnLoc > 0;
                        break;
                    case 77:
                        cursors[0].SetFromCellOffset(e.Dat);
                        cursors[1].SetFromCellOffset(e.Dat2 > 0 ? e.Dat2 : e.Dat);
                        RelocateTrack(0);
                        RelocateTrack(1);
                        break;
                    case 81:
                        cursors[1].SetWrap(e.Dat, e.Dat2);
                        uniformCursors[1].SetWrap(e.Dat, e.Dat2);
                        break;
                }
            }

            // The engine executes every due event before drawing this tick. A speed
            // or map-position event later in the same batch therefore also affects
            // enemies spawned by earlier records in that batch.
            for (int i = occurrenceBatchStart; i < occurrences.Count; i++)
            {
                occurrences[i] = occurrences[i] with
                {
                    Move1 = move1,
                    Move2 = move2,
                    Move3 = move3,
                    SourceY1 = cursors[0].SourceY,
                    SourceY2 = cursors[1].SourceY,
                    SourceY3 = cursors[2].SourceY,
                    UniformSourceY1 = uniformCursors[0].SourceY,
                    UniformSourceY2 = uniformCursors[1].SourceY,
                    UniformSourceY3 = uniformCursors[2].SourceY,
                };
            }

            if (ended) break;
            if (eventIndex >= lv.Events.Length) break;

            var frameFilter = new ScreenFilterState(
                filterActive, levelFilter, levelBrightness);
            filterByDistance[pathDistance] = frameFilter;
            uniformFilterChanges[uniformSource[0].Count - 1] = frameFilter;

            bool forcedScriptStep = forceEvents && move1 == 0;
            if (forcedScriptStep) curLoc++;

            int d1 = 0, d2 = 0;
            if (--delay1 == 0)
            {
                delay1 = delay1Max;
                d1 = move1;
                curLoc += d1;
            }
            if (--delay2 == 0)
            {
                delay2 = delay2Max;
                d2 = move2;
            }
            int d3 = move3;

            int[] delta = { d1, d2, d3 };
            // The canvas axis follows the gameplay surface: the faster of BG1/BG2 for
            // this tick — the layers terrain sits on and glued objects ride (ground
            // bands follow backMove, skyGlue follows backMove2; tyrian2.c). That layer
            // is drawn pixel-perfect. BG3 is the engine's ambient parallax overlay
            // (clouds, side rails) and is resampled onto the surface axis, faster or
            // slower, exactly as its relative motion dictates.
            int pathDelta = Math.Max(delta[0], delta[1]);
            pathDelta = Math.Min(pathDelta, MaxDistance - pathDistance);
            var movedByLayer = new int[3];

            // Lay this engine tick onto the shared axis. The surface layer is
            // pixel-perfect; the other layers retain their actual cursor and are
            // sampled across the same interval. Stationary event time adds no fake
            // terrain—the game is still showing the same arena at that point.
            for (int pathStep = 1; pathStep <= pathDelta; pathStep++)
            {
                for (int layer = 0; layer < 3; layer++)
                {
                    int target = (int)((long)delta[layer] * pathStep / pathDelta);
                    while (movedByLayer[layer] < target)
                    {
                        if (cursors[layer].MovePixel())
                            wrapCount[layer]++;
                        uniformCursors[layer].MovePixel();
                        uniformSource[layer].Add(uniformCursors[layer].SourceY);
                        movedByLayer[layer]++;
                        layerDistance[layer]++;
                    }
                    source[layer].Add(cursors[layer].SourceY);
                }
                starActiveByDistance.Add(starActive);
                renderFlagsByDistance.Add(renderFlags);
                filterByDistance.Add(frameFilter);
            }
            // A tick can move a layer further than the surface axis advanced (BG3
            // outrunning the surface, or a BG3-only phase adding no canvas rows at
            // all). The engine's cursor still moved, so ours must too — otherwise
            // every later row of that layer would come from the wrong map position.
            for (int layer = 0; layer < 3; layer++)
            {
                while (movedByLayer[layer] < delta[layer])
                {
                    if (cursors[layer].MovePixel())
                        wrapCount[layer]++;
                    uniformCursors[layer].MovePixel();
                    uniformSource[layer].Add(uniformCursors[layer].SourceY);
                    movedByLayer[layer]++;
                    layerDistance[layer]++;
                }
            }
            pathDistance += pathDelta;

            // JE_filterScreen receives the current filter values by value, then
            // advances the fade globals for the following frame.
            if (filterActive && filterFade)
            {
                levelBrightness += levelBrightnessChg;
                if ((filterFadeStart && levelBrightness < -14) || levelBrightness > 14)
                {
                    levelBrightnessChg = -levelBrightnessChg;
                    filterFadeStart = false;
                    levelFilter = levelFilterNew;
                }
                if (!filterFadeStart && levelBrightness == 0)
                {
                    filterFade = false;
                    levelBrightness = -99;
                }
            }

            if (pendingReturn)
            {
                Jump(returnLoc, rememberReturn: false);
                pendingReturn = false;
            }

            if (timer && timerLeft > 0 && --timerLeft == 0)
                Jump(timerJump);
        }

        // Every level renders along its one-player route: that is the only axis on which
        // the three backgrounds keep their in-game alignment (they scroll at different
        // speeds, so raw side-by-side grids cannot). Only a level that never moves at all
        // falls back to the raw grids.
        bool unrolled = pathDistance > 0;
        if (!unrolled)
        {
            // Keep the successful one-player event route even when no continuous
            // background unroll is needed. Raw-grid Y is BG1's physical axis, so keep
            // the uniform event-state tracks as well: late star/order changes still
            // occur in ordinary levels and must not be collapsed into the start state.
            return new LevelTimeline
            {
                Occurrences = occurrences,
                _uniformStarActive = ExpandChanges(uniformStarChanges,
                    pathDistance + 1, true),
                _uniformRenderFlags = ExpandChanges(uniformRenderChanges,
                    pathDistance + 1, LevelStartFlags.Defaults),
                _uniformScreenFilters = ExpandChanges(uniformFilterChanges,
                    pathDistance + 1, initialFilter),
            };
        }

        // The 1:1 texture canvas only spans recorded content: the longest layer history
        // plus room for spawns anchored while their carrier was stopped. Slower layers
        // simply end where their history ends — the engine never shows a player the map
        // beyond that point, so the viewer must not invent it either.
        int uniformExtent = Math.Max(spawnExtent,
            Math.Max(layerDistance[0], Math.Max(layerDistance[1], layerDistance[2])));

        static T[] ExpandChanges<T>(SortedDictionary<int, T> changes, int length, T initial)
        {
            var result = new T[length];
            T state = initial;
            using var enumerator = changes.GetEnumerator();
            bool hasChange = enumerator.MoveNext();
            for (int i = 0; i < length; i++)
            {
                while (hasChange && enumerator.Current.Key <= i)
                {
                    state = enumerator.Current.Value;
                    hasChange = enumerator.MoveNext();
                }
                result[i] = state;
            }
            return result;
        }

        return new LevelTimeline
        {
            IsUnrolled = true,
            Distance = pathDistance,
            UniformExtent = uniformExtent,
            Occurrences = occurrences,
            _layerDistance = layerDistance,
            _wrapCount = wrapCount,
            _sourceY = source.Select(x => x.ToArray()).ToArray(),
            _uniformSourceY = uniformSource.Select(x => x.ToArray()).ToArray(),
            _starActive = starActiveByDistance.ToArray(),
            _uniformStarActive = ExpandChanges(uniformStarChanges, pathDistance + 1, true),
            _renderFlags = renderFlagsByDistance.ToArray(),
            _uniformRenderFlags = ExpandChanges(uniformRenderChanges, pathDistance + 1,
                LevelStartFlags.Defaults),
            _screenFilters = filterByDistance.ToArray(),
            _uniformScreenFilters = ExpandChanges(uniformFilterChanges,
                pathDistance + 1, initialFilter),
        };
    }

    /// <summary>Every event type that places an object (mirrors ObjectPlacer.IsSpawn).</summary>
    private static bool IsSpawnType(byte type)
        => type is 6 or 7 or 10 or 12 or 15 or 17 or 18 or 23 or 32 or 49 or 50 or 51 or 52 or 56;

    private sealed class MapCursor
    {
        private readonly int _cols;
        private int _row;
        private int _pixel;
        private int _wrapFrom = 1;
        private int _wrapTo = 1;

        public MapCursor(int cols, int row, bool loopAtTop = false)
        {
            _cols = cols;
            _row = row;
            if (loopAtTop) _wrapTo = row;
        }

        public int ByteOffset => _row * _cols * 2;
        public int SourceY => _row * ShapeTable.TileH - _pixel;
        public int PixelsUntilWrap => Math.Max(0, (_row - _wrapFrom) * ShapeTable.TileH - _pixel);

        public void SetFromCellOffset(int byteOffset)
        {
            int cells = Math.Max(0, byteOffset) / 2;
            _row = (cells + 1) / _cols;
            _pixel = 0;
        }

        public void SetWrap(int fromByteOffset, int toByteOffset)
        {
            _wrapFrom = Math.Max(0, fromByteOffset / 2 / _cols);
            int cells = Math.Max(0, toByteOffset) / 2;
            _wrapTo = (cells + 1) / _cols;
        }

        public void CopyPosition(MapCursor other)
        {
            _row = other._row;
            _pixel = other._pixel;
        }

        public bool MovePixel()
        {
            _pixel++;
            if (_pixel < ShapeTable.TileH) return false;
            _pixel = 0;
            _row--;
            if (_row > _wrapFrom) return false;
            if (_wrapTo > _wrapFrom)
            {
                _row = _wrapTo;
                return true;
            }
            _row = _wrapTo;
            return true;
        }
    }
}
