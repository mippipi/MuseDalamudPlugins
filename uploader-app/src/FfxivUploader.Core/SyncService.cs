using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FfxivUploader.Core;

/// <summary>
/// PS 版 Upload-FfxivStatus.ps1 v6 の通常実行の移植。
/// ステータス／テル／適用結果のアップロード、設定リクエストの配信、
/// to_muse フォルダへの集約、設定 zip の送信を行う。
/// </summary>
public sealed class SyncService
{
    public const string RequestsFileName = "ffxiv-config-requests.json";
    public const string ToMuseFolderName = "to_muse";

    private readonly AppSettings settings;
    private readonly string appDir;
    private readonly IDriveApi drive;
    private readonly AppLog log;
    private string? toMuseFolderId;

    public SyncService(AppSettings settings, string appDir, IDriveApi drive, AppLog log)
    {
        this.settings = settings;
        this.appDir = appDir;
        this.drive = drive;
        this.log = log;
    }

    private string FolderStatePath => Path.Combine(appDir, "to-muse-folder.json");

    public async Task RunAsync()
    {
        toMuseFolderId = await ResolveToMuseFolderAsync();
        await UploadFilesAsync();
        await DeliverConfigRequestsAsync();
        await MoveStrayFilesAsync();
    }

    // ------------------------------------------------------------------
    // to_muse フォルダ
    // ------------------------------------------------------------------
    private void SaveFolderId(string folderId)
    {
        try
        {
            var json = new JsonObject { ["FolderId"] = folderId }.ToJsonString();
            File.WriteAllText(FolderStatePath, json);
        }
        catch { }
    }

    private async Task<string?> ResolveToMuseFolderAsync()
    {
        if (toMuseFolderId != null) return toMuseFolderId;
        try
        {
            // 1) 保存済み ID が生きていれば使う
            if (File.Exists(FolderStatePath))
            {
                try
                {
                    var saved = JsonNode.Parse(File.ReadAllText(FolderStatePath)) as JsonObject;
                    var savedId = saved?["FolderId"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(savedId))
                    {
                        var info = await drive.GetFileAsync(savedId);
                        if (info != null && info.MimeType == "application/vnd.google-apps.folder" && !info.Trashed)
                        {
                            toMuseFolderId = savedId;
                            return savedId;
                        }
                    }
                }
                catch { }
            }

            // 2) 同名フォルダを探す
            var found = await drive.ListFilesAsync(
                $"name = '{ToMuseFolderName}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false", 1);
            if (found.Count > 0)
            {
                toMuseFolderId = found[0].Id;
                SaveFolderId(toMuseFolderId);
                return toMuseFolderId;
            }

            // 3) 無ければ作成する
            var newId = await drive.CreateFolderAsync(ToMuseFolderName);
            toMuseFolderId = newId;
            SaveFolderId(newId);
            log.Write($"集約先フォルダを作成しました: {ToMuseFolderName} id={newId}");
            return newId;
        }
        catch (Exception ex)
        {
            log.Write($"集約先フォルダの解決に失敗したため、ルートへアップロードします: {ex.Message}");
            return null;
        }
    }

    private async Task EnsureInFolderAsync(string fileId)
    {
        if (toMuseFolderId == null) return;
        try
        {
            var info = await drive.GetFileAsync(fileId);
            var parents = info?.Parents ?? Array.Empty<string>();
            if (parents.Contains(toMuseFolderId)) return;
            await drive.SetParentsAsync(fileId, toMuseFolderId,
                parents.Count > 0 ? string.Join(",", parents) : null);
        }
        catch (Exception ex)
        {
            log.Write($"フォルダへの移動に失敗しました（次回再試行します）: {ex.Message}");
        }
    }

    private async Task MoveStrayFilesAsync()
    {
        if (toMuseFolderId == null) return;
        try
        {
            // 新旧どちらの zip 名も拾えるよう 'FF14-pluginConfigs' で検索する
            var files = await drive.ListFilesAsync(
                "(name contains 'ffxiv-' or name contains 'FF14-pluginConfigs') and trashed = false", 100);
            foreach (var f in files)
            {
                if (!f.Parents.Contains(toMuseFolderId))
                {
                    await EnsureInFolderAsync(f.Id);
                    log.Write($"フォルダへ移動しました: {f.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            log.Write($"フォルダ外ファイルの走査に失敗しました: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // ファイルのアップロード
    // ------------------------------------------------------------------
    private async Task UploadOneFileAsync(string localPath, string remoteName, string contentType, bool skipIfExists)
    {
        var existing = await drive.ListFilesAsync($"name = '{remoteName}' and trashed = false", 1, "modifiedTime desc");
        var bytes = await File.ReadAllBytesAsync(localPath);
        if (existing.Count > 0)
        {
            var fileId = existing[0].Id;
            if (skipIfExists)
            {
                log.Write($"送信済みのためスキップします: {remoteName}");
            }
            else
            {
                await drive.UploadMediaAsync(fileId, bytes, contentType);
                log.Write($"アップロード完了（更新）: {remoteName} id={fileId}");
            }
            await EnsureInFolderAsync(fileId);
            return;
        }

        var parents = toMuseFolderId != null ? new[] { toMuseFolderId } : null;
        var newId = await drive.CreateMultipartAsync(remoteName, contentType, parents, bytes);
        log.Write($"アップロード完了（新規作成）: {remoteName} id={newId}");
    }

    private async Task UploadFilesAsync()
    {
        var targets = new List<FileInfo>();
        var statusDir = settings.PluginDir;
        if (Directory.Exists(statusDir))
        {
            targets.AddRange(new DirectoryInfo(statusDir).GetFiles("ffxiv-status-*.json"));
            targets.AddRange(new DirectoryInfo(statusDir).GetFiles("ffxiv-tells-*.json"));
            targets = targets.OrderBy(f => f.Name).ToList();
            var resultsFile = new FileInfo(Path.Combine(statusDir, "ffxiv-config-results.json"));
            if (resultsFile.Exists) targets.Add(resultsFile);
        }
        if (targets.Count == 0)
        {
            log.Write($"対象ファイルが無いためスキップします: {statusDir}\\ffxiv-status-*.json / ffxiv-tells-*.json");
        }

        // 設定 zip の最新1件（新名 FF14-pluginConfigs-*.zip は旧名 -light- も含む）
        FileInfo? configZip = null;
        if (Directory.Exists(settings.CollectRootDir))
        {
            configZip = new DirectoryInfo(settings.CollectRootDir)
                .GetFiles("FF14-pluginConfigs-*.zip")
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            if (configZip == null)
            {
                log.Write($"設定の軽量zipが無いためスキップします: {settings.CollectRootDir}\\FF14-pluginConfigs-*.zip");
            }
        }

        if (targets.Count == 0 && configZip == null) return;

        foreach (var file in targets)
        {
            await UploadOneFileAsync(file.FullName, file.Name, "application/json", skipIfExists: false);
        }
        if (configZip != null)
        {
            await UploadOneFileAsync(configZip.FullName, configZip.Name, "application/zip", skipIfExists: true);
        }
    }

    // ------------------------------------------------------------------
    // 設定ダウンリンク（Drive → config-inbox）
    // ------------------------------------------------------------------
    private static readonly Regex RequestIdRegex = new("^[A-Za-z0-9][A-Za-z0-9-]{7,63}$", RegexOptions.Compiled);

    private async Task DeliverConfigRequestsAsync()
    {
        var existing = await drive.ListFilesAsync($"name = '{RequestsFileName}' and trashed = false", 1, "modifiedTime desc");
        if (existing.Count == 0)
        {
            // 無ければ空のリクエストファイルを作る（Muse 側がこのファイルへリクエストを書く）
            var empty = new JsonObject { ["Version"] = 1, ["Requests"] = new JsonArray() }.ToJsonString();
            var parents = toMuseFolderId != null ? new[] { toMuseFolderId } : null;
            await drive.CreateMultipartAsync(RequestsFileName, "application/json", parents, Encoding.UTF8.GetBytes(empty));
            log.Write("設定リクエストファイルが無かったため、空で作成しました。");
            return;
        }

        var fileId = existing[0].Id;
        var bytes = await drive.DownloadAsync(fileId);
        var doc = JsonNode.Parse(Encoding.UTF8.GetString(bytes)) as JsonObject;
        if (doc == null || doc["Requests"] is not JsonArray requests)
        {
            log.Write("設定リクエストファイルの形式が想定と違うため、ダウンリンクをスキップします。");
            return;
        }

        var inboxDir = Path.Combine(settings.PluginDir, "config-inbox");
        var changed = false;
        var delivered = 0;
        foreach (var node in requests)
        {
            if (node is not JsonObject req) continue;
            var status = req["Status"]?.GetValue<string>() ?? "";
            if (status != "pending") continue;

            var id = req["Id"]?.GetValue<string>() ?? "";
            var valid = RequestIdRegex.IsMatch(id) && req["Target"] != null && req["ContentBase64"] != null;
            if (!valid)
            {
                req["Status"] = "invalid";
                req["Note"] = "Id / Target / ContentBase64 が不足しているため配信できませんでした。";
                changed = true;
                log.Write($"設定リクエストが不正なため invalid にしました: id={id}");
                continue;
            }

            Directory.CreateDirectory(inboxDir);
            var envelopeJson = req.ToJsonString();
            var tmpPath = Path.Combine(inboxDir, id + ".tmp");
            var finalPath = Path.Combine(inboxDir, id + ".json");
            await File.WriteAllTextAsync(tmpPath, envelopeJson);
            File.Move(tmpPath, finalPath, overwrite: true);

            req["Status"] = "delivered";
            req["DeliveredAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            changed = true;
            delivered++;
            log.Write($"設定リクエストを配信しました: id={id} target={req["Target"]?.GetValue<string>()}");
        }

        if (changed)
        {
            var outBytes = Encoding.UTF8.GetBytes(doc.ToJsonString());
            await drive.UploadMediaAsync(fileId, outBytes, "application/json");
        }
        if (delivered > 0)
        {
            log.Write($"設定リクエスト配信: 合計 {delivered} 件");
        }
    }
}
