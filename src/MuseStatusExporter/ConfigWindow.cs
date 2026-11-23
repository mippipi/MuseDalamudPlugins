using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace MuseStatusExporter;

/// <summary>
/// Settings window in the style of Auto FATE Grind's configuration UI:
/// a sidebar with six categories on the left, and on the right one page
/// per category holding panelled sections whose rows carry a graphical
/// toggle switch. The switches are the same settings as before (the
/// Configuration fields are unchanged); changes are saved immediately,
/// so there is no separate apply/save button. The whole window can be
/// switched between Japanese and English from the header buttons; the
/// choice is stored in Configuration.UiLanguage (0 = Japanese,
/// 1 = English).
/// </summary>
public sealed class ConfigWindow : Window
{
    private const float RowHeight = 36f;
    private const float SidebarWidth = 184f;
    private const float ToggleWidth = 40f;
    private const float ToggleHeight = 22f;

    private static readonly Vector4 Accent = new(0.62f, 0.45f, 0.98f, 1f);
    private static readonly Vector4 AccentDim = new(0.38f, 0.27f, 0.66f, 0.65f);
    private static readonly Vector4 HoverDim = new(0.30f, 0.22f, 0.50f, 0.45f);
    private static readonly Vector4 WindowBackground = new(0.05f, 0.05f, 0.08f, 1f);
    private static readonly Vector4 SidebarBackground = new(0.07f, 0.07f, 0.11f, 1f);
    private static readonly Vector4 ContentBackground = new(0.06f, 0.06f, 0.10f, 1f);
    private static readonly Vector4 PanelBackground = new(0.10f, 0.10f, 0.15f, 1f);
    private static readonly Vector4 BorderColor = new(0.30f, 0.26f, 0.42f, 0.55f);
    private static readonly Vector4 MutedText = new(0.62f, 0.62f, 0.70f, 1f);
    private static readonly Vector4 ToggleOff = new(0.24f, 0.24f, 0.30f, 1f);
    private static readonly Vector4 ToggleOffHover = new(0.34f, 0.34f, 0.42f, 1f);
    private static readonly Vector4 RowSeparator = new(1f, 1f, 1f, 0.07f);

    private readonly Configuration configuration;
    private readonly List<Category> categories;
    private int selectedCategory;

    private bool English => configuration.UiLanguage == 1;

    private string L(string japanese, string english) => English ? english : japanese;

    public ConfigWindow(Configuration configuration)
        : base("Muse Status Exporter 設定###MuseStatusExporterConfig")
    {
        this.configuration = configuration;
        if (configuration.UiLanguage == 1)
            WindowName = "Muse Status Exporter Settings###MuseStatusExporterConfig";
        this.categories = BuildCategories();
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(660, 440),
            MaximumSize = new Vector2(1000, 1000),
        };
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
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.ChildBg, SidebarBackground);
        ImGui.BeginChild("##categories", new Vector2(SidebarWidth, 0), true);
        ImGui.PopStyleColor();
        for (var i = 0; i < categories.Count; i++)
            DrawCategoryRow(i);
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.ChildBg, ContentBackground);
        ImGui.BeginChild("##categoryContent", new Vector2(0, 0), true);
        ImGui.PopStyleColor();
        DrawCategoryPage(categories[selectedCategory]);
        ImGui.EndChild();
    }

    private void DrawHeader()
    {
        DrawIcon(FontAwesomeIcon.ChartBar, Accent);
        ImGui.SameLine(0, 9);
        var titlePos = ImGui.GetCursorPos();
        ImGui.SetWindowFontScale(1.2f);
        ImGui.TextUnformatted(L("Muse Status Exporter 設定", "Muse Status Exporter Settings"));
        ImGui.SetWindowFontScale(1f);

        DrawLanguageButtons(titlePos);

        ImGui.TextColored(MutedText, L(
            "各機能のオン／オフを切り替えます。変更はすぐに保存されます。",
            "Toggle each feature on or off. Changes are saved immediately."));

        var enabled = 0;
        var total = 0;
        foreach (var category in categories)
        {
            foreach (var item in category.Items)
            {
                total++;
                if (item.Get())
                    enabled++;
            }
        }

        ImGui.Spacing();
        ImGui.TextColored(MutedText, English
            ? $"Enabled features: {enabled} / {total}"
            : $"有効な機能: {enabled} / {total}");
        ImGui.ProgressBar(total == 0 ? 0f : (float)enabled / total, new Vector2(-1, 6), string.Empty);
    }

    /// <summary>
    /// Japanese / English switch at the right end of the title line. The
    /// language names are always written in their own language. Switching
    /// also retitles the window; the ### id part of the window name stays
    /// constant, so the window keeps its position and size.
    /// </summary>
    private void DrawLanguageButtons(Vector2 titlePos)
    {
        const string jaLabel = "日本語";
        const string enLabel = "English";
        var style = ImGui.GetStyle();
        var buttonsWidth = ImGui.CalcTextSize(jaLabel).X + ImGui.CalcTextSize(enLabel).X
            + style.FramePadding.X * 4 + style.ItemSpacing.X;
        ImGui.SetCursorPos(new Vector2(ImGui.GetWindowContentRegionMax().X - buttonsWidth, titlePos.Y));

        if (DrawLanguageButton(jaLabel, !English))
        {
            configuration.UiLanguage = 0;
            configuration.Save();
            WindowName = "Muse Status Exporter 設定###MuseStatusExporterConfig";
        }

        ImGui.SameLine();

        if (DrawLanguageButton(enLabel, English))
        {
            configuration.UiLanguage = 1;
            configuration.Save();
            WindowName = "Muse Status Exporter Settings###MuseStatusExporterConfig";
        }
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

    private void DrawCategoryRow(int index)
    {
        var category = categories[index];
        var selected = index == selectedCategory;
        var pos = ImGui.GetCursorPos();
        var avail = ImGui.GetContentRegionAvail().X;

        ImGui.PushStyleColor(ImGuiCol.Header, selected ? AccentDim : Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, HoverDim);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, AccentDim);
        if (ImGui.Selectable($"##category{index}", selected, ImGuiSelectableFlags.None, new Vector2(0, 34)))
            selectedCategory = index;
        ImGui.PopStyleColor(3);

        ImGui.SetCursorPos(new Vector2(pos.X + 12, pos.Y + 8));
        DrawIcon(category.Icon, selected ? Vector4.One : MutedText);
        ImGui.SameLine(0, 9);
        if (selected)
            ImGui.TextUnformatted(category.Name(English));
        else
            ImGui.TextColored(MutedText, category.Name(English));

        var enabled = category.Items.Count(item => item.Get());
        var countText = $"{enabled}/{category.Items.Count}";
        var countWidth = ImGui.CalcTextSize(countText).X;
        ImGui.SetCursorPos(new Vector2(pos.X + avail - countWidth, pos.Y + 8));
        ImGui.TextColored(MutedText, countText);

        ImGui.SetCursorPos(new Vector2(pos.X, pos.Y + 36));
    }

    private void DrawCategoryPage(Category category)
    {
        DrawIcon(category.Icon, Accent);
        ImGui.SameLine(0, 9);
        var titlePos = ImGui.GetCursorPos();
        ImGui.SetWindowFontScale(1.15f);
        ImGui.TextUnformatted(category.Name(English));
        ImGui.SetWindowFontScale(1f);

        // "All on / all off" buttons for this category, right-aligned on
        // the title line. The row width is computed from the actual style
        // metrics (frame padding and item spacing), and the row is aligned
        // against the child window's content region max. (An earlier
        // version measured GetContentRegionAvail() after the title text
        // had already advanced the cursor to the next line, which made
        // the computed right edge overshoot by the title icon's offset
        // and clipped the last button.)
        var onLabel = L("すべてオン", "All On");
        var offLabel = L("すべてオフ", "All Off");
        var style = ImGui.GetStyle();
        var buttonsWidth = ImGui.CalcTextSize(onLabel).X + ImGui.CalcTextSize(offLabel).X
            + style.FramePadding.X * 4 + style.ItemSpacing.X;
        ImGui.SetCursorPos(new Vector2(ImGui.GetWindowContentRegionMax().X - buttonsWidth, titlePos.Y));
        if (ImGui.SmallButton(onLabel))
            SetAll(category, true);
        ImGui.SameLine();
        if (ImGui.SmallButton(offLabel))
            SetAll(category, false);

        for (var s = 0; s < category.Sections.Count; s++)
        {
            var section = category.Sections[s];
            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.TextColored(MutedText, section.Title(English));
            ImGui.Spacing();

            var panelHeight = section.Items.Count * RowHeight + 10;
            ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBackground);
            ImGui.BeginChild($"##panel{selectedCategory}_{s}", new Vector2(0, panelHeight), true, ImGuiWindowFlags.NoScrollbar);
            ImGui.PopStyleColor();
            for (var j = 0; j < section.Items.Count; j++)
                DrawSettingRow(section.Items[j], j == section.Items.Count - 1);
            ImGui.EndChild();
        }
    }

    private void DrawSettingRow(SettingItem item, bool isLast)
    {
        var pos = ImGui.GetCursorPos();
        var screenPos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;

        ImGui.SetCursorPos(new Vector2(pos.X + 12, pos.Y + (RowHeight - ImGui.GetTextLineHeight()) / 2f));
        ImGui.TextUnformatted(item.Label(English));
        var tooltip = item.Tooltip(English);
        if (tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);

        ImGui.SetCursorPos(new Vector2(pos.X + avail - ToggleWidth - 12, pos.Y + (RowHeight - ToggleHeight) / 2f));
        if (DrawToggle(item.Id, item.Get()))
        {
            item.Set(!item.Get());
            configuration.Save();
        }

        if (!isLast)
        {
            var drawList = ImGui.GetWindowDrawList();
            var y = screenPos.Y + RowHeight - 0.5f;
            drawList.AddLine(
                new Vector2(screenPos.X + 12, y),
                new Vector2(screenPos.X + avail, y),
                ImGui.GetColorU32(RowSeparator),
                1f);
        }

        ImGui.SetCursorPos(new Vector2(pos.X, pos.Y + RowHeight));
    }

    /// <summary>Draws a pill-shaped on/off switch; returns true when clicked.</summary>
    private static bool DrawToggle(string id, bool value)
    {
        var size = new Vector2(ToggleWidth, ToggleHeight);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##toggle{id}", size);
        var clicked = ImGui.IsItemClicked();
        var hovered = ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();
        var background = value ? Accent : hovered ? ToggleOffHover : ToggleOff;
        drawList.AddRectFilled(pos, pos + size, ImGui.GetColorU32(background), ToggleHeight / 2f);
        var knobCenter = new Vector2(
            value ? pos.X + size.X - ToggleHeight / 2f : pos.X + ToggleHeight / 2f,
            pos.Y + ToggleHeight / 2f);
        drawList.AddCircleFilled(knobCenter, ToggleHeight / 2f - 3f, ImGui.GetColorU32(Vector4.One));
        return clicked;
    }

    private static void DrawIcon(FontAwesomeIcon icon, Vector4 color)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            ImGui.TextColored(color, ((char)icon).ToString());
        }
    }

    private void SetAll(Category category, bool value)
    {
        foreach (var item in category.Items)
            item.Set(value);
        configuration.Save();
    }

    private List<Category> BuildCategories()
    {
        SettingItem Item(string id, string labelJa, string labelEn, string? tooltipJa, string? tooltipEn, Func<bool> get, Action<bool> set)
            => new(id, labelJa, labelEn, tooltipJa, tooltipEn, get, set);

        return new List<Category>
        {
            new(FontAwesomeIcon.ChartBar, "ステータス", "Status", new List<SettingSection>
            {
                new("基本", "Basics", new List<SettingItem>
                {
                    Item("ExportStatus", "ステータス書き出し（マスター）", "Status Export (Master)",
                        "ジョブレベル・コスモ・装備など、ステータスJSONの書き出し全体のオン／オフです",
                        "Master switch for the whole status JSON export (job levels, cosmic exploration, gear, and more).",
                        () => configuration.ExportStatus, v => configuration.ExportStatus = v),
                }),
                new("記録する情報", "Recorded Information", new List<SettingItem>
                {
                    Item("ExportCurrencies", "所持通貨", "Currencies", null, null,
                        () => configuration.ExportCurrencies, v => configuration.ExportCurrencies = v),
                    Item("ExportInventory", "インベントリ", "Inventory", null, null,
                        () => configuration.ExportInventory, v => configuration.ExportInventory = v),
                    Item("ExportRetainers", "リテイナー情報", "Retainer Information", null, null,
                        () => configuration.ExportRetainers, v => configuration.ExportRetainers = v),
                    Item("ExportFreeCompany", "FC情報（名前・タグ）", "Free Company Info (Name & Tag)",
                        "ログイン中のキャラクターのFC（名前・タグ）を記録します",
                        "Records the logged-in character's Free Company (name and tag).",
                        () => configuration.ExportFreeCompany, v => configuration.ExportFreeCompany = v),
                    Item("RecordPlayTime", "プレイ時間", "Play Time", null, null,
                        () => configuration.RecordPlayTime, v => configuration.RecordPlayTime = v),
                }),
            }),
            new(FontAwesomeIcon.Tasks, "進行状況", "Progress", new List<SettingSection>
            {
                new("進行・達成度", "Progress & Achievements", new List<SettingItem>
                {
                    Item("ExportMainScenario", "メインクエスト進行状況", "Main Scenario Progress", null, null,
                        () => configuration.ExportMainScenario, v => configuration.ExportMainScenario = v),
                    Item("ExportFateProgress", "共有FATE達成度", "Shared FATE Progress", null, null,
                        () => configuration.ExportFateProgress, v => configuration.ExportFateProgress = v),
                    Item("ExportQuestProgress", "クラス・ジョブ・ロールクエスト進行状況", "Class, Job & Role Quest Progress",
                        "全ジョブのクラス・ジョブ・ロールクエストと、ギャザクラのデリバリー（クリスタリウム／スタディウム／ワチュメキメキ）の完了／未完了を記録します",
                        "Records completion of every class, job and role quest, plus the crafter/gatherer delivery lines (Crystalline Mean / Studium / Wachumeqimeqi).",
                        () => configuration.ExportQuestProgress, v => configuration.ExportQuestProgress = v),
                    Item("ExportOccultCrescent", "クレセントアイルの進捗", "Occult Crescent Progress",
                        "知識レベル・ファントムジョブ・銀貨／金貨を記録します",
                        "Records knowledge level, phantom jobs, and silver/gold pieces.",
                        () => configuration.ExportOccultCrescent, v => configuration.ExportOccultCrescent = v),
                    Item("ExportBuddy", "バディ（チョコボ）", "Buddy (Chocobo)",
                        "バディのランクと経験値を記録します",
                        "Records your buddy's rank and EXP.",
                        () => configuration.ExportBuddy, v => configuration.ExportBuddy = v),
                    Item("ExportWeeklyTomestone", "週間トームストーン", "Weekly Tomestones", null, null,
                        () => configuration.ExportWeeklyTomestone, v => configuration.ExportWeeklyTomestone = v),
                    Item("ExportRouletteCompletion", "コンテンツルーレット消化状況", "Duty Roulette Completion", null, null,
                        () => configuration.ExportRouletteCompletion, v => configuration.ExportRouletteCompletion = v),
                }),
            }),
            new(FontAwesomeIcon.ShieldAlt, "GC・リテイナー", "GC & Retainers", new List<SettingSection>
            {
                new("大隊・リテイナー", "Grand Company & Retainers", new List<SettingItem>
                {
                    Item("CaptureGcSupply", "GC軍需品リストの取得", "Capture GC Supply List", null, null,
                        () => configuration.CaptureGcSupply, v => configuration.CaptureGcSupply = v),
                    Item("ProbeGcAgent", "GCエージェントの状態（実験）", "GC Agent State (Experimental)", null, null,
                        () => configuration.ProbeGcAgent, v => configuration.ProbeGcAgent = v),
                    Item("ExportRetainerMarket", "リテイナーの出品リスト", "Retainer Market Listings", null, null,
                        () => configuration.ExportRetainerMarket, v => configuration.ExportRetainerMarket = v),
                }),
            }),
            new(FontAwesomeIcon.Home, "ハウス", "Housing", new List<SettingSection>
            {
                new("ハウス", "Housing", new List<SettingItem>
                {
                    Item("RecordHousingVisits", "ハウス訪問の記録", "Record House Visits", null, null,
                        () => configuration.RecordHousingVisits, v => configuration.RecordHousingVisits = v),
                    Item("LearnHousingOwnership", "所有情報を看板で学習", "Learn Ownership from Placards",
                        "看板を開いたときにハウスの所有情報を学習し、所有者／FCメンバーの入室だけを最終入室に反映します",
                        "Learns each house's ownership when you view its placard, so only the owner's or an FC member's entry advances the last entry date.",
                        () => configuration.LearnHousingOwnership, v => configuration.LearnHousingOwnership = v),
                    Item("HousingDeadlineNotice", "ログイン時に撤去予定日を表示", "Show Demolition Dates on Login",
                        "各ハウスの最終入室からの経過日数と撤去予定日を表示し、撤去が近い場合は警告します",
                        "Shows the days since each house's last entry and its demolition date, and warns when demolition is near.",
                        () => configuration.HousingDeadlineNotice, v => configuration.HousingDeadlineNotice = v),
                    Item("HousingEntryNotice", "最終入室の更新を知らせる", "Notify on Last-Entry Updates",
                        "資格のある入室でハウスの最終入室が更新されたら知らせます",
                        "Notifies you when a qualified entry updates a house's last entry date.",
                        () => configuration.HousingEntryNotice, v => configuration.HousingEntryNotice = v),
                }),
            }),
            new(FontAwesomeIcon.Comments, "チャット・通知", "Chat & Alerts", new List<SettingSection>
            {
                new("チャット記録", "Chat Recording", new List<SettingItem>
                {
                    Item("RecordTells", "Tell（送受信）", "Tells (Incoming & Outgoing)", null, null,
                        () => configuration.RecordTells, v => configuration.RecordTells = v),
                    Item("RecordPartyChat", "パーティチャット", "Party Chat",
                        "クロスワールドPTのチャットも含みます",
                        "Includes cross-world party chat.",
                        () => configuration.RecordPartyChat, v => configuration.RecordPartyChat = v),
                }),
                new("ゲーム内通知", "In-Game Notifications", new List<SettingItem>
                {
                    Item("InGameAlerts", "ゲーム内通知", "In-Game Notifications",
                        "ベンチャー完了・バディのレベルアップ可能・GC新リストを通知します",
                        "Notifies venture completion, buddy level-up readiness, and new GC supply lists.",
                        () => configuration.InGameAlerts, v => configuration.InGameAlerts = v),
                    Item("LoginSummary", "ログイン時サマリー", "Login Summary",
                        "GC軍需品の不足・ベンチャー・週間トームストーンを表示します",
                        "Shows GC supply shortages, ventures, and weekly tomestones on login.",
                        () => configuration.LoginSummary, v => configuration.LoginSummary = v),
                    Item("CrossCharacterSummary", "他キャラのベンチャー完了を集約", "Include Other Characters' Ventures",
                        "ログインサマリーに他キャラのベンチャー完了をまとめます",
                        "Adds other characters' completed ventures to the login summary.",
                        () => configuration.CrossCharacterSummary, v => configuration.CrossCharacterSummary = v),
                    Item("DurabilityWarning", "装備の耐久度低下の警告", "Low Durability Warning", null, null,
                        () => configuration.DurabilityWarning, v => configuration.DurabilityWarning = v),
                }),
            }),
            new(FontAwesomeIcon.Cog, "システム", "System", new List<SettingSection>
            {
                new("連携・その他", "Integration & Other", new List<SettingItem>
                {
                    Item("ConfigDownlink", "設定ダウンリンク", "Configuration Downlink",
                        "Museからの設定適用（config-inbox）を受け付けます",
                        "Accepts configuration changes sent by Muse (config-inbox).",
                        () => configuration.ConfigDownlink, v => configuration.ConfigDownlink = v),
                    Item("AutoEquipHaloneEarring", "ハルオーネイヤリングの自動装備", "Auto-Equip Halone Earring", null, null,
                        () => configuration.AutoEquipHaloneEarring, v => configuration.AutoEquipHaloneEarring = v),
                    Item("AutoUseThavnairianOnion", "バディ満タン時のサベネアの野菜自動使用", "Auto-Use Thavnairian Onion at Full Buddy EXP",
                        "バディ（ランク10〜19）の経験値が満タンでランク上限を上げられる状態のとき、フィールドでバディを呼び出していれば、カバンのサベネアの野菜を自動で使います。貴重なアイテムを消費するため、初期設定はオフです",
                        "When your buddy (rank 10-19) has a full EXP bar and its rank cap can be raised, automatically uses a Thavnairian Onion from your bags while the buddy is summoned in the field. Off by default, as the onion is a valuable consumable.",
                        () => configuration.AutoUseThavnairianOnion, v => configuration.AutoUseThavnairianOnion = v),
                }),
            }),
        };
    }

    private sealed class SettingItem
    {
        public SettingItem(string id, string labelJa, string labelEn, string? tooltipJa, string? tooltipEn, Func<bool> get, Action<bool> set)
        {
            Id = id;
            LabelJa = labelJa;
            LabelEn = labelEn;
            TooltipJa = tooltipJa;
            TooltipEn = tooltipEn;
            Get = get;
            Set = set;
        }

        public string Id { get; }
        public string LabelJa { get; }
        public string LabelEn { get; }
        public string? TooltipJa { get; }
        public string? TooltipEn { get; }
        public Func<bool> Get { get; }
        public Action<bool> Set { get; }

        public string Label(bool english) => english ? LabelEn : LabelJa;

        public string? Tooltip(bool english) => english ? TooltipEn : TooltipJa;
    }

    private sealed class SettingSection
    {
        public SettingSection(string titleJa, string titleEn, List<SettingItem> items)
        {
            TitleJa = titleJa;
            TitleEn = titleEn;
            Items = items;
        }

        public string TitleJa { get; }
        public string TitleEn { get; }
        public List<SettingItem> Items { get; }

        public string Title(bool english) => english ? TitleEn : TitleJa;
    }

    private sealed class Category
    {
        public Category(FontAwesomeIcon icon, string nameJa, string nameEn, List<SettingSection> sections)
        {
            Icon = icon;
            NameJa = nameJa;
            NameEn = nameEn;
            Sections = sections;
            Items = sections.SelectMany(section => section.Items).ToList();
        }

        public FontAwesomeIcon Icon { get; }
        public string NameJa { get; }
        public string NameEn { get; }
        public List<SettingSection> Sections { get; }
        public List<SettingItem> Items { get; }

        public string Name(bool english) => english ? NameEn : NameJa;
    }
}
