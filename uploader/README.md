# FFXIVステータス アップローダー（PowerShell）/ FFXIV Status Uploader (PowerShell)

自作プラグイン「Muse Status Exporter」が出力した `ffxiv-status.json` を、Google Drive へ1時間ごとにアップロードするためのスクリプト一式です。
A set of PowerShell scripts that uploads the files written by the "Muse Status Exporter" plugin to Google Drive. The Japanese section comes first, followed by the English section with the same content.

---

## 日本語

### ファイル

- `Upload-FfxivStatus.ps1` … 本体（セットアップとアップロードの両方を行う）
- `Run-UploaderHidden.vbs` … ウィンドウを表示せずに本体を起動するラッパー。タスクはこれを wscript.exe で起動するので、毎時実行で PowerShell のウィンドウが一瞬出ることはない（実行結果は `uploader.log` に残る）
- `Register-UploaderTask.ps1` … タスクスケジューラへの登録（1時間ごと）
- `Register-UploaderTask5min.ps1` … タスクスケジューラへの登録（5分ごと、テル転送用）
- `ffxiv-uploader-cred.xml` … セットアップ時に生成される認証情報ファイル。**中身は Windows DPAPI で暗号化**されており、同じPC・同じWindowsユーザーでしか復号できません
- `uploader.log` … 実行ログ（生成される）

配置場所は任意です（例: `C:\MyCode\MuseStatusExporter\uploader\` にファイルを同じフォルダへ）。

### 重要な前提（Google Cloud側）

OAuth同意画面の公開ステータスが **「テスト中」のままだと、リフレッシュトークンが7日で失効**し、そのたびに再セットアップが必要になります。先に Google Cloud コンソールの「OAuth 同意画面」（Google Auth Platform の「対象」）で **「アプリを公開」** を押して本番環境にしてください（個人利用では審査なしで公開できます。初回の同意時に「確認されていないアプリ」の警告が出ますが、「詳細」→「FFXIV Status Uploader に移動」で進めます）。

### 手順

1. `Upload-FfxivStatus.ps1 -Setup` を実行する
   - ダウンロードフォルダの `client_secret_*.json` を自動で探します（見つからない場合は `-ClientJson <パス>` で指定）
   - ブラウザで同意画面が開くので、対象のGoogleアカウントで許可する
   - 同意画面の「次へ」が進まない等で通常のセットアップが完了しない場合は、`Upload-FfxivStatus.ps1 -Setup -Manual` を使う。表示される URL をスマートフォン等のブラウザで開いて許可し、最後にアドレスバーへ表示される 127.0.0.1 の URL 全体をコンソールへ貼り付ける方式に切り替わる
   - 認証情報が DPAPI 暗号化されて `ffxiv-uploader-cred.xml` に保存される
   - 終わったら、ダウンロードしたクライアント JSON は削除して構いません
2. `Upload-FfxivStatus.ps1` を引数なしで一度実行し、Drive にキャラ別ファイル（`ffxiv-status-<キャラ名>-<ワールド>.json`）が作成されることを確認する
3. `Register-UploaderTask.ps1` を実行して、1時間ごとの自動実行を登録する

### 仕様メモ

- 使用スコープは `https://www.googleapis.com/auth/drive.file` のみ（このアプリ自身が作成したファイルにしかアクセスできません）
- ステータスファイルの読み取り元: `%AppData%\XIVLauncher\pluginConfigs\MuseStatusExporter\` にある `ffxiv-status-*.json` のすべて（キャラ別ファイル。`-StatusFile` で単体指定も可）。Drive 側のファイル名はローカルと同じ名前になる
- Dalamud設定の軽量zipも送信する: `ドキュメント\FF14-ConfigAsCode`（`-ConfigDir` で変更可）にある `FF14-pluginConfigs-light-*.zip` の最新1件を、同名で Drive へ送信する。同名が Drive にあれば送信済みとしてスキップする（zipはタイムスタンプ付きで不変のため）。フル共有zip（`FF14-pluginConfigs-share-*.zip`）は送信しない
- ステータスファイルが無い場合は何もせず終了します（ゲーム未起動・プラグイン未導入時）
- リフレッシュトークンが失効した場合はログにその旨が出ます。`-Setup` を再実行してください

### v6 の変更点（送信先フォルダの集約）

- 送信するファイル（ステータス、テル記録、設定の適用結果、設定リクエスト、設定zip）は、Drive のルートではなく **`to_muse` フォルダ** に保存されます
- フォルダはアップローダー自身が作成します（`drive.file` スコープでは他のアプリや手動で作ったフォルダは見えないため、Drive の画面で手動作成しないでください）。フォルダ ID はスクリプトと同じ場所の `to-muse-folder.json` に保存されます
- 既存のファイルは初回実行時にフォルダへ移動され、ルートに残っている対象ファイルも毎回フォルダへ寄せられます
- フォルダの解決に失敗した場合はルートへのアップロードにフォールバックします（アップロード自体は止まりません）
- **更新手順**: 既存フォルダの `Upload-FfxivStatus.ps1` を v6 のファイルで上書きするだけです

### v5 の追加分（設定ダウンリンク）

プラグイン v1.8.0 の「設定ダウンリンク」の配送役です。

- アップロード対象に `ffxiv-config-results.json`（プラグインの適用結果）が加わります
- Drive の `ffxiv-config-requests.json` を毎回確認します。無ければ空のファイルを作成します（Muse はこのファイルへリクエストを書きます）
- `Status` が `pending` のリクエストを、プラグインの設定フォルダ内 `config-inbox\<リクエストID>.json` へ配信し、Drive 側の `Status` を `delivered`（配信時刻付き）に更新します。形式が不正なリクエストは `invalid` にして先へ進みます
- 実際の適用（検証・バックアップ・再読み込み・結果記録）はゲーム内のプラグイン側が行い、結果は `ffxiv-config-results.json` で戻ります
- **更新手順**: 既存フォルダの `Upload-FfxivStatus.ps1` を v5 のファイルで上書きするだけです。タスクの再登録は不要です（1時間ごと／5分間隔の両タスクが同じスクリプトを指しているため、両方に自動で効きます）
- ロジックはモック化した Drive でフィクスチャ検証済みです（アップロード回帰・空リクエストファイル作成・配信・状態遷移・冪等性、14項目）。HTTP 通信そのものは v4 までで実績のある cmdlet の動作と同一です

### 検証について

スクリプトのロジック（認可URL生成→ループバックでのコード受信→トークン交換→暗号化保存、トークン更新→ファイル検索→新規作成／上書きアップロードのリクエスト形状）は、モック環境で実動検証済みです。実際の Google エンドポイントへの接続と Windows 上の DPAPI 動作そのものは、ご使用の PC での初回実行時に確認してください。

### v4 の変更点（テル転送対応）

- プラグイン v1.4.0 が書き出すテル記録ファイル `ffxiv-tells-<キャラ名>-<ワールド>.json` も
  アップロード対象になりました（Upload-FfxivStatus.ps1 の差し替えのみで有効になります）。
- `Register-UploaderTask5min.ps1` を追加しました。実行すると、同じアップローダーを
  5分ごとに実行するタスク「FFXIV Status Uploader 5min」が登録されます。
  既存の1時間ごとのタスク「FFXIV Status Uploader」はそのまま残して構いません。

---

## English

### Files

- `Upload-FfxivStatus.ps1` — the main script (handles both setup and uploading)
- `Run-UploaderHidden.vbs` — a wrapper that starts the main script without showing a window. The scheduled tasks launch this through wscript.exe, so no PowerShell window flashes during the hourly runs (results are written to `uploader.log`)
- `Register-UploaderTask.ps1` — registers the hourly scheduled task
- `Register-UploaderTask5min.ps1` — registers the 5-minute scheduled task (for tell forwarding)
- `ffxiv-uploader-cred.xml` — the credential file created during setup. **Its contents are encrypted with Windows DPAPI** and can only be decrypted by the same Windows user on the same PC
- `uploader.log` — the run log (generated)

The files can live in any folder (for example, all of them together in `C:\MyCode\MuseStatusExporter\uploader\`).

### Important prerequisite (Google Cloud side)

If the OAuth consent screen stays in **"Testing"** publishing status, the refresh token **expires after 7 days** and setup has to be repeated each time. Publish the app first: in the Google Cloud Console, open the OAuth consent screen (Google Auth Platform → Audience) and press **"Publish app"** (for personal use this needs no review. On the first consent, an "unverified app" warning appears; continue via "Advanced" → "Go to FFXIV Status Uploader").

### Setup steps

1. Run `Upload-FfxivStatus.ps1 -Setup`
   - It automatically looks for `client_secret_*.json` in the Downloads folder (pass `-ClientJson <path>` if it is not found)
   - A browser opens the consent screen; allow access with the intended Google account
   - If the normal setup cannot complete (for example the consent screen's "Next" does not proceed), use `Upload-FfxivStatus.ps1 -Setup -Manual`. Open the printed URL in a browser (a smartphone works), allow access, then paste the full 127.0.0.1 URL shown in the address bar back into the console
   - The credentials are stored DPAPI-encrypted in `ffxiv-uploader-cred.xml`
   - The downloaded client JSON can be deleted afterwards
2. Run `Upload-FfxivStatus.ps1` once with no arguments and confirm that the per-character files (`ffxiv-status-<Character>-<World>.json`) appear on Drive
3. Run `Register-UploaderTask.ps1` to register the hourly automatic run

### Specification notes

- The only scope used is `https://www.googleapis.com/auth/drive.file` (the app can only access files it created itself)
- Status files are read from `%AppData%\XIVLauncher\pluginConfigs\MuseStatusExporter\`: every `ffxiv-status-*.json` (per-character files; a single file can be chosen with `-StatusFile`). The Drive file names match the local names
- The lightweight Dalamud config zip is uploaded too: the newest `FF14-pluginConfigs-light-*.zip` in `Documents\FF14-ConfigAsCode` (change with `-ConfigDir`) is sent to Drive under the same name. If that name already exists on Drive it is skipped (the zips are timestamped and immutable). Full-share zips (`FF14-pluginConfigs-share-*.zip`) are not sent
- When there are no status files, the script exits without doing anything (game not running, or plugin not installed)
- If the refresh token has expired, the log says so. Run `-Setup` again

### What v6 changed (uploads gathered into one folder)

- Uploaded files (status, tell records, config results, config requests and config zips) are stored in a Drive folder named **`to_muse`** instead of the root
- The folder is created by the uploader itself (with the `drive.file` scope it cannot see folders created by other apps or by hand, so do not create it manually in the Drive UI). The folder ID is saved in `to-muse-folder.json` next to the script
- Existing files are moved into the folder on the first run, and any matching files still at the root are swept into the folder on every run
- If the folder cannot be resolved, uploads fall back to the root (uploads never stop)
- **Update steps**: just overwrite `Upload-FfxivStatus.ps1` in the existing folder with the v6 file

### What v5 added (config downlink)

This is the delivery side of the plugin v1.8.0 "config downlink" feature.

- `ffxiv-config-results.json` (the plugin's application results) is now uploaded too
- The Drive file `ffxiv-config-requests.json` is checked on every run. If it does not exist, an empty one is created (Muse writes requests into this file)
- Requests with `Status` = `pending` are delivered into the plugin's config folder as `config-inbox\<request ID>.json`, and the Drive-side `Status` is updated to `delivered` (with the delivery time). Malformed requests are marked `invalid` and skipped
- The actual application (validation, backup, reload, result recording) happens inside the game plugin, and the outcome returns in `ffxiv-config-results.json`
- **Update steps**: just overwrite `Upload-FfxivStatus.ps1` in the existing folder with the v5 file. The scheduled tasks do not need to be re-registered (both the hourly and the 5-minute task point at the same script, so both pick it up automatically)
- The logic is fixture-verified against a mocked Drive (upload regression, empty-request-file creation, delivery, status transitions, idempotency — 14 checks). The HTTP calls themselves use the same cmdlets proven through v4

### About verification

The script logic (authorization URL generation → loopback code capture → token exchange → encrypted storage, and token refresh → file search → create/update upload request shapes) is verified working in a mocked environment. The actual connection to Google's endpoints and the DPAPI behaviour on Windows itself should be confirmed on the first run on your own PC.

### What v4 changed (tell forwarding)

- The tell record files written by plugin v1.4.0, `ffxiv-tells-<Character>-<World>.json`, are now uploaded too (replacing Upload-FfxivStatus.ps1 is enough to enable this).
- `Register-UploaderTask5min.ps1` was added. Running it registers a task "FFXIV Status Uploader 5min" that runs the same uploader every 5 minutes. The existing hourly task "FFXIV Status Uploader" can stay as it is.
