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

    /// <summary>Record incoming /tell messages to the tells file.</summary>
    public bool RecordTells { get; set; } = true;

    /// <summary>Apply plugin configuration files sent by Muse (config-inbox).</summary>
    public bool ConfigDownlink { get; set; } = true;

    /// <summary>Keep the Halone Earring equipped while leveling a battle job.</summary>
    public bool AutoEquipHaloneEarring { get; set; } = true;

    /// <summary>Include main scenario quest progress in the snapshot.</summary>
    public bool ExportMainScenario { get; set; } = true;

    /// <summary>Include shared FATE progress (per-zone rank and completed-FATE counts) in the snapshot.</summary>
    public bool ExportFateProgress { get; set; } = true;

    /// <summary>Include chocobo companion (buddy) rank and EXP in the snapshot.</summary>
    public bool ExportBuddy { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
