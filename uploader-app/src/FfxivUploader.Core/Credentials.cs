using System.Text.Json;

namespace FfxivUploader.Core;

/// <summary>秘密情報の保護（Windows では DPAPI、テストでは差し替え可能）。</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] encrypted);
}

public sealed class StoredCredentials
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";
}

/// <summary>
/// アプリ専用の資格情報ストア。ClientId は平文、秘密2件は ISecretProtector で
/// 暗号化して base64 で保持する（uploader-app-cred.json）。
/// </summary>
public sealed class CredentialStore
{
    private readonly string path;
    private readonly ISecretProtector protector;

    public CredentialStore(string appDir, ISecretProtector protector)
    {
        path = Path.Combine(appDir, "uploader-app-cred.json");
        this.protector = protector;
    }

    public bool Exists => File.Exists(path);

    private sealed class CredFile
    {
        public string ClientId { get; set; } = "";
        public string ClientSecretProtected { get; set; } = "";
        public string RefreshTokenProtected { get; set; } = "";
    }

    public void Save(StoredCredentials cred)
    {
        var file = new CredFile
        {
            ClientId = cred.ClientId,
            ClientSecretProtected = Convert.ToBase64String(protector.Protect(System.Text.Encoding.UTF8.GetBytes(cred.ClientSecret))),
            RefreshTokenProtected = Convert.ToBase64String(protector.Protect(System.Text.Encoding.UTF8.GetBytes(cred.RefreshToken))),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
    }

    public StoredCredentials Load()
    {
        var file = JsonSerializer.Deserialize<CredFile>(File.ReadAllText(path))
                   ?? throw new InvalidDataException("資格情報ファイルの形式が想定と違います。");
        return new StoredCredentials
        {
            ClientId = file.ClientId,
            ClientSecret = System.Text.Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(file.ClientSecretProtected))),
            RefreshToken = System.Text.Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(file.RefreshTokenProtected))),
        };
    }
}
