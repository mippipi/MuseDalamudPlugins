using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace MuseStatusExporter;

/// <summary>
/// In-game report window (v1.22.0), opened with "/mstatus report". It
/// shows the same items as the status report Muse maintains: job
/// levels, Cosmic Exploration tool stages and scores, the GC supply
/// list with owned / shortfall counts computed here, known houses'
/// last entry and demolition dates, main scenario progress, shared
/// FATE progress, the buddy, weekly tomestones, retainers (venture
/// completion times and market listings), Occult Crescent
/// progress and folklore tome unlock state. Other characters are shown as tabs, read from their
/// saved ffxiv-status-*.json files in the plugin config folder (the
/// same reading pattern as the cross-character login summary); the
/// current character uses the live snapshot. The design follows the
/// settings window: dark violet theme with panelled sections. The
/// display language follows Configuration.UiLanguage and can be
/// switched with the header buttons, like in the settings window
/// (v1.22.1).
/// </summary>
public sealed class ReportWindow : Window
{
    private static readonly Vector4 Accent = new(0.62f, 0.45f, 0.98f, 1f);
    private static readonly Vector4 AccentDim = new(0.38f, 0.27f, 0.66f, 0.65f);
    private static readonly Vector4 WindowBackground = new(0.05f, 0.05f, 0.08f, 1f);
    private static readonly Vector4 ContentBackground = new(0.06f, 0.06f, 0.10f, 1f);
    private static readonly Vector4 PanelBackground = new(0.10f, 0.10f, 0.15f, 1f);
    private static readonly Vector4 BorderColor = new(0.30f, 0.26f, 0.42f, 0.55f);
    private static readonly Vector4 MutedText = new(0.62f, 0.62f, 0.70f, 1f);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Configuration configuration;
    private readonly string configDirectory;
    private readonly Func<Plugin.Snapshot?> currentSnapshotProvider;
    private readonly List<CharacterTab> tabs = new();
    private int selectedTab;

    private bool English => configuration.UiLanguage == 1;

    private string L(string japanese, string english) => English ? english : japanese;

    public ReportWindow(Configuration configuration, string configDirectory, Func<Plugin.Snapshot?> currentSnapshotProvider)
        : base("Muse Status Exporter レポート###MuseStatusExporterReport")
    {
        this.configuration = configuration;
        this.configDirectory = configDirectory;
        this.currentSnapshotProvider = currentSnapshotProvider;
        if (configuration.UiLanguage == 1)
            WindowName = "Muse Status Exporter Report###MuseStatusExporterReport";
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 480),
            MaximumSize = new Vector2(1100, 1000),
        };
    }

    public override void OnOpen()
    {
        WindowName = English
            ? "Muse Status Exporter Report###MuseStatusExporterReport"
            : "Muse Status Exporter レポート###MuseStatusExporterReport";
        RefreshTabs();
    }

    public override void PreDraw()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBackground);
        ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Spacing();

        if (tabs.Count == 0)
        {
            ImGui.TextColored(MutedText, L(
                "表示できるデータがありません。ログイン後に開き直してください。",
                "No data to display. Please reopen this window after logging in."));
            return;
        }

        DrawTabChips();
        ImGui.Spacing();

        if (selectedTab < 0 || selectedTab >= tabs.Count)
            selectedTab = 0;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, ContentBackground);
        ImGui.BeginChild("##reportContent", new Vector2(0, 0), true);
        ImGui.PopStyleColor();
        var sections = BuildSections(tabs[selectedTab].Snapshot);
        for (var i = 0; i < sections.Count; i++)
            DrawSection(i, sections[i].Title, sections[i].Lines);
        ImGui.EndChild();
    }

    private void DrawHeader()
    {
        DrawIcon(FontAwesomeIcon.ChartBar, Accent);
        ImGui.SameLine(0, 9);
        var titlePos = ImGui.GetCursorPos();
        ImGui.SetWindowFontScale(1.2f);
        ImGui.TextUnformatted(L("Muse Status Exporter レポート", "Muse Status Exporter Report"));
        ImGui.SetWindowFontScale(1f);

        DrawTitleLineButtons(titlePos);

        ImGui.TextColored(MutedText, L(
            "各キャラクターのステータスです。他のキャラクターは保存済みのデータから表示します。",
            "Status of each character. Other characters are shown from their saved data."));
    }

    /// <summary>
    /// Language switch and refresh button at the right end of the title
    /// line. The language buttons mirror the settings window (v1.21.5):
    /// the active language is highlighted with the accent color, the
    /// language names are always written in their own language, and
    /// switching saves immediately (the shared Configuration.UiLanguage,
    /// so the settings window follows too) and retitles this window;
    /// the ### id part stays constant, so the window keeps its position
    /// and size. Added in v1.22.1.
    /// </summary>
    private void DrawTitleLineButtons(Vector2 titlePos)
    {
        const string jaLabel = "日本語";
        const string enLabel = "English";
        var refreshLabel = L("更新", "Refresh");
        var style = ImGui.GetStyle();
        var languageWidth = ImGui.CalcTextSize(jaLabel).X + ImGui.CalcTextSize(enLabel).X
            + style.FramePadding.X * 4 + style.ItemSpacing.X;
        var refreshWidth = ImGui.CalcTextSize(refreshLabel).X + style.FramePadding.X * 2;
        var totalWidth = languageWidth + style.ItemSpacing.X + refreshWidth;
        ImGui.SetCursorPos(new Vector2(ImGui.GetWindowContentRegionMax().X - totalWidth, titlePos.Y));

        if (DrawLanguageButton(jaLabel, !English))
        {
            configuration.UiLanguage = 0;
            configuration.Save();
            WindowName = "Muse Status Exporter レポート###MuseStatusExporterReport";
        }

        ImGui.SameLine();

        if (DrawLanguageButton(enLabel, English))
        {
            configuration.UiLanguage = 1;
            configuration.Save();
            WindowName = "Muse Status Exporter Report###MuseStatusExporterReport";
        }

        ImGui.SameLine();

        if (ImGui.SmallButton(refreshLabel))
            RefreshTabs();
    }

    private static bool DrawLanguageButton(string label, bool active)
    {
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, AccentDim);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentDim);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentDim);
            ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One);
        }

        var clicked = ImGui.SmallButton(label);

        if (active)
            ImGui.PopStyleColor(4);

        return clicked;
    }

    private void DrawTabChips()
    {
        var style = ImGui.GetStyle();
        for (var i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            var label = tab.Label + (tab.IsCurrent ? L("（現在）", " (current)") : string.Empty);
            var chipWidth = ImGui.CalcTextSize(label).X + style.FramePadding.X * 2 + 4;
            if (i > 0)
            {
                if (ImGui.GetCursorPosX() + chipWidth <= ImGui.GetWindowContentRegionMax().X)
                    ImGui.SameLine();
            }

            var selected = i == selectedTab;
            if (selected)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, AccentDim);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentDim);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentDim);
                ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One);
            }

            if (ImGui.Button($"{label}##tab{i}", new Vector2(chipWidth, 26)))
                selectedTab = i;

            if (selected)
                ImGui.PopStyleColor(4);
        }
    }

    private void DrawSection(int index, string title, List<string> lines)
    {
        if (index > 0)
            ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(MutedText, title);
        ImGui.Spacing();

        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var height = Math.Max(30f, lines.Count * lineHeight + 14f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBackground);
        ImGui.BeginChild($"##reportPanel{index}", new Vector2(0, height), true);
        ImGui.PopStyleColor();
        foreach (var line in lines)
            ImGui.TextUnformatted(line);
        ImGui.EndChild();
    }

    /// <summary>
    /// Rebuilds the tab list: the live snapshot first (when logged in),
    /// then every other character's saved status file in the config
    /// folder, ordered by character and world.
    /// </summary>
    private void RefreshTabs()
    {
        var previousKey = selectedTab >= 0 && selectedTab < tabs.Count ? tabs[selectedTab].Key : null;
        tabs.Clear();

        Plugin.Snapshot? live = null;
        try
        {
            live = currentSnapshotProvider();
        }
        catch
        {
            // The provider only reads a field; stay defensive anyway.
        }

        if (live != null && live.Character.Length > 0)
            tabs.Add(new CharacterTab(live, true));

        var others = new List<Plugin.Snapshot>();
        try
        {
            foreach (var path in Directory.GetFiles(configDirectory, "ffxiv-status-*.json"))
            {
                try
                {
                    var other = JsonSerializer.Deserialize<Plugin.Snapshot>(File.ReadAllText(path), JsonOptions);
                    if (other == null || other.Character.Length == 0)
                        continue;
                    if (live != null
                        && string.Equals(other.Character, live.Character, StringComparison.Ordinal)
                        && string.Equals(other.World, live.World, StringComparison.Ordinal))
                        continue;
                    others.Add(other);
                }
                catch
                {
                    // A file that cannot be read (being written right
                    // now, or an older format) is skipped.
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to read the saved character status files for the report window.");
        }

        foreach (var other in others
                     .OrderBy(o => o.Character, StringComparer.Ordinal)
                     .ThenBy(o => o.World, StringComparer.Ordinal))
            tabs.Add(new CharacterTab(other, false));

        selectedTab = 0;
        if (previousKey != null)
        {
            var index = tabs.FindIndex(tab => tab.Key == previousKey);
            if (index >= 0)
                selectedTab = index;
        }
    }

    private List<(string Title, List<string> Lines)> BuildSections(Plugin.Snapshot snapshot)
    {
        return new List<(string Title, List<string> Lines)>
        {
            (L("概要", "Overview"), BuildOverviewLines(snapshot)),
            (L("ジョブレベル", "Job Levels"), BuildJobLines(snapshot)),
            (L("コスモエクスプローラー", "Cosmic Exploration"), BuildCosmicLines(snapshot)),
            (L("GC軍需品", "GC Supply Requests"), BuildGcSupplyLines(snapshot)),
            (L("ハウス", "Housing"), BuildHousingLines(snapshot)),
            (L("メインクエスト", "Main Scenario"), BuildMainScenarioLines(snapshot)),
            (L("FATE達成度", "Shared FATE Progress"), BuildFateLines(snapshot)),
            (L("バディ", "Buddy"), BuildBuddyLines(snapshot)),
            (L("週間トームストーン", "Weekly Tomestones"), BuildWeeklyTomestoneLines(snapshot)),
            (L("リテイナー", "Retainers"), BuildRetainerLines(snapshot)),
            (L("冒険者小隊", "Adventurer Squadron"), BuildSquadronLines(snapshot)),
            (L("クレセントアイル", "Occult Crescent"), BuildOccultLines(snapshot)),
            (L("伝承録", "Folklore Tomes"), BuildFolkloreLines(snapshot)),
        };
    }

    private List<string> BuildOverviewLines(Plugin.Snapshot snapshot)
    {
        var lines = new List<string>
        {
            $"{snapshot.Character}（{snapshot.World}）",
            L($"現在のジョブ: {snapshot.CurrentJob ?? "-"}", $"Current job: {snapshot.CurrentJob ?? "-"}"),
        };
        if (snapshot.FreeCompany?.Name != null)
            lines.Add($"FC: {snapshot.FreeCompany.Name} [{snapshot.FreeCompany.Tag}]");
        var gil = snapshot.Currencies.FirstOrDefault(c => c.ItemId == 1);
        if (gil != null)
            lines.Add(L($"ギル: {Num(gil.Count)}", $"Gil: {Num(gil.Count)}"));
        if (snapshot.DataUpdatedAt.Length > 0)
            lines.Add(L($"データ更新: {FormatTimestamp(snapshot.DataUpdatedAt)}", $"Data updated: {FormatTimestamp(snapshot.DataUpdatedAt)}"));
        if (snapshot.PluginVersion.Length > 0)
            lines.Add(L($"プラグインバージョン: {snapshot.PluginVersion}", $"Plugin version: {snapshot.PluginVersion}"));
        return lines;
    }

    private List<string> BuildJobLines(Plugin.Snapshot snapshot)
    {
        if (snapshot.Jobs.Count == 0)
            return NoData();
        var lines = new List<string>();
        for (var i = 0; i < snapshot.Jobs.Count; i += 2)
        {
            var left = FormatJob(snapshot.Jobs[i]);
            var right = i + 1 < snapshot.Jobs.Count ? FormatJob(snapshot.Jobs[i + 1]) : null;
            lines.Add(right == null ? left : $"{left} / {right}");
        }

        return lines;
    }

    private string FormatJob(Plugin.JobEntry job)
    {
        var text = $"{job.Abbrev} Lv{job.Level}";
        if (job.NeededExp > 0 && job.Level < 100)
            text += $" ({Num(job.Exp)} / {Num(job.NeededExp)})";
        return text;
    }

    private List<string> BuildCosmicLines(Plugin.Snapshot snapshot)
    {
        if (snapshot.Cosmic == null || snapshot.Cosmic.Classes.Count == 0)
            return NoData();
        var lines = new List<string>();
        foreach (var entry in snapshot.Cosmic.Classes)
        {
            var stage = entry.ToolStage.HasValue
                ? L($"ツール段階 {entry.ToolStage.Value}", $"Tool stage {entry.ToolStage.Value}")
                : L("ツール段階 未取得", "Tool stage: no data");
            lines.Add($"{entry.Abbrev}: {stage} / {L($"スコア {Num(entry.Score)}", $"Score {Num(entry.Score)}")}");
        }

        return lines;
    }

    private List<string> BuildGcSupplyLines(Plugin.Snapshot snapshot)
    {
        if (snapshot.GrandCompanySupply == null || snapshot.GrandCompanySupply.Supply.Count == 0)
        {
            return new List<string>
            {
                L("未取得です（コンテンツ情報「調達任務期限」の詳細を開くと取得されます）",
                    "No data yet (open the Contents Info supply timer details to capture it)."),
            };
        }

        // Owned counts cover everything the character carries (bags,
        // armoury chest, equipped), HQ and NQ combined - the same rule
        // as the login summary. Item ids are normalized so an
        // HQ-encoded id still matches the base id of a request.
        var owned = new Dictionary<uint, int>();
        foreach (var container in snapshot.Inventory)
            foreach (var item in container.Items)
                owned[NormalizeItemId(item.ItemId)] = owned.GetValueOrDefault(NormalizeItemId(item.ItemId)) + item.Count;
        if (snapshot.Equipped != null)
            foreach (var item in snapshot.Equipped.Items)
                owned[NormalizeItemId(item.ItemId)] = owned.GetValueOrDefault(NormalizeItemId(item.ItemId)) + 1;

        var supply = snapshot.GrandCompanySupply.Supply;
        var shortageKinds = supply.Count(item => owned.GetValueOrDefault(NormalizeItemId(item.ItemId)) < item.NumRequested);
        var lines = new List<string>
        {
            shortageKinds > 0
                ? L($"不足 {shortageKinds} 件 / 全 {supply.Count} 件", $"{shortageKinds} short / {supply.Count} requested")
                : L("全て所持済みです", "Everything is on hand"),
        };
        foreach (var item in supply)
        {
            var have = owned.GetValueOrDefault(NormalizeItemId(item.ItemId));
            var shortfall = Math.Max(0, item.NumRequested - have);
            lines.Add(shortfall > 0
                ? L($"{item.Name}: 必要 {item.NumRequested} / 所持 {have} / 不足 {shortfall}",
                    $"{item.Name}: requested {item.NumRequested} / owned {have} / short {shortfall}")
                : L($"{item.Name}: 必要 {item.NumRequested} / 所持 {have}（所持済み）",
                    $"{item.Name}: requested {item.NumRequested} / owned {have} (covered)"));
        }

        return lines;
    }

    private List<string> BuildHousingLines(Plugin.Snapshot snapshot)
    {
        if (snapshot.KnownHouses.Count == 0)
            return new List<string> { L("記録がありません", "No records") };
        var lines = new List<string>();
        foreach (var house in snapshot.KnownHouses)
        {
            var place = L($"{house.World} 第{house.Ward}区{house.Plot}番地（{house.Kind}）",
                $"{house.World} Ward {house.Ward} Plot {house.Plot} ({house.Kind})");
            string entry;
            if (house.LastEntryAt == null)
            {
                entry = L("入室記録がありません", "no entry recorded");
            }
            else
            {
                var date = FormatTimestamp(house.LastEntryAt, dateOnly: true);
                entry = house.DaysSinceEntry.HasValue
                    ? L($"最終入室 {date}（{house.DaysSinceEntry.Value}日前）", $"last entry {date} ({house.DaysSinceEntry.Value} days ago)")
                    : L($"最終入室 {date}", $"last entry {date}");
                if (house.DemolitionDate != null)
                {
                    entry += house.DaysUntilDemolition.HasValue
                        ? L($" / 撤去予定日 {house.DemolitionDate}（あと{house.DaysUntilDemolition.Value}日）",
                            $" / demolition {house.DemolitionDate} ({house.DaysUntilDemolition.Value} days left)")
                        : L($" / 撤去予定日 {house.DemolitionDate}", $" / demolition {house.DemolitionDate}");
                }

                if (house.LastEntryUnverified)
                    entry += L("（所有者未確認）", " (owner unverified)");
            }

            lines.Add($"{place}: {entry}");
        }

        return lines;
    }

    private List<string> BuildMainScenarioLines(Plugin.Snapshot snapshot)
    {
        if (snapshot.MainScenario == null)
            return NoData();
        var msq = snapshot.MainScenario;
        var lines = new List<string>
        {
            L($"完了 {msq.CompletedQuests} / {msq.TotalQuests}", $"Completed {msq.CompletedQuests} / {msq.TotalQuests}"),
        };
        if (msq.AllComplete)
            lines.Add(L("全て完了しています", "All complete"));
        else if (msq.CurrentQuestName != null)
            lines.Add(L($"現在のクエスト: {msq.CurrentQuestName}", $"Current quest: {msq.CurrentQuestName}"));
        foreach (var expansion in msq.Expansions)
            lines.Add($"{expansion.Name}: {expansion.CompletedQuests} / {expansion.TotalQuests}");
        return lines;
    }

    private List<string> BuildFateLines(Plugin.Snapshot snapshot)
    {
        if (snapshot.FateProgress.Count == 0)
            return NoData();
        var lines = new List<string>();
        foreach (var zone in snapshot.FateProgress.OrderBy(z => z.TabIndex).ThenBy(z => z.DisplayOrder))
        {
            var line = $"{zone.ZoneName}: "
                + L($"ランク {zone.CurrentRank}/{zone.MaxRank}", $"Rank {zone.CurrentRank}/{zone.MaxRank}")
                + $" ({zone.FateProgress}/66)";
            line += zone.NeededFates > 0
                ? L($" 次のランクまで {zone.NeededFates}", $" {zone.NeededFates} to next rank")
                : " MAX";
            lines.Add(line);
        }

        return lines;
    }

    private List<string> BuildBuddyLines(Plugin.Snapshot snapshot)
    {
        if (snapshot.Buddy == null)
            return NoData();
        var buddy = snapshot.Buddy;
        var line = $"{buddy.Name}: "
            + L($"ランク {buddy.Rank}", $"Rank {buddy.Rank}")
            + $" EXP {Num(buddy.CurrentExp)}";
        if (buddy.MaxExp.HasValue)
            line += $" / {Num(buddy.MaxExp.Value)}";
        line += L($" / スキルポイント {buddy.SkillPoints}", $" / Skill points {buddy.SkillPoints}");
        if (buddy.LevelUpReady)
            line += L(" / レベルアップ可能", " / Level up ready");
        return new List<string> { line };
    }

    private List<string> BuildWeeklyTomestoneLines(Plugin.Snapshot snapshot)
    {
        if (snapshot.WeeklyTomestone == null)
            return NoData();
        var weekly = snapshot.WeeklyTomestone;
        var remaining = Math.Max(0, weekly.Limit - weekly.Acquired);
        return new List<string>
        {
            L($"{weekly.Acquired} / {weekly.Limit}（残り {remaining}）",
                $"{weekly.Acquired} / {weekly.Limit} ({remaining} remaining)"),
        };
    }

    private List<string> BuildSquadronLines(Plugin.Snapshot snapshot)
    {
        if (snapshot.Squadron == null)
            return NoData();
        var squadron = snapshot.Squadron;
        var lines = new List<string>();
        if (squadron.ExpeditionId == 0)
            lines.Add(L("任務なし", "No mission in progress"));
        else
        {
            var name = squadron.ExpeditionName ?? $"#{squadron.ExpeditionId}";
            if (squadron.MissionCompleted)
                lines.Add(L($"任務完了（受け取り待ち）: {name}",
                    $"Mission complete (awaiting debrief): {name}"));
            else if (squadron.MissionCompleteAt != null &&
                     DateTimeOffset.TryParse(squadron.MissionCompleteAt, out var completeAt))
                lines.Add(L($"任務中: {name}（完了 {completeAt:M/d HH:mm}）",
                    $"On mission: {name} (completes {completeAt:M/d HH:mm})"));
            else
                lines.Add(L($"任務中: {name}", $"On mission: {name}"));
        }
        if (squadron.TrainingId != 0)
        {
            var name = squadron.TrainingName ?? $"#{squadron.TrainingId}";
            lines.Add(squadron.TrainingCompleted
                ? L($"訓練完了: {name}", $"Training complete: {name}")
                : L($"訓練中: {name}", $"Training in progress: {name}"));
        }
        if (squadron.HasNewCandidate)
            lines.Add(L("新しい入隊希望者がいます", "A new recruit candidate is waiting"));
        return lines;
    }

    private List<string> BuildRetainerLines(Plugin.Snapshot snapshot)
    {
        if (snapshot.Retainers.Count == 0)
            return NoData();
        var now = DateTimeOffset.Now;
        var lines = new List<string>();
        foreach (var retainer in snapshot.Retainers)
        {
            var job = retainer.ClassJob != null ? $"{retainer.ClassJob} Lv{retainer.Level}" : $"Lv{retainer.Level}";
            string venture;
            if (retainer.VentureId == 0 || retainer.VentureCompleteAt == null)
                venture = L("ベンチャーなし", "no venture");
            else if (DateTimeOffset.TryParse(retainer.VentureCompleteAt, out var completeAt))
                venture = completeAt <= now
                    ? L("ベンチャー完了", "venture complete")
                    : L($"ベンチャー完了 {completeAt:M/d HH:mm}", $"venture completes {completeAt:M/d HH:mm}");
            else
                venture = L($"ベンチャー完了 {retainer.VentureCompleteAt}", $"venture completes {retainer.VentureCompleteAt}");
            lines.Add(L($"{retainer.Name}（{job}）: {venture} / 出品 {retainer.MarketItemCount} 件",
                $"{retainer.Name} ({job}): {venture} / {retainer.MarketItemCount} listed"));

            var market = snapshot.RetainerMarket.FirstOrDefault(m => m.RetainerName == retainer.Name);
            if (market != null && market.Listings.Count > 0)
            {
                var total = market.Listings.Sum(listing => (long)listing.Price * listing.Count);
                lines.Add(L($"  出品リスト {market.Listings.Count} 件（合計 {Num(total)} ギル）",
                    $"  {market.Listings.Count} listings (total {Num(total)} gil)"));
            }
        }

        return lines;
    }

    private List<string> BuildOccultLines(Plugin.Snapshot snapshot)
    {
        if (snapshot.OccultCrescent == null)
            return NoData();
        var occult = snapshot.OccultCrescent;
        var level = L($"知識レベル {occult.KnowledgeLevel}", $"Knowledge level {occult.KnowledgeLevel}");
        if (occult.KnowledgeLevelEffective != occult.KnowledgeLevel)
            level += L($"（実効 {occult.KnowledgeLevelEffective}）", $" (effective {occult.KnowledgeLevelEffective})");
        level += L($" / 知識EXP {Num(occult.CurrentKnowledge)} / {Num(occult.NeededKnowledge)}",
            $" / Knowledge EXP {Num(occult.CurrentKnowledge)} / {Num(occult.NeededKnowledge)}");
        var lines = new List<string>
        {
            level,
            L($"現在のファントムジョブ: {occult.CurrentSupportJobName ?? "-"}",
                $"Current phantom job: {occult.CurrentSupportJobName ?? "-"}"),
            L($"南ホーン: 銀貨 {Num(occult.SilverPieces)} / 金貨 {Num(occult.GoldPieces)} ・ 北ホーン: 銀貨オーボル {Num(occult.SilverObols)} / 金貨オーボル {Num(occult.GoldObols)}",
                $"South Horn: {Num(occult.SilverPieces)} silver / {Num(occult.GoldPieces)} gold pieces · North Horn: {Num(occult.SilverObols)} silver obols / {Num(occult.GoldObols)} gold obols"),
        };
        for (var i = 0; i < occult.SupportJobs.Count; i += 2)
        {
            var left = FormatSupportJob(occult.SupportJobs[i]);
            var right = i + 1 < occult.SupportJobs.Count ? FormatSupportJob(occult.SupportJobs[i + 1]) : null;
            lines.Add(right == null ? left : $"{left} / {right}");
        }

        return lines;
    }

    private List<string> BuildFolkloreLines(Plugin.Snapshot snapshot)
    {
        if (snapshot.Folklore == null || snapshot.Folklore.Count == 0)
            return NoData();
        var books = snapshot.Folklore;
        var unlocked = books.Count(b => b.Unlocked);
        var lines = new List<string>
        {
            L($"解放済み {unlocked} / {books.Count} 冊", $"{unlocked} / {books.Count} books unlocked"),
        };
        foreach (var group in books.GroupBy(b => b.ClassJobId).OrderBy(g => g.Key))
        {
            var groupBooks = group.OrderBy(b => b.QuestIndex).ToList();
            var groupUnlocked = groupBooks.Count(b => b.Unlocked);
            lines.Add($"{groupBooks[0].Job}: {groupUnlocked} / {groupBooks.Count}");
            for (var i = 0; i < groupBooks.Count; i += 2)
            {
                var left = FormatFolkloreBook(groupBooks[i]);
                var right = i + 1 < groupBooks.Count ? FormatFolkloreBook(groupBooks[i + 1]) : null;
                lines.Add(right == null ? left : $"{left} / {right}");
            }
        }

        return lines;
    }

    private string FormatFolkloreBook(Plugin.FolkloreTomeEntry book)
    {
        return book.Unlocked
            ? book.Name
            : L($"{book.Name}（未解放）", $"{book.Name} (locked)");
    }

    private string FormatSupportJob(Plugin.OccultSupportJobEntry job)
    {
        var text = $"{job.Name ?? $"#{job.JobId}"} Lv{job.Level}";
        if (job.LevelMax > 0 && job.Level >= job.LevelMax)
            text += " MASTER";
        else if (job.NeededExp > 0)
            text += $" ({Num(job.Exp)} / {Num(job.NeededExp)})";
        return text;
    }

    private List<string> NoData() => new() { L("未取得", "No data") };

    private static uint NormalizeItemId(uint itemId) => itemId % 1_000_000;

    private static string Num(long value) => value.ToString("#,0");

    private static string FormatTimestamp(string value, bool dateOnly = false)
    {
        if (DateTimeOffset.TryParse(value, out var parsed))
            return dateOnly
                ? parsed.LocalDateTime.ToString("yyyy-MM-dd")
                : parsed.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        return value;
    }

    private static void DrawIcon(FontAwesomeIcon icon, Vector4 color)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            ImGui.TextColored(color, ((char)icon).ToString());
        }
    }

    private sealed class CharacterTab
    {
        public CharacterTab(Plugin.Snapshot snapshot, bool isCurrent)
        {
            Snapshot = snapshot;
            IsCurrent = isCurrent;
        }

        public Plugin.Snapshot Snapshot { get; }
        public bool IsCurrent { get; }
        public string Label => $"{Snapshot.Character} ({Snapshot.World})";
        public string Key => $"{Snapshot.Character}|{Snapshot.World}";
    }
}
