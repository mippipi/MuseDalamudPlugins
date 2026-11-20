using System.Text.Json;

namespace FfxivUploader.Core;

/// <summary>アプリの設定（uploader-app-settings.json）と実行状態（uploader-app-state.json）。</summary>
public sealed class AppSettings
{
    public string PluginDir { get; set; } = "";
    public string PluginConfigsDir { get; set; } = "";
    public string CollectRootDir { get; set; } = "";
    public string DalamudLogPath { get; set; } = "";
    public bool AutoRunEnabled { get; set; } = true;

    public static AppSettings CreateDefault(string appDir)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return new AppSettings
        {
            PluginDir = Path.Combine(appData, "XIVLauncher", "pluginConfigs", "MuseStatusExporter"),
            PluginConfigsDir = Path.Combine(appData, "XIVLauncher", "pluginConfigs"),
            CollectRootDir = Path.Combine(documents, "FF14-ConfigAsCode"),
            DalamudLogPath = "",
            AutoRunEnabled = true,
        };
    }

    /// <summary>Dalamud ログの実効パス。未指定なら XIVLauncher フォルダ直下の dalamud.log。</summary>
    public string EffectiveDalamudLogPath =>
        string.IsNullOrWhiteSpace(DalamudLogPath)
            ? Path.Combine(Directory.GetParent(PluginConfigsDir)?.FullName ?? "", "dalamud.log")
            : DalamudLogPath;

    private static string SettingsPath(string appDir) => Path.Combine(appDir, "uploader-app-settings.json");

    public static AppSettings Load(string appDir)
    {
        try
        {
            var path = SettingsPath(appDir);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch { }
        return CreateDefault(appDir);
    }

    public void Save(string appDir)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath(appDir), json);
    }
}

public sealed class AppState
{
    public string? LastSyncAt { get; set; }
    public string? LastSyncResult { get; set; }
    public string? LastCollectAt { get; set; }
    public string? LastCollectResult { get; set; }
    public string? LastUpdateCheckAt { get; set; }
    public string? AvailableVersion { get; set; }

    private static string StatePath(string appDir) => Path.Combine(appDir, "uploader-app-state.json");

    public static AppState Load(string appDir)
    {
        try
        {
            var path = StatePath(appDir);
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path));
                if (loaded != null) return loaded;
            }
        }
        catch { }
        return new AppState();
    }

    public void Save(string appDir)
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StatePath(appDir), json);
        }
        catch { }
    }
}
