using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin;

namespace MuseStatusExporter;

/// <summary>
/// Applies plugin configuration files delivered by Muse (the "config
/// downlink", since v1.8.0). The uploader drops request envelopes into the
/// "config-inbox" folder inside this plugin's config directory; each
/// envelope names a target file under the pluginConfigs folder (relative
/// path), the full replacement content (base64) and, optionally, the
/// SHA-256 of the file as Muse last saw it.
///
/// Safety model:
/// - Targets are confined to the pluginConfigs folder, must be .json and
///   must not point into this plugin's own folder.
/// - When an expected hash is given and the current file differs, the
///   request is aborted ("aborted-stale") instead of overwriting changes
///   made locally after Muse's snapshot.
/// - The current file is backed up under "config-backups" before writing
///   (backups older than 30 days are purged).
/// - When the target plugin is currently loaded and a reload is requested,
///   the plugin is disabled via /xldisableplugin, the file is replaced,
///   and the plugin is re-enabled via /xlenableplugin, so the new config
///   takes effect immediately. A plugin that is not loaded is never
///   enabled as a side effect; its new config applies on next load.
/// - Every request produces an entry in "ffxiv-config-results.json"
///   (uploaded by the uploader) so Muse can confirm the outcome.
/// </summary>
internal sealed class ConfigSyncProcessor
{
    private const int MaxResultEntries = 100;
    private const int RetentionDays = 30;
    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PostUnloadGraceTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EnableSettleTimeout = TimeSpan.FromSeconds(90);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string configDirectory;
    private readonly string pluginConfigsRoot;
    private readonly string inboxDirectory;
    private readonly string doneDirectory;
    private readonly string backupRoot;
    private readonly string resultsPath;

    private bool initialized;
    private ConfigResultsFile? results;
    private ActiveJob? activeJob;

    public ConfigSyncProcessor(string configDirectory)
    {
        this.configDirectory = configDirectory;
        pluginConfigsRoot = Directory.GetParent(configDirectory)?.FullName ?? configDirectory;
        inboxDirectory = Path.Combine(configDirectory, "config-inbox");
        doneDirectory = Path.Combine(inboxDirectory, "done");
        backupRoot = Path.Combine(configDirectory, "config-backups");
        resultsPath = Path.Combine(configDirectory, "ffxiv-config-results.json");
    }

    /// <summary>Short human-readable state for the /mstatus config subcommand.</summary>
    public string DescribeState()
    {
        EnsureInitialized();
        var pending = 0;
        try
        {
            if (Directory.Exists(inboxDirectory))
                pending = Directory.GetFiles(inboxDirectory, "*.json").Length;
        }
        catch
        {
            // Non-fatal: the count is informational only.
        }

        var last = results?.Results.Count > 0 ? results.Results[^1] : null;
        var lastText = last == null
            ? "no results yet"
            : $"{last.Status}: {last.Target} ({last.At})";
        return $"inbox {pending} pending; last result: {lastText}";
    }

    /// <summary>Advances the request queue. Called about once a second.</summary>
    public void Tick()
    {
        try
        {
            EnsureInitialized();

            if (activeJob != null)
            {
                AdvanceJob(activeJob);
                return;
            }

            var next = FindNextEnvelope();
            if (next != null)
                StartJob(next);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Config sync tick failed.");
        }
    }

    // ------------------------------------------------------------------
    // Initialization / startup sweep
    // ------------------------------------------------------------------

    private void EnsureInitialized()
    {
        if (initialized)
            return;
        initialized = true;

        Directory.CreateDirectory(inboxDirectory);
        Directory.CreateDirectory(doneDirectory);
        Directory.CreateDirectory(backupRoot);
        LoadResults();

        // Any entry still marked "processing" belongs to a run that was
        // cut short (game closed / plugin reloaded mid-apply). Finalize it
        // as interrupted instead of silently retrying: the target file may
        // or may not have been replaced, so a human/Muse check is safer.
        var dirty = false;
        foreach (var entry in results!.Results)
        {
            if (entry.Status == "processing")
            {
                entry.Status = "interrupted";
                entry.Detail = "The game or plugin stopped while this request was being applied. "
                    + "Check the target config before re-sending the request.";
                entry.At = DateTimeOffset.Now.ToString("o");
                dirty = true;
            }
        }
        if (dirty)
            SaveResults();

        // Envelopes whose request already has a final result are duplicate
        // deliveries; file them away without applying again.
        try
        {
            foreach (var path in Directory.GetFiles(inboxDirectory, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                var existing = results.Results.FirstOrDefault(r => r.Id == id);
                if (existing != null && existing.Status != "processing")
                    MoveToDone(path);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Config sync startup sweep failed.");
        }

        PurgeOldFiles(backupRoot);
        PurgeOldFiles(doneDirectory);
    }

    private void PurgeOldFiles(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return;
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to purge old config sync files under {Root}.", root);
        }
    }

    // ------------------------------------------------------------------
    // Results bookkeeping
    // ------------------------------------------------------------------

    private void LoadResults()
    {
        try
        {
            if (File.Exists(resultsPath))
                results = JsonSerializer.Deserialize<ConfigResultsFile>(File.ReadAllText(resultsPath));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to load the config results file; starting fresh.");
        }

        results ??= new ConfigResultsFile();
    }

    private void SaveResults()
    {
        if (results == null)
            return;

        results.UpdatedAt = DateTimeOffset.Now.ToString("o");
        if (results.Results.Count > MaxResultEntries)
            results.Results.RemoveRange(0, results.Results.Count - MaxResultEntries);

        var json = JsonSerializer.Serialize(results, JsonOptions);
        var tmpPath = resultsPath + ".tmp";
        File.WriteAllText(tmpPath, json);
        if (File.Exists(resultsPath))
            File.Replace(tmpPath, resultsPath, null);
        else
            File.Move(tmpPath, resultsPath);
    }

    private ConfigResultEntry UpsertResult(string id, string target, string? pluginName,
        string status, string detail)
    {
        var entry = results!.Results.FirstOrDefault(r => r.Id == id);
        if (entry == null)
        {
            entry = new ConfigResultEntry { Id = id };
            results.Results.Add(entry);
        }

        entry.Target = target;
        entry.PluginName = pluginName;
        entry.Status = status;
        entry.Detail = detail;
        entry.At = DateTimeOffset.Now.ToString("o");
        SaveResults();
        return entry;
    }

    // ------------------------------------------------------------------
    // Job lifecycle
    // ------------------------------------------------------------------

    private string? FindNextEnvelope()
    {
        if (!Directory.Exists(inboxDirectory))
            return null;

        foreach (var path in Directory.GetFiles(inboxDirectory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (results!.Results.All(r => r.Id != id))
                return path;
        }

        return null;
    }

    private void StartJob(string inboxPath)
    {
        ConfigRequestEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ConfigRequestEnvelope>(File.ReadAllText(inboxPath));
        }
        catch (Exception ex)
        {
            var fallbackId = Path.GetFileNameWithoutExtension(inboxPath);
            UpsertResult(fallbackId, string.Empty, null, "failed",
                $"The request envelope could not be parsed: {ex.Message}");
            MoveToDone(inboxPath);
            return;
        }

        if (envelope == null || !IsValidId(envelope.Id))
        {
            var fallbackId = Path.GetFileNameWithoutExtension(inboxPath);
            UpsertResult(fallbackId, envelope?.Target ?? string.Empty, envelope?.PluginName,
                "failed", "The request envelope has no valid Id.");
            MoveToDone(inboxPath);
            return;
        }

        UpsertResult(envelope.Id, envelope.Target ?? string.Empty, envelope.PluginName,
            "processing", "Applying.");

        try
        {
            var targetPath = ResolveTargetPath(envelope.Target, out var targetError);
            if (targetPath == null)
            {
                FinalizeJob(envelope, inboxPath, "failed", targetError);
                return;
            }

            byte[] contentBytes;
            try
            {
                contentBytes = Convert.FromBase64String(envelope.ContentBase64 ?? string.Empty);
            }
            catch (Exception ex)
            {
                FinalizeJob(envelope, inboxPath, "failed", $"ContentBase64 is invalid: {ex.Message}");
                return;
            }

            try
            {
                using var _ = JsonDocument.Parse(contentBytes);
            }
            catch (Exception ex)
            {
                FinalizeJob(envelope, inboxPath, "failed", $"The replacement content is not valid JSON: {ex.Message}");
                return;
            }

            // Stale-baseline guard: never overwrite a file that changed
            // locally after Muse's snapshot.
            var targetExists = File.Exists(targetPath);
            if (!string.IsNullOrWhiteSpace(envelope.ExpectedSha256))
            {
                if (!targetExists)
                {
                    FinalizeJob(envelope, inboxPath, "aborted-stale",
                        "The target file no longer exists, but the request expected an existing file.");
                    return;
                }

                var currentHash = Sha256Hex(File.ReadAllBytes(targetPath));
                if (!currentHash.Equals(envelope.ExpectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    FinalizeJob(envelope, inboxPath, "aborted-stale",
                        "The target file changed after Muse's snapshot (hash mismatch); nothing was overwritten.");
                    return;
                }
            }

            if (targetExists)
                BackupCurrentFile(envelope.Target ?? string.Empty, targetPath);

            var job = new ActiveJob
            {
                Envelope = envelope,
                InboxPath = inboxPath,
                TargetPath = targetPath,
                ContentBytes = contentBytes,
                WrittenSha256 = Sha256Hex(contentBytes),
            };

            // Decide whether a live reload applies. Only a plugin that is
            // loaded right now is cycled; a plugin that is not loaded keeps
            // its state and simply reads the new file on its next load.
            job.MatchedPlugin = FindTargetPlugin(envelope);
            if (job.MatchedPlugin != null
                && job.MatchedPlugin.InternalName.Equals("MuseStatusExporter", StringComparison.OrdinalIgnoreCase))
            {
                FinalizeJob(envelope, inboxPath, "failed",
                    "Refusing to reload Muse Status Exporter itself.");
                return;
            }

            job.ReloadLive = envelope.Reload && job.MatchedPlugin is { IsLoaded: true };
            job.Step = job.ReloadLive ? JobStep.Disable : JobStep.Write;
            job.StepReadyUtc = DateTime.UtcNow;
            activeJob = job;
        }
        catch (Exception ex)
        {
            FinalizeJob(envelope, inboxPath, "failed", $"Unexpected error before applying: {ex.Message}");
        }
    }

    private void AdvanceJob(ActiveJob job)
    {
        if (DateTime.UtcNow < job.StepReadyUtc)
            return;

        try
        {
            switch (job.Step)
            {
                case JobStep.Disable:
                    job.DisableDispatched = Plugin.CommandManager.ProcessCommand(
                        $"/xldisableplugin \"{job.MatchedPlugin!.Name}\"");
                    Plugin.Log.Info("Config sync {Id}: disable dispatched for {Plugin} (result {Result}).",
                        job.Envelope.Id, job.MatchedPlugin.Name, job.DisableDispatched);
                    job.DisableStartedUtc = DateTime.UtcNow;
                    job.Step = JobStep.WaitUnload;
                    job.StepReadyUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    break;

                case JobStep.WaitUnload:
                {
                    // Do not write on a fixed timer: a large plugin can
                    // still be unloading (and saving its config on dispose)
                    // for a long time after the disable command. Wait until
                    // it actually reports unloaded, so its own save lands
                    // before the replacement write instead of reverting it.
                    var current = FindPlugin(job.MatchedPlugin!.InternalName, null);
                    if (current is { IsLoaded: true })
                    {
                        if (DateTime.UtcNow - job.DisableStartedUtc < UnloadTimeout)
                        {
                            job.StepReadyUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                            return;
                        }

                        FinalizeJob(job.Envelope, job.InboxPath, "failed",
                            $"The plugin did not unload within {UnloadTimeout.TotalSeconds:0}s "
                            + "after the disable command; nothing was changed.");
                        activeJob = null;
                        break;
                    }

                    Plugin.Log.Info("Config sync {Id}: {Plugin} reported unloaded.",
                        job.Envelope.Id, job.MatchedPlugin.Name);
                    job.Step = JobStep.Write;
                    job.StepReadyUtc = DateTime.UtcNow + PostUnloadGraceTime;
                    break;
                }

                case JobStep.Write:
                {
                    var directory = Path.GetDirectoryName(job.TargetPath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                    var tmpPath = job.TargetPath + ".muse-tmp";
                    File.WriteAllBytes(tmpPath, job.ContentBytes);
                    if (File.Exists(job.TargetPath))
                        File.Replace(tmpPath, job.TargetPath, null);
                    else
                        File.Move(tmpPath, job.TargetPath);
                    job.Step = job.ReloadLive ? JobStep.Enable : JobStep.Verify;
                    job.StepReadyUtc = DateTime.UtcNow;
                    break;
                }

                case JobStep.Enable:
                    job.EnableDispatched = Plugin.CommandManager.ProcessCommand(
                        $"/xlenableplugin \"{job.MatchedPlugin!.Name}\"");
                    Plugin.Log.Info("Config sync {Id}: enable dispatched for {Plugin} (result {Result}).",
                        job.Envelope.Id, job.MatchedPlugin.Name, job.EnableDispatched);
                    job.Step = JobStep.Verify;
                    job.VerifyStartedUtc = DateTime.UtcNow;
                    job.StepReadyUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    break;

                case JobStep.Verify:
                {
                    // Give a re-enabled plugin time to come back before
                    // judging the outcome.
                    if (job.ReloadLive && job.MatchedPlugin != null)
                    {
                        var current = FindPlugin(job.MatchedPlugin.InternalName, null);
                        if (current is { IsLoaded: false }
                            && DateTime.UtcNow - job.VerifyStartedUtc < EnableSettleTimeout)
                        {
                            job.StepReadyUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                            return;
                        }

                        job.PluginLoadedAfter = current?.IsLoaded ?? false;
                    }

                    VerifyAndFinalize(job);
                    activeJob = null;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            FinalizeJob(job.Envelope, job.InboxPath, "failed", $"Failed while applying: {ex.Message}");
            activeJob = null;
        }
    }

    private void VerifyAndFinalize(ActiveJob job)
    {
        string status;
        string detail;

        if (!File.Exists(job.TargetPath))
        {
            status = "failed";
            detail = "The target file is missing after the write step.";
        }
        else
        {
            var afterBytes = File.ReadAllBytes(job.TargetPath);
            var intact = Sha256Hex(afterBytes).Equals(job.WrittenSha256, StringComparison.OrdinalIgnoreCase);
            var reformatted = false;
            if (!intact)
            {
                try
                {
                    var writtenNode = JsonNode.Parse(job.ContentBytes);
                    var afterNode = JsonNode.Parse(afterBytes);
                    reformatted = writtenNode != null && afterNode != null && JsonNode.DeepEquals(writtenNode, afterNode);
                }
                catch
                {
                    reformatted = false;
                }
            }

            if (job.ReloadLive && !job.PluginLoadedAfter)
            {
                status = "applied-unverified";
                detail = "The file was replaced, but the plugin did not report as loaded again within "
                    + $"{EnableSettleTimeout.TotalSeconds:0}s (enable dispatched: {job.EnableDispatched}). "
                    + "Check the plugin state in /xlplugins.";
            }
            else if (intact || reformatted)
            {
                status = job.ReloadLive ? "applied" : "applied-pending-load";
                detail = job.ReloadLive
                    ? "Config replaced and the plugin was reloaded; the new content is in place"
                        + (reformatted ? " (the plugin re-saved it in its own formatting)." : ".")
                    : "Config replaced. The plugin was not loaded, so the new config takes effect on its next load.";
            }
            else
            {
                status = "applied-unverified";
                detail = "The file was replaced, but its content differs from what was sent; "
                    + "the plugin likely adjusted its config while loading. Re-collect the config to see the effective values.";
            }
        }

        FinalizeJob(job.Envelope, job.InboxPath, status, detail);
    }

    private void FinalizeJob(ConfigRequestEnvelope envelope, string inboxPath, string status, string detail)
    {
        UpsertResult(envelope.Id, envelope.Target ?? string.Empty, envelope.PluginName, status, detail);
        Plugin.Log.Info("Config sync {Id}: {Status} ({Detail})", envelope.Id, status, detail);
        MoveToDone(inboxPath);
    }

    private void MoveToDone(string inboxPath)
    {
        try
        {
            if (!File.Exists(inboxPath))
                return;
            Directory.CreateDirectory(doneDirectory);
            var destination = Path.Combine(doneDirectory, Path.GetFileName(inboxPath));
            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(inboxPath, destination);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to file away the processed config request {Path}.", inboxPath);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private IExposedPlugin? FindTargetPlugin(ConfigRequestEnvelope envelope)
        => FindPlugin(envelope.PluginInternalName, envelope.PluginName);

    private IExposedPlugin? FindPlugin(string? internalName, string? displayName)
    {
        try
        {
            var plugins = Plugin.PluginInterface.InstalledPlugins;
            if (!string.IsNullOrWhiteSpace(internalName))
            {
                var byInternal = plugins.FirstOrDefault(p =>
                    p.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase));
                if (byInternal != null)
                    return byInternal;
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                return plugins.FirstOrDefault(p =>
                    p.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to enumerate installed plugins.");
        }

        return null;
    }

    private static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length < 8 || id.Length > 64)
            return false;
        return id.All(c => char.IsLetterOrDigit(c) || c == '-');
    }

    private string? ResolveTargetPath(string? target, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            error = "Target is empty.";
            return null;
        }

        var normalized = target.Replace('\\', '/').Trim();
        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            error = "Target must be a relative path under pluginConfigs.";
            return null;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            error = "Target contains invalid path segments.";
            return null;
        }

        if (!segments[^1].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            error = "Target must be a .json file.";
            return null;
        }

        if (segments[0].Equals("MuseStatusExporter", StringComparison.OrdinalIgnoreCase))
        {
            error = "Refusing to modify Muse Status Exporter's own config folder.";
            return null;
        }

        var rootFull = Path.GetFullPath(pluginConfigsRoot);
        var full = Path.GetFullPath(Path.Combine(rootFull, string.Join(Path.DirectorySeparatorChar, segments)));
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            error = "Target escapes the pluginConfigs folder.";
            return null;
        }

        return full;
    }

    private void BackupCurrentFile(string target, string targetPath)
    {
        var safeName = target.Replace('\\', '/').Replace('/', '_');
        var backupDir = Path.Combine(backupRoot, safeName);
        Directory.CreateDirectory(backupDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = Path.Combine(backupDir, $"{stamp}.json");
        var suffix = 1;
        while (File.Exists(backupPath))
            backupPath = Path.Combine(backupDir, $"{stamp}-{suffix++}.json");
        File.Copy(targetPath, backupPath);
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private enum JobStep
    {
        Disable,
        WaitUnload,
        Write,
        Enable,
        Verify,
    }

    private sealed class ActiveJob
    {
        public required ConfigRequestEnvelope Envelope { get; init; }
        public required string InboxPath { get; init; }
        public required string TargetPath { get; init; }
        public required byte[] ContentBytes { get; init; }
        public required string WrittenSha256 { get; init; }
        public IExposedPlugin? MatchedPlugin { get; set; }
        public bool ReloadLive { get; set; }
        public JobStep Step { get; set; }
        public DateTime StepReadyUtc { get; set; }
        public DateTime DisableStartedUtc { get; set; }
        public DateTime VerifyStartedUtc { get; set; }
        public bool DisableDispatched { get; set; }
        public bool EnableDispatched { get; set; }
        public bool PluginLoadedAfter { get; set; }
    }
}

/// <summary>One config-change request delivered through the uploader.</summary>
public sealed class ConfigRequestEnvelope
{
    public string Id { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string? PluginName { get; set; }
    public string? PluginInternalName { get; set; }
    public bool Reload { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string ContentBase64 { get; set; } = string.Empty;
}

/// <summary>The results file uploaded back to Muse.</summary>
public sealed class ConfigResultsFile
{
    public string UpdatedAt { get; set; } = string.Empty;
    public List<ConfigResultEntry> Results { get; set; } = [];
}

public sealed class ConfigResultEntry
{
    public string Id { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string? PluginName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string At { get; set; } = string.Empty;
}
