using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MuseStatusExporter;

/// <summary>
/// Settings window with one checkbox per plugin feature. Changes are saved
/// immediately, so there is no separate apply/save button.
/// </summary>
public sealed class ConfigWindow : Window
{
    private readonly Configuration configuration;

    public ConfigWindow(Configuration configuration)
        : base("Muse Status Exporter 設定###MuseStatusExporterConfig")
    {
        this.configuration = configuration;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(430, 330),
            MaximumSize = new Vector2(700, 900),
        };
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("各機能のオン／オフを切り替えます。変更はすぐに保存されます。");
        ImGui.Separator();

        DrawCheckbox("ステータス書き出し（ジョブレベル・コスモ・装備）",
            () => configuration.ExportStatus, v => configuration.ExportStatus = v);
        DrawCheckbox("所持通貨を記録する",
            () => configuration.ExportCurrencies, v => configuration.ExportCurrencies = v);
        DrawCheckbox("インベントリを記録する",
            () => configuration.ExportInventory, v => configuration.ExportInventory = v);
        DrawCheckbox("リテイナー情報を記録する",
            () => configuration.ExportRetainers, v => configuration.ExportRetainers = v);
        DrawCheckbox("GC軍需品リストを取得する",
            () => configuration.CaptureGcSupply, v => configuration.CaptureGcSupply = v);
        DrawCheckbox("ハウス訪問を記録する",
            () => configuration.RecordHousingVisits, v => configuration.RecordHousingVisits = v);
        DrawCheckbox("Tellを記録する",
            () => configuration.RecordTells, v => configuration.RecordTells = v);
        DrawCheckbox("設定ダウンリンク（Museからの設定適用）",
            () => configuration.ConfigDownlink, v => configuration.ConfigDownlink = v);
        DrawCheckbox("ハルオーネイヤリングを自動装備する",
            () => configuration.AutoEquipHaloneEarring, v => configuration.AutoEquipHaloneEarring = v);
        DrawCheckbox("メインクエスト進行状況を記録する",
            () => configuration.ExportMainScenario, v => configuration.ExportMainScenario = v);
        DrawCheckbox("共有FATE達成度を記録する",
            () => configuration.ExportFateProgress, v => configuration.ExportFateProgress = v);
        DrawCheckbox("バディ（チョコボ）のランクと経験値を記録する",
            () => configuration.ExportBuddy, v => configuration.ExportBuddy = v);
    }

    private void DrawCheckbox(string label, Func<bool> get, Action<bool> set)
    {
        var value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            configuration.Save();
        }
    }
}
