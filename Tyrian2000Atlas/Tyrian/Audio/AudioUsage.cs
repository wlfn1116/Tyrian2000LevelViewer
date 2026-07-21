namespace T2A.Tyrian.Audio;

/// <summary>Where a song or a sound is used. The first four kinds are read out of the
/// data files; <see cref="Engine"/> is the handful of places the engine hard-codes.</summary>
public enum UseKind
{
    /// <summary>A level's starting song, from the ]L script line's song field.</summary>
    LevelStart,
    /// <summary>A mid-level change: event 35 for music, event 62 for a sound effect.</summary>
    LevelEvent,
    /// <summary>A ]M cutscene music command in the episode script.</summary>
    Cutscene,
    /// <summary>A ]i command setting the outpost/shop music for the rest of the episode.</summary>
    ShopMusic,
    /// <summary>An event-16 text window, which speaks a fixed announcer line.</summary>
    TextWindow,
    /// <summary>A weapon whose shots make this sound (weapons[].sound).</summary>
    Weapon,
    /// <summary>Hard-coded in the engine: menus, jingles, pickups, explosions.</summary>
    Engine,
}

/// <summary>One place a song or sound turns up.</summary>
public readonly record struct AudioUse(
    UseKind Kind,
    int Episode,          // 1..5, or 0 when it is not episode-specific
    int LevelFile,        // 1-based section in tyrian%d.lvl, or 0
    string LevelName,
    int Section,          // episode-script section, or 0
    int Time,             // event time within the level, or 0
    string Detail)
{
    /// <summary>A short label for the row: where this is, in the player's terms.</summary>
    public string Where => Kind switch
    {
        UseKind.LevelStart or UseKind.LevelEvent =>
            $"Ep{Episode}  {(LevelName.Length > 0 ? LevelName : $"level {LevelFile}")}  #{LevelFile:00}",
        UseKind.Cutscene or UseKind.ShopMusic => $"Ep{Episode}  script section {Section}",
        UseKind.TextWindow => $"Ep{Episode}  {(LevelName.Length > 0 ? LevelName : $"level {LevelFile}")}  #{LevelFile:00}",
        UseKind.Weapon => Detail,
        _ => Detail,
    };
}

/// <summary>
/// Every use of every song and sound the data set can be made to admit to: level
/// start songs and event-35 changes, ]M cutscene cues and ]i shop music, event-62
/// sound cues and the event-16 announcer lines, and which weapons carry which
/// firing sound. Built once per data folder and cached.
/// </summary>
public sealed class AudioUsageIndex
{
    private readonly Dictionary<int, List<AudioUse>> _bySong = new();
    private readonly Dictionary<int, List<AudioUse>> _bySound = new();

    /// <summary>Uses of a 0-based song index.</summary>
    public IReadOnlyList<AudioUse> Song(int index) =>
        _bySong.TryGetValue(index, out var l) ? l : Array.Empty<AudioUse>();

    /// <summary>Uses of a 1-based sound number.</summary>
    public IReadOnlyList<AudioUse> Sound(int number) =>
        _bySound.TryGetValue(number, out var l) ? l : Array.Empty<AudioUse>();

    /// <summary>How many levels (counting a level once) start on or switch to this song.</summary>
    public int SongLevelCount(int index) =>
        Song(index).Where(u => u.Kind is UseKind.LevelStart or UseKind.LevelEvent)
                   .Select(u => (u.Episode, u.LevelFile)).Distinct().Count();

    private void AddSong(int songIndex, AudioUse use)
    {
        if (songIndex < 0) return;
        if (!_bySong.TryGetValue(songIndex, out var l)) _bySong[songIndex] = l = new List<AudioUse>();
        l.Add(use);
    }

    private void AddSound(int number, AudioUse use)
    {
        if (number < 1) return;
        if (!_bySound.TryGetValue(number, out var l)) _bySound[number] = l = new List<AudioUse>();
        l.Add(use);
    }

    /// <summary>Scans every episode script and every level in the data set.</summary>
    public static AudioUsageIndex Build(GameData gd)
    {
        var ix = new AudioUsageIndex();
        ix.AddEngineUses();

        foreach (var ep in gd.Episodes)
        {
            // --- ]L starting songs, and the ]M / ]i script commands around them ---
            foreach (var e in ep.ScriptLevels)
            {
                if (e.Song <= 0) continue;
                ix.AddSong(e.Song - 1, new AudioUse(UseKind.LevelStart, ep.Number, e.LvlFileNum,
                    e.Name.Trim(), e.SectionIndex, 0, "starts the level"));
            }

            if (ep.Script != null)
            {
                int section = 0;
                foreach (var line in ep.Script.Lines)
                {
                    if (line.Length == 0) continue;
                    if (line[0] == '*') { section++; continue; }
                    if (line.Length < 3 || line[0] != ']') continue;
                    if (line[1] == 'M')
                    {
                        int song = EpisodeScript.AtoiAt(line, 3);
                        if (song > 0)
                            ix.AddSong(song - 1, new AudioUse(UseKind.Cutscene, ep.Number, 0, "",
                                section, 0, Title(ep, section, "cutscene")));
                    }
                    else if (line[1] == 'i')
                    {
                        int song = EpisodeScript.AtoiAt(line, 3);
                        if (song > 0)
                            ix.AddSong(song - 1, new AudioUse(UseKind.ShopMusic, ep.Number, 0, "",
                                section, 0, "outpost / shop music from here on"));
                    }
                }
            }

            // --- level events: 35 changes the song, 62 fires a sound, 16 speaks a line ---
            foreach (var item in ep.Levels)
            {
                Level lv;
                try { lv = gd.LoadLevel(ep, item.FileNum); }
                catch { continue; }
                string name = item.Name.Trim();

                foreach (var evt in lv.Events)
                {
                    switch (evt.Type)
                    {
                        case 35 when evt.Dat > 0:
                            ix.AddSong(evt.Dat - 1, new AudioUse(UseKind.LevelEvent, ep.Number,
                                item.FileNum, name, 0, evt.Time, "event 35 switches to it"));
                            break;
                        case 62 when evt.Dat is > 0 and <= SoundBank.SoundCount:
                            ix.AddSound(evt.Dat, new AudioUse(UseKind.LevelEvent, ep.Number,
                                item.FileNum, name, 0, evt.Time, "event 62 plays it"));
                            break;
                        case 16 when evt.Dat is > 0 and <= 9:
                            ix.AddSound(SoundBank.WindowTextSamples[evt.Dat - 1],
                                new AudioUse(UseKind.TextWindow, ep.Number, item.FileNum, name, 0,
                                    evt.Time, $"event 16 text window {evt.Dat}"));
                            break;
                    }
                }
            }

            // --- weapons[].sound: which shots make which noise ---
            try
            {
                var wd = gd.GetWeapons(ep);
                var ed = gd.GetEnemyData(ep);
                var byEnemy = new Dictionary<int, List<int>>();   // weapon id -> enemy ids that fire it
                for (int i = 0; i < ed.Enemies.Length; i++)
                {
                    ref var en = ref ed.Enemies[i];
                    if (!en.Loaded) continue;
                    foreach (int t in new[] { (int)en.Tur0, en.Tur1, en.Tur2 })
                    {
                        if (t <= 0 || t > 250) continue;   // 251..255 are engine specials, not table rows
                        if (!byEnemy.TryGetValue(t, out var l)) byEnemy[t] = l = new List<int>();
                        if (!l.Contains(i)) l.Add(i);
                    }
                }

                var perSound = new Dictionary<int, (int Weapons, int Enemies)>();
                for (int w = 0; w < wd.Weapons.Length; w++)
                {
                    ref var weap = ref wd.Weapons[w];
                    if (!weap.Loaded || weap.Sound == 0) continue;
                    int enemies = byEnemy.TryGetValue(w, out var users) ? users.Count : 0;
                    perSound.TryGetValue(weap.Sound, out var acc);
                    perSound[weap.Sound] = (acc.Weapons + 1, acc.Enemies + enemies);
                }
                foreach (var (sound, acc) in perSound)
                {
                    if (sound > SoundBank.SoundCount) continue;
                    string detail = $"Ep{ep.Number}  {acc.Weapons} weapon{(acc.Weapons == 1 ? "" : "s")}"
                        + (acc.Enemies > 0 ? $", fired by {acc.Enemies} enemy turret{(acc.Enemies == 1 ? "" : "s")}" : "");
                    ix.AddSound(sound, new AudioUse(UseKind.Weapon, ep.Number, 0, "", 0, 0, detail));
                }
            }
            catch { /* an episode whose item block will not resolve simply contributes no weapon rows */ }
        }

        foreach (var l in ix._bySong.Values) l.Sort(Compare);
        foreach (var l in ix._bySound.Values) l.Sort(Compare);
        return ix;
    }

    private static int Compare(AudioUse a, AudioUse b)
    {
        int c = a.Kind.CompareTo(b.Kind);
        if (c != 0) return c;
        c = a.Episode.CompareTo(b.Episode);
        if (c != 0) return c;
        c = a.LevelFile.CompareTo(b.LevelFile);
        if (c != 0) return c;
        return a.Time.CompareTo(b.Time);
    }

    private static string Title(EpisodeInfo ep, int section, string fallback)
    {
        string t = ep.Script?.TitleOf(section) ?? "";
        t = t.TrimStart('*').Trim();
        return t.Length > 0 ? t : fallback;
    }

    /// <summary>
    /// The places the engine names a song or sound in code rather than in data. Taken
    /// from the call sites in tyrian2.c / mainint.c / game_menu.c / jukebox.c /
    /// destruct.c; nothing in the data files can reveal these.
    /// </summary>
    private void AddEngineUses()
    {
        void Song(int index, string what) =>
            AddSong(index, new AudioUse(UseKind.Engine, 0, 0, "", 0, 0, what));

        Song(2, "Outpost / buy-sell screen (the default shop song)");
        Song(7, "Credits roll");
        Song(8, "End of the credits");
        Song(9, "Level-complete jingle");
        Song(10, "Game over");
        Song(16, "SuperTyrian intro screen");
        Song(19, "Help / galaxy map / datacube screen");
        Song(21, "Level-complete screen when the episode-4 battle timer runs out");
        Song(26, "\"Next episode\" transition card");
        Song(29, "Title screen and main menu");
        Song(30, "Picking up a secret-level warp orb");
        Song(31, "High-score name entry");
        Song(33, "High-score table");
        foreach (int s in new[] { 0, 1, 5, 11, 12, 13, 16, 22, 23, 25, 27, 28, 31, 32 })
            Song(s, "Destruct minigame (one of its fourteen)");

        void Sound(int number, string what) =>
            AddSound(number, new AudioUse(UseKind.Engine, 0, 0, "", 0, 0, what));

        Sound(3, "An enemy takes a hit but survives");
        Sound(4, "A small enemy is destroyed");
        Sound(8, "Menu: confirm / activate; also a small enemy's death in the main path");
        Sound(9, "A large enemy (esize 1) is destroyed, and boss death cascades");
        Sound(11, "The last blast of a boss death cascade; player death animation");
        Sound(13, "One of the three random enemy-launch sounds");
        Sound(6, "One of the three random enemy-launch sounds");
        Sound(26, "One of the three random enemy-launch sounds; launching a sidekick");
        Sound(7, "A new sky enemy of type 2 arrives");
        Sound(15, "Lightning -- the one sound the mixer plays at quarter volume");
        Sound(16, "Menu: back / cancel / rejected");
        Sound(17, "Low-armour siren (it speeds up as armour drops) and the level timer");
        Sound(18, "Money and score pickups; shop purchase; the level-end tally");
        Sound(19, "The hull takes damage (shields already gone)");
        Sound(21, "Zinglon's blast, and the Galaga-mode extra life");
        Sound(22, "The player's ship explodes");
        Sound(23, "A sidekick re-attaches; menu: cannot afford it");
        Sound(24, "Menu click, and the level timer's tick");
        Sound(27, "The shield takes damage");
        Sound(28, "Menu: move the selection");
        Sound(29, "Every power-up, weapon and armour pickup");
        Sound(32, "A Destruct round is won");
        Sound(35, "Level start (\"Good luck\"), and the Super Tyrian new-game screen");
        Sound(36, "The level-complete screen");
        Sound(37, "The ENGAGE super-code on the title menu");
        Sound(39, "A datacube is collected");
    }
}
