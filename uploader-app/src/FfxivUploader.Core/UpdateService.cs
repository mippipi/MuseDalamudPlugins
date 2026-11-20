using System.Security.Cryptography;
using System.Text.Json;

namespace FfxivUploader.Core;

public sealed record UpdateInfo(string Version, string ZipUrl, string Sha256, string? Notes);

/// <summary>
/// GitHub リポジトリの uploader-app/version.json を確認し、新しい版があれば
/// zip をダウンロードして SHA-256 を検証するところまでを行う。
/// 実際の入れ替えと再起動はアプリ側（--apply-update モード）が担う。
/// </summary>
public sealed class UpdateService
{
    private readonly HttpClient http;
    private readonly string versionJsonUrl;
    private readonly AppLog log;

    public UpdateService(HttpClient http, string versionJsonUrl, AppLog log)
    {
        this.http = http;
        this.versionJsonUrl = versionJsonUrl;
        this.log = log;
    }

    public const string DefaultVersionJsonUrl =
        "https://raw.githubusercontent.com/mippipi/MuseDalamudPlugins/main/uploader-app/version.json";

    public async Task<UpdateInfo?> FetchInfoAsync()
    {
        var json = await http.GetStringAsync(versionJsonUrl);
        var info = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return info;
    }

    public static bool IsNewer(string availableVersion, string currentVersion)
    {
        if (Version.TryParse(availableVersion, out var a) && Version.TryParse(currentVersion, out var c))
            return a > c;
        return !string.Equals(availableVersion, currentVersion, StringComparison.Ordinal);
    }

    /// <summary>zip をダウンロードして SHA-256 を検証し、保存先パスを返す。</summary>
    public async Task<string> DownloadAndVerifyAsync(UpdateInfo info, string destPath)
    {
        var bytes = await http.GetByteArrayAsync(info.ZipUrl);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ダウンロードした更新ファイルの SHA-256 が一致しません（期待: {info.Sha256}、実際: {hash}）。更新を中止します。");
        }
        await File.WriteAllBytesAsync(destPath, bytes);
        log.Write($"更新ファイルをダウンロードしました: version {info.Version} ({bytes.Length} bytes, SHA-256 照合 OK)");
        return destPath;
    }
}
