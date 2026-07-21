using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// Saving sprites out of the browser: one sprite on its own, or a whole bank as a sheet.
///
/// Same shape as <see cref="StartAudioExport"/> -- the Save-As box pumps its own message loop
/// and has to run on an STA thread, so the picker and the file write share one background
/// thread and the frame keeps running while the box is open. What differs is where the pixels
/// come from: they are built HERE, on the UI thread, before the thread starts. Decoding a bank
/// touches <see cref="GameData"/>'s lazily-filled caches, and doing that from a second thread
/// while the frame is drawing out of the same caches is the kind of race that shows up once a
/// month and never in a repro.
///
/// Everything is written at 1:1. These are 12x14 pixel sprites drawn for a 320x200 screen;
/// the browser's zoom is for looking at them, and a file that has been point-scaled 4x is
/// worse than useless as a source image.
/// </summary>
public sealed unsafe partial class App
{
    /// <summary>Where a bank's sheet wraps when the grid is on "fit" -- 19 is the stride the
    /// game's own sheets are laid out on, which is what lines the 2x2 icons up into whole
    /// pictures. A fixed column count in the browser is honoured instead.</summary>
    private const int SheetDefaultCols = 19;

    private volatile bool _sprExpActive;

    // --- "export all banks": the folder pick, then one sheet a frame ---
    /// <summary>The chooser's answer, handed back from its thread. Null = nothing waiting,
    /// empty = cancelled.</summary>
    private volatile string? _sprAllPicked;
    private volatile bool _sprAllPicking;
    private List<SpriteSource>? _sprAllQueue;      // non-null while a run is in flight
    private string _sprAllDir = "";
    private int _sprAllAt, _sprAllWritten, _sprAllFailed, _sprAllPalette, _sprAllCols;

    /// <summary>True from the moment the "all banks" button is pressed until the last sheet
    /// is written.</summary>
    private bool SpriteExportAllBusy => _sprAllPicking || _sprAllQueue != null;

    /// <summary>True while a sprite Save-As box is open or its file is being written. Shares
    /// <see cref="_exportActive"/> with the level/screenshot saves on purpose: one PNG write
    /// at a time, and one status line to report it.</summary>
    private bool SpriteExportBusy =>
        _sprExpActive || _exportActive || _pickActive || SpriteExportAllBusy;

    /// <summary>
    /// Ask where to put a finished image and write it there. <paramref name="pixels"/> is
    /// already built and owned by the export from here on.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void StartSpritePngExport(string suggested, int w, int h, uint[] pixels)
    {
        if (SpriteExportBusy || w <= 0 || h <= 0) return;

        _sprExpActive = true;
        _status = "Choose where to save " + suggested + " ...";
        IntPtr owner = NativeFileDialog.ForegroundWindow();
        string startDir = DefaultExportDir();

        var t = new Thread(() =>
        {
            try
            {
                string? path = NativeFileDialog.SaveFileBlocking(startDir, suggested, owner, "Export PNG");
                if (path == null) { _exportDone = "Export cancelled."; return; }
                if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
                _exportDir = Path.GetDirectoryName(path) ?? _exportDir;
                Util.Png.WriteRgba(path, w, h, pixels);
                _exportDone = $"Saved {path} ({w}x{h})";
            }
            catch (Exception e) { _exportDone = "PNG save failed: " + e.Message; }
            finally { _sprExpActive = false; }
        })
        { IsBackground = true, Name = "SpritePngExport" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>One sprite, its own size, everything the format calls transparent left clear.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ExportOneSprite(SpriteSource src, int palette, int index)
    {
        if (_gd == null || SpriteExportBusy) return;
        Sprite?[] sprites;
        try { sprites = SpritesOf(_gd, src); }
        catch (Exception e) { _status = "Could not read that bank: " + e.Message; return; }
        if (index < 0 || index >= sprites.Length || sprites[index] is not { } s || s.W <= 0 || s.H <= 0)
        { _status = "That slot is empty -- nothing to save."; return; }

        var pal = _gd.Palettes.Get(palette);
        var rgba = new uint[s.W * s.H];
        Blit(rgba, s.W, s, pal, 0, 0);
        StartSpritePngExport($"{SpriteFileStem(src)}_pal{palette}_{index:000}.png", s.W, s.H, rgba);
    }

    /// <summary>
    /// The whole bank on one grid, cells butted together so the result is a usable sheet.
    /// Every sprite sits at its cell's top-left -- where the game blits it -- so neighbouring
    /// tiles join up, and empty slots stay transparent so that cell arithmetic still finds a
    /// sprite by its own number.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ExportSpriteSheet(SpriteSource src, int palette, int cols)
    {
        if (_gd == null || SpriteExportBusy) return;
        if (!BuildSpriteSheet(src, palette, cols, out int w, out int h, out var rgba, out string why))
        { _status = why; return; }
        StartSpritePngExport($"{SpriteFileStem(src)}_pal{palette}_sheet.png", w, h, rgba);
    }

    /// <summary>
    /// Lay one bank out as a sheet. Returns false with a reason rather than throwing, so the
    /// batch below can skip a bank the data folder does not have and carry on.
    /// </summary>
    private bool BuildSpriteSheet(SpriteSource src, int palette, int cols,
        out int w, out int h, out uint[] rgba, out string why)
    {
        w = h = 0;
        rgba = Array.Empty<uint>();
        Sprite?[] sprites;
        try { sprites = SpritesOf(_gd!, src); }
        catch (Exception e) { why = "Could not read that bank: " + e.Message; return false; }

        int first = src.FirstIndex;
        int count = Math.Max(0, sprites.Length - first);
        if (count == 0) { why = "That bank has nothing in it."; return false; }

        // The cell is the bank's largest sprite, exactly as the sprite atlas sizes it, so no sprite
        // can spill into its neighbour.
        int cw = 1, ch = 1;
        for (int i = first; i < sprites.Length; i++)
        {
            if (sprites[i] is not { } s || s.W <= 0 || s.H <= 0) continue;
            cw = Math.Max(cw, s.W);
            ch = Math.Max(ch, s.H);
        }

        cols = Math.Max(1, cols);
        int rows = (count + cols - 1) / cols;
        w = cols * cw; h = rows * ch;
        // 600 tiles of 24x28 in one column would be 16,800px tall; a sane ceiling costs
        // nothing and keeps a mis-set column count from asking for a gigabyte.
        if ((long)w * h > 64_000_000L)
        { why = $"That sheet would be {w}x{h} -- pick a column count that fits."; return false; }

        var pal = _gd!.Palettes.Get(palette);
        rgba = new uint[w * h];
        for (int i = first; i < sprites.Length; i++)
        {
            if (sprites[i] is not { } s || s.W <= 0 || s.H <= 0) continue;
            int k = i - first;
            Blit(rgba, w, s, pal, (k % cols) * cw, (k / cols) * ch);
        }
        why = "";
        return true;
    }

    /// <summary>
    /// Every bank in the browser's list, each as its own sheet, into one folder. Only the
    /// chooser goes on a thread: the sheets themselves are laid out by the pump below, on the
    /// UI thread, for the reason at the top of this file.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void StartSpriteExportAll(int palette, int cols)
    {
        if (_gd == null || SpriteExportBusy) return;

        _sprAllPicking = true;
        _sprAllPicked = null;
        _sprAllPalette = palette;
        _sprAllCols = cols;
        _status = "Choose where to put the sprite sheets...";

        IntPtr owner = NativeFileDialog.ForegroundWindow();
        string startDir = DefaultExportDir();

        var t = new Thread(() =>
        {
            try
            {
                _sprAllPicked = NativeFileDialog.PickFolderBlocking(startDir, owner,
                    "Export every sprite bank as a sheet - pick a folder") ?? "";
            }
            catch { _sprAllPicked = ""; }
        })
        { IsBackground = true, Name = "SpriteExportAllPick" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>
    /// Drives an "export all banks" run: collect the chooser's answer, then write one sheet a
    /// frame. Laying them out has to happen on this thread -- decoding a bank fills
    /// <see cref="GameData"/>'s caches, which the frame is drawing out of -- and doing all
    /// sixty-odd in one go would both freeze for as long as it took and hold every sheet in
    /// memory at once. One a frame is over in about a second and the window keeps drawing,
    /// which is why this needs no meter of its own; the status line counts them off.
    /// </summary>
    private void PumpSpriteExportAll()
    {
        if (_sprAllPicked is { } picked)
        {
            _sprAllPicked = null;
            _sprAllPicking = false;
            if (picked.Length == 0) { _status = "Export cancelled."; return; }
            _sprAllDir = picked;
            _exportDir = picked;                  // where the next save opens
            _sprAllQueue = AllSpriteSources();
            _sprAllAt = _sprAllWritten = _sprAllFailed = 0;
        }
        if (_sprAllQueue is not { } queue) return;
        // The data folder went away under the run (it can be changed while this is going).
        // Abandon it rather than leaving a queue that can never drain and a button that can
        // never be pressed again.
        if (_gd == null)
        {
            _sprAllQueue = null;
            _status = $"Sprite export stopped after {_sprAllWritten}: the data folder changed.";
            return;
        }

        if (_sprAllAt >= queue.Count)
        {
            _sprAllQueue = null;
            _status = $"Wrote {_sprAllWritten} sprite sheet{(_sprAllWritten == 1 ? "" : "s")} " +
                      $"to {FolderLabel(_sprAllDir)}" +
                      (_sprAllFailed > 0 ? $"; {_sprAllFailed} could not be read." : ".");
            return;
        }

        var src = queue[_sprAllAt++];
        try
        {
            if (BuildSpriteSheet(src, _sprAllPalette, _sprAllCols, out int w, out int h,
                    out var rgba, out _))
            {
                Util.Png.WriteRgba(Path.Combine(_sprAllDir,
                    $"{SpriteFileStem(src)}_pal{_sprAllPalette}_sheet.png"), w, h, rgba);
                _sprAllWritten++;
            }
            else _sprAllFailed++;
        }
        catch { _sprAllFailed++; }
        _status = $"Writing sprite sheets... {_sprAllAt}/{queue.Count}";
    }

    /// <summary>One sprite into a target buffer. Colour 0 is the formats' transparency, not
    /// palette entry 0 -- same rule as <see cref="SpriteAtlas"/> and <see cref="SpriteImage"/>.</summary>
    private static void Blit(uint[] dst, int dstW, Sprite s, uint[] pal, int atX, int atY)
    {
        for (int y = 0; y < s.H; y++)
        {
            int srcRow = y * s.W, dstRow = (atY + y) * dstW + atX;
            for (int x = 0; x < s.W; x++)
            {
                byte c = s.Pixels[srcRow + x];
                if (c != 0) dst[dstRow + x] = pal[c];
            }
        }
    }

    /// <summary>What a bank's files are named after: where it lives on disk, so an exported
    /// sheet can still be traced back to the bank it came out of.</summary>
    private static string SpriteFileStem(SpriteSource src) => src.Store switch
    {
        SpriteStore.Newsh =>
            $"bank{src.Index:00}_newsh{char.ToLowerInvariant(GameData.ShapeBankChar(src.Index))}",
        SpriteStore.NewshFile => $"newsh{char.ToLowerInvariant(src.FileChar)}",
        SpriteStore.MainSheet => $"{(src.Xmas ? "tyrianc" : "tyrian")}_sheet{src.Index:00}",
        SpriteStore.MainBank => $"{(src.Xmas ? "tyrianc" : "tyrian")}_bank{src.Index:00}",
        _ => $"shapes{char.ToLowerInvariant(src.FileChar)}",
    };
}
