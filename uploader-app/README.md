# FFXIV Status Uploader（トレイ常駐アプリ）

## 日本語

PowerShell 版アップローダー（`uploader/`）とコレクター（Collect-DalamudConfig.ps1）の後継です。両者の機能を1つの Windows アプリに統合し、タスクトレイに常駐して自動実行します。ファイルの受け渡し仕様（[連携仕様](../docs/INTEGRATION.md)）は PowerShell 版と同一です。

### 動作

- **送信サイクル（5分ごと）**: プラグインが出力したキャラ別ステータス JSON、テル記録、設定適用結果を Google Drive の `to_muse` フォルダへアップロードし、設定リクエスト（`ffxiv-config-requests.json`）をプラグインの `config-inbox` へ配信します。
- **収集サイクル（1時間ごと）**: Dalamud プラグイン設定の軽量スナップショットを作成します（ルート直下の JSON＋AutoDuty の JSON＋Dalamud ログ末尾20,000行、マスク済み）。zip 名は `FF14-pluginConfigs-<日時>.zip` で、過去分は収集先の `OLD` フォルダへ移動し30日保持します。フル共有 zip は廃止しました。
- **自動アップデート（24時間ごと＋起動時）**: このリポジトリの `uploader-app/version.json` を確認し、新しい版があればダウンロードして SHA-256 を照合し、自分自身を入れ替えて再起動します。

### インストール

1. このリポジトリの Releases（タグ `uploader-app-v*`）から `FfxivStatusUploaderApp.zip` をダウンロードして展開し、`FfxivStatusUploader.exe` を任意のフォルダ（旧アップローダーと同じフォルダ推奨）へ置きます。
2. 初回起動時に、旧アップローダーの資格情報ファイル `ffxiv-uploader-cred.xml` が同じフォルダにあれば自動で取り込みます。無い場合は状態画面の「セットアップ（Google認証）」から初回認証を行います（ダウンロードフォルダの `client_secret_*.json` を使用）。
3. トレイメニューで「ログイン時に自動起動」を有効にします。

資格情報は DPAPI で暗号化して `uploader-app-cred.json` に保存します（平文では保存しません）。使用スコープは `drive.file` のみです。

### 操作

- トレイアイコン右クリック: 状態を表示 / 今すぐ送信 / 今すぐ収集 / 自動実行の切替 / ログイン時に自動起動の切替 / 更新を確認 / ログを開く / 終了
- ダブルクリック: 状態画面（最終実行結果の確認とフォルダ設定の変更）

### ソースコード

`src/` にあります（.NET 10、WinForms）。中核ロジックは `FfxivUploader.Core`（プラットフォーム非依存）、画面と Windows 固有処理は `FfxivUploader.App` です。

---

## English

The successor to the PowerShell uploader (`uploader/`) and the collector (Collect-DalamudConfig.ps1). It combines both into a single Windows tray application. The file hand-off contract (see the [integration spec](../docs/INTEGRATION.md)) is identical to the PowerShell version.

### What it does

- **Sync cycle (every 5 minutes)**: uploads the plugin's per-character status JSON files, tell records and config-application results to the `to_muse` folder on Google Drive, and delivers config requests (`ffxiv-config-requests.json`) into the plugin's `config-inbox`.
- **Collect cycle (every hour)**: builds a light snapshot of the Dalamud plugin configs (root-level JSON files + AutoDuty JSON files + the last 20,000 lines of the Dalamud log, masked). The zip is named `FF14-pluginConfigs-<timestamp>.zip`; older outputs rotate into an `OLD` folder with 30-day retention. The full-share zip was retired.
- **Auto-update (daily + at startup)**: checks `uploader-app/version.json` in this repository; when a newer version exists it downloads the package, verifies its SHA-256, replaces itself and restarts.

### Install

1. Download `FfxivStatusUploaderApp.zip` from this repository's Releases (tag `uploader-app-v*`), extract it, and place `FfxivStatusUploader.exe` in any folder (the same folder as the old uploader is recommended).
2. On first launch it imports the old uploader's credential file `ffxiv-uploader-cred.xml` if it sits next to the exe. Otherwise, run the first-time sign-in from the status window ("セットアップ（Google認証）"), which uses a `client_secret_*.json` in your Downloads folder.
3. Enable "ログイン時に自動起動" (start at logon) from the tray menu.

Credentials are stored DPAPI-encrypted in `uploader-app-cred.json` (never in plain text). The only OAuth scope used is `drive.file`.

### Source

In `src/` (.NET 10, WinForms). Core logic lives in `FfxivUploader.Core` (platform-independent); the UI and Windows-specific parts live in `FfxivUploader.App`.
