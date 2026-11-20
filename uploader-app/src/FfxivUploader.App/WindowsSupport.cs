using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FfxivUploader.Core;
using Microsoft.Win32;

namespace FfxivUploader.App;

/// <summary>DPAPI（現在の Windows ユーザー）で秘密情報を保護する。</summary>
public sealed class DpapiProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain) =>
        ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] encrypted) =>
        ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
}

/// <summary>
/// PS 版の ffxiv-uploader-cred.xml（Export-Clixml のハッシュテーブル）を読み、
/// ClientId / ClientSecret / RefreshToken を取り出す。SecureString は DPAPI で復号する。
/// </summary>
public static class ClixmlCredentialImporter
{
    public static bool TryImport(string credPath, out StoredCredentials cred)
    {
        cred = new StoredCredentials();
        if (!File.Exists(credPath)) return false;
        try
        {
            var doc = XDocument.Load(credPath);
            XNamespace ns = "http://schemas.microsoft.com/powershell/2004/04";
            var values = new Dictionary<string, string>();
            foreach (var en in doc.Descendants(ns + "En"))
            {
                string? key = null;
                string? value = null;
                foreach (var child in en.Elements())
                {
                    var n = child.Attribute("N")?.Value;
                    if (n == "Key") key = child.Value;
                    else if (n == "Value")
                    {
                        if (child.Name == ns + "SS")
                        {
                            var bytes = Convert.FromBase64String(child.Value.Trim());
                            value = Encoding.Unicode.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
                        }
                        else value = child.Value;
                    }
                }
                if (key != null && value != null) values[key] = value;
            }
            if (values.TryGetValue("ClientId", out var cid) &&
                values.TryGetValue("ClientSecret", out var sec) &&
                values.TryGetValue("RefreshToken", out var tok) &&
                !string.IsNullOrEmpty(cid) && !string.IsNullOrEmpty(tok))
            {
                cred = new StoredCredentials { ClientId = cid, ClientSecret = sec, RefreshToken = tok };
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>HKCU の Run キーによるログオン時自動起動。</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FfxivStatusUploader";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && !string.IsNullOrEmpty(s);
    }

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{exePath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// 初回セットアップ（PS 版 -Setup の移植）。クライアント JSON を読み、
/// ブラウザ同意 → ループバックでコード受信 → トークン交換 → 暗号化保存。
/// </summary>
public sealed class SetupService
{
    private readonly string appDir;
    private readonly CredentialStore store;
    private readonly AppLog log;

    public SetupService(string appDir, CredentialStore store, AppLog log)
    {
        this.appDir = appDir;
        this.store = store;
        this.log = log;
    }

    public static string? FindClientJson()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloads)) return null;
        return new DirectoryInfo(downloads).GetFiles("client_secret_*.json")
            .OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
    }

    public async Task<StoredCredentials> RunAsync(string clientJsonPath, CancellationToken ct)
    {
        var parsed = JsonNode.Parse(await File.ReadAllTextAsync(clientJsonPath, ct)) as JsonObject
                     ?? throw new InvalidOperationException("クライアント JSON の形式が想定と違います。");
        var client = (parsed["installed"] as JsonObject) ?? (parsed["web"] as JsonObject)
                     ?? throw new InvalidOperationException("クライアント JSON の形式が想定と違います（installed / web キーがありません）。");
        var clientId = client["client_id"]?.GetValue<string>() ?? throw new InvalidOperationException("client_id がありません。");
        var clientSecret = client["client_secret"]?.GetValue<string>() ?? throw new InvalidOperationException("client_secret がありません。");

        // ループバック待受ポート
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var redirectUri = $"http://127.0.0.1:{port}/";

        var query = string.Join("&", new[]
        {
            "client_id=" + Uri.EscapeDataString(clientId),
            "redirect_uri=" + Uri.EscapeDataString(redirectUri),
            "response_type=code",
            "scope=" + Uri.EscapeDataString("https://www.googleapis.com/auth/drive.file"),
            "access_type=offline",
            "prompt=consent",
        });
        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth?" + query;
        log.Write("ブラウザで Google の同意画面を開きます。");
        try { Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true }); }
        catch { log.Write("ブラウザの自動起動に失敗しました。ログの URL を手動で開いてください: " + authUrl); }

        string? code = null;
        string? authError = null;
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            while (code == null && authError == null)
            {
                var ctxTask = listener.GetContextAsync();
                var done = await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, timeout.Token));
                if (done != ctxTask) throw new TimeoutException("認可コードを受け取れませんでした（タイムアウト）。");
                var ctx = await ctxTask;
                var q = ctx.Request.QueryString;
                if (q["code"] != null) code = q["code"];
                else if (q["error"] != null) authError = q["error"];
                var html = "<html><head><meta charset=\"utf-8\"></head><body><p>認証が完了しました。このタブを閉じてください。</p></body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }
        finally { listener.Stop(); }

        if (authError != null) throw new InvalidOperationException($"Google 側で認可が拒否されました: {authError}");
        if (code == null) throw new InvalidOperationException("認可コードを受け取れませんでした。");

        using var http = new HttpClient();
        var token = await RefreshTokenProvider.ExchangeCodeAsync(http, DriveHttpApi.DefaultTokenUri(), clientId, clientSecret, code, redirectUri);
        var refresh = token["refresh_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(refresh))
            throw new InvalidOperationException("リフレッシュトークンが発行されませんでした。同意画面で許可し直してください。");

        var cred = new StoredCredentials { ClientId = clientId, ClientSecret = clientSecret, RefreshToken = refresh };
        store.Save(cred);
        log.Write($"セットアップ完了。暗号化した認証情報を保存しました: {appDir}");
        return cred;
    }
}
