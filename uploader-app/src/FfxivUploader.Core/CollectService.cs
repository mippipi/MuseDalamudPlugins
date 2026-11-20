using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FfxivUploader.Core;

/// <summary>PS 版 Collect-DalamudConfig.ps1 v3.6.1 のマスク処理の移植。</summary>
public static class Masking
{
    private static readonly Regex JsonMaskRegex = new(
        "(\"[A-Za-z0-9_\\.\\-]*?(?:token|password|passwd|secret|auth|license|api[-_]?key|patreon|activation|serial)[A-Za-z0-9_\\.\\-]*\"\\s*:\\s*\")[^\"]*\"",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(30));

    private static readonly Regex LogKeyValueRegex = new(
        "\\b(token|password|passwd|secret|auth|license|api[-_]?key|patreon|activation|serial)(\\s*[:=]\\s*)\\S+",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    private static readonly Regex LogBearerRegex = new(
        "Bearer\\s+\\S+", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    private static readonly Regex LogEmailRegex = new(
        "[\\w\\.\\-]+@[\\w\\-]+(?:\\.[\\w\\-]+)+", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>JSON テキスト内の秘密情報らしい値をマスクする。タイムアウト時は原文を返す。</summary>
    public static string MaskJsonText(string raw)
    {
        try { return JsonMaskRegex.Replace(raw, "$1***MASKED***\""); }
        catch (RegexMatchTimeoutException) { return raw; }
    }

    public static void MaskJsonFilesInDirectory(string dir, AppLog log)
    {
        foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            var raw = File.ReadAllText(file, Encoding.UTF8);
            string masked;
            try { masked = JsonMaskRegex.Replace(raw, "$1***MASKED***\""); }
            catch (RegexMatchTimeoutException)
            {
                log.Write($"  マスクがタイムアウトしたためスキップしました: {Path.GetFileName(file)}");
                continue;
            }
            if (masked != raw)
            {
                File.WriteAllText(file, masked, new UTF8Encoding(false));
                log.Write($"  マスク適用: {Path.GetFileName(file)}");
            }
        }
    }

    public static string MaskLogLine(string text, string userName)
    {
        var masked = text;
        try { masked = LogKeyValueRegex.Replace(masked, "$1$2***MASKED***"); }
        catch (RegexMatchTimeoutException) { }
        try { masked = LogBearerRegex.Replace(masked, "Bearer ***MASKED***"); }
        catch (RegexMatchTimeoutException) { }
        try { masked = LogEmailRegex.Replace(masked, "***MASKED***"); }
        catch (RegexMatchTimeoutException)
        {
            masked = Regex.Replace(masked, "[\\w\\.\\-]+@\\S+", "***MASKED***");
        }
        if (userName.Length >= 3) masked = masked.Replace(userName, "<user>");
        return masked;
    }

    /// <summary>ファイルの末尾 Count 行を返す（PS 版 Get-LogTailLines 相当）。</summary>
    public static List<string> ReadTailLines(string path, int count)
    {
        var queue = new Queue<string>();
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            queue.Enqueue(line);
            if (queue.Count > count) queue.Dequeue();
        }
        return queue.ToList();
    }
}

/// <summary>
/// PS 版 Collect-DalamudConfig.ps1 の軽量モード（-LightOnly）の移植。
/// フル共有 zip は廃止し、軽量 zip のみ。zip 名は FF14-pluginConfigs-&lt;日時&gt;.zip。
/// </summary>
public sealed class CollectService
{
    private const int RetentionDays = 30;
    private const int LogTailLines = 20000;

    private readonly AppSettings settings;
    private readonly AppLog log;

    public CollectService(AppSettings settings, AppLog log)
    {
        this.settings = settings;
        this.log = log;
    }

    /// <summary>収集を実行し、作った zip のパス（作らなかった場合は null）を返す。</summary>
    public string? Run()
    {
        var sourcePath = settings.PluginConfigsDir;
        var rootPath = settings.CollectRootDir;
        if (!Directory.Exists(sourcePath))
        {
            log.Write($"pluginConfigs が見つかりません: {sourcePath}");
            return null;
        }
        Directory.CreateDirectory(rootPath);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        // 0) 前回までの出力を OLD へ移動し、保持期間を過ぎたものを削除
        var moved = MoveToOld(rootPath, stamp);
        if (moved > 0) log.Write($"前回までの出力 {moved} 件を OLD フォルダへ移動しました。");
        var removed = RemoveOldArchive(rootPath, RetentionDays);
        if (removed > 0) log.Write($"OLD フォルダ内の {RetentionDays} 日より古い出力 {removed} 件を削除しました。");

        var lightDir = Path.Combine(rootPath, "share-light", stamp);
        Directory.CreateDirectory(lightDir);

        // 1) Dalamud ログ（軽量モードでは末尾抽出に元ログを直接使う）
        var logSource = settings.EffectiveDalamudLogPath;
        string? logForTail = null;
        if (File.Exists(logSource)) logForTail = logSource;
        else log.Write($"Dalamud ログが見つかりませんでした (スキップします): {logSource}");

        // 2) 軽量 zip の中身（ルート直下の JSON + AutoDuty の JSON + ログ末尾）
        var rootJsons = Directory.GetFiles(sourcePath, "*.json", SearchOption.TopDirectoryOnly);
        var adDir = Path.Combine(sourcePath, "AutoDuty");
        var adJsons = Directory.Exists(adDir)
            ? Directory.GetFiles(adDir, "*.json", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

        string? zipPath = null;
        if (rootJsons.Length > 0 || adJsons.Length > 0 || logForTail != null)
        {
            foreach (var f in rootJsons) File.Copy(f, Path.Combine(lightDir, Path.GetFileName(f)), overwrite: true);
            if (adJsons.Length > 0)
            {
                var adDest = Path.Combine(lightDir, "AutoDuty");
                Directory.CreateDirectory(adDest);
                foreach (var f in adJsons) File.Copy(f, Path.Combine(adDest, Path.GetFileName(f)), overwrite: true);
                log.Write($"  AutoDuty 設定を軽量zipに追加: {adJsons.Length} 件");
            }
            if (logForTail != null)
            {
                try
                {
                    log.Write("  Dalamud ログ末尾を処理しています...");
                    var tail = Masking.ReadTailLines(logForTail, LogTailLines);
                    var userName = Environment.UserName;
                    var masked = tail.Select(l => Masking.MaskLogLine(l, userName));
                    File.WriteAllLines(Path.Combine(lightDir, "dalamud-tail.log"), masked, new UTF8Encoding(false));
                    log.Write($"  Dalamud ログ末尾 {tail.Count} 行を軽量zipに追加 (マスク済み)");
                }
                catch (Exception ex)
                {
                    log.Write($"  Dalamud ログ末尾の抽出に失敗しました (スキップします): {ex.Message}");
                }
            }
            Masking.MaskJsonFilesInDirectory(lightDir, log);
            zipPath = Path.Combine(rootPath, $"FF14-pluginConfigs-{stamp}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(lightDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            log.Write($"軽量zipを作成しました: {zipPath}");
        }
        else
        {
            log.Write("設定JSONもログも見つかりませんでした。軽量zipは作成しません。");
        }
        return zipPath;
    }

    private static int MoveToOld(string root, string stamp)
    {
        var moved = 0;
        var oldRoot = Path.Combine(root, "OLD");
        foreach (var sub in new[] { "snapshots", "share-light", "share" })
        {
            var srcDir = Path.Combine(root, sub);
            if (!Directory.Exists(srcDir)) continue;
            foreach (var item in new DirectoryInfo(srcDir).GetFileSystemInfos())
            {
                if (item.Name == stamp) continue;
                var destDir = Path.Combine(oldRoot, sub);
                var dest = Path.Combine(destDir, item.Name);
                if (File.Exists(dest) || Directory.Exists(dest)) continue;
                Directory.CreateDirectory(destDir);
                if (item is DirectoryInfo d) d.MoveTo(dest);
                else if (item is FileInfo f) f.MoveTo(dest);
                else continue;
                moved++;
            }
        }
        foreach (var zip in new DirectoryInfo(root).GetFiles("FF14-pluginConfigs-*.zip"))
        {
            var dest = Path.Combine(oldRoot, zip.Name);
            if (File.Exists(dest)) continue;
            Directory.CreateDirectory(oldRoot);
            zip.MoveTo(dest);
            moved++;
        }
        return moved;
    }

    private static int RemoveOldArchive(string root, int retentionDays)
    {
        if (retentionDays <= 0) return 0;
        var oldRoot = Path.Combine(root, "OLD");
        if (!Directory.Exists(oldRoot)) return 0;
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var removed = 0;
        var candidates = new List<FileSystemInfo>();
        foreach (var sub in new[] { "snapshots", "share-light", "share" })
        {
            var subDir = Path.Combine(oldRoot, sub);
            if (Directory.Exists(subDir)) candidates.AddRange(new DirectoryInfo(subDir).GetFileSystemInfos());
        }
        candidates.AddRange(new DirectoryInfo(oldRoot).GetFiles("FF14-pluginConfigs-*.zip"));
        foreach (var item in candidates)
        {
            if (item.LastWriteTime < cutoff)
            {
                try
                {
                    if (item is DirectoryInfo d) d.Delete(recursive: true);
                    else item.Delete();
                    removed++;
                }
                catch { }
            }
        }
        return removed;
    }
}
