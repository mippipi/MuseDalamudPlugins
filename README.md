# MuseDalamudPlugins

個人用 Dalamud プラグイン リポジトリ / Personal Dalamud plugin repository.

自作プラグイン「Muse Status Exporter」と、その付属アップローダーを公開しています。
This repository publishes my custom plugin "Muse Status Exporter" and its companion uploader.

---

## 日本語

### このリポジトリについて

FFXIV の Dalamud 用カスタムプラグイン リポジトリです。プラグイン本体は Dalamud のカスタムリポジトリ機能から直接インストール・更新できます。プラグインのソースコードと、PC 側で動かすアップローダーアプリもあわせて公開しています。

### Dalamud への登録方法

1. ゲーム内で `/xlsettings` を開き、**Experimental** タブを開く
2. **Custom Plugin Repositories** に次の URL を追加して保存する

   ```
   https://github.com/mippipi/MuseDalamudPlugins/raw/main/pluginmaster.json
   ```

3. `/xlplugins` → **Available Plugins** でプラグインを検索してインストールする

インストールしたプラグインの更新は Dalamud 側で行われます（`/xlplugins` の更新通知、または Dalamud の自動更新設定が有効なら起動時に自動更新）。

### プラグイン: Muse Status Exporter

ログイン中のキャラクターのステータスを、プラグインの設定フォルダへキャラ別 JSON ファイルとして書き出すプラグインです。

- ジョブ／クラスのレベルと経験値
- コスモエクスプローラーのスコアとコスモツールの段階
- 保存済みギアセットと現在の装備
- 通貨、インベントリ、リテイナーのベンチャー状態
- GC軍需品／調達品のリクエスト（アドオンの AtkValues から取得）
- ハウスへの入室履歴
- メインクエストの進行状況
- 全18エリアの共有FATE達成度（Auto FATE Grind と同じ方式でサーバーへ能動取得するため、FATEウィンドウを開く必要はありません）
- バディ（チョコボ）のランクと経験値、レベルアップ可能かどうかの判定

そのほか、受信した `/tell` の記録、付属アップローダーを経由して届くプラグイン設定ファイルの適用（設定ダウンリンク: 検証・バックアップ・期待ハッシュ照合・プラグイン再読み込みつき）、レベルキャップ未満の戦闘ジョブ使用時のハルオーネイヤリング（経験値+30%、Lv1〜100）の自動装備を行います。すべての機能はプラグインの設定ウィンドウで個別にオン／オフできます。

ソースコード: [`src/MuseStatusExporter`](src/MuseStatusExporter)

### 他のプラグイン・ツールとの連携

他のプラグインやツールがこの仕組みに接続するためのインターフェース仕様（ステータス JSON の形式、設定ダウンリンクのプロトコル、トランスポートの契約）を [連携仕様（docs/INTEGRATION.md）](docs/INTEGRATION.md) に公開しています。

### アップローダーアプリ（uploader-app/）

`uploader-app/` フォルダには、PowerShell 版アップローダーとコレクターを統合した Windows トレイ常駐アプリ「FFXIV Status Uploader」が入っています。ステータス送信（5分ごと）とプラグイン設定の収集（1時間ごと）を常駐実行し、自動アップデートにも対応します。詳しくは `uploader-app/README.md` を参照してください。

### 構成

- `pluginmaster.json` — Dalamud が読むリポジトリの索引
- `plugins/<InternalName>/latest.zip` — インストール用プラグインパッケージ
- `src/` — プラグインのソースコード
- `uploader-app/` — トレイ常駐アップローダーアプリ

---

## English

### About this repository

A custom plugin repository for Dalamud (FFXIV). The plugin itself can be installed and updated directly through Dalamud's custom repository feature. The plugin source code and the companion uploader app (runs on the PC) are published here as well.

### How to add this repository in Dalamud

1. In game, open `/xlsettings` → **Experimental** tab.
2. Under **Custom Plugin Repositories**, add this URL and save:

   ```
   https://github.com/mippipi/MuseDalamudPlugins/raw/main/pluginmaster.json
   ```

3. Open `/xlplugins` → **Available Plugins**, search for the plugin, and install it.

Installed plugins from this repository are updated through Dalamud itself
(update notifications in `/xlplugins`, or automatically at launch when
Dalamud's auto-update setting is enabled).

### Plugin: Muse Status Exporter

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
- Chocobo companion (buddy) rank and EXP, including level-up readiness

It also records incoming `/tell` messages, applies plugin configuration
files delivered through a companion uploader (config downlink, with
validation, backups, an expected-hash guard and plugin reload), and keeps
the Halone Earring (EXP +30%, levels 1–100) equipped while a battle
class/job below the level cap is active. Every feature has its own
checkbox in the plugin's settings window.

Source code: [`src/MuseStatusExporter`](src/MuseStatusExporter)

### Integrating other plugins and tools

The interface specification for connecting other plugins and tools to this system (the status JSON format, the config downlink protocol, and the transport contract) is published in the [integration spec (docs/INTEGRATION.md)](docs/INTEGRATION.md).

### Uploader app (uploader-app/)

The `uploader-app/` folder holds "FFXIV Status Uploader", a Windows tray application that unifies the PowerShell uploader and the collector. It runs the status sync (every 5 minutes) and the plugin-config collection (every hour) as a resident app and updates itself automatically. See `uploader-app/README.md` for details.

### Layout

- `pluginmaster.json` — the repository index Dalamud reads
- `plugins/<InternalName>/latest.zip` — the installable plugin package
- `src/` — plugin source code
- `uploader-app/` — the tray uploader app
