using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using SdlNs = Hexa.NET.SDL2;
using ImSdl = Hexa.NET.ImGui.Backends.SDL2;

namespace T2LV;

internal static unsafe class Program
{
    const uint SDL_INIT_VIDEO = 0x00000020u;
    const int SDL_WINDOWPOS_CENTERED = 0x2FFF0000;

    // SDL window flags
    const uint SDL_WINDOW_MAXIMIZED = 0x00000080u;
    const uint SDL_WINDOW_RESIZABLE = 0x00000020u;
    const uint SDL_WINDOW_ALLOW_HIGHDPI = 0x00002000u;
    // SDL renderer flags
    const uint SDL_RENDERER_ACCELERATED = 0x00000002u;
    const uint SDL_RENDERER_PRESENTVSYNC = 0x00000004u;

    static int Main(string[] args)
    {
        bool smoke = Array.IndexOf(args, "--smoke") >= 0;
        int uishot = Array.IndexOf(args, "--uishot");
        if (Array.IndexOf(args, "--dump") >= 0)
            return DumpSelfTest();
        if (Array.IndexOf(args, "--export") >= 0)
            return ExportLevel(args);
        if (Array.IndexOf(args, "--findenemy") >= 0)
            return FindEnemy(args);
        if (Array.IndexOf(args, "--sprites") >= 0)
            return SpriteGrid(args);
        if (Array.IndexOf(args, "--checksprites") >= 0)
            return CheckSprites();
        if (Array.IndexOf(args, "--checktimelines") >= 0)
            return CheckTimelines();
        if (Array.IndexOf(args, "--checkalllevels") >= 0)
            return CheckAllLevels();
        if (Array.IndexOf(args, "--auditcontrol") >= 0)
            return AuditControlEvents();
        if (Array.IndexOf(args, "--auditspawns") >= 0)
            return AuditSpawns();
        if (Array.IndexOf(args, "--events") >= 0)
            return DumpEvents(args);
        if (Array.IndexOf(args, "--enemydat") >= 0)
            return DumpEnemyDat(args);
        if (Array.IndexOf(args, "--simtest") >= 0)
            return SimTest(args);
        if (Array.IndexOf(args, "--simshot") >= 0)
            return SimShot(args);
        if (Array.IndexOf(args, "--tree") >= 0)
            return DumpTree(args);

        PreloadBundledNativeLibraries();
        if (SdlNs.SDL.Init(SDL_INIT_VIDEO) != 0)
        {
            Console.Error.WriteLine("SDL_Init failed: " + SdlNs.SDL.GetErrorS());
            return 1;
        }

        // Only persist UI state in normal interactive runs (not --smoke / --uishot).
        bool persist = !smoke && uishot < 0;
        bool useSettings = Array.IndexOf(args, "--usesettings") >= 0;   // load (but don't save) in test runs
        var settings = (persist || useSettings) ? AppSettings.Load() : new AppSettings();

        int winW = settings.WinW > 200 ? settings.WinW : 1280;
        int winH = settings.WinH > 200 ? settings.WinH : 800;
        int winX = persist && settings.WinX != int.MinValue ? settings.WinX : SDL_WINDOWPOS_CENTERED;
        int winY = persist && settings.WinY != int.MinValue ? settings.WinY : SDL_WINDOWPOS_CENTERED;
        uint winFlags = SDL_WINDOW_RESIZABLE | SDL_WINDOW_ALLOW_HIGHDPI;
        if (persist && settings.WinMaximized) winFlags |= SDL_WINDOW_MAXIMIZED;

        var window = SdlNs.SDL.CreateWindow("Tyrian 2000 Level Viewer", winX, winY, winW, winH, winFlags);
        if (window.IsNull)
        {
            Console.Error.WriteLine("CreateWindow failed: " + SdlNs.SDL.GetErrorS());
            return 1;
        }

        var renderer = SdlNs.SDL.CreateRenderer(window, -1,
            SDL_RENDERER_ACCELERATED | SDL_RENDERER_PRESENTVSYNC);
        if (renderer.IsNull)
        {
            Console.Error.WriteLine("CreateRenderer failed: " + SdlNs.SDL.GetErrorS());
            return 1;
        }

        var ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(ctx);
        ImSdl.ImGuiImplSDL2.SetCurrentContext(ctx);

        var io = ImGui.GetIO();
        // No NavEnableKeyboard: the arrow/page keys are viewer shortcuts (level switching,
        // canvas jumps) and must not also wander the widget focus.
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.IniFilename = null;   // window/panel state lives in settings.json, not imgui.ini

        var bWindow = new ImSdl.SDLWindowPtr((ImSdl.SDLWindow*)(void*)window.Handle);
        var bRenderer = new ImSdl.SDLRendererPtr((ImSdl.SDLRenderer*)(void*)renderer.Handle);

        ImSdl.ImGuiImplSDL2.InitForSDLRenderer(bWindow, bRenderer);
        ImSdl.ImGuiImplSDL2.SDLRenderer2Init(bRenderer);

        Console.WriteLine("Init OK. ImGui " + ImGui.GetVersionS());

        int si = Array.IndexOf(args, "--start");
        int cliEp = si >= 0 && si + 1 < args.Length ? int.Parse(args[si + 1]) : -1;
        int cliLevel = si >= 0 && si + 2 < args.Length ? int.Parse(args[si + 2]) : -1;
        int pi = Array.IndexOf(args, "--playback");
        int cliPlaybackTick = pi >= 0
            ? (pi + 1 < args.Length && int.TryParse(args[pi + 1], out int pt) ? pt : 1) : -1;
        if (Array.IndexOf(args, "--ext") >= 0) settings.SimExtendedView = true;
        bool noSmoothies = Array.IndexOf(args, "--no-smoothies") >= 0;
        if (Array.IndexOf(args, "--no-filters") >= 0 ||
            Array.IndexOf(args, "--no-color-fades") >= 0) settings.ShowScreenFilter = false;
        if (noSmoothies || Array.IndexOf(args, "--no-terrain-smoothies") >= 0)
            settings.ShowSmoothies = false;
        if (noSmoothies || Array.IndexOf(args, "--no-spotlight") >= 0)
            settings.ShowSpotlight = false;
        if (noSmoothies || Array.IndexOf(args, "--no-screen-flip") >= 0)
            settings.ShowScreenFlip = false;
        if (Array.IndexOf(args, "--no-boss-bars") >= 0) settings.ShowBossBars = false;
        if (Array.IndexOf(args, "--vanilla-stars") >= 0) settings.WideStarfield = false;
        if (Array.IndexOf(args, "--wide") >= 0) settings.Widescreen = true;
        if (Array.IndexOf(args, "--click-kill") >= 0) settings.ClickKill = true;
        if (Array.IndexOf(args, "--showtree") >= 0) settings.ShowTree = true;
        if (Array.IndexOf(args, "--showcubes") >= 0) settings.ShowCubes = true;
        if (Array.IndexOf(args, "--cubesbylevel") >= 0) { settings.ShowCubes = true; settings.CubesByLevel = true; }
        if (Array.IndexOf(args, "--allepisodes") >= 0) settings.AllEpisodes = true;
        int pli = Array.IndexOf(args, "--player");
        int cliPlayerX = pli >= 0 && pli + 1 < args.Length && int.TryParse(args[pli + 1], out int pxv) ? pxv : -1;
        int cliPlayerY = pli >= 0 && pli + 2 < args.Length && int.TryParse(args[pli + 2], out int pyv) ? pyv : 150;
        var app = new App(renderer, settings, cliEp, cliLevel, window, cliPlaybackTick, cliPlayerX, cliPlayerY);

        // "--showcubes N" / "--cubesbylevel N": open the reader on that cube of the started
        // episode, so a screenshot can frame one particular outpost's shelf.
        foreach (string flag in new[] { "--showcubes", "--cubesbylevel" })
        {
            int ci = Array.IndexOf(args, flag);
            if (ci >= 0 && ci + 1 < args.Length && int.TryParse(args[ci + 1], out int cube))
                app.ShowCube(Math.Max(0, cliEp), cube);
        }

        int mi = Array.IndexOf(args, "--mouse");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        System.Numerics.Vector2? fakeMouse = mi >= 0 && mi + 2 < args.Length
            ? new System.Numerics.Vector2(
                float.Parse(args[mi + 1], inv), float.Parse(args[mi + 2], inv))
            : null;
        // "--mousedown left|middle|right": hold that button from frame 3 on, so --uishot can
        // capture drag-only state (the right-drag player aim). Late enough that the widget
        // under --mouse is already hovered when ImGui sees the press, or the click is lost.
        int mdi = Array.IndexOf(args, "--mousedown");
        int fakeButton = mdi >= 0 && mdi + 1 < args.Length
            ? args[mdi + 1] switch { "left" => 0, "right" => 1, "middle" => 2, _ => -1 } : -1;

        bool running = true;
        int frame = 0;
        while (running)
        {
            SdlNs.SDLEvent e = default;
            while (SdlNs.SDL.PollEvent(ref e) != 0)
            {
                ImSdl.ImGuiImplSDL2.ProcessEvent(new ImSdl.SDLEventPtr((ImSdl.SDLEvent*)&e));
                if (e.Type == (uint)SdlNs.SDLEventType.Quit)
                    running = false;
            }

            ImSdl.ImGuiImplSDL2.SDLRenderer2NewFrame();
            ImSdl.ImGuiImplSDL2.NewFrame();
            // "--mouse X Y": park the cursor at a fixed point so --uishot can capture
            // hover-only UI (markers tooltip, hover readout) without a real mouse.
            // queued last, so it wins over the backend's own position event
            if (fakeMouse.HasValue) io.AddMousePosEvent(fakeMouse.Value.X, fakeMouse.Value.Y);
            if (fakeButton >= 0 && frame >= 3) io.AddMouseButtonEvent(fakeButton, true);
            ImGui.NewFrame();

            app.Render();

            ImGui.Render();
            SdlNs.SDL.SetRenderDrawColor(renderer, 30, 30, 35, 255);
            SdlNs.SDL.RenderClear(renderer);
            ImSdl.ImGuiImplSDL2.SDLRenderer2RenderDrawData(ImGui.GetDrawData(), bRenderer);

            if (uishot >= 0 && ++frame >= 5)
            {
                int w, h; SdlNs.SDL.GetWindowSize(window, &w, &h);
                var buf = new uint[w * h];
                fixed (uint* bp = buf)
                    SdlNs.SDL.RenderReadPixels(renderer, default, Render.Gfx.SDL_PIXELFORMAT_ABGR8888, (nint)bp, w * 4);
                string outp = uishot + 1 < args.Length ? args[uishot + 1]
                    : Path.Combine(Environment.CurrentDirectory, "uishot.png");
                T2LV.Util.Png.WriteRgba(outp, w, h, buf);
                Console.WriteLine($"Wrote UI screenshot {outp} ({w}x{h})");
                running = false;
            }

            SdlNs.SDL.RenderPresent(renderer);

            if (smoke && ++frame >= 3)
                running = false;
        }

        if (persist)
        {
            uint fl = SdlNs.SDL.GetWindowFlags(window);
            settings.WinMaximized = (fl & SDL_WINDOW_MAXIMIZED) != 0;
            if (!settings.WinMaximized)  // keep the last *windowed* size/pos, not the maximized one
            {
                int w, h, x, y;
                SdlNs.SDL.GetWindowSize(window, &w, &h);
                SdlNs.SDL.GetWindowPosition(window, &x, &y);
                settings.WinW = w; settings.WinH = h; settings.WinX = x; settings.WinY = y;
            }
            app.PopulateSettings(settings);
            settings.Save();
        }

        app.Dispose();
        ImSdl.ImGuiImplSDL2.SDLRenderer2Shutdown();
        ImSdl.ImGuiImplSDL2.Shutdown();
        ImGui.DestroyContext(ctx);
        SdlNs.SDL.DestroyRenderer(renderer);
        SdlNs.SDL.DestroyWindow(window);
        SdlNs.SDL.Quit();

        Console.WriteLine("Clean exit.");
        return 0;
    }

    /// <summary>
    /// Hexa loads its native libraries by filename. In a single-file build, preload the
    /// copies extracted by the .NET host so those filename-based lookups resolve normally.
    /// </summary>
    private static void PreloadBundledNativeLibraries()
    {
        var nativeSearchPath = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string;
        if (string.IsNullOrWhiteSpace(nativeSearchPath)) return;

        string[] names = ["SDL2.dll", "cimgui.dll", "ImGuiImpl.dll", "ImGuiImplSDL2.dll"];
        foreach (string directory in nativeSearchPath.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string name in names)
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path)) NativeLibrary.Load(path);
            }
        }
    }

    static int ExportLevel(string[] args)
    {
        int gi = Array.IndexOf(args, "--export");
        int epNum = gi + 1 < args.Length ? int.Parse(args[gi + 1]) : 1;
        int fileNum = gi + 2 < args.Length ? int.Parse(args[gi + 2]) : 1;
        int pal = gi + 3 < args.Length ? int.Parse(args[gi + 3]) : AppSettings.GamePalette;
        string defaultOut = Environment.CurrentDirectory;
        string outDir = (gi + 4 < args.Length && (args[gi + 4].Contains('/') || args[gi + 4].Contains('\\'))) ? args[gi + 4] : defaultOut;
        Directory.CreateDirectory(outDir);

        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        if (dir == null) { Console.Error.WriteLine("no data dir"); return 1; }
        var gd = new T2LV.Tyrian.GameData(dir);
        var ep = gd.Episodes.Find(e => e.Number == epNum);
        if (ep == null) { Console.Error.WriteLine("no episode"); return 1; }
        var lv = gd.LoadLevel(ep, fileNum);
        var shapes = gd.GetShapeTable(lv.ShapeChar);

        bool objOnly = Array.IndexOf(args, "objonly") >= 0;
        bool bgOnly = Array.IndexOf(args, "bgonly") >= 0;
        bool parallax = Array.IndexOf(args, "parallax") >= 0;
        bool uniformScale = Array.IndexOf(args, "uniformscale") >= 0;
        bool route = Array.IndexOf(args, "route") >= 0;   // legacy route-axis render
        bool customOrder = args.Any(a => a.StartsWith("ontop="));
        var img = new T2LV.Render.CompositeImage();
        var ed = gd.GetEnemyData(ep);
        var timeline = T2LV.Tyrian.LevelTimeline.Build(lv);
        var layerScroll = new T2LV.Tyrian.ObjectPlacer.LayerScroll();
        var objs = T2LV.Tyrian.ObjectPlacer.Place(gd, ep, lv, ed,
            route ? timeline : null, layerScroll);
        foreach (var a in args)
            if (a.StartsWith("isobase="))
            {
                int b = int.Parse(a.Substring(8));
                var first = objs.First(o => o.Esize == 1 && o.SpriteIndex == b);
                objs = new List<T2LV.Tyrian.PlacedObject> { first };
                Console.WriteLine($"  isolating base {b} at ({first.X:0},{first.Y:0})");
            }
            else if (a == "iso2x2")
            {
                objs = objs.Where(o => o.Esize == 1).ToList();
                Console.WriteLine($"  keeping only {objs.Count} 2x2 objects");
            }
            else if (a.StartsWith("isoy="))
            {
                int yc = int.Parse(a.Substring(5));
                var near = objs.Where(o => o.Esize == 1 && Math.Abs(o.Y - yc) < 120).OrderBy(o => o.Y).ToList();
                foreach (var o in near)
                    Console.WriteLine($"    obj base={o.SpriteIndex} X={o.X:0} Y={o.Y:0} band={o.Band} cat={o.Cat}");
            }
        // Build the level's in-game layer stack and apply dev-flag overrides.
        var stack = T2LV.Render.LayerStack.GameOrder(T2LV.Render.LayerStack.CreateDefault(), lv.ComputeStartFlags());
        T2LV.Render.LayerDef Bg(int s) => stack.First(l => l.Kind == T2LV.Render.LayerKind.Background && l.Slot == s);
        if (objOnly) foreach (var l in stack) if (l.Kind == T2LV.Render.LayerKind.Background) l.Visible = false;
        if (Array.IndexOf(args, "l1only") >= 0) { foreach (var l in stack) l.Visible = false; Bg(0).Visible = true; }
        if (Array.IndexOf(args, "l2only") >= 0) { foreach (var l in stack) l.Visible = false; Bg(1).Visible = true; }
        if (Array.IndexOf(args, "l3only") >= 0) { foreach (var l in stack) l.Visible = false; Bg(2).Visible = true; }
        foreach (var a in args)
        {
            if (a.StartsWith("a1=")) Bg(0).Alpha = int.Parse(a.Substring(3));
            else if (a.StartsWith("a2=")) Bg(1).Alpha = int.Parse(a.Substring(3));
            else if (a.StartsWith("a3=")) Bg(2).Alpha = int.Parse(a.Substring(3));
            else if (a.StartsWith("ao=")) { int v = int.Parse(a.Substring(3)); foreach (var l in stack) if (l.Kind == T2LV.Render.LayerKind.Objects) l.Alpha = v; }
        }
        foreach (var a in args)
            if (a.StartsWith("ontop="))   // dev: move layer with this id to the front
            {
                string id = a.Substring(6);
                var l = stack.FirstOrDefault(x => x.Id == id);
                if (l != null) { stack.Remove(l); stack.Insert(0, l); }
            }
        bool drawObjs = !bgOnly;
        if (parallax)
            T2LV.Render.LevelRenderer.ComposeParallax(img, lv, shapes, gd.Palettes.Get(pal),
                stack, objs, drawObjs, timeline, uniformScale, !customOrder, !route, layerScroll);
        else
            T2LV.Render.LevelRenderer.Compose(img, lv, shapes, gd.Palettes.Get(pal),
                stack, objs, drawObjs, route ? timeline : null, uniformScale, !customOrder,
                !route, route ? null : layerScroll);
        Console.WriteLine($"placed objects: {objs.Count}");
        if (timeline.IsUnrolled)
            Console.WriteLine($"  continuous length: {timeline.Distance}px " +
                $"(layers {timeline.LayerDistance(0)}/{timeline.LayerDistance(1)}/{timeline.LayerDistance(2)}), " +
                $"canvas: {img.Width}x{img.Height}");
        int withSheet = objs.Count(o => o.Sheet != null);
        Console.WriteLine($"  objects with resolved sprite: {withSheet}/{objs.Count}");
        if (Array.IndexOf(args, "dump") >= 0)
        {
            int dmy = ArgInt("my", 15650);
            var seen2 = new HashSet<int>();
            foreach (var o in objs.Where(o => o.Esize == 1 && o.Sheet != null && o.Y >= dmy && o.Y <= dmy + 240))
            {
                if (!seen2.Add(o.SpriteIndex)) continue;
                var sheet = o.Sheet!;
                var sb = new System.Text.StringBuilder($"  base {o.SpriteIndex} cat={o.Cat} ani={ed.Get(o.EnemyId).Ani} Y={o.Y:0}: ");
                foreach (int off in new[] { 0, 1, 19, 20 })
                {
                    var s = sheet.Decode(o.SpriteIndex + off);
                    int nz = 0; if (s != null) foreach (var pp in s.Pixels) if (pp != 0) nz++;
                    sb.Append($"+{off}={(s == null ? "NULL" : $"{s.W}x{s.H}/{nz}")}  ");
                }
                Console.WriteLine(sb.ToString());
            }
        }

        int W = img.Width;
        int H = img.Height;

        // Flatten onto an opaque dark background so transparent areas are visible.
        uint dark = T2LV.Render.Gfx.Rgba(18, 18, 22);
        var flat = new uint[img.Pixels.Length];
        for (int i = 0; i < flat.Length; i++)
        {
            uint p = img.Pixels[i];
            flat[i] = (p >> 24) == 0 ? dark : (p | 0xFF000000u);
        }

        // Crop: bottom 2000 rows = the start of the level (or crop=Y0 for an arbitrary band).
        int cropH = Math.Min(2000, H);
        int y0 = H - cropH;
        foreach (var a in args) if (a.StartsWith("crop=")) { y0 = Math.Clamp(int.Parse(a.Substring(5)), 0, H - 1); cropH = Math.Min(1600, H - y0); }
        var crop = new uint[W * cropH];
        Array.Copy(flat, y0 * W, crop, 0, crop.Length);
        string startPath = Path.Combine(outDir, "export_start.png");
        T2LV.Util.Png.WriteRgba(startPath, W, cropH, crop);

        // Magnified crop (3x nearest) of a region with objects, so sprites are clear.
        int ArgInt(string key, int def) { foreach (var a in args) if (a.StartsWith(key + "=")) return int.Parse(a.Substring(key.Length + 1)); return def; }
        int mScale = 3, mw = 120, mh = 230;
        int mx0 = ArgInt("mx", 30), my0 = ArgInt("my", H - 1500);
        var mag = new uint[mw * mScale * mh * mScale];
        for (int y = 0; y < mh * mScale; y++)
            for (int x = 0; x < mw * mScale; x++)
            {
                int sxp = mx0 + x / mScale, syp = my0 + y / mScale;
                mag[y * mw * mScale + x] = (sxp >= 0 && sxp < W && syp >= 0 && syp < H) ? flat[syp * W + sxp] : dark;
            }
        T2LV.Util.Png.WriteRgba(Path.Combine(outDir, "export_mag.png"), mw * mScale, mh * mScale, mag);

        // Whole level downsampled vertically so structure is visible (fits the viewer).
        int step = Math.Max(4, (H + 1699) / 1700);
        int dh = H / step;
        var thumb = new uint[W * dh];
        for (int y = 0; y < dh; y++)
            Array.Copy(flat, (y * step) * W, thumb, y * W, W);
        string thumbPath = Path.Combine(outDir, "export_thumb.png");
        T2LV.Util.Png.WriteRgba(thumbPath, W, dh, thumb);

        Console.WriteLine($"Episode {epNum} level #{fileNum} '{(ep.Levels.Find(l => l.FileNum == fileNum)?.Name ?? "").Trim()}' shapes{char.ToLower(lv.ShapeChar)}.dat palette {pal}");
        Console.WriteLine($"Wrote {startPath} ({W}x{cropH}) and {thumbPath} ({W}x{dh})");
        img.Dispose();
        return 0;
    }

    /// <summary>"--tree [ep]": print the resolved level-flow graph of one or every episode.</summary>
    static int DumpTree(string[] args)
    {
        int gi = Array.IndexOf(args, "--tree");
        int only = gi + 1 < args.Length && int.TryParse(args[gi + 1], out int e) ? e : -1;
        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        if (dir == null) { Console.Error.WriteLine("Could not find Tyrian 2000 data files."); return 1; }
        var gd = new T2LV.Tyrian.GameData(dir);
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected */ }

        foreach (var ep in gd.Episodes)
        {
            if (only > 0 && ep.Number != only) continue;
            var g = gd.GetGraph(ep);
            if (g == null) { Console.WriteLine($"== Episode {ep.Number}: no script"); continue; }
            Console.WriteLine($"== Episode {ep.Number}: {g.Nodes.Count} nodes, {g.Edges.Count} edges, " +
                $"{g.MaxDepth + 1} rows, layout {g.Width}x{g.Height}");
            foreach (var n in g.Nodes)
            {
                string flags = "";
                if (n.Kind == T2LV.Tyrian.GraphNodeKind.Level)
                {
                    flags = $"  sec {n.Section}  lvl #{n.LvlFileNum}  song {n.Song}";
                    if (n.Bonus) flags += "  [bonus]";
                    if (n.Galaga) flags += "  [galaga]";
                    if (n.Engage) flags += "  [engage]";
                    if (n.Extra) flags += "  [extra]";
                    if (n.SavePoint) flags += "  [savepoint]";
                    if (n.Shop) flags += "  [outpost]";
                }
                Console.WriteLine($"  [{n.Depth}] {n.Title,-16}{flags}");
                if (n.CubeStops.Count > 0)
                {
                    var cubes = gd.GetCubes(ep);
                    string Title(int i) => i >= 1 && i <= cubes.Count ? cubes[i - 1].Title : "(missing)";
                    for (int s = 0; s < n.CubeStops.Count; s++)
                    {
                        var stop = n.CubeStops[s];
                        if (n.CubeStops.Count > 1) Console.WriteLine($"        outpost {s + 1}:");
                        foreach (int idx in stop.Cubes)
                            Console.WriteLine($"        cube {idx,3} [{(stop.IsFree(idx) ? "always " : "if found")}] {Title(idx)}");
                        foreach (int idx in stop.Dropped)
                            Console.WriteLine($"        cube {idx,3} [dropped  ] {Title(idx)}");
                    }
                }
                foreach (int ei in n.Out)
                {
                    var edge = g.Edges[ei];
                    string via = edge.Detail.Length > 0 ? $"  ({edge.Detail})" : "";
                    Console.WriteLine($"        --{edge.Kind,-12}-> {g.Nodes[edge.To].Title}{via}");
                }
            }
            Console.WriteLine();
        }
        return 0;
    }

    static int DumpEnemyDat(string[] args)
    {
        int gi = Array.IndexOf(args, "--enemydat");
        int epNum = int.Parse(args[gi + 1]);
        int lo = int.Parse(args[gi + 2]);
        int hi = int.Parse(args[gi + 3]);
        var gd = new T2LV.Tyrian.GameData(T2LV.Tyrian.GameData.FindDataDir()!);
        var ep = gd.Episodes.Find(e => e.Number == epNum)!;
        var ed = gd.GetEnemyData(ep);
        for (int i = lo; i <= hi; i++)
        {
            var d = ed.Get(i);
            Console.WriteLine($"  enemyDat[{i}] esize={d.Esize} egr0={(d.EGraphic!=null&&d.EGraphic.Length>0?d.EGraphic[0]:0)} bank={d.ShapeBank} armor={d.Armor} value={d.Value} startX={d.StartX}+/-{d.StartXC} startY={d.StartY}+/-{d.StartYC}");
        }
        return 0;
    }

    static int DumpEvents(string[] args)
    {
        int gi = Array.IndexOf(args, "--events");
        int epNum = gi + 1 < args.Length ? int.Parse(args[gi + 1]) : 1;
        int fileNum = gi + 2 < args.Length ? int.Parse(args[gi + 2]) : 1;
        int wantType = gi + 3 < args.Length ? int.Parse(args[gi + 3]) : -1;
        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        var gd = new T2LV.Tyrian.GameData(dir!);
        var ep = gd.Episodes.Find(e => e.Number == epNum)!;
        var lv = gd.LoadLevel(ep, fileNum);
        var ed = gd.GetEnemyData(ep);
        Console.WriteLine($"ep{epNum} #{fileNum} mapX={lv.MapX},{lv.MapX2},{lv.MapX3} events={lv.Events.Length}");
        foreach (var e in lv.Events)
        {
            if (wantType >= 0 && e.Type != wantType) continue;
            string extra = "";
            if (e.Type == 12 || e.Type == 6 || e.Type == 7 || e.Type == 15 || e.Type == 10)
            {
                var d = ed.Get(e.Dat);
                extra = $" enemyDat[{e.Dat}] esize={d.Esize} egr0={(d.EGraphic!=null&&d.EGraphic.Length>0?d.EGraphic[0]:0)} bank={d.ShapeBank}";
            }
            Console.WriteLine($"  t={e.Time} type={e.Type} dat={e.Dat} dat2={e.Dat2} dat3={e.Dat3} dat4={e.Dat4} dat5={e.Dat5} dat6={e.Dat6}{extra}");
        }
        return 0;
    }

    static int CheckSprites()
    {
        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        var gd = new T2LV.Tyrian.GameData(dir!);
        foreach (var ep in gd.Episodes)
        {
            var ed = gd.GetEnemyData(ep);
            foreach (var item in ep.Levels)
            {
                var lv = gd.LoadLevel(ep, item.FileNum);
                var objs = T2LV.Tyrian.ObjectPlacer.Place(gd, ep, lv, ed);
                int big = 0, nullBottom = 0, shortTop = 0;
                var seen = new HashSet<int>();
                foreach (var o in objs)
                {
                    if (o.Esize != 1 || o.Sheet == null || o.SpriteIndex <= 0) continue;
                    big++;
                    var s0 = o.Sheet.Decode(o.SpriteIndex);
                    var s19 = o.Sheet.Decode(o.SpriteIndex + 19);
                    var s20 = o.Sheet.Decode(o.SpriteIndex + 20);
                    if (s19 == null || s20 == null) nullBottom++;
                    if (s0 != null && s0.H < 13) shortTop++;
                    if (seen.Add(o.SpriteIndex) && seen.Count <= 3 && (s19 == null || s20 == null))
                        Console.WriteLine($"      ep{ep.Number} {item.Name.Trim()} base {o.SpriteIndex} sheetCount {o.Sheet.Count} +0H={s0?.H} +19={(s19==null?"NULL":s19.H.ToString())} +20={(s20==null?"NULL":s20.H.ToString())}");
                }
                if (big > 0 && (nullBottom > 0 || shortTop > 0))
                    Console.WriteLine($"  ep{ep.Number} #{item.FileNum} {item.Name.Trim(),-10} 2x2={big} nullBottom={nullBottom} shortTop={shortTop}");
            }
        }
        return 0;
    }

    /// <summary>
    /// Headless playback-simulator check: runs every level (or one, with
    /// "--simtest ep level"), reports duration/end/typical perf, and verifies that
    /// scrubbing is deterministic (seek == linear replay, by playfield hash).
    /// </summary>
    static int SimTest(string[] args)
    {
        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        if (dir == null) { Console.Error.WriteLine("no data dir"); return 1; }
        var gd = new T2LV.Tyrian.GameData(dir);

        int si = Array.IndexOf(args, "--simtest");
        int onlyEp = si + 1 < args.Length && int.TryParse(args[si + 1], out int e) ? e : -1;
        int onlyLevel = si + 2 < args.Length && int.TryParse(args[si + 2], out int l) ? l : -1;
        bool trace = Array.IndexOf(args, "--trace") >= 0;

        static ulong Hash(byte[] screen)
        {
            ulong h = 14695981039346656037UL;
            for (int y = 0; y < T2LV.Tyrian.GameSim.ViewH; y++)
            {
                int row = (y + T2LV.Tyrian.GameSim.OY) * T2LV.Tyrian.GameSim.BufW
                    + T2LV.Tyrian.GameSim.OX + T2LV.Tyrian.GameSim.ViewX;
                for (int x = 0; x < T2LV.Tyrian.GameSim.ViewW; x++)
                    h = (h ^ screen[row + x]) * 1099511628211UL;
            }
            return h;
        }

        bool failed = false;
        int count = 0;
        foreach (var ep in gd.Episodes)
        {
            if (onlyEp > 0 && ep.Number != onlyEp) continue;
            foreach (var item in ep.Levels)
            {
                if (onlyLevel > 0 && item.FileNum != onlyLevel) continue;
                var level = gd.LoadLevel(ep, item.FileNum);
                var shapes = gd.GetShapeTable(level.ShapeChar);
                var sim = new T2LV.Tyrian.GameSim(gd, ep, level, shapes);
                T2LV.Tyrian.SimPlayback pb;
                try
                {
                    pb = new T2LV.Tyrian.SimPlayback(sim, 10 * 60 * 35);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ep{ep.Number} #{item.FileNum:00} {item.Name,-10} FAILED: {ex.Message}");
                    failed = true;
                    continue;
                }
                count++;

                // determinism: a frame reached by scrubbing must equal the same frame
                // reached by linear stepping from the previous keyframe boundary
                int mid = Math.Max(1, pb.Duration / 2);
                pb.SeekTo(mid);
                ulong h1 = Hash(sim.Screen);
                pb.SeekTo(1);
                pb.SeekTo(mid);
                ulong h2 = Hash(sim.Screen);
                pb.SeekTo(Math.Max(1, mid - 130));   // land before the keyframe, walk over it
                pb.SeekTo(mid);
                ulong h3 = Hash(sim.Screen);
                bool ok = h1 == h2 && h2 == h3;
                if (!ok) failed = true;

                if (trace)
                {
                    foreach (var x in pb.Events.Where(x => x.Type is 11 or 38 or 44 or 54 or 57 or 64 or 67 or 70 or 76)
                                 .TakeLast(120))
                    {
                        var raw = level.Events[x.Index];
                        Console.WriteLine($"  t{x.Tick,5} i{x.Index,4} at{raw.Time,5} type{x.Type,2} " +
                            $"dat {raw.Dat,6},{raw.Dat2,6},{raw.Dat3,4},{raw.Dat4,4} " +
                            (x.Backward ? "back" : ""));
                    }
                    pb.SeekTo(pb.Duration);
                    var moves = sim.BackMoves;
                    Console.WriteLine($"  END loc={sim.CurLoc} enemies={sim.EnemyOnScreen} " +
                        $"scroll={moves.b1}/{moves.b2}/{moves.b3}");
                }

                string previewText = pb.LoopRegions.Count == 0 ? "" :
                    $"previews {pb.LoopRegions.Count}: " + string.Join(";", pb.LoopRegions.Select(r =>
                        $"{r.Kind}@{r.StartTick}->{r.EndTick}")) + "  ";
                Console.WriteLine($"ep{ep.Number} #{item.FileNum:00} {item.Name,-10} " +
                    $"{T2LV.Tyrian.SimPlayback.FormatTime(pb.Duration),7} " +
                    $"({pb.Duration,5} ticks) " +
                    (pb.EndedNaturally ? "end  " :
                        pb.LoopRegions.Any(r => r.Kind != T2LV.Tyrian.SimPlayback.HoldLoopKind.EnemyHold)
                            ? "loop " : pb.LoopDetected ? "hold " : "cap  ") +
                    $"build {pb.PrecomputeMs,4} ms  events {pb.Events.Count,4}  " +
                    previewText +
                    (ok ? "determinism OK" : $"DETERMINISM MISMATCH {h1:x} {h2:x} {h3:x}"));
            }
        }
        Console.WriteLine($"{count} levels simulated; {(failed ? "FAILURES present" : "all OK")}");
        return failed ? 1 : 0;
    }

    /// <summary>
    /// "--hide bg1,bg2,bg3,star,air,ground,fg,powerup,money,cube,decor,objects": the same
    /// draw-only layer visibility the GUI's layer list drives, so playback rendering with
    /// layers switched off is reproducible from the command line.
    /// </summary>
    static void ApplyHideList(T2LV.Tyrian.GameSim sim, string[] args)
    {
        int hi = Array.IndexOf(args, "--hide");
        if (hi < 0 || hi + 1 >= args.Length) return;
        foreach (string raw in args[hi + 1].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = raw.Trim().ToLowerInvariant();
            void Drop(T2LV.Tyrian.ObjCategory c) => sim.ObjectCategoryMask &= ~(1 << (int)c);
            switch (name)
            {
                case "bg1": sim.ShowBg1 = false; break;
                case "bg2": sim.ShowBg2 = false; break;
                case "bg3": sim.ShowBg3 = false; break;
                case "star": sim.ShowStarfield = false; break;
                case "objects": sim.ObjectCategoryMask = 0; break;
                case "air": Drop(T2LV.Tyrian.ObjCategory.EnemyAir); break;
                case "ground": Drop(T2LV.Tyrian.ObjCategory.EnemyGround); break;
                case "fg": Drop(T2LV.Tyrian.ObjCategory.EnemyForeground); break;
                case "powerup": Drop(T2LV.Tyrian.ObjCategory.Powerup); break;
                case "money": Drop(T2LV.Tyrian.ObjCategory.Money); break;
                case "cube": Drop(T2LV.Tyrian.ObjCategory.Datacube); break;
                case "decor": Drop(T2LV.Tyrian.ObjCategory.Decor); break;
                default: Console.Error.WriteLine($"--hide: unknown layer '{name}'"); break;
            }
        }
    }

    /// <summary>"--simshot ep level tick[,tick...] out_prefix": save playback frames as PNGs.</summary>
    static int SimShot(string[] args)
    {
        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        if (dir == null) { Console.Error.WriteLine("no data dir"); return 1; }
        int si = Array.IndexOf(args, "--simshot");
        if (si + 4 >= args.Length)
        { Console.Error.WriteLine("usage: --simshot ep level tick[,tick...] out_prefix"); return 1; }
        int ep = int.Parse(args[si + 1]);
        int lvl = int.Parse(args[si + 2]);
        int[] ticks = args[si + 3].Split(',').Select(int.Parse).ToArray();
        string prefix = args[si + 4];

        var gd = new T2LV.Tyrian.GameData(dir);
        var epi = gd.Episodes.First(e => e.Number == ep);
        var level = gd.LoadLevel(epi, lvl);
        var shapes = gd.GetShapeTable(level.ShapeChar);
        var sim = new T2LV.Tyrian.GameSim(gd, epi, level, shapes);
        bool noSmoothies = Array.IndexOf(args, "--no-smoothies") >= 0;
        sim.ShowScreenFilter = Array.IndexOf(args, "--no-filters") < 0 &&
                               Array.IndexOf(args, "--no-color-fades") < 0;
        sim.ShowTerrainSmoothies = !noSmoothies &&
                                    Array.IndexOf(args, "--no-terrain-smoothies") < 0;
        sim.ShowSpotlight = !noSmoothies && Array.IndexOf(args, "--no-spotlight") < 0;
        sim.ShowScreenFlip = !noSmoothies && Array.IndexOf(args, "--no-screen-flip") < 0;
        // Widescreen playback and its sub-options, so the widescreen-only paths (parallax
        // span, mirrored layers, starfield) are reproducible from the command line.
        sim.Widescreen = Array.IndexOf(args, "--wide") >= 0;
        sim.ExpandedParallax = sim.Widescreen && Array.IndexOf(args, "--parallax") >= 0;
        sim.MirrorLayers = sim.Widescreen && Array.IndexOf(args, "--mirror") >= 0;
        sim.WideStarfield = Array.IndexOf(args, "--vanilla-stars") < 0;
        int plx = Array.IndexOf(args, "--player");
        if (plx >= 0 && plx + 2 < args.Length)
        { sim.PlayerX = int.Parse(args[plx + 1]); sim.PlayerY = int.Parse(args[plx + 2]); }
        ApplyHideList(sim, args);
        var pb = new T2LV.Tyrian.SimPlayback(sim, 21000);
        uint[] pal = gd.Palettes.Get(AppSettings.GamePalette);

        bool ext = Array.IndexOf(args, "--ext") >= 0;
        sim.ExtendedDraw = ext;
        int W = ext ? T2LV.Tyrian.GameSim.BufW : sim.PlayfieldWidth;
        int H = ext ? T2LV.Tyrian.GameSim.BufH : T2LV.Tyrian.GameSim.ViewH;
        int cx = ext ? 0 : T2LV.Tyrian.GameSim.OX + T2LV.Tyrian.GameSim.ViewX;
        int cy = ext ? 0 : T2LV.Tyrian.GameSim.OY;
        // "--kill sx,sy [--kill-damage N] [--no-kill-explosions]": exercise the viewer's
        // click-to-kill at a screen-space point, listing what was live at that tick first so a
        // target is easy to name. Applied on the frame requested, then one tick is stepped so
        // the result is what gets written — the same thing the GUI click does.
        int ki = Array.IndexOf(args, "--kill");
        int[] killAt = ki >= 0 && ki + 1 < args.Length
            ? args[ki + 1].Split(',').Select(int.Parse).ToArray() : Array.Empty<int>();
        int kdi = Array.IndexOf(args, "--kill-damage");
        int killDamage = kdi >= 0 && kdi + 1 < args.Length
            ? int.Parse(args[kdi + 1]) : T2LV.Tyrian.GameSim.InstantKillDamage;
        bool killBoom = Array.IndexOf(args, "--no-kill-explosions") < 0;

        var rgba = new uint[W * H];
        var live = new List<T2LV.Tyrian.GameSim.EnemyView>();
        foreach (int t in ticks)
        {
            pb.SeekTo(t);
            if (ext) pb.RedrawCurrent();
            if (killAt.Length >= 2)
            {
                sim.CollectEnemies(live);
                Console.WriteLine($"t={pb.CurrentTick}: {live.Count} enemies drawn");
                foreach (var v in live)
                    Console.WriteLine($"   slot {v.Slot,2} id {v.EnemyId,4} link {v.LinkNum,3} " +
                        $"screen {v.ScreenX,4},{v.ScreenY,4} armor {v.ArmorLeft,3} " +
                        $"{(v.Size == 1 ? "2x2" : "1x1")} {v.Category}");
                int slot = sim.PickEnemyAt(killAt[0] + T2LV.Tyrian.GameSim.OX,
                                           killAt[1] + T2LV.Tyrian.GameSim.OY);
                Console.WriteLine(slot < 0
                    ? $"   --kill {killAt[0]},{killAt[1]}: no enemy there"
                    : $"   --kill {killAt[0]},{killAt[1]}: hit slot {slot} for {killDamage}" +
                      (killBoom ? " (with explosions)" : " (silent)") +
                      $", flash filter 0x{sim.HitFilter:x2}");
                if (slot >= 0)
                {
                    sim.DamageEnemy(slot, killDamage, killBoom);
                    pb.Advance(1);
                    sim.CollectEnemies(live);
                    Console.WriteLine($"   -> {live.Count} enemies left at t={pb.CurrentTick}");
                }
            }
            sim.PreparePresent();
            for (int y = 0; y < H; y++)
            {
                int src = (cy + y) * T2LV.Tyrian.GameSim.BufW + cx;
                for (int x = 0; x < W; x++)
                    rgba[y * W + x] = pal[sim.PresentScreen[src + x]] | 0xFF000000u;
            }
            string path = $"{prefix}_ep{ep}_{lvl:00}_t{pb.CurrentTick}.png";
            Util.Png.WriteRgba(path, W, H, rgba);
            Console.WriteLine($"wrote {path} (duration {pb.Duration})");
        }
        return 0;
    }

    static int CheckTimelines()
    {
        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        if (dir == null) { Console.Error.WriteLine("no data dir"); return 1; }
        var gd = new T2LV.Tyrian.GameData(dir);
        bool failed = false;
        var seen = new HashSet<(int Episode, int File)>();
        foreach (var ep in gd.Episodes)
        foreach (var item in ep.Levels)
        {
            if (!seen.Add((ep.Number, item.FileNum))) continue;
            var level = gd.LoadLevel(ep, item.FileNum);
            var timeline = T2LV.Tyrian.LevelTimeline.Build(level);
            if (!timeline.IsUnrolled) continue;
            var objects = T2LV.Tyrian.ObjectPlacer.Place(
                gd, ep, level, gd.GetEnemyData(ep), timeline);
            int invalidObjects = objects.Count(o =>
                o.PathDistance < 0 || o.PathDistance > timeline.Distance ||
                o.UniformPathDistance < 0 || o.UniformPathDistance > timeline.UniformExtent);
            int badAnchors = objects.Count(o => o.UniformLayer >= 0 &&
                timeline.SourceY(o.UniformLayer, o.UniformPathDistance, true) != o.UniformSourceY);
            int badGameAnchors = objects.Count(o => o.UniformLayer >= 0 &&
                timeline.SourceY(o.UniformLayer, o.PathDistance) != o.GameSourceY);
            var badFootprintObjects = objects.Where(o =>
            {
                if (o.UniformLayer < 0) return false;
                int screenY = (int)o.ScreenY;
                int sampleDistance = o.UniformPathDistance - screenY;
                if (sampleDistance < 0 || sampleDistance > timeline.Distance) return false;
                if (timeline.HasSourceDiscontinuity(o.UniformLayer,
                    o.UniformPathDistance, sampleDistance, true)) return false;
                return timeline.SourceY(o.UniformLayer, sampleDistance, true) !=
                    o.UniformSourceY + screenY;
            }).ToList();
            int badFootprints = badFootprintObjects.Count;
            static bool SameFlags(in T2LV.Tyrian.LevelStartFlags a,
                in T2LV.Tyrian.LevelStartFlags b) =>
                a.Background2Over == b.Background2Over &&
                a.Background3Over == b.Background3Over &&
                a.TopEnemyOver == b.TopEnemyOver &&
                a.SkyEnemyOverAll == b.SkyEnemyOverAll &&
                a.Background2NotTransparent == b.Background2NotTransparent;
            var expectedFlags = T2LV.Tyrian.LevelStartFlags.Defaults;
            bool expectedStars = true;
            var flagsByGameDistance = new Dictionary<int, T2LV.Tyrian.LevelStartFlags>();
            var flagsByUniformDistance = new Dictionary<int, T2LV.Tyrian.LevelStartFlags>();
            var starsByGameDistance = new Dictionary<int, bool>();
            var starsByUniformDistance = new Dictionary<int, bool>();
            foreach (var occurrence in timeline.Occurrences)
            {
                var e = occurrence.Event;
                switch (e.Type)
                {
                    case 8: expectedStars = false; break;
                    case 9: expectedStars = true; break;
                    case 21: expectedFlags.Background3Over = 1; break;
                    case 22: expectedFlags.Background3Over = 0; break;
                    case 28: expectedFlags.TopEnemyOver = false; break;
                    case 29: expectedFlags.TopEnemyOver = true; break;
                    case 42: expectedFlags.Background3Over = 2; break;
                    case 43: expectedFlags.Background2Over = unchecked((byte)e.Dat); break;
                    case 48: expectedFlags.Background2NotTransparent = true; break;
                    case 73: expectedFlags.SkyEnemyOverAll = e.Dat == 1; break;
                }
                flagsByGameDistance[occurrence.PathDistance] = expectedFlags;
                flagsByUniformDistance[occurrence.UniformDistance1] = expectedFlags;
                starsByGameDistance[occurrence.PathDistance] = expectedStars;
                starsByUniformDistance[occurrence.UniformDistance1] = expectedStars;
            }
            int badVisualStates =
                flagsByGameDistance.Count(kv => !SameFlags(timeline.RenderFlags(kv.Key), kv.Value)) +
                flagsByUniformDistance.Count(kv => !SameFlags(timeline.RenderFlags(kv.Key, true), kv.Value)) +
                starsByGameDistance.Count(kv => timeline.StarActive(kv.Key) != kv.Value) +
                starsByUniformDistance.Count(kv => timeline.StarActive(kv.Key, true) != kv.Value);
            int expectedHeight = timeline.Distance + T2LV.Tyrian.LevelTimeline.ViewBottom;
            int expectedUniformHeight = timeline.UniformExtent + T2LV.Tyrian.LevelTimeline.ViewBottom;
            bool badHeight = T2LV.Render.LevelRenderer.HeightFor(timeline, false) != expectedHeight ||
                T2LV.Render.LevelRenderer.HeightFor(timeline, true) != expectedHeight ||
                T2LV.Render.LevelRenderer.HeightFor(timeline, false, true) != expectedUniformHeight ||
                T2LV.Render.LevelRenderer.HeightFor(timeline, true, true) != expectedUniformHeight;
            Console.WriteLine($"ep{ep.Number} #{item.FileNum,-2} {item.Name.Trim(),-10} {timeline.Distance,6}px " +
                $"uniform {timeline.UniformExtent,6}px " +
                $"[{timeline.LayerDistance(0),6}/{timeline.LayerDistance(1),6}/{timeline.LayerDistance(2),6}] " +
                $"wraps {timeline.WrapCount(0)}/{timeline.WrapCount(1)}/{timeline.WrapCount(2)} " +
                $"objects {objects.Count} anchors {badGameAnchors}/{badAnchors} footprints {badFootprints} " +
                $"states {badVisualStates}");
            if (timeline.Distance >= 120_000 || invalidObjects != 0 ||
                badGameAnchors != 0 || badAnchors != 0 || badVisualStates != 0 || badHeight)
            {
                Console.Error.WriteLine($"  invalid transformed objects: {invalidObjects}; " +
                    $"misaligned anchors: game={badGameAnchors}, uniform={badAnchors}; " +
                    $"visual states={badVisualStates}; height={(badHeight ? "bad" : "ok")}");
                failed = true;
            }
            foreach (var o in badFootprintObjects.Take(8))
                Console.WriteLine($"  footprint mismatch t={o.Time} enemy={o.EnemyId} " +
                    $"band={o.Band} layer={o.UniformLayer + 1} y={o.ScreenY} " +
                    $"distance={o.UniformPathDistance}");
        }
        return failed ? 1 : 0;
    }

    static int CheckAllLevels()
    {
        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        if (dir == null) { Console.Error.WriteLine("no data dir"); return 1; }
        var gd = new T2LV.Tyrian.GameData(dir);
        var image = new T2LV.Render.CompositeImage();
        var seen = new HashSet<(int Episode, int File)>();
        bool failed = false;
        int rendered = 0;
        foreach (var ep in gd.Episodes)
        foreach (var item in ep.Levels)
        {
            if (!seen.Add((ep.Number, item.FileNum))) continue;
            try
            {
                var level = gd.LoadLevel(ep, item.FileNum);
                var timeline = T2LV.Tyrian.LevelTimeline.Build(level);
                var objects = T2LV.Tyrian.ObjectPlacer.Place(
                    gd, ep, level, gd.GetEnemyData(ep), timeline);
                int unresolved = objects.Count(o => o.SpriteIndex > 0 && o.SpriteIndex != 999 &&
                    (o.Sheet == null || o.Sheet.Decode(o.SpriteIndex) == null));
                int nonFinite = objects.Count(o => !float.IsFinite(o.X) || !float.IsFinite(o.Y) ||
                    !float.IsFinite(o.ScreenY));
                var layers = T2LV.Render.LayerStack.GameOrder(
                    T2LV.Render.LayerStack.CreateDefault(), level.ComputeStartFlags());
                var palette = gd.Palettes.Get(AppSettings.GamePalette);
                T2LV.Render.LevelRenderer.ComposeParallax(image, level,
                    gd.GetShapeTable(level.ShapeChar), palette, layers, objects, true,
                    timeline, false, true);
                if (timeline.IsUnrolled)
                    T2LV.Render.LevelRenderer.ComposeParallax(image, level,
                        gd.GetShapeTable(level.ShapeChar), palette, layers, objects, true,
                        timeline, true, true);
                Console.WriteLine($"ep{ep.Number} #{item.FileNum,-2} {item.Name.Trim(),-10} " +
                    $"events {level.Events.Length,4} objects {objects.Count,4} " +
                    $"{image.Width}x{image.Height} unresolved {unresolved}");
                if (unresolved != 0 || nonFinite != 0)
                {
                    Console.Error.WriteLine($"  invalid sprites={unresolved}, coordinates={nonFinite}");
                    failed = true;
                }
                rendered++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ep{ep.Number} #{item.FileNum} {item.Name.Trim()}: {ex}");
                failed = true;
            }
        }
        Console.WriteLine($"rendered {rendered} unique levels in normal and preserved modes");
        return failed ? 1 : 0;
    }

    static int AuditControlEvents()
    {
        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        if (dir == null) { Console.Error.WriteLine("no data dir"); return 1; }
        var gd = new T2LV.Tyrian.GameData(dir);
        int[] controlTypes = [8, 9, 21, 22, 26, 28, 29, 38, 42, 43, 44, 48, 53, 54, 61, 63,
            64, 65, 66, 67, 70, 71, 72, 73, 75, 76, 77, 80, 81, 84];
        var wanted = controlTypes.ToHashSet();
        var seen = new HashSet<(int Episode, int File)>();
        foreach (var ep in gd.Episodes)
        foreach (var item in ep.Levels)
        {
            if (!seen.Add((ep.Number, item.FileNum))) continue;
            var level = gd.LoadLevel(ep, item.FileNum);
            var counts = level.Events.Where(e => wanted.Contains(e.Type))
                .GroupBy(e => e.Type).OrderBy(g => g.Key)
                .Select(g => $"{g.Key}:{g.Count()}").ToArray();
            if (counts.Length != 0)
            {
                Console.WriteLine($"ep{ep.Number} #{item.FileNum,-2} {item.Name.Trim(),-10} " +
                    string.Join(" ", counts));
                foreach (var e in level.Events.Where(e => e.Type is 21 or 22 or 28 or 29 or 42 or 43 or 44 or 48 or 64 or 73))
                    Console.WriteLine($"  t={e.Time} type={e.Type} dat={e.Dat} dat2={e.Dat2} " +
                        $"dat3={e.Dat3} dat4={e.Dat4} dat5={e.Dat5} dat6={e.Dat6}");
            }
        }
        return 0;
    }

    static int AuditSpawns()
    {
        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        if (dir == null) { Console.Error.WriteLine("no data dir"); return 1; }
        var gd = new T2LV.Tyrian.GameData(dir);
        byte[] spawnTypes = [6, 7, 10, 12, 15, 17, 18, 23, 32, 49, 50, 51, 52, 56];
        var wanted = spawnTypes.ToHashSet();
        var seen = new HashSet<(int Episode, int File)>();
        foreach (var ep in gd.Episodes)
        {
            var enemyData = gd.GetEnemyData(ep);
            foreach (var item in ep.Levels)
            {
                if (!seen.Add((ep.Number, item.FileNum))) continue;
                var level = gd.LoadLevel(ep, item.FileNum);
                var special = new List<string>();
                foreach (var e in level.Events.Where(e => wanted.Contains(e.Type)))
                {
                    int count = e.Type == 12 ? 4 : 1;
                    for (int k = 0; k < count; k++)
                    {
                        int enemyId = e.Type is >= 49 and <= 52 ? 0 : e.Dat + k;
                        var d = enemyData.Get(enemyId);
                        if (e.Dat2 == -200)
                            special.Add($"t{e.Time}:type{e.Type}:id{enemyId}:randomX");
                        else if (e.Dat2 == -99)
                            special.Add($"t{e.Time}:type{e.Type}:id{enemyId}:default" +
                                (d.StartXC != 0 || d.StartYC != 0
                                    ? $"({d.StartX}+/-{d.StartXC},{d.StartY}+/-{d.StartYC})"
                                    : $"({d.StartX},{d.StartY})"));
                    }
                }
                if (special.Count != 0)
                    Console.WriteLine($"ep{ep.Number} #{item.FileNum,-2} {item.Name.Trim(),-10} " +
                        string.Join(" ", special));
            }
        }
        return 0;
    }

    static int SpriteGrid(string[] args)
    {
        int gi = Array.IndexOf(args, "--sprites");
        int epNum = gi + 1 < args.Length ? int.Parse(args[gi + 1]) : 1;
        int fileNum = gi + 2 < args.Length ? int.Parse(args[gi + 2]) : 1;
        string outDir = Environment.CurrentDirectory;
        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        var gd = new T2LV.Tyrian.GameData(dir!);
        var ep = gd.Episodes.Find(e => e.Number == epNum)!;
        var lv = gd.LoadLevel(ep, fileNum);
        var edd = gd.GetEnemyData(ep);
        var objs = T2LV.Tyrian.ObjectPlacer.Place(gd, ep, lv, edd);
        var pal = gd.Palettes.Get(0);

        // distinct enemies by (sprite, esize), with a resolved sheet
        var seen = new HashSet<(int, int)>();
        var distinct = new List<T2LV.Tyrian.PlacedObject>();
        foreach (var o in objs)
            if (o.Sheet != null && o.SpriteIndex > 0 && o.SpriteIndex != 999 && seen.Add((o.SpriteIndex, o.Esize)))
                distinct.Add(o);

        // grid layout
        int cols = 10, cellW = 30, cellH = 36, scale = 4;
        int rows = (distinct.Count + cols - 1) / cols;
        int gw = cols * cellW, gh = rows * cellH;
        var buf = new uint[gw * gh];
        for (int i = 0; i < buf.Length; i++) buf[i] = T2LV.Render.Gfx.Rgba(20, 20, 28);

        void BlitG(T2LV.Tyrian.CompShapes sheet, int index, int x, int y)
        {
            var s = sheet.Decode(index);
            if (s == null) return;
            for (int sy = 0; sy < s.H; sy++)
            {
                int dy = y + sy; if (dy < 0 || dy >= gh) continue;
                for (int sx = 0; sx < s.W; sx++)
                {
                    byte v = s.Pixels[sy * s.W + sx]; if (v == 0) continue;
                    int dx = x + sx; if (dx < 0 || dx >= gw) continue;
                    buf[dy * gw + dx] = pal[v];
                }
            }
        }

        for (int i = 0; i < distinct.Count; i++)
        {
            var o = distinct[i];
            int cellX = (i % cols) * cellW, cellY = (i / cols) * cellH;
            // cell reference point (matches in-game ex,ey within the 24x28 box)
            int rx = cellX + 12, ry = cellY + 12;
            if (o.Esize == 1)
            {
                BlitG(o.Sheet!, o.SpriteIndex, rx - 6, ry - 7);
                BlitG(o.Sheet!, o.SpriteIndex + 1, rx + 6, ry - 7);
                BlitG(o.Sheet!, o.SpriteIndex + 19, rx - 6, ry + 7);
                BlitG(o.Sheet!, o.SpriteIndex + 20, rx + 6, ry + 7);
            }
            else BlitG(o.Sheet!, o.SpriteIndex, rx - 6, ry - 7);
            // cell border
            for (int x = 0; x < cellW; x++) { buf[cellY * gw + cellX + x] = 0xFF404050; }
            for (int y = 0; y < cellH; y++) { buf[(cellY + y) * gw + cellX] = 0xFF404050; }
        }

        // print decoded heights for first 16 distinct
        foreach (var o in distinct.Take(16))
        {
            string dims;
            if (o.Esize == 1)
                dims = string.Join(",", new[] { 0, 1, 19, 20 }.Select(off => { var s = o.Sheet!.Decode(o.SpriteIndex + off); return s == null ? "null" : $"{s.W}x{s.H}"; }));
            else { var s = o.Sheet!.Decode(o.SpriteIndex); dims = s == null ? "null" : $"{s.W}x{s.H}"; }
            Console.WriteLine($"  id{o.EnemyId} esize{o.Esize} base{o.SpriteIndex} cat={o.Cat}: {dims}");
        }

        // magnify
        var mag = new uint[gw * scale * gh * scale];
        for (int y = 0; y < gh * scale; y++)
            for (int x = 0; x < gw * scale; x++)
                mag[y * gw * scale + x] = buf[(y / scale) * gw + (x / scale)];
        string path = Path.Combine(outDir, "sprites.png");
        T2LV.Util.Png.WriteRgba(path, gw * scale, gh * scale, mag);
        Console.WriteLine($"{distinct.Count} distinct enemy sprites -> {path} ({gw * scale}x{gh * scale})");
        return 0;
    }

    static int FindEnemy(string[] args)
    {
        int gi = Array.IndexOf(args, "--findenemy");
        int epNum = gi + 1 < args.Length ? int.Parse(args[gi + 1]) : 1;
        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        var gd = new T2LV.Tyrian.GameData(dir!);
        var ep = gd.Episodes.Find(e => e.Number == epNum)!;
        var (raw, blockStart) = T2LV.Tyrian.EnemyData.LocateBlock(gd.DataDir, ep);
        int computed = blockStart + new T2LV.Tyrian.EnemyData().PreEnemyOffset();
        Console.WriteLine($"ep{epNum} blockStart={blockStart} computedEnemies={computed} fileLen={raw.Length}");

        // Lightweight scorer: read shapebank(off+63), esize(off+20), egr0(off+21) per 77-byte record.
        int Score(int off)
        {
            int good = 0;
            for (int i = 0; i < 600; i++)
            {
                int rec = off + i * 77;
                if (rec + 77 > raw.Length) break;
                int esize = raw[rec + 20];
                int bank = raw[rec + 63];
                int egr0 = raw[rec + 21] | (raw[rec + 22] << 8);
                if (esize <= 1 && bank >= 1 && bank <= 36 && egr0 > 0 && egr0 < 2000) good++;
            }
            return good;
        }

        // Find the lowest delta where the score becomes high (the table-start boundary).
        int firstHigh = int.MinValue;
        for (int delta = -8000; delta <= 8000; delta++)
        {
            int off = computed + delta;
            if (off < 0 || off + 600 * 77 > raw.Length) continue;
            if (Score(off) >= 580) { firstHigh = delta; break; }
        }
        Console.WriteLine($"  first high-score delta: {firstHigh}  -> enemiesOffset={computed + firstHigh}");
        // Print scores in a fine window around the boundary to confirm the low->high edge.
        if (firstHigh != int.MinValue)
        {
            for (int d = firstHigh - 5; d <= firstHigh + 2; d++)
                Console.WriteLine($"     delta {d}: score {Score(computed + d)}");
        }
        return 0;
    }

    static int DumpSelfTest()
    {
        string? dir = T2LV.Tyrian.GameData.FindDataDir();
        if (dir == null) { Console.Error.WriteLine("Could not find Tyrian 2000 data files."); return 1; }
        Console.WriteLine($"Data dir: {dir}");
        var gd = new T2LV.Tyrian.GameData(dir);
        Console.WriteLine($"Palettes: {gd.Palettes.Count}");
        foreach (var ep in gd.Episodes)
        {
            Console.WriteLine($"\n== Episode {ep.Number}: lvlNum={ep.Container.LvlNum} sections={ep.Container.SectionCount} scriptLevels={ep.ScriptLevels.Count}");
            foreach (var item in ep.Levels)
                Console.WriteLine($"   {item.Display}");

            // Parse first section as a sanity check.
            if (ep.Levels.Count > 0)
            {
                var lv = gd.LoadLevel(ep, ep.Levels[0].FileNum);
                var sh = gd.GetShapeTable(lv.ShapeChar);
                int nonBlank = 0; foreach (var t in sh.Tiles) if (t != null) nonBlank++;
                Console.WriteLine($"   -> section {lv.FileNum}: shape='{lv.ShapeChar}' mapX={lv.MapX},{lv.MapX2},{lv.MapX3} enemies={lv.LevelEnemy.Length} events={lv.Events.Length} shapeTiles={nonBlank}/600");
            }
        }
        return 0;
    }
}
