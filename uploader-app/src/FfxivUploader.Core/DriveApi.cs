using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FfxivUploader.Core;

public sealed record DriveFileInfo(
    string Id,
    string Name,
    string? MimeType,
    bool Trashed,
    IReadOnlyList<string> Parents,
    string? ModifiedTime);

/// <summary>Drive API の最小インターフェース。テストではインメモリ実装に差し替える。</summary>
public interface IDriveApi
{
    Task<IReadOnlyList<DriveFileInfo>> ListFilesAsync(string query, int pageSize, string? orderBy = null);
    Task<DriveFileInfo?> GetFileAsync(string fileId);
    Task<byte[]> DownloadAsync(string fileId);
    Task<string> UploadMediaAsync(string fileId, byte[] bytes, string contentType);
    Task<string> CreateMultipartAsync(string name, string mimeType, IReadOnlyList<string>? parents, byte[] bytes);
    Task<string> CreateFolderAsync(string name);
    Task SetParentsAsync(string fileId, string addParents, string? removeParents);
}

public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync();
}

/// <summary>リフレッシュトークンからアクセストークンを取得する（PS 版 Get-AccessHeaders 相当）。</summary>
public sealed class RefreshTokenProvider : IAccessTokenProvider
{
    private readonly HttpClient http;
    private readonly string tokenUri;
    private readonly StoredCredentials cred;
    private string? cachedToken;
    private DateTimeOffset cachedUntil = DateTimeOffset.MinValue;

    public RefreshTokenProvider(HttpClient http, string tokenUri, StoredCredentials cred)
    {
        this.http = http;
        this.tokenUri = tokenUri;
        this.cred = cred;
    }

    public async Task<string> GetAccessTokenAsync()
    {
        if (cachedToken != null && DateTimeOffset.Now < cachedUntil) return cachedToken;
        var body = new Dictionary<string, string>
        {
            ["client_id"] = cred.ClientId,
            ["client_secret"] = cred.ClientSecret,
            ["refresh_token"] = cred.RefreshToken,
            ["grant_type"] = "refresh_token",
        };
        using var content = new FormUrlEncodedContent(body);
        using var resp = await http.PostAsync(tokenUri, content);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"アクセストークンの取得に失敗しました: {(int)resp.StatusCode} {text}");
        var json = JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidOperationException("アクセストークンの応答形式が想定と違います。");
        var token = json["access_token"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("アクセストークンの応答に access_token がありません。-Setup 相当の再認証が必要です。");
        var expiresIn = json["expires_in"]?.GetValue<int>() ?? 3600;
        cachedToken = token;
        cachedUntil = DateTimeOffset.Now.AddSeconds(Math.Max(60, expiresIn - 120));
        return token;
    }

    /// <summary>セットアップ用: 認可コードをトークンへ交換する。</summary>
    public static async Task<JsonObject> ExchangeCodeAsync(
        HttpClient http, string tokenUri, string clientId, string clientSecret, string code, string redirectUri)
    {
        var body = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };
        using var content = new FormUrlEncodedContent(body);
        using var resp = await http.PostAsync(tokenUri, content);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"トークン交換に失敗しました: {(int)resp.StatusCode} {text}");
        return JsonNode.Parse(text) as JsonObject
               ?? throw new InvalidOperationException("トークン交換の応答形式が想定と違います。");
    }
}

/// <summary>Drive REST API の HTTP 実装。接続先は PS 版の -ApiBase と同様に差し替え可能。</summary>
public sealed class DriveHttpApi : IDriveApi
{
    private readonly HttpClient http;
    private readonly IAccessTokenProvider tokens;
    private readonly string filesBase;
    private readonly string uploadBase;

    public DriveHttpApi(HttpClient http, IAccessTokenProvider tokens, string apiBase = "")
    {
        this.http = http;
        this.tokens = tokens;
        if (!string.IsNullOrEmpty(apiBase))
        {
            filesBase = $"{apiBase}/drive/v3/files";
            uploadBase = $"{apiBase}/upload/drive/v3/files";
        }
        else
        {
            filesBase = "https://www.googleapis.com/drive/v3/files";
            uploadBase = "https://www.googleapis.com/upload/drive/v3/files";
        }
    }

    public static string DefaultTokenUri(string apiBase = "") =>
        string.IsNullOrEmpty(apiBase) ? "https://oauth2.googleapis.com/token" : $"{apiBase}/token";

    private async Task<HttpRequestMessage> NewRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync());
        return req;
    }

    private static async Task<string> EnsureSuccess(HttpResponseMessage resp, string what)
    {
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{what}に失敗しました: {(int)resp.StatusCode} {text}");
        return text;
    }

    private static DriveFileInfo ToInfo(JsonObject f)
    {
        var parents = new List<string>();
        if (f["parents"] is JsonArray arr)
            foreach (var p in arr) if (p != null) parents.Add(p.GetValue<string>());
        return new DriveFileInfo(
            f["id"]?.GetValue<string>() ?? "",
            f["name"]?.GetValue<string>() ?? "",
            f["mimeType"]?.GetValue<string>(),
            f["trashed"]?.GetValue<bool>() ?? false,
            parents,
            f["modifiedTime"]?.GetValue<string>());
    }

    public async Task<IReadOnlyList<DriveFileInfo>> ListFilesAsync(string query, int pageSize, string? orderBy = null)
    {
        var fields = Uri.EscapeDataString("files(id,name,mimeType,trashed,parents,modifiedTime)");
        var url = $"{filesBase}?q={Uri.EscapeDataString(query)}&fields={fields}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(orderBy)) url += $"&orderBy={Uri.EscapeDataString(orderBy)}";
        using var req = await NewRequest(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req);
        var text = await EnsureSuccess(resp, "ファイル一覧の取得");
        var json = JsonNode.Parse(text) as JsonObject;
        var result = new List<DriveFileInfo>();
        if (json?["files"] is JsonArray files)
            foreach (var f in files) if (f is JsonObject o) result.Add(ToInfo(o));
        return result;
    }

    public async Task<DriveFileInfo?> GetFileAsync(string fileId)
    {
        var fields = Uri.EscapeDataString("id,name,mimeType,trashed,parents,modifiedTime");
        using var req = await NewRequest(HttpMethod.Get, $"{filesBase}/{fileId}?fields={fields}");
        using var resp = await http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var text = await EnsureSuccess(resp, "ファイル情報の取得");
        var json = JsonNode.Parse(text) as JsonObject;
        return json == null ? null : ToInfo(json);
    }

    public async Task<byte[]> DownloadAsync(string fileId)
    {
        using var req = await NewRequest(HttpMethod.Get, $"{filesBase}/{fileId}?alt=media");
        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"ダウンロードに失敗しました: {(int)resp.StatusCode} {text}");
        }
        return await resp.Content.ReadAsByteArrayAsync();
    }

    public async Task<string> UploadMediaAsync(string fileId, byte[] bytes, string contentType)
    {
        using var req = await NewRequest(HttpMethod.Patch, $"{uploadBase}/{fileId}?uploadType=media");
        req.Content = new ByteArrayContent(bytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var resp = await http.SendAsync(req);
        var text = await EnsureSuccess(resp, "アップロード（更新）");
        var json = JsonNode.Parse(text) as JsonObject;
        return json?["id"]?.GetValue<string>() ?? fileId;
    }

    public async Task<string> CreateMultipartAsync(string name, string mimeType, IReadOnlyList<string>? parents, byte[] bytes)
    {
        var boundary = "ffxiv-" + Guid.NewGuid().ToString("N");
        var meta = new JsonObject { ["name"] = name, ["mimeType"] = mimeType };
        if (parents != null && parents.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var p in parents) arr.Add(p);
            meta["parents"] = arr;
        }
        using var ms = new MemoryStream();
        void WriteString(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }
        WriteString($"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{meta.ToJsonString()}\r\n");
        WriteString($"--{boundary}\r\nContent-Type: {mimeType}\r\n\r\n");
        ms.Write(bytes, 0, bytes.Length);
        WriteString($"\r\n--{boundary}--\r\n");

        using var req = await NewRequest(HttpMethod.Post, $"{uploadBase}?uploadType=multipart");
        req.Content = new ByteArrayContent(ms.ToArray());
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/related") { Parameters = { new NameValueHeaderValue("boundary", boundary) } };
        using var resp = await http.SendAsync(req);
        var text = await EnsureSuccess(resp, "アップロード（新規作成）");
        var json = JsonNode.Parse(text) as JsonObject;
        return json?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("作成応答に id がありません。");
    }

    public async Task<string> CreateFolderAsync(string name)
    {
        var meta = new JsonObject { ["name"] = name, ["mimeType"] = "application/vnd.google-apps.folder" };
        using var req = await NewRequest(HttpMethod.Post, filesBase);
        req.Content = new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        var text = await EnsureSuccess(resp, "フォルダの作成");
        var json = JsonNode.Parse(text) as JsonObject;
        return json?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("フォルダ作成応答に id がありません。");
    }

    public async Task SetParentsAsync(string fileId, string addParents, string? removeParents)
    {
        var url = $"{filesBase}/{fileId}?addParents={Uri.EscapeDataString(addParents)}";
        if (!string.IsNullOrEmpty(removeParents)) url += $"&removeParents={Uri.EscapeDataString(removeParents)}";
        using var req = await NewRequest(HttpMethod.Patch, url);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        await EnsureSuccess(resp, "フォルダへの移動");
    }
}
