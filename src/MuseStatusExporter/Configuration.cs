using Dalamud.Configuration;

namespace MuseStatusExporter;

/// <summary>
/// On/off switches for every plugin feature, shown as checkboxes in the
/// configuration window. Everything defaults to on, which preserves the
/// behaviour of the versions that had no settings UI.
/// </summary>
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Settings window language: 0 = Japanese, 1 = English (v1.21.5). Not a feature toggle.</summary>
    public int UiLanguage { get; set; } = 0;

    /// <summary>Master switch: write the per-character status JSON at all.</summary>
    public bool ExportStatus { get; set; } = true;

    /// <summary>Include the currency list in the snapshot.</summary>
    public bool ExportCurrencies { get; set; } = true;

    /// <summary>Include the inventory containers in the snapshot.</summary>
    public bool ExportInventory { get; set; } = true;

    /// <summary>Include retainer state in the snapshot.</summary>
    public bool ExportRetainers { get; set; } = true;

    /// <summary>Capture the Grand Company supply list from its windows.</summary>
    public bool CaptureGcSupply { get; set; } = true;

    /// <summary>Record visits to the player's own houses.</summary>
    public bool RecordHousingVisits { get; set; } = true;

    /// <summary>Record /tell messages (incoming and outgoing) to the chat file.</summary>
    public bool RecordTells { get; set; } = true;

    /// <summary>Record party and cross-world party chat to the chat file.</summary>
    public bool RecordPartyChat { get; set; } = true;

    /// <summary>Apply plugin configuration files sent by Muse (config-inbox).</summary>
    public bool ConfigDownlink { get; set; } = true;

    /// <summary>Keep the Halone Earring equipped while leveling a battle job.</summary>
    public bool AutoEquipHaloneEarring { get; set; } = true;

    /// <summary>Use a Thavnairian Onion from the bags when the buddy's EXP bar is full and its rank cap can be raised (rank 10-19, buddy summoned in the field).</summary>
    public bool AutoUseThavnairianOnion { get; set; } = false;

    /// <summary>Include main scenario quest progress in the snapshot.</summary>
    public bool ExportMainScenario { get; set; } = true;

    /// <summary>Include shared FATE progress (per-zone rank and completed-FATE counts) in the snapshot.</summary>
    public bool ExportFateProgress { get; set; } = true;

    /// <summary>Include chocobo companion (buddy) rank and EXP in the snapshot.</summary>
    public bool ExportBuddy { get; set; } = true;

    /// <summary>Include the weekly limited-tomestone progress in the snapshot.</summary>
    public bool ExportWeeklyTomestone { get; set; } = true;

    /// <summary>Include the daily duty-roulette completion state in the snapshot.</summary>
    public bool ExportRouletteCompletion { get; set; } = true;

    /// <summary>Record per-character play sessions and daily play time.</summary>
    public bool RecordPlayTime { get; set; } = true;

    /// <summary>Record the Grand Company supply agent's state in the snapshot (experimental).</summary>
    public bool ProbeGcAgent { get; set; } = true;

    /// <summary>Capture a summoned retainer's market-board listings into the snapshot.</summary>
    public bool ExportRetainerMarket { get; set; } = true;

    /// <summary>Print in-game chat notifications (venture completion, buddy level-up ready, new GC list).</summary>
    public bool InGameAlerts { get; set; } = true;

    /// <summary>Print a one-time summary (GC supply shortage, next venture completion, weekly tomestone) to the chat log on login.</summary>
    public bool LoginSummary { get; set; } = true;

    /// <summary>Show each known house's days since last entry and demolition date on login, with a warning when demolition is near.</summary>
    public bool HousingDeadlineNotice { get; set; } = true;

    /// <summary>Learn each known house's ownership (owner / FC) when its placard is viewed, so only the owner's or an FC member's entry advances the last entry (v1.16.0).</summary>
    public bool LearnHousingOwnership { get; set; } = true;

    /// <summary>Record the logged-in character's Free Company (id, name, tag) in the snapshot (v1.16.0).</summary>
    public bool ExportFreeCompany { get; set; } = true;

    /// <summary>Print a chat notice when a qualified entry updates a known house's last entry and demolition date (v1.17.0).</summary>
    public bool HousingEntryNotice { get; set; } = true;

    /// <summary>Warn in chat and in the login summary when an equipped item's durability is low (v1.18.0).</summary>
    public bool DurabilityWarning { get; set; } = true;

    /// <summary>Include other characters' completed retainer ventures in the login summary (v1.18.0).</summary>
    public bool CrossCharacterSummary { get; set; } = true;

    /// <summary>Include Occult Crescent progress (knowledge level, phantom job levels/EXP, crescent currencies) in the snapshot (v1.20.0).</summary>
    public bool ExportOccultCrescent { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
