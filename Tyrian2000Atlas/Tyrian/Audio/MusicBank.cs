namespace T2A.Tyrian.Audio;

/// <summary>One song out of <c>music.mus</c>: the raw LDS bytes plus whatever has
/// been decoded from them so far. Decoding is lazy -- a 41-song MIDI conversion on
/// startup would be wasted on someone who never opens the music window.</summary>
public sealed class MusicTrack
{
    /// <summary>0-based index, the number the engine's <c>play_song</c> takes.</summary>
    public int Index;

    /// <summary>Title from musmast.c.</summary>
    public string Title = "";

    /// <summary>Raw LDS bytes as stored in music.mus.</summary>
    public byte[] Raw = Array.Empty<byte>();

    /// <summary>Byte offset of the song inside music.mus.</summary>
    public int Offset;

    private LdsSong? _lds;
    private bool _ldsTried;
    private LdsMidiSong? _midi;
    private bool _midiTried;

    /// <summary>The parsed LDS song, decoded on first use. Null if it will not parse.</summary>
    public LdsSong? Lds
    {
        get
        {
            if (!_ldsTried) { _ldsTried = true; try { _lds = LdsSong.Load(Raw); } catch { _lds = null; } }
            return _lds;
        }
    }

    /// <summary>The MIDI conversion of the song, done on first use. Null if it will not convert.</summary>
    public LdsMidiSong? Midi
    {
        get
        {
            if (!_midiTried) { _midiTried = true; try { _midi = LdsMidi.Convert(Raw); } catch { _midi = null; } }
            return _midi;
        }
    }

    /// <summary>True once the MIDI conversion has been attempted (so the UI can show progress).</summary>
    public bool MidiReady => _midiTried;

    /// <summary>Length in Loudness ticks (~69.5 Hz), from the MIDI conversion.</summary>
    public int Ticks => Midi?.Duration is uint d ? (int)d : 0;

    /// <summary>Length in seconds at the OPL player's update rate.</summary>
    public float Seconds => Ticks / MusicBank.LdsUpdateRate;
}

/// <summary>
/// <c>music.mus</c>: a count, a table of file offsets, then one LDS (AdLib
/// "LOUDNESS") song per entry -- exactly what <c>load_music</c> in loudness.c reads.
/// </summary>
public sealed class MusicBank
{
    /// <summary>The rate the game steps the Loudness player at (loudness.c: 69.5 Hz).</summary>
    public const float LdsUpdateRate = 69.5f;

    /// <summary>Song titles from musmast.c, in file order.</summary>
    public static readonly string[] Titles =
    {
        "Asteroid Dance Part 2",
        "Asteroid Dance Part 1",
        "Buy/Sell Music",
        "CAMANIS",
        "CAMANISE",
        "Deli Shop Quartet",
        "Deli Shop Quartet No. 2",
        "Ending Number 1",
        "Ending Number 2",
        "End of Level",
        "Game Over Solo",
        "Gryphons of the West",
        "Somebody pick up the Gryphone",
        "Gyges, Will You Please Help Me?",
        "I speak Gygese",
        "Halloween Ramble",
        "Tunneling Trolls",
        "Tyrian, The Level",
        "The MusicMan",
        "The Navigator",
        "Come Back to Me, Savara",
        "Come Back again to Savara",
        "Space Journey 1",
        "Space Journey 2",
        "The final edge",
        "START5",
        "Parlance",
        "Torm - The Gathering",
        "TRANSON",
        "Tyrian: The Song",
        "ZANAC3",
        "ZANACS",
        "Return me to Savara",
        "High Score Table",
        "One Mustn't Fall",
        "Sarah's Song",
        "A Field for Mag",
        "Rock Garden",
        "Quest for Peace",
        "Composition in Q",
        "BEER"
    };

    /// <summary>The songs, in file order.</summary>
    public MusicTrack[] Tracks { get; private set; } = Array.Empty<MusicTrack>();

    /// <summary>True when music.mus parsed.</summary>
    public bool Loaded => Tracks.Length > 0;

    /// <summary>Where music.mus was read from.</summary>
    public string Path { get; private set; } = "";

    /// <summary>Reads music.mus out of the data folder. Returns false if it is missing or malformed.</summary>
    public bool Load(string dataDir)
    {
        string path = System.IO.Path.Combine(dataDir, "music.mus");
        if (!File.Exists(path)) return false;
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 2) return false;
            int count = data[0] | (data[1] << 8);
            if (count <= 0 || count > 512 || data.Length < 2 + count * 4) return false;

            var offsets = new int[count + 1];
            for (int i = 0; i < count; i++)
                offsets[i] = data[2 + i * 4] | (data[3 + i * 4] << 8) | (data[4 + i * 4] << 16) | (data[5 + i * 4] << 24);
            offsets[count] = data.Length;

            var tracks = new MusicTrack[count];
            for (int i = 0; i < count; i++)
            {
                int start = offsets[i], len = offsets[i + 1] - offsets[i];
                if (start < 0 || len < 0 || start + len > data.Length) return false;
                tracks[i] = new MusicTrack
                {
                    Index = i,
                    Title = i < Titles.Length ? Titles[i] : $"Song {i + 1}",
                    Offset = start,
                    Raw = data[start..(start + len)],
                };
            }
            Tracks = tracks;
            Path = path;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Song by index, or null if out of range.</summary>
    public MusicTrack? this[int index] => index >= 0 && index < Tracks.Length ? Tracks[index] : null;

    /// <summary>Title for a song number, safe for any index (the level data is not validated).</summary>
    public string TitleOf(int index) =>
        index >= 0 && index < Tracks.Length ? Tracks[index].Title
        : index >= 0 && index < Titles.Length ? Titles[index]
        : $"song {index + 1}";
}
