using System.Text.Json;
using System.Text.Json.Serialization;

namespace T2LV;

public sealed class LayerState
{
    public string Id { get; set; } = "";
    public bool Visible { get; set; } = true;
    public int Alpha { get; set; } = 255;
}

/// <summary>
/// Persisted UI state, so the viewer reopens exactly where it was left. Stored as JSON
/// under %LOCALAPPDATA%/Tyrian2000LevelViewer/settings.json.
/// </summary>
public sealed class AppSettings
{
    public string? DataDir { get; set; }
    public string? ExportDir { get; set; }             // where the last PNG/screenshot was saved
    public int EpisodeIdx { get; set; }
    public int LevelFileNum { get; set; } = 1;
    public int Palette { get; set; } = GamePalette;
    public int ObjMode { get; set; }
    public int ScrollMode { get; set; }
    public bool UniformTextureScale { get; set; }
    public bool GameLayerOrder { get; set; } = true;   // auto-apply the level's in-game layer order
    public bool SimExtendedView { get; set; }
    public bool Widescreen { get; set; }               // true-widescreen playback (356px playfield)
    public bool ExpandedParallax { get; set; }         // widescreen sub-option: wider all-layer parallax sweep
    public bool MirrorLayers { get; set; } = true;     // widescreen sub-option: mirror layers past their side edges
    public bool WideStarfield { get; set; } = true;    // the build's rewritten starfield (either mode)
    public bool ShowScreenFilter { get; set; } = true;
    public bool ShowSmoothies { get; set; } = true; // retained JSON name: terrain smoothies
    // Nullable so an older settings file can inherit its former broad smoothie toggle.
    public bool? ShowSpotlight { get; set; }
    public bool? ShowScreenFlip { get; set; }
    public bool ShowBossBars { get; set; } = true;
    public bool ClickKill { get; set; }                 // left-click damages the enemy under the cursor
    public bool ClickKillInstant { get; set; } = true;  // ... for all of its armor
    public int ClickKillDamage { get; set; } = 10;      // ... or for this much
    public bool ClickKillExplosions { get; set; } = true;
    /// <summary>Bitmask of the unfolded playback-HUD sections (PbSec); -1 = never saved.</summary>
    public int PbSections { get; set; } = -1;
    public bool PbPinRight { get; set; }                // dock the playback HUD to the view's right edge
    public bool PbFitAroundHud { get; set; }            // "UI fit": fit the view clear of that HUD
    public bool ShowTree { get; set; }                  // the episode level-tree window
    public bool ShowCubes { get; set; }                 // the datacube reader window
    public bool ShowSprites { get; set; }               // the sprite-bank browser
    public bool ShowEnemies { get; set; }               // the enemy / assembly browser
    public bool ShowItems { get; set; }                 // the ship & item database
    public bool ShowAnalysis { get; set; }              // the level analysis panel
    public float SpriteListWidth { get; set; }          // 0 = default
    public float EnemyListWidth { get; set; }
    public float ItemListWidth { get; set; }
    public int EnemyBrowseMode { get; set; }            // 0 = entries, 1 = assemblies
    public bool? AssembliesUnique { get; set; }         // fold repeats of one body; null = on
    public bool SpritesGapless { get; set; }            // pack the sprite grid with no gaps
    public int SpritesColumns { get; set; }             // 0 = fit to the panel width
    public bool? SpritesCheckerboard { get; set; }      // null = never saved, defaults on
    public bool CubesByLevel { get; set; }              // ... listing cubes under their level
    public float CubeListWidth { get; set; }            // ... width of its list column; 0 = default
    public bool AllEpisodes { get; set; }               // browse every episode at once
    /// <summary>Bitmask of the EdgeKinds the level tree draws; 0 = never saved.</summary>
    public int TreeEdgeMask { get; set; }

    /// <summary>The palette gameplay always runs in: JE_loadPic(3) -> pcxpal[2] = 5.</summary>
    public const int GamePalette = 5;

    public bool HasView { get; set; }
    public float Zoom { get; set; } = 1f;
    public float ScrollX { get; set; }
    public float ScrollY { get; set; }

    public float LevelsHeight { get; set; } = 170f;
    public float LayersHeight { get; set; }          // 0 = fit to content
    public List<LayerState> Layers { get; set; } = new();

    public int WinW { get; set; } = 1280;
    public int WinH { get; set; } = 800;
    public int WinX { get; set; } = int.MinValue;    // MinValue = centered / unset
    public int WinY { get; set; } = int.MinValue;
    public bool WinMaximized { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tyrian2000LevelViewer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonContext.Default.AppSettings)
                    ?? new AppSettings();
        }
        catch { /* corrupt/locked settings -> start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppJsonContext.Default.AppSettings));
        }
        catch { /* best-effort */ }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppJsonContext : JsonSerializerContext;
