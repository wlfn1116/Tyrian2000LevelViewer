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
/// One reference window's frame. ImGui would keep these in imgui.ini, which this app turns off
/// so that everything it remembers is in one file; without them a window you had sized to suit
/// you opened back at its built-in default every run.
/// </summary>
public sealed class WindowGeom
{
    public string Id { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
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
    /// <summary>Which difficulty the analysis panel measures at (0 wimp .. 10). Deliberately
    /// separate from the playback difficulty: comparing levels at Impossible should not disturb
    /// the run being watched.</summary>
    public int AnalysisDifficulty { get; set; } = 2;
    public float SpriteListWidth { get; set; }          // 0 = default
    public float EnemyListWidth { get; set; }
    public float ItemListWidth { get; set; }
    public int EnemyBrowseMode { get; set; }            // 0 = entries, 1 = assemblies
    public bool? AssembliesUnique { get; set; }         // fold repeats of one body; null = on
    public bool SpritesGapless { get; set; }            // pack the sprite grid with no gaps
    public int SpritesColumns { get; set; }             // 0 = fit to the panel width
    public bool? SpritesCheckerboard { get; set; }      // null = never saved, defaults on
    public bool SpritesNumbers { get; set; }            // print each cell's sprite index on it
    /// <summary>Shop tables with the widescreen fork's post-load pass applied; null = never
    /// saved, and the fork's view is the default.</summary>
    public bool? ItemsFork { get; set; }
    public bool? EnemyMotion { get; set; }              // velocity arrows over the enemy stage; null = on
    public bool CubesByLevel { get; set; }              // ... listing cubes under their level
    public float CubeListWidth { get; set; }            // ... width of its list column; 0 = default
    public bool AllEpisodes { get; set; }               // browse every episode at once
    /// <summary>Bitmask of the EdgeKinds the level tree draws; 0 = never saved.</summary>
    public int TreeEdgeMask { get; set; }

    // --- Audio ---
    public bool ShowMusic { get; set; }                 // the music player window
    public bool ShowSounds { get; set; }                // the sound player window
    public float MusicListWidth { get; set; }           // 0 = default
    public float SoundListWidth { get; set; }
    public bool? AudioEnabled { get; set; }             // master switch; null = never saved, on
    public int MusicVolume { get; set; } = 191;         // the engine's own 0..255 scale
    public int FxVolume { get; set; } = 191;
    public bool? GameMusic { get; set; }                // play the level's song during playback
    public bool? GameSounds { get; set; }               // play the simulation's sound queue
    public int MusicDevice { get; set; }                // 0 OPL, 1 FluidSynth, 2 native MIDI
    public string? SoundFont { get; set; }              // .sf2 for FluidSynth
    public bool XmasVoices { get; set; }                // voicesc.snd instead of voices.snd
    /// <summary>Music-timeline channel height in pixels; 0 = fit the nine into the panel.</summary>
    public float MusicLaneHeight { get; set; }
    /// <summary>Timeline zoom in the music window, pixels per Loudness tick; 0 = fit.</summary>
    public float MusicZoom { get; set; }
    public int MusicSelected { get; set; }              // last song browsed
    public int SoundSelected { get; set; }              // last sound browsed
    /// <summary>How many playback-HUD sections <see cref="PbSections"/> was saved with. A newer
    /// build's extra sections keep their own defaults instead of reading a 0 bit that only means
    /// "that section did not exist yet".</summary>
    public int PbSectionCount { get; set; }

    /// <summary>The palette gameplay always runs in: JE_loadPic(3) -> pcxpal[2] = 5.</summary>
    public const int GamePalette = 5;

    public bool HasView { get; set; }
    public float Zoom { get; set; } = 1f;
    public float ScrollX { get; set; }
    public float ScrollY { get; set; }

    public float LevelsHeight { get; set; } = 170f;
    public float LayersHeight { get; set; }          // 0 = fit to content
    public List<LayerState> Layers { get; set; } = new();

    /// <summary>The reference windows' own frames, keyed by the id RefBegin opens them with.</summary>
    public List<WindowGeom> RefWindows { get; set; } = new();

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
