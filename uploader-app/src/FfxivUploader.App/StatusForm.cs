using FfxivUploader.Core;

namespace FfxivUploader.App;

/// <summary>状態表示と設定編集の画面。</summary>
public sealed class StatusForm : Form
{
    private readonly TrayContext ctx;
    private readonly Label lblVersion = new();
    private readonly Label lblCred = new();
    private readonly Label lblSync = new();
    private readonly Label lblCollect = new();
    private readonly Label lblUpdate = new();
    private readonly TextBox txtPluginDir = new();
    private readonly TextBox txtConfigsDir = new();
    private readonly TextBox txtCollectRoot = new();
    private readonly TextBox txtLogPath = new();
    private readonly Button btnSetup = new();

    public StatusForm(TrayContext ctx)
    {
        this.ctx = ctx;
        Text = "FFXIV Status Uploader - 状態";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 470);
        Font = new Font("Yu Gothic UI", 9F);

        var y = 14;
        void AddRow(Label label, string caption)
        {
            var cap = new Label { Text = caption, Location = new Point(16, y), Size = new Size(110, 20) };
            label.Location = new Point(130, y);
            label.Size = new Size(414, 20);
            Controls.Add(cap);
            Controls.Add(label);
            y += 24;
        }
        AddRow(lblVersion, "バージョン");
        AddRow(lblCred, "Google 認証");
        AddRow(lblSync, "最終送信");
        AddRow(lblCollect, "最終収集");
        AddRow(lblUpdate, "更新確認");

        y += 6;
        var btnSync = new Button { Text = "今すぐ送信", Location = new Point(16, y), Size = new Size(110, 28) };
        btnSync.Click += async (_, _) => await ctx.RunSyncAsync(manual: true);
        var btnCollect = new Button { Text = "今すぐ収集", Location = new Point(134, y), Size = new Size(110, 28) };
        btnCollect.Click += async (_, _) => await ctx.RunCollectAsync(manual: true);
        var btnUpdate = new Button { Text = "更新を確認", Location = new Point(252, y), Size = new Size(110, 28) };
        btnUpdate.Click += async (_, _) => await ctx.CheckUpdateAsync(manual: true);
        var btnLog = new Button { Text = "ログを開く", Location = new Point(370, y), Size = new Size(110, 28) };
        btnLog.Click += (_, _) => ctx.OpenPath(ctx.LogPath);
        Controls.AddRange(new Control[] { btnSync, btnCollect, btnUpdate, btnLog });
        y += 38;

        btnSetup.Text = "セットアップ（Google認証）";
        btnSetup.Location = new Point(16, y);
        btnSetup.Size = new Size(200, 28);
        btnSetup.Click += async (_, _) => await SetupClicked();
        Controls.Add(btnSetup);
        y += 42;

        var grp = new GroupBox { Text = "フォルダ設定", Location = new Point(16, y), Size = new Size(528, 168) };
        Controls.Add(grp);
        var gy = 24;
        void AddPathRow(TextBox box, string caption)
        {
            var cap = new Label { Text = caption, Location = new Point(12, gy + 3), Size = new Size(150, 20) };
            box.Location = new Point(166, gy);
            box.Size = new Size(348, 23);
            grp.Controls.Add(cap);
            grp.Controls.Add(box);
            gy += 30;
        }
        AddPathRow(txtPluginDir, "ステータス出力先");
        AddPathRow(txtConfigsDir, "プラグイン設定");
        AddPathRow(txtCollectRoot, "収集先ルート");
        AddPathRow(txtLogPath, "Dalamud ログ（空=標準）");
        var btnSave = new Button { Text = "設定を保存", Location = new Point(166, gy + 2), Size = new Size(120, 26) };
        btnSave.Click += (_, _) =>
        {
            ctx.Settings.PluginDir = txtPluginDir.Text.Trim();
            ctx.Settings.PluginConfigsDir = txtConfigsDir.Text.Trim();
            ctx.Settings.CollectRootDir = txtCollectRoot.Text.Trim();
            ctx.Settings.DalamudLogPath = txtLogPath.Text.Trim();
            ctx.Settings.Save(ctx.AppDir);
            MessageBox.Show("設定を保存しました。", "FFXIV Status Uploader");
        };
        grp.Controls.Add(btnSave);

        RefreshView();
    }

    private async Task SetupClicked()
    {
        var path = SetupService.FindClientJson();
        if (path == null)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Google のクライアント JSON を選んでください",
                Filter = "クライアント JSON (client_secret_*.json)|client_secret_*.json|JSON ファイル|*.json",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        btnSetup.Enabled = false;
        try
        {
            await ctx.RunSetupAsync(path);
            MessageBox.Show("セットアップが完了しました。", "FFXIV Status Uploader");
        }
        catch (Exception ex)
        {
            MessageBox.Show("セットアップに失敗しました: " + ex.Message, "FFXIV Status Uploader");
        }
        finally
        {
            btnSetup.Enabled = true;
            RefreshView();
        }
    }

    public void RefreshView()
    {
        if (InvokeRequired) { BeginInvoke(RefreshView); return; }
        lblVersion.Text = ctx.CurrentVersion;
        lblCred.Text = ctx.HasCredentials ? "設定済み" : "未設定（セットアップが必要です）";
        lblSync.Text = (ctx.State.LastSyncAt ?? "—") + "  " + (ctx.State.LastSyncResult ?? "");
        lblCollect.Text = (ctx.State.LastCollectAt ?? "—") + "  " + (ctx.State.LastCollectResult ?? "");
        lblUpdate.Text = (ctx.State.LastUpdateCheckAt ?? "—") +
                         (ctx.State.AvailableVersion != null ? $"  新しい版: {ctx.State.AvailableVersion}" : "");
        if (!txtPluginDir.Focused) txtPluginDir.Text = ctx.Settings.PluginDir;
        if (!txtConfigsDir.Focused) txtConfigsDir.Text = ctx.Settings.PluginConfigsDir;
        if (!txtCollectRoot.Focused) txtCollectRoot.Text = ctx.Settings.CollectRootDir;
        if (!txtLogPath.Focused) txtLogPath.Text = ctx.Settings.DalamudLogPath;
    }
}
