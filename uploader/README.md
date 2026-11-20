# FFXIVステータス アップローダー（PowerShell）

自作プラグイン「Muse Status Exporter」が出力した `ffxiv-status.json` を、Google Drive へ1時間ごとにアップロードするためのスクリプト一式です。

## ファイル

- `Upload-FfxivStatus.ps1` … 本体（セットアップとアップロードの両方を行う）
- `Run-UploaderHidden.vbs` … ウィンドウを表示せずに本体を起動するラッパー。タスクはこれを wscript.exe で起動するので、毎時実行で PowerShell のウィンドウが一瞬出ることはない（実行結果は `uploader.log` に残る）
- `Register-UploaderTask.ps1` … タスクスケジューラへの登録（1時間ごと）
- `ffxiv-uploader-cred.xml` … セットアップ時に生成される認証情報ファイル。**中身は Windows DPAPI で暗号化**されており、同じPC・同じWindowsユーザーでしか復号できません
- `uploader.log` … 実行ログ（生成される）

配置場所は任意です（例: `C:\MyCode\FfxivStatusUploader\` に3ファイルを同じフォルダへ）。

## 重要な前提（Google Cloud側）

OAuth同意画面の公開ステータスが **「テスト中」のままだと、リフレッシュトークンが7日で失効**し、そのたびに再セットアップが必要になります。先に Google Cloud コンソールの「OAuth 同意画面」（Google Auth Platform の「対象」）で **「アプリを公開」** を押して本番環境にしてください（個人利用では審査なしで公開できます。初回の同意時に「確認されていないアプリ」の警告が出ますが、「詳細」→「FFXIV Status Uploader に移動」で進めます）。

## 手順

1. `Upload-FfxivStatus.ps1 -Setup` を実行する
   - ダウンロードフォルダの `client_secret_*.json` を自動で探します（見つからない場合は `-ClientJson <パス>` で指定）
   - ブラウザで同意画面が開くので、対象のGoogleアカウントで許可する
   - 同意画面の「次へ」が進まない等で通常のセットアップが完了しない場合は、`Upload-FfxivStatus.ps1 -Setup -Manual` を使う。表示される URL をスマートフォン等のブラウザで開いて許可し、最後にアドレスバーへ表示される 127.0.0.1 の URL 全体をコンソールへ貼り付ける方式に切り替わる
   - 認証情報が DPAPI 暗号化されて `ffxiv-uploader-cred.xml` に保存される
   - 終わったら、ダウンロードしたクライアント JSON は削除して構いません
2. `Upload-FfxivStatus.ps1` を引数なしで一度実行し、Drive にキャラ別ファイル（`ffxiv-status-<キャラ名>-<ワールド>.json`）が作成されることを確認する
3. `Register-UploaderTask.ps1` を実行して、1時間ごとの自動実行を登録する

## 仕様メモ

- 使用スコープは `https://www.googleapis.com/auth/drive.file` のみ（このアプリ自身が作成したファイルにしかアクセスできません）
- ステータスファイルの読み取り元: `%AppData%\XIVLauncher\pluginConfigs\MuseStatusExporter\` にある `ffxiv-status-*.json` のすべて（キャラ別ファイル。`-StatusFile` で単体指定も可）。Drive 側のファイル名はローカルと同じ名前になる
- Dalamud設定の軽量zipも送信する: `ドキュメント\FF14-ConfigAsCode`（`-ConfigDir` で変更可）にある `FF14-pluginConfigs-light-*.zip` の最新1件を、同名で Drive へ送信する。同名が Drive にあれば送信済みとしてスキップする（zipはタイムスタンプ付きで不変のため）。フル共有zip（`FF14-pluginConfigs-share-*.zip`）は送信しない
- ステータスファイルが無い場合は何もせず終了します（ゲーム未起動・プラグイン未導入時）
- リフレッシュトークンが失効した場合はログにその旨が出ます。`-Setup` を再実行してください

## v5 の追加分（設定ダウンリンク）

プラグイン v1.8.0 の「設定ダウンリンク」の配送役です。

- アップロード対象に `ffxiv-config-results.json`（プラグインの適用結果）が加わります
- Drive の `ffxiv-config-requests.json` を毎回確認します。無ければ空のファイルを作成します（Muse はこのファイルへリクエストを書きます）
- `Status` が `pending` のリクエストを、プラグインの設定フォルダ内 `config-inbox\<リクエストID>.json` へ配信し、Drive 側の `Status` を `delivered`（配信時刻付き）に更新します。形式が不正なリクエストは `invalid` にして先へ進みます
- 実際の適用（検証・バックアップ・再読み込み・結果記録）はゲーム内のプラグイン側が行い、結果は `ffxiv-config-results.json` で戻ります
- **更新手順**: 既存フォルダの `Upload-FfxivStatus.ps1` を v5 のファイルで上書きするだけです。タスクの再登録は不要です（1時間ごと／5分間隔の両タスクが同じスクリプトを指しているため、両方に自動で効きます）
- ロジックはモック化した Drive でフィクスチャ検証済みです（アップロード回帰・空リクエストファイル作成・配信・状態遷移・冪等性、14項目）。HTTP 通信そのものは v4 までで実績のある cmdlet の動作と同一です

## 検証について

スクリプトのロジック（認可URL生成→ループバックでのコード受信→トークン交換→暗号化保存、トークン更新→ファイル検索→新規作成／上書きアップロードのリクエスト形状）は、モック環境で実動検証済みです。実際の Google エンドポイントへの接続と Windows 上の DPAPI 動作そのものは、ご使用の PC での初回実行時に確認してください。

## v4 の変更点（テル転送対応）

- プラグイン v1.4.0 が書き出すテル記録ファイル `ffxiv-tells-<キャラ名>-<ワールド>.json` も
  アップロード対象になりました（Upload-FfxivStatus.ps1 の差し替えのみで有効になります）。
- `Register-UploaderTask5min.ps1` を追加しました。実行すると、同じアップローダーを
  5分ごとに実行するタスク「FFXIV Status Uploader 5min」が登録されます。
  既存の1時間ごとのタスク「FFXIV Status Uploader」はそのまま残して構いません。
