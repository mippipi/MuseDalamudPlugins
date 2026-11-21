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
/// so there is no separate apply/save button.
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

    public ConfigWindow(Configuration configuration)
        : base("Muse Status Exporter 設定###MuseStatusExporterConfig")
    {
        this.configuration = configuration;
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
        ImGui.SetWindowFontScale(1.2f);
        ImGui.TextUnformatted("Muse Status Exporter 設定");
        ImGui.SetWindowFontScale(1f);
        ImGui.TextColored(MutedText, "各機能のオン／オフを切り替えます。変更はすぐに保存されます。");

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
        ImGui.TextColored(MutedText, $"有効な機能: {enabled} / {total}");
        ImGui.ProgressBar(total == 0 ? 0f : (float)enabled / total, new Vector2(-1, 6), string.Empty);
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
            ImGui.TextUnformatted(category.Name);
        else
            ImGui.TextColored(MutedText, category.Name);

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
        ImGui.TextUnformatted(category.Name);
        ImGui.SetWindowFontScale(1f);

        // "All on / all off" buttons for this category, right-aligned on
        // the title line.
        const string onLabel = "すべてオン";
        const string offLabel = "すべてオフ";
        var buttonsWidth = ImGui.CalcTextSize(onLabel).X + ImGui.CalcTextSize(offLabel).X + 44;
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPos(new Vector2(titlePos.X + avail - buttonsWidth + ImGui.GetStyle().WindowPadding.X, titlePos.Y));
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
            ImGui.TextColored(MutedText, section.Title);
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
        ImGui.TextUnformatted(item.Label);
        if (item.Tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(item.Tooltip);

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
        SettingItem Item(string id, string label, string? tooltip, Func<bool> get, Action<bool> set)
            => new(id, label, tooltip, get, set);

        return new List<Category>
        {
            new(FontAwesomeIcon.ChartBar, "ステータス", new List<SettingSection>
            {
                new("基本", new List<SettingItem>
                {
                    Item("ExportStatus", "ステータス書き出し（マスター）",
                        "ジョブレベル・コスモ・装備など、ステータスJSONの書き出し全体のオン／オフです",
                        () => configuration.ExportStatus, v => configuration.ExportStatus = v),
                }),
                new("記録する情報", new List<SettingItem>
                {
                    Item("ExportCurrencies", "所持通貨", null,
                        () => configuration.ExportCurrencies, v => configuration.ExportCurrencies = v),
                    Item("ExportInventory", "インベントリ", null,
                        () => configuration.ExportInventory, v => configuration.ExportInventory = v),
                    Item("ExportRetainers", "リテイナー情報", null,
                        () => configuration.ExportRetainers, v => configuration.ExportRetainers = v),
                    Item("ExportFreeCompany", "FC情報（名前・タグ）",
                        "ログイン中のキャラクターのFC（名前・タグ）を記録します",
                        () => configuration.ExportFreeCompany, v => configuration.ExportFreeCompany = v),
                    Item("RecordPlayTime", "プレイ時間", null,
                        () => configuration.RecordPlayTime, v => configuration.RecordPlayTime = v),
                }),
            }),
            new(FontAwesomeIcon.Tasks, "進行状況", new List<SettingSection>
            {
                new("進行・達成度", new List<SettingItem>
                {
                    Item("ExportMainScenario", "メインクエスト進行状況", null,
                        () => configuration.ExportMainScenario, v => configuration.ExportMainScenario = v),
                    Item("ExportFateProgress", "共有FATE達成度", null,
                        () => configuration.ExportFateProgress, v => configuration.ExportFateProgress = v),
                    Item("ExportOccultCrescent", "クレセントアイルの進捗",
                        "知識レベル・ファントムジョブ・銀貨／金貨を記録します",
                        () => configuration.ExportOccultCrescent, v => configuration.ExportOccultCrescent = v),
                    Item("ExportBuddy", "バディ（チョコボ）",
                        "バディのランクと経験値を記録します",
                        () => configuration.ExportBuddy, v => configuration.ExportBuddy = v),
                    Item("ExportWeeklyTomestone", "週間トームストーン", null,
                        () => configuration.ExportWeeklyTomestone, v => configuration.ExportWeeklyTomestone = v),
                    Item("ExportRouletteCompletion", "コンテンツルーレット消化状況", null,
                        () => configuration.ExportRouletteCompletion, v => configuration.ExportRouletteCompletion = v),
                }),
            }),
            new(FontAwesomeIcon.ShieldAlt, "GC・リテイナー", new List<SettingSection>
            {
                new("大隊・リテイナー", new List<SettingItem>
                {
                    Item("CaptureGcSupply", "GC軍需品リストの取得", null,
                        () => configuration.CaptureGcSupply, v => configuration.CaptureGcSupply = v),
                    Item("ProbeGcAgent", "GCエージェントの状態（実験）", null,
                        () => configuration.ProbeGcAgent, v => configuration.ProbeGcAgent = v),
                    Item("ExportRetainerMarket", "リテイナーの出品リスト", null,
                        () => configuration.ExportRetainerMarket, v => configuration.ExportRetainerMarket = v),
                }),
            }),
            new(FontAwesomeIcon.Home, "ハウス", new List<SettingSection>
            {
                new("ハウス", new List<SettingItem>
                {
                    Item("RecordHousingVisits", "ハウス訪問の記録", null,
                        () => configuration.RecordHousingVisits, v => configuration.RecordHousingVisits = v),
                    Item("LearnHousingOwnership", "所有情報を看板で学習",
                        "看板を開いたときにハウスの所有情報を学習し、所有者／FCメンバーの入室だけを最終入室に反映します",
                        () => configuration.LearnHousingOwnership, v => configuration.LearnHousingOwnership = v),
                    Item("HousingDeadlineNotice", "ログイン時に撤去予定日を表示",
                        "各ハウスの最終入室からの経過日数と撤去予定日を表示し、撤去が近い場合は警告します",
                        () => configuration.HousingDeadlineNotice, v => configuration.HousingDeadlineNotice = v),
                    Item("HousingEntryNotice", "最終入室の更新を知らせる",
                        "資格のある入室でハウスの最終入室が更新されたら知らせます",
                        () => configuration.HousingEntryNotice, v => configuration.HousingEntryNotice = v),
                }),
            }),
            new(FontAwesomeIcon.Comments, "チャット・通知", new List<SettingSection>
            {
                new("チャット記録", new List<SettingItem>
                {
                    Item("RecordTells", "Tell（送受信）", null,
                        () => configuration.RecordTells, v => configuration.RecordTells = v),
                    Item("RecordPartyChat", "パーティチャット",
                        "クロスワールドPTのチャットも含みます",
                        () => configuration.RecordPartyChat, v => configuration.RecordPartyChat = v),
                }),
                new("ゲーム内通知", new List<SettingItem>
                {
                    Item("InGameAlerts", "ゲーム内通知",
                        "ベンチャー完了・バディのレベルアップ可能・GC新リストを通知します",
                        () => configuration.InGameAlerts, v => configuration.InGameAlerts = v),
                    Item("LoginSummary", "ログイン時サマリー",
                        "GC軍需品の不足・ベンチャー・週間トームストーンを表示します",
                        () => configuration.LoginSummary, v => configuration.LoginSummary = v),
                    Item("CrossCharacterSummary", "他キャラのベンチャー完了を集約",
                        "ログインサマリーに他キャラのベンチャー完了をまとめます",
                        () => configuration.CrossCharacterSummary, v => configuration.CrossCharacterSummary = v),
                    Item("DurabilityWarning", "装備の耐久度低下の警告", null,
                        () => configuration.DurabilityWarning, v => configuration.DurabilityWarning = v),
                }),
            }),
            new(FontAwesomeIcon.Cog, "システム", new List<SettingSection>
            {
                new("連携・その他", new List<SettingItem>
                {
                    Item("ConfigDownlink", "設定ダウンリンク",
                        "Museからの設定適用（config-inbox）を受け付けます",
                        () => configuration.ConfigDownlink, v => configuration.ConfigDownlink = v),
                    Item("AutoEquipHaloneEarring", "ハルオーネイヤリングの自動装備", null,
                        () => configuration.AutoEquipHaloneEarring, v => configuration.AutoEquipHaloneEarring = v),
                }),
            }),
        };
    }

    private sealed class SettingItem
    {
        public SettingItem(string id, string label, string? tooltip, Func<bool> get, Action<bool> set)
        {
            Id = id;
            Label = label;
            Tooltip = tooltip;
            Get = get;
            Set = set;
        }

        public string Id { get; }
        public string Label { get; }
        public string? Tooltip { get; }
        public Func<bool> Get { get; }
        public Action<bool> Set { get; }
    }

    private sealed class SettingSection
    {
        public SettingSection(string title, List<SettingItem> items)
        {
            Title = title;
            Items = items;
        }

        public string Title { get; }
        public List<SettingItem> Items { get; }
    }

    private sealed class Category
    {
        public Category(FontAwesomeIcon icon, string name, List<SettingSection> sections)
        {
            Icon = icon;
            Name = name;
            Sections = sections;
            Items = sections.SelectMany(section => section.Items).ToList();
        }

        public FontAwesomeIcon Icon { get; }
        public string Name { get; }
        public List<SettingSection> Sections { get; }
        public List<SettingItem> Items { get; }
    }
}
