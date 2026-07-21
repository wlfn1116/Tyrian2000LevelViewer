namespace T2A.Tyrian;

public struct EventRec
{
    public ushort Time;
    public byte Type;
    public short Dat;    // dat
    public short Dat2;   // dat2
    public sbyte Dat3;
    public sbyte Dat5;   // note: on disk order is dat3, dat5, dat6, dat4
    public sbyte Dat6;
    public byte Dat4;
}

/// <summary>
/// The engine flags that decide the in-game draw order of the backgrounds vs the
/// enemy bands. Defaults per tyrian2.c:1997-2002; changed by events 21/22/42
/// (background3over), 43 (background2over), 48 (opaque BG2), 28/29
/// (topEnemyOver), 73 (skyEnemyOverAll).
/// </summary>
public struct LevelStartFlags
{
    public int Background2Over;    // 0/3 = early, 1 = over ground, 2 = frontmost; other values disable it
    public int Background3Over;    // 0 = over sky enemies (default), 1 = over everything, 2 = under sky enemies
    public bool TopEnemyOver;      // top band drawn over bg3over==1
    public bool SkyEnemyOverAll;   // sky band drawn last of all
    public bool Background2NotTransparent; // event 48 disables the default indexed blend

    public static LevelStartFlags Defaults => new() { Background2Over = 1 };
}

public readonly record struct ScreenFilterState(bool Active, int Hue, int Brightness);

/// <summary>
/// A single parsed level section from a tyrian%d.lvl file.
/// Layout: tyrian2.c:3106-3252.
/// </summary>
public sealed class Level
{
    public const int Bg1Cols = 14, Bg1Rows = 300;
    public const int Bg2Cols = 14, Bg2Rows = 600;
    public const int Bg3Cols = 15, Bg3Rows = 600;

    public int FileNum;
    public byte MapFileChar;
    public char ShapeChar;       // -> shapes%c.dat
    public ushort MapX, MapX2, MapX3;
    public ushort[] LevelEnemy = Array.Empty<ushort>();
    public EventRec[] Events = Array.Empty<EventRec>();

    // mapSh[layer][0..127] = 1-based shape id (big-endian on disk).
    public readonly ushort[][] MapSh = { new ushort[128], new ushort[128], new ushort[128] };

    // Raw tile-index cells per layer (row-major). Each cell indexes mapSh[layer][cell].
    public byte[] Bg1 = new byte[Bg1Cols * Bg1Rows];
    public byte[] Bg2 = new byte[Bg2Cols * Bg2Rows];
    public byte[] Bg3 = new byte[Bg3Cols * Bg3Rows];

    public int SectionStart;     // file offset where this section began

    public static Level Parse(LevelContainer container, int fileNum)
    {
        int start = container.SectionOffset(fileNum);
        var r = new ByteReader(container.Raw, start);
        var lv = new Level { FileNum = fileNum, SectionStart = start };

        lv.MapFileChar = r.U8();
        lv.ShapeChar = (char)r.U8();
        lv.MapX = r.U16();
        lv.MapX2 = r.U16();
        lv.MapX3 = r.U16();

        int enemyMax = r.U16();
        lv.LevelEnemy = new ushort[enemyMax];
        for (int i = 0; i < enemyMax; i++) lv.LevelEnemy[i] = r.U16();

        int maxEvent = r.U16();
        lv.Events = new EventRec[maxEvent];
        for (int i = 0; i < maxEvent; i++)
        {
            lv.Events[i] = new EventRec
            {
                Time = r.U16(),
                Type = r.U8(),
                Dat = r.S16(),
                Dat2 = r.S16(),
                Dat3 = r.S8(),
                Dat5 = r.S8(),
                Dat6 = r.S8(),
                Dat4 = r.U8(),
            };
        }

        // Map shape lookup table: 3 layers x 128 big-endian u16.
        for (int layer = 0; layer < 3; layer++)
            for (int i = 0; i < 128; i++)
                lv.MapSh[layer][i] = r.U16BE();

        r.Read(lv.Bg1, Bg1Cols * Bg1Rows);
        r.Read(lv.Bg2, Bg2Cols * Bg2Rows);
        r.Read(lv.Bg3, Bg3Cols * Bg3Rows);

        return lv;
    }

    /// <summary>
    /// Resolve a cell byte to a 1-based shape id for the given layer, honoring the
    /// reserved-index rules (layer1 idx 71, layer2 idx>=70 are forced empty).
    /// Returns 0 for "no tile".
    /// </summary>
    public int ResolveShapeId(int layer, byte cell)
    {
        if (layer == 1 && cell == 71) return 0;
        if (layer == 2 && cell >= 70) return 0;
        if (cell > 71) return 0;
        return MapSh[layer][cell];
    }

    /// <summary>
    /// The draw-order flags in effect when the level starts playing: engine defaults,
    /// overridden by every flag event due in the first engine tick.
    /// Continuous timelines carry later changes per section; raw-grid rendering uses
    /// this initial state because it has no single time axis.
    /// </summary>
    public LevelStartFlags ComputeStartFlags()
    {
        var f = LevelStartFlags.Defaults;
        foreach (var e in Events)
        {
            // JE_eventSystem drains every event at time zero before the first frame is
            // drawn. A spawn does not end that batch, so a later time-zero flag must
            // still affect the initial stack.
            if (e.Time != 0) break;
            switch (e.Type)
            {
                case 21: f.Background3Over = 1; break;
                case 22: f.Background3Over = 0; break;
                case 42: f.Background3Over = 2; break;
                case 43: f.Background2Over = unchecked((byte)e.Dat); break;
                case 48: f.Background2NotTransparent = true; break;
                case 28: f.TopEnemyOver = false; break;
                case 29: f.TopEnemyOver = true; break;
                case 73: f.SkyEnemyOverAll = e.Dat == 1; break;
            }
        }
        return f;
    }

    public byte[] CellsFor(int layer) => layer == 0 ? Bg1 : layer == 1 ? Bg2 : Bg3;
    public static int ColsFor(int layer) => layer == 0 ? Bg1Cols : layer == 1 ? Bg2Cols : Bg3Cols;
    public static int RowsFor(int layer) => layer == 0 ? Bg1Rows : layer == 1 ? Bg2Rows : Bg3Rows;
}
