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
        DrawCheckbox("Tell（送受信）を記録する",
            () => configuration.RecordTells, v => configuration.RecordTells = v);
        DrawCheckbox("パーティチャット（クロスワールドPT含む）を記録する",
            () => configuration.RecordPartyChat, v => configuration.RecordPartyChat = v);
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
        DrawCheckbox("週間トームストーンの取得状況を記録する",
            () => configuration.ExportWeeklyTomestone, v => configuration.ExportWeeklyTomestone = v);
        DrawCheckbox("コンテンツルーレットの消化状況を記録する",
            () => configuration.ExportRouletteCompletion, v => configuration.ExportRouletteCompletion = v);
        DrawCheckbox("プレイ時間を記録する",
            () => configuration.RecordPlayTime, v => configuration.RecordPlayTime = v);
        DrawCheckbox("GCエージェントの状態を記録する（実験）",
            () => configuration.ProbeGcAgent, v => configuration.ProbeGcAgent = v);
        DrawCheckbox("リテイナーの出品リストを記録する",
            () => configuration.ExportRetainerMarket, v => configuration.ExportRetainerMarket = v);
        DrawCheckbox("ゲーム内通知（ベンチャー完了・バディ・GC新リスト）",
            () => configuration.InGameAlerts, v => configuration.InGameAlerts = v);
        DrawCheckbox("ログイン時にサマリーを表示する（GC軍需品・ベンチャー・週間トームストーン）",
            () => configuration.LoginSummary, v => configuration.LoginSummary = v);
        DrawCheckbox("ログイン時にハウスの入室期限を表示する（撤去予定日が近い場合は警告）",
            () => configuration.HousingDeadlineNotice, v => configuration.HousingDeadlineNotice = v);
        DrawCheckbox("看板を開いたときにハウスの所有情報を学習する（所有者／FCメンバーの入室だけを最終入室に反映）",
            () => configuration.LearnHousingOwnership, v => configuration.LearnHousingOwnership = v);
        DrawCheckbox("ログイン中のキャラクターのFC（名前・タグ）を記録する",
            () => configuration.ExportFreeCompany, v => configuration.ExportFreeCompany = v);
        DrawCheckbox("資格のある入室でハウスの最終入室が更新されたら知らせる",
            () => configuration.HousingEntryNotice, v => configuration.HousingEntryNotice = v);
        DrawCheckbox("装備の耐久度が低下したら知らせる",
            () => configuration.DurabilityWarning, v => configuration.DurabilityWarning = v);
        DrawCheckbox("ログインサマリーに他キャラのベンチャー完了をまとめる",
            () => configuration.CrossCharacterSummary, v => configuration.CrossCharacterSummary = v);
        DrawCheckbox("クレセントアイルの進捗（知識レベル・ファントムジョブ・銀貨／金貨）を記録する",
            () => configuration.ExportOccultCrescent, v => configuration.ExportOccultCrescent = v);
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
