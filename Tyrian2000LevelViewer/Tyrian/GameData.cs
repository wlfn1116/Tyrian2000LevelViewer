namespace T2LV.Tyrian;

public sealed class LevelListItem
{
    public int FileNum;
    public string Name = "";
    public bool BonusLevel;
    public string Display => $"{FileNum:00}  {(string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name.Trim())}" + (BonusLevel ? "  [bonus]" : "");
}

public sealed class EpisodeInfo
{
    public int Number;                 // 1..5
    public LevelContainer Container = null!;
    public List<LevelEntry> ScriptLevels = new();
    public List<LevelListItem> Levels = new();
}

/// <summary>
/// Root of the loaded Tyrian 2000 data set: locates the data directory,
/// loads the palette, scans the 5 episodes, and caches shape tables.
/// </summary>
public sealed class GameData
{
    public string DataDir { get; }
    public PaletteSet Palettes { get; }
    public readonly List<EpisodeInfo> Episodes = new();

    private readonly Dictionary<char, ShapeTable> _shapeCache = new();
    private readonly Dictionary<char, CompShapes?> _newshCache = new();
    private readonly Dictionary<int, EnemyData> _enemyCache = new();
    private readonly Dictionary<int, WeaponData> _weaponCache = new();
    private MainShapes? _main;

    // shapeFile[] from lvlmast.c: enemy shape-bank (1-based) -> newsh file char.
    private static readonly char[] ShapeFile =
    {
        '2','4','7','8','A','B','C','D','E','F','G','H','I','J','K','L','M','N',
        'O','P','Q','R','S','T','U','5','#','V','0','@','3','^','5','9','\'','%'
    };

    public GameData(string dataDir)
    {
        DataDir = dataDir;
        Palettes = PaletteSet.Load(Path.Combine(dataDir, "palette.dat"));

        for (int ep = 1; ep <= 5; ep++)
        {
            string lvlPath = Path.Combine(dataDir, $"tyrian{ep}.lvl");
            if (!File.Exists(lvlPath)) continue;

            var info = new EpisodeInfo { Number = ep, Container = new LevelContainer(lvlPath) };

            string scriptPath = Path.Combine(dataDir, $"levels{ep}.dat");
            if (File.Exists(scriptPath))
            {
                try { info.ScriptLevels = EpisodeScript.ParseLevels(scriptPath); }
                catch { /* tolerate a malformed script */ }
            }

            // Map lvlFileNum -> first name/bonus seen in the script.
            var nameByFile = new Dictionary<int, LevelEntry>();
            foreach (var e in info.ScriptLevels)
                if (!nameByFile.ContainsKey(e.LvlFileNum))
                    nameByFile[e.LvlFileNum] = e;

            int sections = info.Container.SectionCount;
            for (int f = 1; f <= sections; f++)
            {
                var item = new LevelListItem { FileNum = f };
                if (nameByFile.TryGetValue(f, out var e))
                {
                    item.Name = e.Name;
                    item.BonusLevel = e.BonusLevel || e.NormalBonus;
                }
                info.Levels.Add(item);
            }

            Episodes.Add(info);
        }
    }

    public Level LoadLevel(EpisodeInfo ep, int fileNum) => Level.Parse(ep.Container, fileNum);

    public MainShapes Main => _main ??= MainShapes.Load(Path.Combine(DataDir, "tyrian.shp"));

    public EnemyData GetEnemyData(EpisodeInfo ep)
    {
        if (_enemyCache.TryGetValue(ep.Number, out var ed)) return ed;
        ed = EnemyData.Load(DataDir, ep);
        _enemyCache[ep.Number] = ed;
        return ed;
    }

    /// <summary>newsh file for a 1-based enemy shape bank (1..36), cached.</summary>
    public CompShapes? GetNewsh(int bank)
    {
        if (bank < 1 || bank > ShapeFile.Length) return null;
        return GetNewshChar(ShapeFile[bank - 1]);
    }

    /// <summary>newsh file by its literal file character (the engine's JE_loadCompShapes).</summary>
    public CompShapes? GetNewshChar(char fileChar)
    {
        char c = char.ToLowerInvariant(fileChar);
        if (_newshCache.TryGetValue(c, out var cs)) return cs;
        string path = Path.Combine(DataDir, $"newsh{c}.shp");
        cs = File.Exists(path) ? CompShapes.LoadFile(path) : null;
        _newshCache[c] = cs;
        return cs;
    }

    public WeaponData GetWeapons(EpisodeInfo ep)
    {
        if (_weaponCache.TryGetValue(ep.Number, out var wd)) return wd;
        wd = WeaponData.Load(DataDir, ep);
        _weaponCache[ep.Number] = wd;
        return wd;
    }

    /// <summary>Resolve a shape bank to a sprite sheet given the 4 currently active event-5 banks.</summary>
    public CompShapes? ResolveBankSheet(int shapeBank, int[] activeBanks)
    {
        if (shapeBank == 21) return Main.CoinsGems;
        if (shapeBank == 26) return Main.PowerUps;
        for (int i = 0; i < activeBanks.Length; i++)
            if (activeBanks[i] == shapeBank)
                return GetNewsh(shapeBank);
        // Fall back: try loading directly (some levels reference a bank without a tracked slot).
        return GetNewsh(shapeBank);
    }

    public ShapeTable GetShapeTable(char shapeChar)
    {
        char key = char.ToLowerInvariant(shapeChar);
        if (_shapeCache.TryGetValue(key, out var t)) return t;
        string path = Path.Combine(DataDir, $"shapes{key}.dat");
        var table = ShapeTable.Load(path, key);
        _shapeCache[key] = table;
        return table;
    }

    /// <summary>Find a Tyrian folder from a user selection or the current application path.</summary>
    public static string? FindDataDir(string? startHint = null)
    {
        var candidates = new List<string>();
        void AddProbe(string? baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return;
            var d = new DirectoryInfo(baseDir);
            for (int up = 0; up < 8 && d != null; up++, d = d.Parent!)
                candidates.Add(d.FullName);
        }

        if (!string.IsNullOrEmpty(startHint) && Directory.Exists(startHint))
        {
            candidates.Add(startHint);
            try { candidates.AddRange(Directory.EnumerateDirectories(startHint)); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        AddProbe(Environment.CurrentDirectory);
        AddProbe(AppContext.BaseDirectory);

        foreach (var c in candidates)
            if (File.Exists(Path.Combine(c, "tyrian1.lvl")) && File.Exists(Path.Combine(c, "palette.dat")))
                return c;
        return null;
    }
}
