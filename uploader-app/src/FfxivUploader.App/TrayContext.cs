using System.Diagnostics;
using System.IO.Compression;
using FfxivUploader.Core;

namespace FfxivUploader.App;

/// <summary>トレイ常駐の中核。送信（5分）・収集（1時間）・更新確認（24時間）の周期実行と手動操作を担う。</summary>
public sealed class TrayContext : ApplicationContext
{
    public string AppDir { get; }
    public AppLog Log { get; }
    public AppSettings Settings { get; }
    public AppState State { get; }
    public string LogPath => Path.Combine(AppDir, "uploader-app.log");
    public string CurrentVersion { get; }
    public bool HasCredentials => cred != null;

    private readonly CredentialStore credStore;
    private readonly HttpClient http = new();
    private StoredCredentials? cred;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem autoRunItem;
    private readonly ToolStripMenuItem autostartItem;
    private readonly System.Windows.Forms.Timer syncTimer;
    private readonly System.Windows.Forms.Timer collectTimer;
    private readonly System.Windows.Forms.Timer updateTimer;
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private readonly SemaphoreSlim collectGate = new(1, 1);
    private StatusForm? statusForm;
    private bool updateNotified;

    public TrayContext()
    {
        AppDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Log = new AppLog(LogPath);
        Settings = AppSettings.Load(AppDir);
        State = AppState.Load(AppDir);
        CurrentVersion = typeof(TrayContext).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        credStore = new CredentialStore(AppDir, new DpapiProtector());
        LoadCredentials();

        tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "FFXIV Status Uploader",
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("状態を表示", null, (_, _) => ShowStatus());
        showItem.Font = new Font(showItem.Font, FontStyle.Bold);
        menu.Items.Add(showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("今すぐ送信", null, async (_, _) => await RunSyncAsync(manual: true)));
        menu.Items.Add(new ToolStripMenuItem("今すぐ収集", null, async (_, _) => await RunCollectAsync(manual: true)));
        menu.Items.Add(new ToolStripSeparator());
        autoRunItem = new ToolStripMenuItem("自動実行", null, (_, _) => ToggleAutoRun()) { Checked = Settings.AutoRunEnabled };
        menu.Items.Add(autoRunItem);
        autostartItem = new ToolStripMenuItem("ログイン時に自動起動", null, (_, _) => ToggleAutostart()) { Checked = Autostart.IsEnabled() };
        menu.Items.Add(autostartItem);
        menu.Items.Add(new ToolStripMenuItem("更新を確認", null, async (_, _) => await CheckUpdateAsync(manual: true)));
        menu.Items.Add(new ToolStripMenuItem("ログを開く", null, (_, _) => OpenPath(LogPath)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("終了", null, (_, _) => ExitApp()));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowStatus();

        syncTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        syncTimer.Tick += async (_, _) => { if (Settings.AutoRunEnabled) await RunSyncAsync(manual: false); };
        collectTimer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };
        collectTimer.Tick += async (_, _) => { if (Settings.AutoRunEnabled) await RunCollectAsync(manual: false); };
        updateTimer = new System.Windows.Forms.Timer { Interval = 24 * 60 * 60 * 1000 };
        updateTimer.Tick += async (_, _) => await CheckUpdateAsync(manual: false);
        syncTimer.Start();
        collectTimer.Start();
        updateTimer.Start();

        Log.Write($"起動しました (version {CurrentVersion})");
        if (cred == null)
        {
            tray.ShowBalloonTip(8000, "FFXIV Status Uploader",
                "Google 認証が未設定です。状態画面からセットアップしてください。", ToolTipIcon.Info);
        }

        // 起動直後の初回実行（ずらして実行）
        _ = DelayedAsync(4000, () => RunSyncAsync(manual: false));
        _ = DelayedAsync(20000, () => RunCollectAsync(manual: false));
        _ = DelayedAsync(45000, () => CheckUpdateAsync(manual: false));
    }

    private static async Task DelayedAsync(int ms, Func<Task> action)
    {
        try { await Task.Delay(ms); await action(); } catch { }
    }

    private void LoadCredentials()
    {
        try
        {
            if (credStore.Exists)
            {
                cred = credStore.Load();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"資格情報の読み込みに失敗しました: {ex.Message}");
        }
        // PS 版の資格情報ファイルからの取り込み
        var psCred = Path.Combine(AppDir, "ffxiv-uploader-cred.xml");
        if (ClixmlCredentialImporter.TryImport(psCred, out var imported))
        {
            try
            {
                credStore.Save(imported);
                cred = imported;
                Log.Write("PowerShell 版の資格情報を取り込みました。");
            }
            catch (Exception ex)
            {
                Log.Write($"資格情報の取り込み保存に失敗しました: {ex.Message}");
            }
        }
    }

    public void SetCredentials(StoredCredentials newCred) => cred = newCred;

    public async Task RunSyncAsync(bool manual)
    {
        if (!await syncGate.WaitAsync(0))
        {
            if (manual) MessageBox.Show("送信処理が実行中です。", "FFXIV Status Uploader");
            return;
        }
        try
        {
            if (cred == null)
            {
                State.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                State.LastSyncResult = "認証情報がありません（セットアップ未完了）";
                State.Save(AppDir);
                Log.Write("送信をスキップしました: 認証情報がありません。");
                return;
            }
            var tokens = new RefreshTokenProvider(http, DriveHttpApi.DefaultTokenUri(), cred);
            var drive = new DriveHttpApi(http, tokens);
            var sync = new SyncService(Settings, AppDir, drive, Log);
            await sync.RunAsync();
            State.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            State.LastSyncResult = "成功";
            State.Save(AppDir);
        }
        catch (Exception ex)
        {
            State.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            State.LastSyncResult = "失敗: " + ex.Message;
            State.Save(AppDir);
            Log.Write("送信に失敗しました: " + ex.Message);
        }
        finally
        {
            syncGate.Release();
            statusForm?.RefreshView();
        }
    }

    public async Task RunCollectAsync(bool manual)
    {
        if (!await collectGate.WaitAsync(0))
        {
            if (manual) MessageBox.Show("収集処理が実行中です。", "FFXIV Status Uploader");
            return;
        }
        try
        {
            var zip = new CollectService(Settings, Log).Run();
            State.LastCollectAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            State.LastCollectResult = zip != null ? $"成功: {Path.GetFileName(zip)}" : "zip は作成されませんでした";
            State.Save(AppDir);
            // 作った zip は次の送信サイクルを待たずに送る
            if (zip != null) await RunSyncAsync(manual: false);
        }
        catch (Exception ex)
        {
            State.LastCollectAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            State.LastCollectResult = "失敗: " + ex.Message;
            State.Save(AppDir);
            Log.Write("収集に失敗しました: " + ex.Message);
        }
        finally
        {
            collectGate.Release();
            statusForm?.RefreshView();
        }
    }

    public async Task CheckUpdateAsync(bool manual)
    {
        try
        {
            var upd = new UpdateService(http, UpdateService.DefaultVersionJsonUrl, Log);
            var info = await upd.FetchInfoAsync();
            State.LastUpdateCheckAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (info != null && UpdateService.IsNewer(info.Version, CurrentVersion))
            {
                State.AvailableVersion = info.Version;
                State.Save(AppDir);
                Log.Write($"新しいバージョンがあります: {info.Version}（現在 {CurrentVersion}）");
                if (!updateNotified)
                {
                    updateNotified = true;
                    tray.ShowBalloonTip(8000, "FFXIV Status Uploader",
                        $"バージョン {info.Version} に更新して再起動します。", ToolTipIcon.Info);
                }
                var zipPath = Path.Combine(AppDir, "update-download.zip");
                await upd.DownloadAndVerifyAsync(info, zipPath);
                ApplyUpdateAndRestart(zipPath);
                return;
            }
            State.AvailableVersion = null;
            State.Save(AppDir);
            if (manual) MessageBox.Show($"最新バージョンです（{CurrentVersion}）。", "FFXIV Status Uploader");
        }
        catch (Exception ex)
        {
            Log.Write("更新確認に失敗しました: " + ex.Message);
            if (manual) MessageBox.Show("更新確認に失敗しました: " + ex.Message, "FFXIV Status Uploader");
        }
        statusForm?.RefreshView();
    }

    private void ApplyUpdateAndRestart(string zipPath)
    {
        Log.Write("更新を適用して再起動します。");
        var exe = Application.ExecutablePath;
        Process.Start(new ProcessStartInfo(exe, $"--stage-update \"{zipPath}\"") { UseShellExecute = false });
        ExitApp();
    }

    private void ToggleAutoRun()
    {
        Settings.AutoRunEnabled = !Settings.AutoRunEnabled;
        Settings.Save(AppDir);
        autoRunItem.Checked = Settings.AutoRunEnabled;
        Log.Write("自動実行: " + (Settings.AutoRunEnabled ? "有効" : "無効"));
    }

    private void ToggleAutostart()
    {
        var next = !Autostart.IsEnabled();
        Autostart.Set(next, Application.ExecutablePath);
        autostartItem.Checked = Autostart.IsEnabled();
        Log.Write("ログイン時自動起動: " + (autostartItem.Checked ? "有効" : "無効"));
    }

    public void ShowStatus()
    {
        if (statusForm == null || statusForm.IsDisposed)
        {
            statusForm = new StatusForm(this);
            statusForm.FormClosed += (_, _) => statusForm = null;
        }
        statusForm.Show();
        statusForm.Activate();
        statusForm.RefreshView();
    }

    public void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("開けませんでした: " + ex.Message, "FFXIV Status Uploader"); }
    }

    public async Task RunSetupAsync(string clientJsonPath)
    {
        var setup = new SetupService(AppDir, credStore, Log);
        var newCred = await setup.RunAsync(clientJsonPath, CancellationToken.None);
        cred = newCred;
        await RunSyncAsync(manual: false);
    }

    public void ExitApp()
    {
        syncTimer.Stop();
        collectTimer.Stop();
        updateTimer.Stop();
        tray.Visible = false;
        tray.Dispose();
        Application.Exit();
    }

    /// <summary>--stage-update / --apply-update の処理（Program.Main から UI 起動前に呼ばれる）。</summary>
    public static bool HandleUpdateArgs(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--stage-update")
        {
            var zipPath = args[1];
            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var exeName = Path.GetFileName(Application.ExecutablePath);
            var tmpExe = Path.Combine(Path.GetTempPath(), "FfxivStatusUploader.Updater.exe");
            File.Copy(Application.ExecutablePath, tmpExe, overwrite: true);
            Process.Start(new ProcessStartInfo(tmpExe,
                $"--apply-update \"{zipPath}\" {Environment.ProcessId} \"{appDir}\" \"{exeName}\"")
            { UseShellExecute = false });
            return true;
        }
        if (args.Length >= 5 && args[0] == "--apply-update")
        {
            var zipPath = args[1];
            var parentPid = int.Parse(args[2]);
            var appDir = args[3];
            var exeName = args[4];
            try
            {
                try
                {
                    var parent = Process.GetProcessById(parentPid);
                    parent.WaitForExit(30000);
                }
                catch { }
                Thread.Sleep(800);
                ZipFile.ExtractToDirectory(zipPath, appDir, overwriteFiles: true);
                try { File.Delete(zipPath); } catch { }
                Process.Start(new ProcessStartInfo(Path.Combine(appDir, exeName)) { UseShellExecute = false });
            }
            catch
            {
                // 失敗しても旧版で再起動を試みる
                try { Process.Start(new ProcessStartInfo(Path.Combine(appDir, exeName)) { UseShellExecute = false }); } catch { }
            }
            return true;
        }
        return false;
    }
}
