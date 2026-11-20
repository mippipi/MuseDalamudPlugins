using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.NativeWrapper;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

// The ClientStructs Achievement struct (shared FATE / achievement progress
// requests), not the Lumina sheet of the same name.
using Achievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;

namespace MuseStatusExporter;

/// <summary>
/// Exports the logged-in character's job/class levels and Cosmic Exploration
/// (cosmic tool) progress to a per-character JSON file
/// ("ffxiv-status-&lt;Character&gt;-&lt;World&gt;.json") in this plugin's config folder.
///
/// Data sources (all standard game structures, no other plugin required):
/// - Levels / EXP: FFXIVClientStructs PlayerState (GetClassJobLevel / GetClassJobExp).
/// - Cosmic class scores: WKSManager.State.Scores (same values ICE displays).
/// - Cosmic tool stages / research analysis: WKSManager.ResearchModule.
///
/// Writes happen on login, whenever the exported data changes, every 5 minutes
/// while logged in (freshness heartbeat), and on demand via /mstatus.
///
/// Since v1.4.0 the plugin also records incoming /tell messages (sender name,
/// sender world and message text) to a per-character file
/// ("ffxiv-tells-&lt;Character&gt;-&lt;World&gt;.json", newest 200 entries),
/// so an external uploader can forward them to the user.
///
/// Since v1.5.0 the snapshot also carries:
/// - Currencies: the Currency container (gil, tomestones, scrips, MGP, ...).
/// - Inventory: every loaded container (bags, armoury chest, saddlebags and,
///   while a retainer is summoned, that retainer's bags), item id / name /
///   count / HQ per occupied slot.
/// - Retainers: name, level, class/job, gil held and venture state
///   (venture id + completion time) from RetainerManager.
/// - GrandCompanySupply: the daily supply / provisioning requests from
///   AgentGrandCompanySupply (populated after the supply window has been
///   opened at least once since login).
/// - HousingVisits: housing-area entries (world, ward, plot, room) detected
///   by polling HousingManager on the regular tick. Visits are kept in a
///   per-character sidecar file ("mstatus-housing-&lt;Character&gt;-&lt;World&gt;.json",
///   newest 100) and embedded into the snapshot.
///
/// Since v1.6.0 the Grand Company supply capture is driven by the supply
/// window itself: when the "GrandCompanySupplyList" addon opens, the plugin
/// polls the agent until the request list is readable and then exports the
/// snapshot immediately, so the list is captured even if it only exists
/// while the window is open.
///
/// Since v1.6.2 the capture is also driven by the Contents Info window
/// ("ContentsInfo", the timers window whose supply / provisioning panel the
/// player actually opens), and the request list is read from BOTH agent
/// stores: SupplyProvisioningData (timers-style list) and the delivery
/// window's ItemArray / NumItems (GrandCompanyItem entries), whichever
/// currently holds data.
///
/// Since v1.7.0 the supply list is read from the windows' AtkValues (the
/// place Artisan reads it): the Contents Info detail window
/// ("ContentsInfoDetail", item ids at AtkValues 233..240, requested counts
/// at +16) and the delivery window ("GrandCompanySupplyList", item ids at
/// 425..432, counts at -40). AgentGrandCompanySupply never held the list
/// in practice. The last captured list is kept per character in a sidecar
/// file ("mstatus-gcsupply-<Character>-<World>.json") and embedded into
/// every snapshot until a newer capture replaces it. The snapshot also
/// carries the plugin version and, for diagnosis, a dump of the captured
/// window's numeric AtkValues ("GcWindowDebug").
///
/// Since v1.8.0 the plugin also applies plugin configuration files sent
/// by Muse (see ConfigSync.cs): request envelopes delivered into the
/// "config-inbox" folder are validated, backed up, written to their
/// target under pluginConfigs, and the target plugin is reloaded via
/// /xldisableplugin + /xlenableplugin when it is currently loaded.
/// Outcomes are recorded in "ffxiv-config-results.json".
///
/// Since v1.12.0 the snapshot also carries shared FATE progress
/// ("FateProgress"): per-zone current rank, completed FATE count and the
/// count needed for the next rank, read from AgentFateProgress (the data
/// behind the game's FATE progress window). The agent only holds data
/// after the progress has been delivered to the client (in practice,
/// after the FATE progress window has been opened once since login), so
/// the last readable state is kept per character in a sidecar file
/// ("mstatus-fateprogress-&lt;Character&gt;-&lt;World&gt;.json") and embedded
/// into every snapshot, the same pattern as the GC supply capture.
///
/// Since v1.12.2 the plugin also fetches the progress itself, following
/// Auto FATE Grind's design: every shared-FATE zone's completed count is
/// backed by a hidden achievement, so the plugin requests the per-tab
/// FATE progress (Achievement.RequestFateProgressTab) and each zone
/// achievement's progress (main command 1000) from the server, and a
/// hook on Achievement.ReceiveAchievementProgress records the answers.
/// Ranks are derived from the per-expansion rank sizes (6 + 60 for
/// Shadowbringers / Endwalker, 6 + 20 + 40 for Dawntrail; 66 total).
/// The FATE progress window no longer has to be opened for the data to
/// appear; the AgentFateProgress read remains as the preferred source
/// whenever it holds data.
///
/// v1.12.4 normalizes the agent-sourced FATE progress values to the
/// achievement path's shape (cumulative completed count, remainder to
/// the next rank, ranks from 1, real max rank), so displays can treat
/// every zone like a job level: rank with "completed / 66" progress.
///
/// v1.12.3 fixes the EXP earring auto-equip firing at the level cap: the
/// v1.9.1 condition tested GetClassJobNeededExp &lt;= 0 as "at cap", but
/// that value is 29,023,000 at level 100 (the next level's requirement),
/// not 0, so the Halone Earring was equipped on a level-100 Warrior.
/// The check now uses PlayerState.GetCurrentClassJobMaxLevel (the cap
/// of the job being played) plus the level &lt; 100 rule.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/mstatus";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);

    // ClassJob row ids 8..18 are the 11 Cosmic Exploration classes, in order.
    private static readonly string[] CosmicAbbreviations =
        ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL", "MIN", "BTN", "FSH"];

    // Equip slot order shared by gearset entries and the equipped-items container
    // (RaptureGearsetModule.GearsetItemIndex). "Belt" no longer exists in game;
    // it is always empty and therefore omitted from the output.
    private static readonly string[] EquipSlotNames =
        ["MainHand", "OffHand", "Head", "Body", "Hands", "Belt", "Legs", "Feet",
         "Ears", "Neck", "Wrists", "RingRight", "RingLeft", "SoulCrystal"];

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const int MaxTellEntries = 200;
    private const int MaxHousingVisitEntries = 100;

    // Inventory containers exported into the snapshot, in a stable order.
    // Retainer pages only carry data while a retainer is summoned; unloaded
    // containers are skipped, so they appear exactly when they are readable.
    private static readonly InventoryType[] InventoryContainers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
        InventoryType.RetainerPage1, InventoryType.RetainerPage2,
        InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7, InventoryType.RetainerEquippedItems,
    ];

    private readonly string configDirectory;
    private readonly object tellLock = new();
    private readonly object housingLock = new();
    private readonly Dictionary<string, List<HousingVisitEntry>> housingVisitsByCharacter = new();
    private readonly Dictionary<string, string> housingKeyByCharacter = new();
    private string? lastPayloadJson;
    private string? lastWrittenPath;
    private string dataUpdatedAt = string.Empty;
    private DateTime lastCheckUtc = DateTime.MinValue;
    private DateTime lastWriteUtc = DateTime.MinValue;

    // Grand Company supply capture (v1.7.0). AgentGrandCompanySupply never
    // held the request list in any export, so the list is read from the
    // windows' AtkValues instead (the same place Artisan reads it):
    //   "ContentsInfoDetail" (the timers detail window the player opens):
    //     item ids at AtkValues 233..240, requested counts at +16.
    //   "GrandCompanySupplyList" (the delivery window):
    //     item ids at 425..432, requested counts at -40.
    // The last captured list is kept per character (sidecar file) and
    // embedded into every snapshot until a newer capture replaces it.
    private const string GcSupplyAddonName = "GrandCompanySupplyList";
    private const string ContentsInfoDetailAddonName = "ContentsInfoDetail";
    private readonly object gcLock = new();
    private readonly Dictionary<string, GrandCompanySupplyData> gcCaptureByCharacter = new();
    private readonly Dictionary<string, string> gcCaptureKeyByCharacter = new();
    private readonly Dictionary<string, GcCaptureDebugData> gcDebugByCharacter = new();
    private DateTime lastGcCheckUtc = DateTime.MinValue;

    // Shared FATE progress capture (v1.12.0). AgentFateProgress holds the
    // per-zone rank / completed-FATE counts behind the FATE progress
    // window, but only after the data has been delivered to the client
    // (in practice after the window has been opened once since login).
    // The last readable state is kept per character (sidecar file) and
    // embedded into every snapshot until a newer capture replaces it.
    private readonly object fateLock = new();
    private readonly Dictionary<string, List<FateProgressZoneEntry>> fateCaptureByCharacter = new();
    private readonly Dictionary<string, string> fateCaptureKeyByCharacter = new();
    private DateTime lastFateCheckUtc = DateTime.MinValue;

    // Shared FATE progress through achievement requests (v1.12.2), the
    // design Auto FATE Grind uses: every shared-FATE zone's completed
    // count is backed by a hidden achievement, so the plugin asks the
    // server for the data itself instead of waiting for the FATE progress
    // window to be opened. Requests are Achievement.RequestFateProgressTab
    // per expansion tab plus main command 1000 per zone achievement (the
    // exact requests Auto FATE Grind issues); the answers arrive through
    // Achievement.ReceiveAchievementProgress, which is hooked to record
    // (current, max) per zone. Ranks are derived from the per-expansion
    // rank sizes: Shadowbringers / Endwalker need 6 + 60 = 66 FATEs for
    // rank 3, Dawntrail needs 6 + 20 + 40 = 66 for rank 4.
    private sealed record SharedFateZoneInfo(uint TerritoryTypeId, uint AchievementId, int TabIndex, int DisplayOrder);

    private static readonly SharedFateZoneInfo[] SharedFateZones =
    [
        // Shadowbringers (tab 0), achievements 2343-2348.
        new(813, 2343, 0, 0), new(814, 2344, 0, 1), new(815, 2345, 0, 2),
        new(816, 2346, 0, 3), new(817, 2347, 0, 4), new(818, 2348, 0, 5),
        // Endwalker (tab 1), achievements 3022-3027.
        new(956, 3022, 1, 0), new(957, 3023, 1, 1), new(958, 3024, 1, 2),
        new(959, 3025, 1, 3), new(961, 3026, 1, 4), new(960, 3027, 1, 5),
        // Dawntrail (tab 2), achievements 3559-3564.
        new(1187, 3559, 2, 0), new(1188, 3560, 2, 1), new(1189, 3561, 2, 2),
        new(1190, 3562, 2, 3), new(1191, 3563, 2, 4), new(1192, 3564, 2, 5),
    ];

    private static readonly int[][] SharedFateRankSizes = [[6, 60], [6, 60], [6, 20, 40]];
    private const int SharedFateTotalFates = 66;
    private static readonly HashSet<uint> SharedFateAchievementIds = SharedFateZones.Select(z => z.AchievementId).ToHashSet();

    private unsafe delegate void ReceiveAchievementProgressDelegate(Achievement* thisPtr, uint id, uint current, uint max);

    private Hook<ReceiveAchievementProgressDelegate>? achievementProgressHook;
    private readonly object fateAchievementLock = new();
    private readonly Dictionary<uint, (uint Current, uint Max)> fateAchievementProgress = new();
    private readonly Dictionary<uint, string> fateZoneNameCache = new();
    private DateTime lastFateRequestUtc = DateTime.MinValue;

    // Config downlink (v1.8.0): applies configuration files sent by Muse.
    private readonly ConfigSyncProcessor configSync;
    private DateTime lastConfigSyncUtc = DateTime.MinValue;

    // ---- Feature settings (v1.10.0) -----------------------------------------
    private readonly Configuration configuration;
    private readonly WindowSystem windowSystem;
    private readonly ConfigWindow configWindow;

    // ---- EXP earring auto-equip (v1.9.0) -----------------------------------
    // Halone Earring (item 52439): pre-order bonus earring whose kill-EXP
    // bonus (+30%) applies at levels 1-100 for battle classes/jobs. While the
    // current job qualifies, keep it in the Ears slot: when another earring
    // is equipped and the Halone Earring sits in the bags or the armoury
    // chest, move it into the equipped container (the game swaps the two).
    private const uint HaloneEarringItemId = 52439;
    private const int EarsSlotIndex = 8; // EquipSlotNames order
    private const int ExpEarringMaxLevel = 100;
    private static readonly TimeSpan ExpEarringCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExpEarringAttemptInterval = TimeSpan.FromSeconds(15);
    private DateTime lastExpEarringCheckUtc = DateTime.MinValue;
    private DateTime lastExpEarringAttemptUtc = DateTime.MinValue;
    private bool expEarringEquipPending;

    public Plugin()
    {
        configDirectory = PluginInterface.GetPluginConfigDirectory();
        configSync = new ConfigSyncProcessor(configDirectory);

        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configWindow = new ConfigWindow(configuration);
        windowSystem = new WindowSystem("MuseStatusExporter");
        windowSystem.AddWindow(configWindow);
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigWindow;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Write the current character status (job levels + cosmic progress + gearsets) to its per-character JSON file now.",
        });

        Framework.Update += OnFrameworkUpdate;
        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;
        ChatGui.ChatMessage += OnChatMessage;
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, GcSupplyAddonName, OnGcWindowEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, GcSupplyAddonName, OnGcWindowEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, GcSupplyAddonName, OnGcWindowEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, ContentsInfoDetailAddonName, OnGcWindowEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, ContentsInfoDetailAddonName, OnGcWindowEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, ContentsInfoDetailAddonName, OnGcWindowEvent);

        // Hook the achievement progress receive function (v1.12.2) so the
        // shared FATE counts are captured when the server answers this
        // plugin's requests. Failing to hook only disables this capture
        // path; the AgentFateProgress read keeps working as before.
        SetupAchievementProgressHook();
    }

    private unsafe void SetupAchievementProgressHook()
    {
        try
        {
            var address = Achievement.Addresses.ReceiveAchievementProgress.Value;
            if (address != nint.Zero)
            {
                achievementProgressHook = GameInteropProvider.HookFromAddress<ReceiveAchievementProgressDelegate>(
                    address, OnReceiveAchievementProgress);
                achievementProgressHook.Enable();
            }
            else
            {
                Log.Warning("Achievement.ReceiveAchievementProgress is not resolved; shared FATE achievement capture is disabled.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to hook Achievement.ReceiveAchievementProgress; shared FATE achievement capture is disabled.");
            achievementProgressHook = null;
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigWindow;
        windowSystem.RemoveAllWindows();

        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, GcSupplyAddonName, OnGcWindowEvent);
        AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, GcSupplyAddonName, OnGcWindowEvent);
        AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, GcSupplyAddonName, OnGcWindowEvent);
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, ContentsInfoDetailAddonName, OnGcWindowEvent);
        AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, ContentsInfoDetailAddonName, OnGcWindowEvent);
        AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, ContentsInfoDetailAddonName, OnGcWindowEvent);
        ChatGui.ChatMessage -= OnChatMessage;
        ClientState.Logout -= OnLogout;
        ClientState.Login -= OnLogin;
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);

        achievementProgressHook?.Disable();
        achievementProgressHook?.Dispose();
        achievementProgressHook = null;
    }

    private void OnLogin()
    {
        // Force the next tick to export, even if nothing appears to have changed.
        lastPayloadJson = null;
        lastWriteUtc = DateTime.MinValue;
    }

    private void OnLogout(int type, int code)
    {
        // Shared FATE achievement progress belongs to the logged-in
        // character; forget it so the next character starts clean (the
        // same reset Auto FATE Grind performs on logout).
        lock (fateAchievementLock)
            fateAchievementProgress.Clear();
        lastFateRequestUtc = DateTime.MinValue;
    }

    private void ToggleConfigWindow() => configWindow.Toggle();

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            configSync.Tick();
            ChatGui.Print($"[Muse Status Exporter] Config sync: {configSync.DescribeState()}");
            return;
        }

        if (TryExport(force: true))
            ChatGui.Print($"[Muse Status Exporter] Wrote status to {lastWrittenPath}");
        else
            ChatGui.Print("[Muse Status Exporter] Not logged in; nothing to write.");
    }

    /// <summary>Records incoming /tell messages to the per-character tells file.</summary>
    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.TellIncoming)
            return;

        if (!configuration.RecordTells)
            return;

        try
        {
            var player = ObjectTable.LocalPlayer;
            if (player == null)
                return;

            var character = player.Name.TextValue;
            var world = ResolveWorldName(player.HomeWorld.RowId);

            var sender = string.Empty;
            var senderWorld = string.Empty;
            foreach (var payload in message.Sender.Payloads)
            {
                if (payload is PlayerPayload playerPayload)
                {
                    sender = playerPayload.PlayerName;
                    if (playerPayload.World.RowId != 0)
                        senderWorld = ResolveWorldName(playerPayload.World.RowId);
                    break;
                }
            }

            if (sender.Length == 0)
                sender = message.Sender.TextValue.Trim();

            var entry = new TellEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                At = DateTimeOffset.Now.ToString("o"),
                Sender = sender,
                SenderWorld = senderWorld.Length == 0 ? null : senderWorld,
                Message = message.Message.TextValue,
            };

            AppendTell(character, world, entry);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to record an incoming tell.");
        }
    }

    private void AppendTell(string character, string world, TellEntry entry)
    {
        lock (tellLock)
        {
            var fileName = $"ffxiv-tells-{SanitizeFilePart(character)}-{SanitizeFilePart(world)}.json";
            var path = Path.Combine(configDirectory, fileName);

            TellFile file;
            try
            {
                file = File.Exists(path)
                    ? JsonSerializer.Deserialize<TellFile>(File.ReadAllText(path)) ?? new TellFile()
                    : new TellFile();
            }
            catch
            {
                file = new TellFile();
            }

            file.Character = character;
            file.World = world;
            file.UpdatedAt = entry.At;
            file.Tells.Add(entry);
            if (file.Tells.Count > MaxTellEntries)
                file.Tells.RemoveRange(0, file.Tells.Count - MaxTellEntries);

            var json = JsonSerializer.Serialize(file, JsonOptions);
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(path))
                File.Replace(tmpPath, path, null);
            else
                File.Move(tmpPath, path);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;

        // Poll the GC supply windows once a second: while either window is
        // open, its AtkValues hold the daily request list (v1.7.0).
        if (now - lastGcCheckUtc >= TimeSpan.FromSeconds(1))
        {
            lastGcCheckUtc = now;
            PollGcWindows();
        }

        // Poll the shared FATE progress agent once a second (v1.12.0): its
        // data only exists after the client has received it, so capture it
        // whenever it is readable.
        if (now - lastFateCheckUtc >= TimeSpan.FromSeconds(1))
        {
            lastFateCheckUtc = now;
            PollFateProgress();
        }

        // Advance the config downlink about once a second (v1.8.0).
        if (configuration.ConfigDownlink && now - lastConfigSyncUtc >= TimeSpan.FromSeconds(1))
        {
            lastConfigSyncUtc = now;
            configSync.Tick();
        }

        // Keep the EXP earring equipped while leveling a battle job (v1.9.0).
        if (now - lastExpEarringCheckUtc >= ExpEarringCheckInterval)
        {
            lastExpEarringCheckUtc = now;
            TryMaintainExpEarring(now);
        }

        if (now - lastCheckUtc < CheckInterval)
            return;
        lastCheckUtc = now;

        TryExport(force: false);
    }

    /// <summary>
    /// Keeps the Halone Earring (EXP +30%, levels 1-100, battle classes only)
    /// in the Ears slot while the current job can actually gain EXP from it:
    /// a battle class/job, inside the earring's level range, and below the
    /// job's own level cap. Does nothing when the earring is not owned (not
    /// in bags / armoury chest) or while gear changes are impossible
    /// (combat, cutscenes, zoning). A successful move is confirmed on a
    /// later check before reporting it, because the move itself only queues
    /// the equip.
    /// </summary>
    private unsafe void TryMaintainExpEarring(DateTime now)
    {
        if (!configuration.AutoEquipHaloneEarring)
            return;

        try
        {
            var player = ObjectTable.LocalPlayer;
            if (player == null || ClientState.IsPvP)
                return;

            var jobId = (int)player.ClassJob.RowId;
            // ClassJob 8-15 are crafters and 16-18 are gatherers; the earring
            // is for battle classes/jobs (Fighter/Sorcerer) only.
            if (jobId >= 8 && jobId <= 18)
                return;

            var playerState = PlayerState.Instance();
            if (playerState == null)
                return;

            var level = playerState->GetClassJobLevel(jobId, false);
            // The earring's bonus applies below level 100 only; at 100 no
            // EXP can be gained, so it has no effect there.
            if (level <= 0 || level >= ExpEarringMaxLevel)
                return;

            // Run only below the current job's own level cap: at the cap
            // no EXP can be gained, so the bonus has no effect. The game
            // itself reports the cap of the job being played (100 for
            // regular jobs, the lower caps of Blue Mage / Beastmaster),
            // so nothing is hardcoded. (Until v1.12.2 this instead tested
            // GetClassJobNeededExp <= 0, but that value is NOT 0 at the
            // cap: at level 100 it returns 29,023,000, the exp the next
            // level would need, so the earring was wrongly equipped on a
            // level-100 Warrior. Fixed in v1.12.3.)
            var maxLevel = playerState->GetCurrentClassJobMaxLevel();
            if (maxLevel > 0 && level >= maxLevel)
                return;

            if (Condition[ConditionFlag.InCombat]
                || Condition[ConditionFlag.WatchingCutscene]
                || Condition[ConditionFlag.OccupiedInCutSceneEvent]
                || Condition[ConditionFlag.BetweenAreas])
                return;

            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return;

            var equipped = inventory->GetInventoryContainer(InventoryType.EquippedItems);
            if (equipped == null || !equipped->IsLoaded || equipped->Items == null
                || equipped->Size <= EarsSlotIndex)
                return;

            var earItem = equipped->Items + EarsSlotIndex;
            if (earItem != null && earItem->GetBaseItemId() == HaloneEarringItemId)
            {
                if (expEarringEquipPending)
                {
                    expEarringEquipPending = false;
                    ChatGui.Print("[Muse Status Exporter] ハルオーネイヤリングを装備しました。");
                }
                return;
            }

            // Find the earring: armoury chest first, then the bags.
            InventoryType sourceType = default;
            var sourceSlot = -1;
            foreach (var type in ExpEarringSearchContainers)
            {
                var container = inventory->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded || container->Items == null)
                    continue;

                for (var slot = 0; slot < container->Size; slot++)
                {
                    var item = container->Items + slot;
                    if (item != null && item->GetBaseItemId() == HaloneEarringItemId)
                    {
                        sourceType = type;
                        sourceSlot = slot;
                        break;
                    }
                }

                if (sourceSlot >= 0)
                    break;
            }

            if (sourceSlot < 0)
                return; // Not owned (or not in a searchable container).

            if (now - lastExpEarringAttemptUtc < ExpEarringAttemptInterval)
                return;
            lastExpEarringAttemptUtc = now;

            var result = inventory->MoveItemSlot(
                sourceType, (ushort)sourceSlot,
                InventoryType.EquippedItems, (ushort)EarsSlotIndex);
            expEarringEquipPending = true;
            Log.Information(
                "EXP earring: MoveItemSlot {Source} slot {Slot} -> EquippedItems slot {Ears} returned {Result}.",
                sourceType, sourceSlot, EarsSlotIndex, result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to maintain the EXP earring.");
        }
    }

    private static readonly InventoryType[] ExpEarringSearchContainers =
    [
        InventoryType.ArmoryEar,
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>
    /// A GC window's values changed; re-read them. The addon is re-located
    /// by name inside the poll so events and polling share one code path.
    /// </summary>
    private void OnGcWindowEvent(AddonEvent type, AddonArgs args)
    {
        PollGcWindows();
    }

    private void PollGcWindows()
    {
        if (!configuration.CaptureGcSupply)
            return;

        try
        {
            CaptureFromAddon(GameGui.GetAddonByName(ContentsInfoDetailAddonName), ContentsInfoDetailAddonName);
            CaptureFromAddon(GameGui.GetAddonByName(GcSupplyAddonName), GcSupplyAddonName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to poll the Grand Company supply windows.");
        }
    }

    /// <summary>
    /// Reads the daily supply list from a window's AtkValues, using the
    /// layout Artisan uses: item ids occupy 8 consecutive slots and the
    /// requested count for a slot sits at a fixed offset from its id slot
    /// (ContentsInfoDetail: ids 233..240, counts +16; GrandCompanySupplyList:
    /// ids 425..432, counts -40).
    /// </summary>
    private void CaptureFromAddon(AtkUnitBasePtr addon, string addonName)
    {
        if (addon.IsNull || !addon.IsVisible)
            return;

        var values = addon.AtkValues.ToList();
        if (values.Count == 0)
            return;

        int idStart, countOffset;
        if (addonName == ContentsInfoDetailAddonName) { idStart = 233; countOffset = 16; }
        else if (addonName == GcSupplyAddonName) { idStart = 425; countOffset = -40; }
        else return;

        var supply = new List<SupplyItemEntry>();
        for (var i = 0; i < 8; i++)
        {
            if (!TryReadAtkUInt(values, idStart + i, out var itemId) || itemId == 0)
                continue;

            TryReadAtkUInt(values, idStart + i + countOffset, out var numRequested);
            var (name, _) = ResolveItem(itemId);
            supply.Add(new SupplyItemEntry
            {
                ItemId = itemId,
                Name = name ?? string.Empty,
                NumRequested = (int)numRequested,
            });
        }

        if (supply.Count == 0)
            return;

        StoreGcCapture(new GrandCompanySupplyData { Supply = supply }, BuildGcDebug(addonName, values));
    }

    private static bool TryReadAtkUInt(List<AtkValuePtr> values, int index, out uint result)
    {
        result = 0;
        if (index < 0 || index >= values.Count)
            return false;

        switch (SafeGetValue(values[index]))
        {
            case uint u:
                result = u;
                return true;
            case int n when n >= 0:
                result = (uint)n;
                return true;
            case long l when l >= 0 && l <= uint.MaxValue:
                result = (uint)l;
                return true;
            default:
                return false;
        }
    }

    private static object? SafeGetValue(AtkValuePtr value)
    {
        try
        {
            return value.GetValue();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Diagnostic dump of a captured window's numeric AtkValues
    /// ("index:Type:value", zero / empty values skipped, capped), embedded
    /// into the snapshot so the layout can be verified from the export.
    /// </summary>
    private static GcCaptureDebugData BuildGcDebug(string addonName, List<AtkValuePtr> values)
    {
        var debug = new GcCaptureDebugData
        {
            Addon = addonName,
            At = DateTimeOffset.Now.ToString("o"),
            ValueCount = values.Count,
        };

        for (var i = 0; i < values.Count && debug.Values.Count < 600; i++)
        {
            var raw = SafeGetValue(values[i]);
            string? repr = raw switch
            {
                int n when n != 0 => n.ToString(),
                uint u when u != 0 => u.ToString(),
                long l when l != 0 => l.ToString(),
                ulong ul when ul != 0 => ul.ToString(),
                float f when f != 0 => f.ToString("R"),
                bool b when b => "true",
                string s when s.Length > 0 => s.Length > 48 ? s[..48] : s,
                _ => null,
            };
            if (repr != null && raw != null)
                debug.Values.Add($"{i}:{raw.GetType().Name}:{repr}");
        }

        return debug;
    }

    /// <summary>
    /// Keeps the newest window capture per character: it is written to a
    /// sidecar file and embedded into snapshots (see BuildGrandCompanySupply)
    /// until a different list is captured. Exports immediately on change.
    /// </summary>
    private void StoreGcCapture(GrandCompanySupplyData data, GcCaptureDebugData debug)
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
            return;

        var character = player.Name.TextValue;
        var world = ResolveWorldName(player.HomeWorld.RowId);
        var key = $"{character}@{world}";
        var dataKey = GcCaptureKey(data);

        var changed = false;
        lock (gcLock)
        {
            gcDebugByCharacter[key] = debug;
            if (!gcCaptureKeyByCharacter.TryGetValue(key, out var existing) || existing != dataKey)
            {
                gcCaptureKeyByCharacter[key] = dataKey;
                gcCaptureByCharacter[key] = data;
                changed = true;
            }
        }

        if (!changed)
            return;

        try
        {
            var file = new GcSupplyFile
            {
                Character = character,
                World = world,
                CapturedAt = DateTimeOffset.Now.ToString("o"),
                Supply = data.Supply,
                Provisioning = data.Provisioning,
            };
            var path = GetGcCapturePath(character, world);
            var json = JsonSerializer.Serialize(file, JsonOptions);
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(path))
                File.Replace(tmpPath, path, null);
            else
                File.Move(tmpPath, path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save the Grand Company supply capture.");
        }

        if (TryExport(force: true))
            Log.Info("Grand Company supply list captured from the {Addon} window and exported.", debug.Addon);
    }

    private static string GcCaptureKey(GrandCompanySupplyData data)
        => string.Join(",", data.Supply.Select(s => $"{s.ItemId}:{s.NumRequested}"))
            + "|" + string.Join(",", data.Provisioning.Select(s => $"{s.ItemId}:{s.NumRequested}"));

    private GrandCompanySupplyData? LoadGcCapture(string character, string world)
    {
        var key = $"{character}@{world}";
        lock (gcLock)
        {
            if (gcCaptureByCharacter.TryGetValue(key, out var cached))
                return cached;
        }

        try
        {
            var path = GetGcCapturePath(character, world);
            if (!File.Exists(path))
                return null;

            var file = JsonSerializer.Deserialize<GcSupplyFile>(File.ReadAllText(path));
            if (file == null || (file.Supply.Count == 0 && file.Provisioning.Count == 0))
                return null;

            var data = new GrandCompanySupplyData { Supply = file.Supply, Provisioning = file.Provisioning };
            lock (gcLock)
            {
                gcCaptureByCharacter[key] = data;
                gcCaptureKeyByCharacter[key] = GcCaptureKey(data);
            }
            return data;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load the Grand Company supply capture.");
            return null;
        }
    }

    private string GetGcCapturePath(string character, string world)
    {
        var fileName = $"mstatus-gcsupply-{SanitizeFilePart(character)}-{SanitizeFilePart(world)}.json";
        return Path.Combine(configDirectory, fileName);
    }

    /// <summary>
    /// Reads the shared FATE progress from AgentFateProgress whenever it
    /// holds data and keeps the newest capture per character (sidecar file
    /// + snapshot embedding), mirroring the GC supply capture. Exports
    /// immediately when the captured values change.
    /// </summary>
    private void PollFateProgress()
    {
        if (!configuration.ExportFateProgress)
            return;

        try
        {
            RequestFateProgressIfNeeded(DateTime.UtcNow);

            var zones = ReadFateProgressFromAgent();
            if (zones == null || zones.Count == 0)
                zones = BuildFateProgressFromAchievements();
            if (zones.Count == 0)
                return;

            var player = ObjectTable.LocalPlayer;
            if (player == null)
                return;

            var character = player.Name.TextValue;
            var world = ResolveWorldName(player.HomeWorld.RowId);
            var key = $"{character}@{world}";
            var dataKey = FateCaptureKey(zones);

            var changed = false;
            lock (fateLock)
            {
                if (!fateCaptureKeyByCharacter.TryGetValue(key, out var existing) || existing != dataKey)
                {
                    fateCaptureKeyByCharacter[key] = dataKey;
                    fateCaptureByCharacter[key] = zones;
                    changed = true;
                }
            }

            if (!changed)
                return;

            try
            {
                var file = new FateProgressFile
                {
                    Character = character,
                    World = world,
                    CapturedAt = DateTimeOffset.Now.ToString("o"),
                    Zones = zones,
                };
                var path = GetFateCapturePath(character, world);
                var json = JsonSerializer.Serialize(file, JsonOptions);
                var tmpPath = path + ".tmp";
                File.WriteAllText(tmpPath, json);
                if (File.Exists(path))
                    File.Replace(tmpPath, path, null);
                else
                    File.Move(tmpPath, path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save the FATE progress capture.");
            }

            if (TryExport(force: true))
                Log.Info("Shared FATE progress captured and exported.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to poll the shared FATE progress agent.");
        }
    }

    /// <summary>
    /// Flattens AgentFateProgress (3 expansion tabs x 6 zones) into export
    /// entries. Returns null when the agent itself is unavailable and an
    /// empty list while the client has not received the progress data yet
    /// (zones still have no territory id).
    /// </summary>
    private static unsafe List<FateProgressZoneEntry>? ReadFateProgressFromAgent()
    {
        var agent = AgentFateProgress.Instance();
        if (agent == null)
            return null;

        var result = new List<FateProgressZoneEntry>();
        var tabs = agent->Tabs;
        for (var t = 0; t < tabs.Length; t++)
        {
            var zones = tabs[t].Zones;
            for (var z = 0; z < zones.Length; z++)
            {
                var zone = zones[z];
                if (zone.TerritoryTypeId == 0)
                    continue;

                // The agent's raw values use a different shape than the
                // achievement path: FateProgress is the count inside the
                // current rank band (not the cumulative total), NeededFates
                // is the band's total size (not the remainder), untouched
                // zones report rank 0, and MaxRank reads 0. (Verified
                // against real data: a rank-3 zone showed FateProgress 7,
                // below the 26 cumulative FATEs rank 3 requires.) Since
                // v1.12.4 the agent values are normalized to the same
                // shape as the achievement path: cumulative completed
                // count, remainder to the next rank, ranks from 1, and
                // the catalog's real max rank.
                var sizes = SharedFateRankSizes[t];
                var maxRank = sizes.Length + 1;
                var rank = zone.CurrentRank <= 0 ? 1 : Math.Min((int)zone.CurrentRank, maxRank);
                var bandsBelow = 0;
                for (var i = 0; i < rank - 1 && i < sizes.Length; i++)
                    bandsBelow += sizes[i];
                var completed = Math.Clamp(bandsBelow + (int)zone.FateProgress, 0, SharedFateTotalFates);
                var needed = rank >= maxRank
                    ? 0
                    : Math.Max(0, bandsBelow + sizes[rank - 1] - completed);

                result.Add(new FateProgressZoneEntry
                {
                    TabIndex = t,
                    TerritoryTypeId = zone.TerritoryTypeId,
                    ZoneName = zone.ZoneName.ToString(),
                    DisplayOrder = zone.DisplayOrder,
                    CurrentRank = rank,
                    MaxRank = maxRank,
                    FateProgress = completed,
                    NeededFates = needed,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Detour for Achievement.ReceiveAchievementProgress (v1.12.2): records
    /// the (current, max) pair of every shared-FATE zone achievement the
    /// server reports, whoever asked for it. The original always runs
    /// first and nothing in here may throw into the game function.
    /// </summary>
    private unsafe void OnReceiveAchievementProgress(Achievement* thisPtr, uint id, uint current, uint max)
    {
        achievementProgressHook!.Original(thisPtr, id, current, max);

        try
        {
            if (!SharedFateAchievementIds.Contains(id))
                return;

            lock (fateAchievementLock)
                fateAchievementProgress[id] = (current, max);
        }
        catch
        {
            // Recording is best-effort; never break the game function.
        }
    }

    /// <summary>
    /// Asks the server for the shared FATE progress (v1.12.2), the same
    /// requests Auto FATE Grind issues: the per-tab FATE progress request
    /// (which also feeds AgentFateProgress) plus main command 1000 for
    /// every zone achievement, whose answers the hook above records.
    /// Retries every 30 seconds until every zone is known, then refreshes
    /// every 10 minutes.
    /// </summary>
    private unsafe void RequestFateProgressIfNeeded(DateTime now)
    {
        if (achievementProgressHook == null)
            return;
        if (ObjectTable.LocalPlayer == null)
            return;
        if (Condition[ConditionFlag.BetweenAreas])
            return;

        bool allKnown;
        lock (fateAchievementLock)
            allKnown = fateAchievementProgress.Count >= SharedFateZones.Length;

        var interval = allKnown ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30);
        if (now - lastFateRequestUtc < interval)
            return;
        lastFateRequestUtc = now;

        try
        {
            var achievement = Achievement.Instance();
            if (achievement == null)
                return;

            for (byte tab = 0; tab < 3; tab++)
                achievement->RequestFateProgressTab(tab);

            foreach (var zone in SharedFateZones)
                GameMain.ExecuteCommand(1000, (int)zone.AchievementId, 0, 0, 0);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to request the shared FATE progress.");
        }
    }

    /// <summary>
    /// Builds export entries from the achievement progress captured by the
    /// hook (v1.12.2). Ranks follow the per-expansion rank sizes; the count
    /// needed for the next rank is the remainder inside the current rank
    /// (0 once the zone is complete).
    /// </summary>
    private List<FateProgressZoneEntry> BuildFateProgressFromAchievements()
    {
        Dictionary<uint, (uint Current, uint Max)> captured;
        lock (fateAchievementLock)
        {
            if (fateAchievementProgress.Count == 0)
                return [];
            captured = new Dictionary<uint, (uint Current, uint Max)>(fateAchievementProgress);
        }

        var result = new List<FateProgressZoneEntry>();
        foreach (var zone in SharedFateZones)
        {
            if (!captured.TryGetValue(zone.AchievementId, out var progress))
                continue;

            var total = progress.Max > 0 ? (int)progress.Max : SharedFateTotalFates;
            var completed = Math.Clamp((int)progress.Current, 0, total);
            var sizes = SharedFateRankSizes[zone.TabIndex];
            var maxRank = sizes.Length + 1;

            var rank = maxRank;
            var needed = 0;
            var remaining = completed;
            for (var i = 0; i < sizes.Length; i++)
            {
                if (remaining < sizes[i])
                {
                    rank = i + 1;
                    needed = sizes[i] - remaining;
                    break;
                }
                remaining -= sizes[i];
            }
            if (completed >= total)
            {
                rank = maxRank;
                needed = 0;
            }

            result.Add(new FateProgressZoneEntry
            {
                TabIndex = zone.TabIndex,
                TerritoryTypeId = zone.TerritoryTypeId,
                ZoneName = ResolveFateZoneName(zone.TerritoryTypeId),
                DisplayOrder = zone.DisplayOrder,
                CurrentRank = rank,
                MaxRank = maxRank,
                FateProgress = completed,
                NeededFates = needed,
            });
        }

        return result;
    }

    private string ResolveFateZoneName(uint territoryTypeId)
    {
        lock (fateZoneNameCache)
        {
            if (fateZoneNameCache.TryGetValue(territoryTypeId, out var cached))
                return cached;
        }

        var name = string.Empty;
        try
        {
            var sheet = DataManager.GetExcelSheet<TerritoryType>();
            if (sheet != null && sheet.TryGetRow(territoryTypeId, out var row))
                name = row.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to resolve a FATE zone name.");
        }

        lock (fateZoneNameCache)
            fateZoneNameCache[territoryTypeId] = name;
        return name;
    }

    private static string FateCaptureKey(List<FateProgressZoneEntry> zones)
        => string.Join(",", zones.Select(z => $"{z.TerritoryTypeId}:{z.CurrentRank}:{z.FateProgress}"));

    private List<FateProgressZoneEntry> BuildFateProgress(string character, string world)
    {
        var key = $"{character}@{world}";
        lock (fateLock)
        {
            if (fateCaptureByCharacter.TryGetValue(key, out var cached))
                return cached;
        }

        try
        {
            var path = GetFateCapturePath(character, world);
            if (!File.Exists(path))
                return [];

            var file = JsonSerializer.Deserialize<FateProgressFile>(File.ReadAllText(path));
            if (file == null || file.Zones.Count == 0)
                return [];

            lock (fateLock)
            {
                fateCaptureByCharacter[key] = file.Zones;
                fateCaptureKeyByCharacter[key] = FateCaptureKey(file.Zones);
            }
            return file.Zones;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load the FATE progress capture.");
            return [];
        }
    }

    private string GetFateCapturePath(string character, string world)
    {
        var fileName = $"mstatus-fateprogress-{SanitizeFilePart(character)}-{SanitizeFilePart(world)}.json";
        return Path.Combine(configDirectory, fileName);
    }

    /// <summary>Builds the current snapshot and writes it when due. Returns false when not logged in.</summary>
    private bool TryExport(bool force)
    {
        if (!configuration.ExportStatus)
            return false;

        Snapshot? snapshot;
        try
        {
            snapshot = BuildSnapshot();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read character status.");
            return false;
        }

        if (snapshot == null)
            return false;

        var payloadJson = JsonSerializer.Serialize(new Payload(snapshot), JsonOptions);
        var now = DateTime.UtcNow;
        var changed = payloadJson != lastPayloadJson;
        if (changed)
        {
            lastPayloadJson = payloadJson;
            dataUpdatedAt = DateTimeOffset.Now.ToString("o");
        }

        if (!force && !changed && now - lastWriteUtc < HeartbeatInterval)
            return true;

        snapshot.DataUpdatedAt = dataUpdatedAt;
        snapshot.ExportedAt = DateTimeOffset.Now.ToString("o");

        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var path = GetOutputPath(snapshot);
            ArchivePreviousVersion(path);
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(path))
                File.Replace(tmpPath, path, null);
            else
                File.Move(tmpPath, path);
            lastWrittenPath = path;
            lastWriteUtc = now;
            PurgeOldArchive();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to write status file.");
        }

        return true;
    }

    /// <summary>
    /// Keeps a rolling history under "old": when the existing character file
    /// was last written on a previous day, it is copied aside (named with its
    /// own write time) before being overwritten, so roughly one snapshot per
    /// character per day is kept. Archives older than 30 days are deleted.
    /// </summary>
    private void ArchivePreviousVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var lastWrite = File.GetLastWriteTime(path);
            if (lastWrite.Date >= DateTime.Today)
                return;
            var oldDir = Path.Combine(configDirectory, "old");
            Directory.CreateDirectory(oldDir);
            var baseName = Path.GetFileNameWithoutExtension(path);
            var archivePath = Path.Combine(oldDir, $"{baseName}-{lastWrite:yyyyMMdd-HHmmss}.json");
            if (File.Exists(archivePath))
                return;
            File.Copy(path, archivePath);
            File.SetLastWriteTime(archivePath, lastWrite);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to archive the previous status file.");
        }
    }

    private void PurgeOldArchive()
    {
        try
        {
            var oldDir = Path.Combine(configDirectory, "old");
            if (!Directory.Exists(oldDir))
                return;
            var cutoff = DateTime.Now.AddDays(-30);
            foreach (var file in Directory.GetFiles(oldDir, "ffxiv-status-*.json"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to purge old status archives.");
        }
    }

    /// <summary>
    /// One output file per character: ffxiv-status-&lt;Character&gt;-&lt;World&gt;.json.
    /// The name is derived from whoever is logged in, so characters added later
    /// are handled automatically and never overwrite each other.
    /// </summary>
    private string GetOutputPath(Snapshot snapshot)
    {
        var fileName = $"ffxiv-status-{SanitizeFilePart(snapshot.Character)}-{SanitizeFilePart(snapshot.World)}.json";
        return Path.Combine(configDirectory, fileName);
    }

    private static string SanitizeFilePart(string value)
    {
        var chars = value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        var result = new string(chars);
        return result.Length == 0 ? "unknown" : result;
    }

    private unsafe Snapshot? BuildSnapshot()
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        var playerState = PlayerState.Instance();
        if (playerState == null)
            return null;

        var snapshot = new Snapshot
        {
            Character = player.Name.TextValue,
            World = ResolveWorldName(player.HomeWorld.RowId),
            PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
        };

        // ---- Job / class levels -------------------------------------------
        var classJobSheet = DataManager.GetExcelSheet<ClassJob>();
        if (classJobSheet != null)
        {
            foreach (var row in classJobSheet)
            {
                var id = (int)row.RowId;
                if (id == 0)
                    continue;

                // shouldGetSynced: false -> the real (unsynced) level.
                var level = playerState->GetClassJobLevel(id, false);
                if (level <= 0)
                    continue;

                snapshot.Jobs.Add(new JobEntry
                {
                    Id = id,
                    Abbrev = row.Abbreviation.ExtractText(),
                    Name = row.Name.ExtractText(),
                    Level = level,
                    Exp = playerState->GetClassJobExp((uint)id),
                    NeededExp = playerState->GetClassJobNeededExp(id),
                });

                if ((int)player.ClassJob.RowId == id)
                    snapshot.CurrentJob = row.Abbreviation.ExtractText();
            }
        }

        // ---- Cosmic Exploration -------------------------------------------
        var wksManager = WKSManager.Instance();
        if (wksManager != null)
        {
            var cosmic = new CosmicData();
            var research = wksManager->ResearchModule;
            var researchLoaded = research != null && research->IsLoaded;

            for (var jobId = 8; jobId <= 18; jobId++)
            {
                var entry = new CosmicClassEntry
                {
                    JobId = jobId,
                    Abbrev = CosmicAbbreviations[jobId - 8],
                    Score = wksManager->State.Scores[jobId - 8],
                };

                if (researchLoaded)
                {
                    var toolClass = (byte)(jobId - 7);
                    entry.ToolStage = research->CurrentStages[toolClass - 1];
                    entry.ToolStageUnlocked = research->UnlockedStages[toolClass - 1];

                    var types = new List<CosmicTypeEntry>();
                    for (byte type = 1; type <= 6; type++)
                    {
                        if (!research->IsTypeAvailable(toolClass, type))
                            break;

                        types.Add(new CosmicTypeEntry
                        {
                            Type = type,
                            Current = research->GetCurrentAnalysis(toolClass, type),
                            Needed = research->GetNeededAnalysis(toolClass, type),
                            Max = research->GetMaxAnalysis(toolClass, type),
                        });
                    }

                    entry.Types = types;
                }

                cosmic.Classes.Add(entry);
            }

            snapshot.Cosmic = cosmic;
        }

        // ---- Gearsets + currently equipped items ---------------------------
        // Isolated on purpose: a failure here must never lose the level data.
        try
        {
            snapshot.Gearsets = BuildGearsets(classJobSheet);
            snapshot.Equipped = BuildEquipped();
            if (snapshot.Equipped != null)
            {
                snapshot.HaloneEarringEquipped =
                    snapshot.Equipped.Items.FirstOrDefault(i => i.Slot == "Ears")?.ItemId
                        == HaloneEarringItemId;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read gearset / equipped data.");
        }

        // ---- Currencies + inventory -----------------------------------------
        try
        {
            if (configuration.ExportCurrencies)
                snapshot.Currencies = BuildCurrencies();
            if (configuration.ExportInventory)
                snapshot.Inventory = BuildInventory();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read currency / inventory data.");
        }

        // ---- Retainers -------------------------------------------------------
        try
        {
            if (configuration.ExportRetainers)
                snapshot.Retainers = BuildRetainers(classJobSheet);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read retainer data.");
        }

        // ---- Grand Company supply / provisioning ----------------------------
        try
        {
            if (configuration.CaptureGcSupply)
            {
                snapshot.GrandCompanySupply = BuildGrandCompanySupply(snapshot.Character, snapshot.World);
                lock (gcLock)
                {
                    if (gcDebugByCharacter.TryGetValue($"{snapshot.Character}@{snapshot.World}", out var gcDebug))
                        snapshot.GcWindowDebug = gcDebug;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read Grand Company supply data.");
        }

        // ---- Housing visits ---------------------------------------------------
        try
        {
            if (configuration.RecordHousingVisits)
                snapshot.HousingVisits = UpdateHousingVisits(snapshot.Character, snapshot.World);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to record housing visits.");
        }

        // ---- Main scenario progress -------------------------------------------
        try
        {
            if (configuration.ExportMainScenario)
                snapshot.MainScenario = BuildMainScenario();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read main scenario progress.");
        }

        // ---- Shared FATE progress ----------------------------------------------
        try
        {
            if (configuration.ExportFateProgress)
                snapshot.FateProgress = BuildFateProgress(snapshot.Character, snapshot.World);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read shared FATE progress.");
        }

        return snapshot;
    }

    private unsafe List<GearsetData> BuildGearsets(Lumina.Excel.ExcelSheet<ClassJob>? classJobSheet)
    {
        var result = new List<GearsetData>();
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            return result;

        for (var i = 0; i < 100; i++)
        {
            if (!module->IsValidGearset(i))
                continue;

            var entry = module->GetGearset(i);
            if (entry == null)
                continue;

            var gearset = new GearsetData
            {
                Id = entry->Id,
                Name = entry->NameString,
                ClassJobId = entry->ClassJob,
                ItemLevel = entry->ItemLevel,
            };

            if (classJobSheet != null && classJobSheet.TryGetRow((uint)entry->ClassJob, out var classJob))
                gearset.ClassJob = classJob.Abbreviation.ExtractText();

            var items = entry->Items;
            var count = Math.Min(items.Length, EquipSlotNames.Length);
            for (var slot = 0; slot < count; slot++)
            {
                var item = items[slot];
                if (item.ItemId == 0)
                    continue;

                gearset.Items.Add(BuildGearItem(EquipSlotNames[slot], item.ItemId, item.GlamourId,
                    item.Stain0Id, item.Stain1Id, item.Materia, item.MateriaGrades));
            }

            result.Add(gearset);
        }

        return result;
    }

    private unsafe EquippedData? BuildEquipped()
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return null;

        var container = inventory->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null || !container->IsLoaded || container->Items == null)
            return null;

        var equipped = new EquippedData();
        var count = Math.Min(container->Size, EquipSlotNames.Length);
        for (var slot = 0; slot < count; slot++)
        {
            var item = container->Items + slot;
            if (item == null || item->ItemId == 0)
                continue;

            var entry = BuildGearItem(EquipSlotNames[slot], item->GetBaseItemId(), item->GlamourId,
                item->Stains[0], item->Stains[1], item->Materia, item->MateriaGrades);
            entry.IsHq = item->IsHighQuality();
            entry.Condition = item->Condition / 300;
            entry.Spiritbond = item->SpiritbondOrCollectability / 100;
            equipped.Items.Add(entry);
        }

        return equipped;
    }

    private unsafe List<CurrencyEntry> BuildCurrencies()
    {
        var result = new List<CurrencyEntry>();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return result;

        var container = inventory->GetInventoryContainer(InventoryType.Currency);
        if (container == null || !container->IsLoaded || container->Items == null)
            return result;

        for (var slot = 0; slot < container->Size; slot++)
        {
            var item = container->Items + slot;
            if (item == null || item->ItemId == 0 || item->Quantity <= 0)
                continue;

            result.Add(new CurrencyEntry
            {
                ItemId = item->ItemId,
                Name = ResolveItem(item->ItemId).Name,
                Count = item->Quantity,
            });
        }

        return result;
    }

    private unsafe List<InventoryContainerData> BuildInventory()
    {
        var result = new List<InventoryContainerData>();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return result;

        foreach (var type in InventoryContainers)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded || container->Items == null)
                continue;

            var data = new InventoryContainerData { Container = type.ToString() };
            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->Items + slot;
                if (item == null || item->ItemId == 0 || item->Quantity <= 0)
                    continue;

                var baseId = item->GetBaseItemId();
                data.Items.Add(new InventoryItemEntry
                {
                    ItemId = baseId,
                    Name = ResolveItem(baseId).Name,
                    Count = item->Quantity,
                    IsHq = item->IsHighQuality(),
                });
            }

            if (data.Items.Count > 0)
                result.Add(data);
        }

        return result;
    }

    // ---- Main scenario progress (v1.11.0, fixed in v1.12.1) ------------------
    // The journal section that groups the main scenario quests
    // (JournalGenre -> JournalCategory -> JournalSection). The section names
    // carry an expansion suffix, so exact matching never worked:
    //   ja: "メインクエスト1（新生・蒼天・紅蓮・漆黒・暁月）",
    //       "メインクエスト2（黄金のレガシー）"
    //   en: "Main Scenario (A Realm Reborn through Endwalker)",
    //       "Main Scenario (Dawntrail)"
    // (verified against XIVAPI v2 JournalSection, 2026-11-20). Match by
    // section RowId (0 and 1) or by name prefix in any client language.
    private static readonly uint[] MainScenarioSectionRowIds = [0, 1];
    private static readonly string[] MainScenarioSectionNamePrefixes =
        ["メインクエスト", "Main Scenario", "Hauptszenario", "Épopée"];

    private static bool IsMainScenarioSection(uint rowId, string name)
        => MainScenarioSectionRowIds.Contains(rowId)
            || MainScenarioSectionNamePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    private sealed class MsqQuestInfo
    {
        public uint Id;
        public string Name = string.Empty;
        public uint ExpansionId;
        public string ExpansionName = string.Empty;
        public ushort SortKey;
    }

    private List<MsqQuestInfo>? msqQuestCache;

    private List<MsqQuestInfo> GetMsqQuests()
    {
        if (msqQuestCache != null)
            return msqQuestCache;

        var result = new List<MsqQuestInfo>();
        var questSheet = DataManager.GetExcelSheet<Quest>();
        if (questSheet != null)
        {
            foreach (var row in questSheet)
            {
                if (row.RowId == 0)
                    continue;

                var genre = row.JournalGenre;
                if (!genre.IsValid)
                    continue;

                var category = genre.Value.JournalCategory;
                if (!category.IsValid)
                    continue;

                var section = category.Value.JournalSection;
                if (!section.IsValid)
                    continue;

                var sectionName = section.Value.Name.ExtractText();
                if (!IsMainScenarioSection(section.Value.RowId, sectionName))
                    continue;

                var expansion = row.Expansion;
                result.Add(new MsqQuestInfo
                {
                    Id = row.RowId,
                    Name = row.Name.ExtractText(),
                    ExpansionId = expansion.RowId,
                    ExpansionName = expansion.IsValid ? expansion.Value.Name.ExtractText() : string.Empty,
                    SortKey = row.SortKey,
                });
            }
        }

        result.Sort(static (a, b) =>
            a.ExpansionId != b.ExpansionId
                ? a.ExpansionId.CompareTo(b.ExpansionId)
                : a.SortKey != b.SortKey
                    ? a.SortKey.CompareTo(b.SortKey)
                    : a.Id.CompareTo(b.Id));

        // Only cache a non-empty list: caching an empty one (e.g. the sheet
        // was not ready on the first call) would keep MainScenario null
        // forever until the plugin is reloaded.
        if (result.Count > 0)
            msqQuestCache = result;
        else
            Log.Warning("No main scenario quests found while scanning the Quest sheet.");

        return result;
    }

    private unsafe MainScenarioData? BuildMainScenario()
    {
        var quests = GetMsqQuests();
        if (quests.Count == 0)
            return null;

        var data = new MainScenarioData { TotalQuests = quests.Count };

        // Currently accepted main scenario quest(s), in journal order.
        var questManager = QuestManager.Instance();
        if (questManager != null)
        {
            var acceptedIds = new HashSet<uint>();
            foreach (var work in questManager->NormalQuests)
            {
                if (work.QuestId != 0)
                    acceptedIds.Add(work.QuestId);
            }

            foreach (var quest in quests)
            {
                if (acceptedIds.Contains(quest.Id))
                {
                    data.CurrentQuestId = quest.Id;
                    data.CurrentQuestName = quest.Name;
                    data.CurrentExpansion = quest.ExpansionName;
                    break;
                }
            }
        }

        // Completion scan in journal order: the first incomplete quest that
        // is not currently accepted is the next one to do.
        MsqQuestInfo? lastCompleted = null;
        var expansions = new Dictionary<uint, MainScenarioExpansionData>();
        foreach (var quest in quests)
        {
            if (!expansions.TryGetValue(quest.ExpansionId, out var expData))
            {
                expData = new MainScenarioExpansionData
                {
                    ExpansionId = quest.ExpansionId,
                    Name = quest.ExpansionName,
                };
                expansions.Add(quest.ExpansionId, expData);
                data.Expansions.Add(expData);
            }

            expData.TotalQuests++;
            if (QuestManager.IsQuestComplete((ushort)quest.Id))
            {
                expData.CompletedQuests++;
                data.CompletedQuests++;
                lastCompleted = quest;
            }
            else if (data.NextQuestId == null && quest.Id != data.CurrentQuestId)
            {
                data.NextQuestId = quest.Id;
                data.NextQuestName = quest.Name;
                if (data.CurrentQuestId == null)
                    data.CurrentExpansion = quest.ExpansionName;
            }
        }

        if (lastCompleted != null)
        {
            data.LastCompletedQuestId = lastCompleted.Id;
            data.LastCompletedQuestName = lastCompleted.Name;
        }

        data.AllComplete = data.CompletedQuests >= data.TotalQuests;
        return data;
    }

    private unsafe List<RetainerEntry> BuildRetainers(Lumina.Excel.ExcelSheet<ClassJob>? classJobSheet)
    {
        var result = new List<RetainerEntry>();
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
            return result;

        int count = manager->GetRetainerCount();
        for (uint i = 0; i < count; i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null || !retainer->Available)
                continue;

            var entry = new RetainerEntry
            {
                Name = retainer->NameString,
                Level = retainer->Level,
                Gil = retainer->Gil,
                ItemCount = retainer->ItemCount,
                VentureId = retainer->VentureId,
            };

            if (classJobSheet != null && classJobSheet.TryGetRow(retainer->ClassJob, out var classJob))
                entry.ClassJob = classJob.Abbreviation.ExtractText();

            if (retainer->VentureId != 0 && retainer->VentureComplete != 0)
                entry.VentureCompleteAt = DateTimeOffset.FromUnixTimeSeconds(retainer->VentureComplete)
                    .ToLocalTime().ToString("o");

            result.Add(entry);
        }

        return result;
    }

    private unsafe GrandCompanySupplyData? BuildGrandCompanySupply(string character, string world)
    {
        var agent = AgentGrandCompanySupply.Instance();
        if (agent == null)
            return null;

        var result = new GrandCompanySupplyData();

        // Store 1: the timers-style list (fixed arrays of 8 supply and 3
        // provisioning requests), present once the server has sent the
        // supply / provisioning data for the Contents Info timers panel.
        if (agent->SupplyProvisioningData != null)
        {
            var data = agent->SupplyProvisioningData;

            var supply = data->SupplyData;
            for (var i = 0; i < supply.Length; i++)
            {
                var item = supply[i];
                if (item.ItemId == 0)
                    continue;

                result.Supply.Add(new SupplyItemEntry
                {
                    ItemId = item.ItemId,
                    Name = item.ItemName.ToString(),
                    NumRequested = item.NumRequested,
                });
            }

            var provisioning = data->ProvisioningData;
            for (var i = 0; i < provisioning.Length; i++)
            {
                var item = provisioning[i];
                if (item.ItemId == 0)
                    continue;

                result.Provisioning.Add(new SupplyItemEntry
                {
                    ItemId = item.ItemId,
                    Name = item.ItemName.ToString(),
                    NumRequested = item.NumRequested,
                });
            }
        }

        // Store 2 (v1.6.2): the delivery window's own item array
        // (GrandCompanyItem entries). Used only when store 1 is empty, so
        // the two stores never mix; the whole array is one window's list.
        if (result.Supply.Count == 0 && result.Provisioning.Count == 0
            && agent->ItemArray != null && agent->NumItems > 0)
        {
            var count = Math.Min(agent->NumItems, 64);
            for (var i = 0; i < count; i++)
            {
                var item = agent->ItemArray[i];
                if (item.ItemId == 0)
                    continue;

                result.Supply.Add(new SupplyItemEntry
                {
                    ItemId = item.ItemId,
                    Name = item.ItemName.ToString(),
                    NumRequested = item.NumRequested,
                });
            }
        }

        if (result.Supply.Count == 0 && result.Provisioning.Count == 0)
            return LoadGcCapture(character, world);

        return result;
    }

    /// <summary>
    /// Polls the current housing territory. When the player newly arrives at a
    /// housing location (residential district, house interior or workshop), the
    /// visit is appended to the per-character sidecar file and the visit list is
    /// returned for embedding into the snapshot. Ward / plot / room numbers are
    /// recorded exactly as the game structures report them.
    /// </summary>
    private unsafe List<HousingVisitEntry> UpdateHousingVisits(string character, string world)
    {
        var cacheKey = $"{character}@{world}";
        lock (housingLock)
        {
            if (!housingVisitsByCharacter.TryGetValue(cacheKey, out var visits))
            {
                visits = LoadHousingVisits(character, world);
                housingVisitsByCharacter[cacheKey] = visits;
                housingKeyByCharacter[cacheKey] = visits.Count > 0 ? visits[^1].Key : string.Empty;
            }

            var current = ReadCurrentHousing(world);
            var currentKey = current?.Key ?? string.Empty;
            if (current != null && currentKey != housingKeyByCharacter[cacheKey])
            {
                // Suppress immediate duplicates (e.g. a plugin reload while
                // standing inside the same house).
                var last = visits.Count > 0 ? visits[^1] : null;
                var isDuplicate = last != null && last.Key == currentKey
                    && DateTimeOffset.TryParse(last.At, out var lastAt)
                    && DateTimeOffset.Now - lastAt < TimeSpan.FromMinutes(10);
                if (!isDuplicate)
                {
                    current.At = DateTimeOffset.Now.ToString("o");
                    visits.Add(current);
                    if (visits.Count > MaxHousingVisitEntries)
                        visits.RemoveRange(0, visits.Count - MaxHousingVisitEntries);
                    SaveHousingVisits(character, world, visits);
                }
            }

            housingKeyByCharacter[cacheKey] = currentKey;
            return new List<HousingVisitEntry>(visits);
        }
    }

    private unsafe HousingVisitEntry? ReadCurrentHousing(string fallbackWorld)
    {
        var housing = HousingManager.Instance();
        if (housing == null)
            return null;

        var territoryType = housing->GetCurrentHousingTerritoryType();
        if (territoryType == HousingTerritoryType.None)
            return null;

        var entry = new HousingVisitEntry
        {
            Kind = territoryType.ToString(),
            Ward = housing->GetCurrentWard(),
            Plot = housing->GetCurrentPlot(),
            Room = housing->GetCurrentRoom(),
            World = fallbackWorld,
        };

        var houseId = housing->GetCurrentHouseId();
        if (houseId.WorldId != 0)
        {
            var houseWorld = ResolveWorldName(houseId.WorldId);
            if (houseWorld.Length > 0)
                entry.World = houseWorld;
        }

        return entry;
    }

    private string GetHousingStorePath(string character, string world)
    {
        var fileName = $"mstatus-housing-{SanitizeFilePart(character)}-{SanitizeFilePart(world)}.json";
        return Path.Combine(configDirectory, fileName);
    }

    private List<HousingVisitEntry> LoadHousingVisits(string character, string world)
    {
        try
        {
            var path = GetHousingStorePath(character, world);
            if (File.Exists(path))
            {
                var file = JsonSerializer.Deserialize<HousingVisitFile>(File.ReadAllText(path));
                if (file != null)
                    return file.Visits;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load housing visit history.");
        }

        return [];
    }

    private void SaveHousingVisits(string character, string world, List<HousingVisitEntry> visits)
    {
        try
        {
            var file = new HousingVisitFile { Character = character, World = world, Visits = visits };
            var json = JsonSerializer.Serialize(file, JsonOptions);
            var path = GetHousingStorePath(character, world);
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(path))
                File.Replace(tmpPath, path, null);
            else
                File.Move(tmpPath, path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save housing visit history.");
        }
    }

    /// <summary>
    /// Builds one item entry. Gearset item ids carry the HQ offset (+1,000,000);
    /// equipped items pass their base id instead and set IsHq separately.
    /// </summary>
    private GearItemEntry BuildGearItem(string slot, uint rawItemId, uint glamourId,
        byte stain0Id, byte stain1Id, ReadOnlySpan<ushort> materia, ReadOnlySpan<byte> materiaGrades)
    {
        var isHq = rawItemId >= 1_000_000;
        var baseId = isHq ? rawItemId - 1_000_000 : rawItemId;

        var entry = new GearItemEntry
        {
            Slot = slot,
            ItemId = baseId,
            IsHq = isHq,
        };

        var (name, itemLevel) = ResolveItem(baseId);
        entry.Name = name;
        entry.ItemLevel = itemLevel;

        if (glamourId != 0)
            entry.Glamour = ResolveItem(glamourId).Name;

        entry.Stain0 = ResolveStain(stain0Id);
        entry.Stain1 = ResolveStain(stain1Id);

        var materiaCount = Math.Min(materia.Length, materiaGrades.Length);
        for (var i = 0; i < materiaCount; i++)
        {
            if (materia[i] == 0)
                continue;

            var materiaName = ResolveMateria(materia[i], materiaGrades[i]);
            if (materiaName != null)
                entry.Materia.Add(materiaName);
        }

        return entry;
    }

    private (string? Name, int ItemLevel) ResolveItem(uint itemId)
    {
        try
        {
            var sheet = DataManager.GetExcelSheet<Item>();
            if (sheet != null && sheet.TryGetRow(itemId, out var item))
                // LevelItem is a RowRef<ItemLevel>; the ItemLevel sheet is keyed
                // by the item level itself, so its RowId is the IL number.
                return (item.Name.ExtractText(), (int)item.LevelItem.RowId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to resolve item {ItemId}.", itemId);
        }

        return (null, 0);
    }

    private string? ResolveStain(byte stainId)
    {
        if (stainId == 0)
            return null;

        try
        {
            var sheet = DataManager.GetExcelSheet<Stain>();
            if (sheet != null && sheet.TryGetRow(stainId, out var stain))
                return stain.Name.ExtractText();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to resolve stain {StainId}.", stainId);
        }

        return null;
    }

    private string? ResolveMateria(ushort materiaId, byte grade)
    {
        try
        {
            var sheet = DataManager.GetExcelSheet<Materia>();
            if (sheet != null && sheet.TryGetRow(materiaId, out var materia) && grade < materia.Item.Count)
            {
                var itemRef = materia.Item[grade];
                if (itemRef.IsValid)
                    return itemRef.Value.Name.ExtractText();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to resolve materia {MateriaId} grade {Grade}.", materiaId, grade);
        }

        return null;
    }

    private string ResolveWorldName(uint worldId)
    {
        try
        {
            var sheet = DataManager.GetExcelSheet<World>();
            if (sheet != null && sheet.TryGetRow(worldId, out var world))
                return world.Name.ExtractText();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to resolve world name.");
        }

        return string.Empty;
    }

    /// <summary>The change-detection payload: everything except the timestamps.</summary>
    private sealed record Payload(Snapshot Snapshot)
    {
        public string Character => Snapshot.Character;
        public string World => Snapshot.World;
        public string? CurrentJob => Snapshot.CurrentJob;
        public List<JobEntry> Jobs => Snapshot.Jobs;
        public CosmicData? Cosmic => Snapshot.Cosmic;
        public List<GearsetData> Gearsets => Snapshot.Gearsets;
        public EquippedData? Equipped => Snapshot.Equipped;
        public List<CurrencyEntry> Currencies => Snapshot.Currencies;
        public List<InventoryContainerData> Inventory => Snapshot.Inventory;
        public List<RetainerEntry> Retainers => Snapshot.Retainers;
        public string PluginVersion => Snapshot.PluginVersion;
        public GrandCompanySupplyData? GrandCompanySupply => Snapshot.GrandCompanySupply;
        public GcCaptureDebugData? GcWindowDebug => Snapshot.GcWindowDebug;
        public List<HousingVisitEntry> HousingVisits => Snapshot.HousingVisits;
        public List<FateProgressZoneEntry> FateProgress => Snapshot.FateProgress;
    }

    public sealed class Snapshot
    {
        public string Character { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public string? CurrentJob { get; set; }
        public string DataUpdatedAt { get; set; } = string.Empty;
        public string ExportedAt { get; set; } = string.Empty;
        public List<JobEntry> Jobs { get; set; } = [];
        public CosmicData? Cosmic { get; set; }
        public List<GearsetData> Gearsets { get; set; } = [];
        public EquippedData? Equipped { get; set; }
        public List<CurrencyEntry> Currencies { get; set; } = [];
        public List<InventoryContainerData> Inventory { get; set; } = [];
        public List<RetainerEntry> Retainers { get; set; } = [];
        public string PluginVersion { get; set; } = string.Empty;
        public bool? HaloneEarringEquipped { get; set; }
        public MainScenarioData? MainScenario { get; set; }
        public List<FateProgressZoneEntry> FateProgress { get; set; } = [];
        public GrandCompanySupplyData? GrandCompanySupply { get; set; }
        public GcCaptureDebugData? GcWindowDebug { get; set; }
        public List<HousingVisitEntry> HousingVisits { get; set; } = [];
    }

    public sealed class MainScenarioData
    {
        public uint? CurrentQuestId { get; set; }
        public string? CurrentQuestName { get; set; }
        public uint? NextQuestId { get; set; }
        public string? NextQuestName { get; set; }
        public uint? LastCompletedQuestId { get; set; }
        public string? LastCompletedQuestName { get; set; }
        public string? CurrentExpansion { get; set; }
        public int CompletedQuests { get; set; }
        public int TotalQuests { get; set; }
        public bool AllComplete { get; set; }
        public List<MainScenarioExpansionData> Expansions { get; set; } = [];
    }

    public sealed class MainScenarioExpansionData
    {
        public uint ExpansionId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int CompletedQuests { get; set; }
        public int TotalQuests { get; set; }
    }

    public sealed class FateProgressZoneEntry
    {
        public int TabIndex { get; set; }
        public uint TerritoryTypeId { get; set; }
        public string ZoneName { get; set; } = string.Empty;
        public int DisplayOrder { get; set; }
        public int CurrentRank { get; set; }
        public int MaxRank { get; set; }
        public int FateProgress { get; set; }
        public int NeededFates { get; set; }
    }

    public sealed class FateProgressFile
    {
        public string Character { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public string CapturedAt { get; set; } = string.Empty;
        public List<FateProgressZoneEntry> Zones { get; set; } = [];
    }

    public sealed class CurrencyEntry
    {
        public uint ItemId { get; set; }
        public string? Name { get; set; }
        public int Count { get; set; }
    }

    public sealed class InventoryContainerData
    {
        public string Container { get; set; } = string.Empty;
        public List<InventoryItemEntry> Items { get; set; } = [];
    }

    public sealed class InventoryItemEntry
    {
        public uint ItemId { get; set; }
        public string? Name { get; set; }
        public int Count { get; set; }
        public bool IsHq { get; set; }
    }

    public sealed class RetainerEntry
    {
        public string Name { get; set; } = string.Empty;
        public string? ClassJob { get; set; }
        public int Level { get; set; }
        public uint Gil { get; set; }
        public int ItemCount { get; set; }
        public int VentureId { get; set; }
        public string? VentureCompleteAt { get; set; }
    }

    public sealed class GrandCompanySupplyData
    {
        public List<SupplyItemEntry> Supply { get; set; } = [];
        public List<SupplyItemEntry> Provisioning { get; set; } = [];
    }

    public sealed class SupplyItemEntry
    {
        public uint ItemId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int NumRequested { get; set; }
    }

    public sealed class GcSupplyFile
    {
        public string Character { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public string CapturedAt { get; set; } = string.Empty;
        public List<SupplyItemEntry> Supply { get; set; } = [];
        public List<SupplyItemEntry> Provisioning { get; set; } = [];
    }

    public sealed class GcCaptureDebugData
    {
        public string Addon { get; set; } = string.Empty;
        public string At { get; set; } = string.Empty;
        public int ValueCount { get; set; }
        public List<string> Values { get; set; } = [];
    }

    public sealed class HousingVisitFile
    {
        public string Character { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public List<HousingVisitEntry> Visits { get; set; } = [];
    }

    public sealed class HousingVisitEntry
    {
        public string At { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public int Ward { get; set; }
        public int Plot { get; set; }
        public int Room { get; set; }

        [JsonIgnore]
        public string Key => $"{World}|{Kind}|{Ward}|{Plot}|{Room}";
    }

    public sealed class GearsetData
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? ClassJob { get; set; }
        public int ClassJobId { get; set; }
        public int ItemLevel { get; set; }
        public List<GearItemEntry> Items { get; set; } = [];
    }

    public sealed class EquippedData
    {
        public List<GearItemEntry> Items { get; set; } = [];
    }

    public sealed class GearItemEntry
    {
        public string Slot { get; set; } = string.Empty;
        public uint ItemId { get; set; }
        public string? Name { get; set; }
        public int ItemLevel { get; set; }
        public bool IsHq { get; set; }
        public string? Glamour { get; set; }
        public string? Stain0 { get; set; }
        public string? Stain1 { get; set; }
        public List<string> Materia { get; set; } = [];

        // Equipped items only: condition / spiritbond in percent.
        public int? Condition { get; set; }
        public int? Spiritbond { get; set; }
    }

    public sealed class JobEntry
    {
        public int Id { get; set; }
        public string Abbrev { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Level { get; set; }
        public uint Exp { get; set; }
        public uint NeededExp { get; set; }
    }

    public sealed class CosmicData
    {
        public List<CosmicClassEntry> Classes { get; set; } = [];
    }

    public sealed class CosmicClassEntry
    {
        public int JobId { get; set; }
        public string Abbrev { get; set; } = string.Empty;
        public int Score { get; set; }
        public int? ToolStage { get; set; }
        public int? ToolStageUnlocked { get; set; }
        public List<CosmicTypeEntry>? Types { get; set; }
    }

    public sealed class CosmicTypeEntry
    {
        public int Type { get; set; }
        public ushort Current { get; set; }
        public ushort Needed { get; set; }
        public ushort Max { get; set; }
    }

    public sealed class TellFile
    {
        public string Character { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public List<TellEntry> Tells { get; set; } = [];
    }

    public sealed class TellEntry
    {
        public string Id { get; set; } = string.Empty;
        public string At { get; set; } = string.Empty;
        public string Sender { get; set; } = string.Empty;
        public string? SenderWorld { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
