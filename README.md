# MuseDalamudPlugins

Personal Dalamud plugin repository.

## How to add this repository in Dalamud

1. In game, open `/xlsettings` → **Experimental** tab.
2. Under **Custom Plugin Repositories**, add this URL and save:

   ```
   https://github.com/mippipi/MuseDalamudPlugins/raw/main/pluginmaster.json
   ```

3. Open `/xlplugins` → **Available Plugins**, search for the plugin, and install it.

Installed plugins from this repository are updated through Dalamud itself
(update notifications in `/xlplugins`, or automatically at launch when
Dalamud's auto-update setting is enabled).

## Plugins

### Muse Status Exporter

Exports the logged-in character's status to per-character JSON files in the
plugin's config folder:

- Job/class levels and EXP
- Cosmic Exploration scores and cosmic tool stages
- Saved gearsets and currently equipped items
- Currencies, inventory containers, retainer venture state
- Grand Company supply/provisioning requests (read from the addons' AtkValues)
- Housing visit history
- Main scenario quest progress
- Shared FATE progress for all 18 zones (requested from the server,
  Auto FATE Grind style — no need to open the FATE window)

It also records incoming `/tell` messages, applies plugin configuration
files delivered through a companion uploader (config downlink, with
validation, backups, an expected-hash guard and plugin reload), and keeps
the Halone Earring (EXP +30%, levels 1–100) equipped while a battle
class/job below the level cap is active. Every feature has its own
checkbox in the plugin's settings window.

Source code: [`src/MuseStatusExporter`](src/MuseStatusExporter)

## Layout

- `pluginmaster.json` — the repository index Dalamud reads
- `plugins/<InternalName>/latest.zip` — the installable plugin package
- `src/` — plugin source code

## Companion uploader (uploader/)

The `uploader/` folder holds the Windows PowerShell uploader that pairs
with Muse Status Exporter: it pushes the plugin's per-character status
JSON files, tell records and plugin config snapshots to Google Drive,
and delivers configuration files sent by Muse back to the plugin.
See `uploader/README.md` for setup. No credentials are stored in these
scripts (the Google refresh token stays in a DPAPI-encrypted local file).
