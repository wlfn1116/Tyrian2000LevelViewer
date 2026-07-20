using System.Numerics;
using Hexa.NET.ImGui;
using T2LV.Render;
using T2LV.Tyrian;

namespace T2LV;

/// <summary>
/// The analysis panel: what a level is actually made of, and how the levels rank against
/// each other. Everything here is read off the event list by <see cref="LevelStats"/> rather
/// than simulated, so it covers every level in an episode at once instead of the one being
/// played back.
/// </summary>
public sealed unsafe partial class App
{
    private bool _showAnalysis;
    private int _analysisMode;            // 0 = this level, 1 = all levels
    private int _analysisSort = 1;        // column index in the ranking table
    private readonly Dictionary<(int Ep, int File), LevelStats> _statsCache = new();

    private static readonly uint AcArmor = Gfx.Rgba(255, 130, 120);
    private static readonly uint AcFire = Gfx.Rgba(255, 195, 90);
    private static readonly uint AcSpawn = Gfx.Rgba(120, 200, 255);

    /// <summary>The "--showanalysis N" entry point: 0 = this level, 1 = the cross-level ranking.</summary>
    public void ShowAnalysis(int mode)
    {
        _showAnalysis = true;
        _analysisMode = Math.Clamp(mode, 0, 1);
    }

    private LevelStats? StatsFor(EpisodeInfo ep, int fileNum)
    {
        var key = (ep.Number, fileNum);
        if (_statsCache.TryGetValue(key, out var hit)) return hit;
        if (_gd == null) return null;
        try
        {
            var lv = _gd.LoadLevel(ep, fileNum);
            var name = ep.Levels.FirstOrDefault(l => l.FileNum == fileNum)?.Name.Trim();
            var st = LevelStats.Build(lv, _gd.GetEnemyData(ep), _gd.GetWeapons(ep),
                string.IsNullOrWhiteSpace(name) ? $"level {fileNum}" : name);
            _statsCache[key] = st;
            return st;
        }
        catch { return null; }
    }

    private void DrawAnalysisWindow()
    {
        if (!_showAnalysis || _gd == null || CurEpisode == null) return;

        ImGui.SetNextWindowSize(new Vector2(940, 620), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(560, 340), new Vector2(float.MaxValue, float.MaxValue));
        bool open = _showAnalysis;
        if (!ImGui.Begin("Analysis###analysis", ref open)) { ImGui.End(); _showAnalysis = open; return; }
        _showAnalysis = open;

        ImGui.SetNextItemWidth(130);
        EpisodeCombo("##anepisode");
        ImGui.SameLine(0, 12);
        if (ImGui.RadioButton("This level", _analysisMode == 0)) _analysisMode = 0;
        ImGui.SameLine();
        if (ImGui.RadioButton("All levels", _analysisMode == 1)) _analysisMode = 1;
        ImGui.SameLine(0, 12);
        ImGui.TextDisabled("read from the event list - an enemy you never shoot still counts");
        ImGui.Separator();

        if (_analysisMode == 0) DrawLevelAnalysis(); else DrawLevelRanking();
        ImGui.End();
    }

    private void DrawLevelAnalysis()
    {
        var ep = CurEpisode!;
        var st = StatsFor(ep, _levelFileNum);
        if (st == null) { ImGui.TextDisabled("This level could not be read."); return; }

        DrawGameHeader(st.Name.ToUpperInvariant(), AcSim);
        ImGui.TextDisabled($"level {st.FileNum}  ·  {st.Duration} event-clock units  ·  " +
            $"{st.SpawnCount} spawns  ·  {st.TotalArmor:n0} destructible armour" +
            (st.Invulnerable > 0 ? $"  ·  {st.Invulnerable} indestructible" : "") +
            (st.BossParts > 0 ? $"  ·  {st.BossParts} boss parts" : ""));

        ImGui.Dummy(new Vector2(0, 4));
        if (ImGui.BeginTable("anrates", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn(); Metric("difficulty", $"{st.Difficulty:0.00}", AcSim,
                "A composite of the three rates below, scaled so an ordinary level\nlands near 1. A ranking key, not an absolute.");
            ImGui.TableNextColumn(); Metric("armour / tick", $"{st.ArmorRate:0.00}", AcArmor,
                "How much armour the level throws at you per unit of event time.");
            ImGui.TableNextColumn(); Metric("fire / 1000 ticks", $"{st.FirePer1000:0.0}", AcFire,
                "Expected enemy damage per 1000 ticks if every spawned turret kept firing:\nvolley damage divided by its reload, summed over every spawn.");
            ImGui.TableNextColumn(); Metric("spawns / tick", $"{st.SpawnRate:0.000}", AcSpawn,
                "Spawn events per unit of event time.");
            ImGui.EndTable();
        }

        ImGui.Dummy(new Vector2(0, 6));
        ImGui.SeparatorText("along the level");
        Profile("armour", st.ArmorProfile, st.PeakArmor, AcArmor);
        Profile("incoming fire", st.FireProfile, st.PeakFire, AcFire);
        Profile("spawns", st.SpawnProfile, st.PeakSpawn, AcSpawn);

        ImGui.Dummy(new Vector2(0, 6));
        if (!ImGui.BeginTable("anlower", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame)) return;

        ImGui.TableNextColumn();
        ImGui.SeparatorText("what is in it");
        for (int c = 0; c < st.ByCategory.Length; c++)
        {
            if (st.ByCategory[c] == 0) continue;
            var cat = (ObjCategory)c;
            ImGui.TextColored(ColorOf(ObjectPlacer.CategoryColor(cat)),
                $"{st.ByCategory[c],5}  {ObjectPlacer.CategoryName(cat)}");
        }

        ImGui.TableNextColumn();
        ImGui.SeparatorText("most spawned");
        var ed = TryEnemyData();
        var dl = ImGui.GetWindowDrawList();
        foreach (var (id, count, armor) in st.TopEnemies)
        {
            var mn = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(26f, 22f));
            if (ed != null)
            {
                var d = ed.Get(id);
                var atlas = d.Loaded ? Atlas(EnemySpriteSource(d.ShapeBank), AppSettings.GamePalette) : null;
                if (atlas != null)
                    DrawEnemyFrameCentered(dl, atlas, d.EGraphic[0], d.Esize == 1, mn,
                        mn + new Vector2(26f, 22f), 1f);
            }
            ImGui.SameLine(0, 4);
            if (ImGui.SmallButton($"#{id}##top{id}")) OpenEnemy(_episodeIdx, id);
            ImGui.SameLine(0, 6);
            ImGui.TextDisabled($"x{count}" + (armor > 0 ? $"   armour {armor}" : ""));
        }
        ImGui.EndTable();
    }

    private static void Metric(string label, string value, uint accent, string tip)
    {
        ImGui.TextDisabled(label);
        ImGui.PushStyleColor(ImGuiCol.Text, accent);
        ImGui.Text(value);
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    /// <summary>One profile strip: the level start to end left to right, bar height relative
    /// to that profile's own peak so the shape reads even when the scales differ wildly.</summary>
    private void Profile(string label, float[] data, float peak, uint accent)
    {
        ImGui.TextDisabled(label);
        const float h = 46f;
        var mn = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        ImGui.Dummy(new Vector2(w, h));
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(mn, mn + new Vector2(w, h), Gfx.Rgba(18, 20, 27), 3f);

        if (peak <= 0)
        {
            dl.AddText(mn + new Vector2(6f, h * 0.5f - 7f), Gfx.Rgba(110, 116, 132), "none");
            dl.AddRect(mn, mn + new Vector2(w, h), Gfx.Rgba(56, 62, 80), 3f);
            return;
        }

        float bw = w / data.Length;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] <= 0) continue;
            float bh = Math.Max(1.5f, data[i] / peak * (h - 4f));
            dl.AddRectFilled(new Vector2(mn.X + i * bw, mn.Y + h - bh),
                new Vector2(mn.X + (i + 1) * bw - 0.5f, mn.Y + h), Shade(accent, 0.95f, 235));
        }
        dl.AddRect(mn, mn + new Vector2(w, h), Gfx.Rgba(56, 62, 80), 3f);

        // Peak readout, right-aligned so it never sits on top of the bars at the start.
        string pk = $"peak {peak:0.##}";
        var sz = ImGui.CalcTextSize(pk);
        dl.AddText(new Vector2(mn.X + w - sz.X - 6f, mn.Y + 3f), Shade(accent, 1f, 150), pk);
    }

    private void DrawLevelRanking()
    {
        // Follow the viewer's own scope: "All episodes" ranks all 70 levels against each other,
        // which is the comparison the panel is actually for.
        var rows = new List<(int EpisodeIdx, int EpisodeNum, LevelStats Stats)>();
        foreach (int e in ShownEpisodes())
        {
            var ep = _gd!.Episodes[e];
            foreach (var item in ep.Levels)
            {
                var st = StatsFor(ep, item.FileNum);
                if (st != null && st.SpawnCount > 0) rows.Add((e, ep.Number, st));
            }
        }
        if (rows.Count == 0) { ImGui.TextDisabled("Nothing to rank."); return; }

        rows.Sort((x, y) =>
        {
            var (a, b) = (x.Stats, y.Stats);
            return _analysisSort switch
            {
                0 => x.EpisodeNum != y.EpisodeNum ? x.EpisodeNum.CompareTo(y.EpisodeNum)
                    : a.FileNum.CompareTo(b.FileNum),
                2 => b.SpawnCount.CompareTo(a.SpawnCount),
                3 => b.TotalArmor.CompareTo(a.TotalArmor),
                4 => b.FirePer1000.CompareTo(a.FirePer1000),
                5 => b.Duration.CompareTo(a.Duration),
                _ => b.Difficulty.CompareTo(a.Difficulty),
            };
        });

        float worst = (float)rows.Max(r => r.Stats.Difficulty);
        ImGui.TextDisabled($"{rows.Count} levels  ·  click a heading to re-sort  ·  click a row to open that level");

        if (!ImGui.BeginTable("rank", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        string[] heads = { "level", "difficulty", "spawns", "armour", "fire/1000", "length" };
        for (int c = 0; c < heads.Length; c++)
            ImGui.TableSetupColumn(heads[c], c == 0 ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.WidthFixed,
                c == 0 ? 0f : 88f);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (int c = 0; c < heads.Length; c++)
        {
            ImGui.TableSetColumnIndex(c);
            bool active = _analysisSort == c;
            ImGui.PushStyleColor(ImGuiCol.Text, active ? AcSim : Gfx.Rgba(190, 196, 212));
            if (ImGui.SmallButton($"{heads[c]}##h{c}")) _analysisSort = c;
            ImGui.PopStyleColor();
        }

        var dl = ImGui.GetWindowDrawList();
        foreach (var (epIdx, epNum, r) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            bool sel = r.FileNum == _levelFileNum && epIdx == _episodeIdx;
            // Level numbers repeat across episodes, so the row id has to carry both.
            string label = _allEpisodes ? $"ep{epNum}  {r.FileNum:00}  {r.Name}" : $"{r.FileNum:00}  {r.Name}";
            if (ImGui.Selectable($"{label}##rk{epNum}_{r.FileNum}", sel, ImGuiSelectableFlags.SpanAllColumns))
                SelectLevelFile(epIdx, r.FileNum);

            ImGui.TableNextColumn();
            // A bar behind the number, so the spread across the set reads at a glance.
            var mn = ImGui.GetCursorScreenPos();
            float cw = ImGui.GetContentRegionAvail().X;
            float f = worst > 0 ? (float)(r.Difficulty / worst) : 0f;
            dl.AddRectFilled(mn, new Vector2(mn.X + cw * f, mn.Y + ImGui.GetTextLineHeight()),
                Shade(AcSim, 0.45f, 190), 2f);
            ImGui.Text($"{r.Difficulty:0.00}");

            ImGui.TableNextColumn(); ImGui.Text($"{r.SpawnCount}");
            ImGui.TableNextColumn(); ImGui.Text($"{r.TotalArmor:n0}");
            ImGui.TableNextColumn(); ImGui.Text($"{r.FirePer1000:0.0}");
            ImGui.TableNextColumn(); ImGui.Text($"{r.Duration}");
        }
        ImGui.EndTable();
    }
}
