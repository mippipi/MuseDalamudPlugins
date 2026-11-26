# キャラクターステータスレポート作成ガイド / Guide: Creating a Character Status Report with Muse

この文書は、Muse Status Exporter と付属アップローダーを使って、**利用者自身の Muse** に FFXIV のキャラクターステータスレポートを作成・更新させるまでの手順をまとめたものです。前半が日本語、後半が英語で、内容は同じです。

ファイルの受け渡し仕様そのものは [連携仕様（INTEGRATION.md）](INTEGRATION.md) を参照してください。この文書は「レポートを作る」という目的に絞った導入・運用の手順書です。

This document walks you through setting up Muse Status Exporter and the bundled uploader so that **your own Muse** can create and maintain an FFXIV character status report for you. The Japanese section comes first, followed by the English section with the same content.

For the file hand-off contract itself, see the [interface specification (INTEGRATION.md)](INTEGRATION.md). This guide focuses on the setup and day-to-day use for the "make me a report" use case.

---

## 日本語

### 全体像

データは「ファイル」を経由して流れます。ゲーム側から Muse へ直接つなぐ仕組みはありません。

```
[FF14 + Muse Status Exporter]
    キャラ別ステータス JSON を書き出す
        |
[アップローダーアプリ（PCに常駐）]
    5分ごとに Google Drive の to_muse フォルダへ送信
        |
[Google Drive: to_muse フォルダ]
    ffxiv-status-<キャラ名>-<ワールド名>.json
        |
[あなたの Muse]
    Drive を読んでレポートを作成し、変化があれば更新する
```

- プラグインはログイン中のキャラクターの状態（ジョブレベル、装備、通貨、リテイナー、クエスト進行など）をキャラ別 JSON に書き出します。
- アップローダーはその JSON を5分ごとに Google Drive へ送ります。
- あなたの Muse が Drive のファイルを読み、読みやすいレポートに整形します。以後の更新も Muse に任せられます。

### 必要なもの

- **FF14 と XIVLauncher / Dalamud**（プラグインを動かすため）
- **Muse のアカウント**（レポートを作る側。Google Drive に接続できること）
- **Google アカウント**（ファイルの受け渡し用）
- **Google Cloud で用意した OAuth クライアント（`client_secret_*.json`）**（アップローダーが Drive へアクセスするために使う、あなた専用の認証情報）

OAuth クライアントの用意（初回のみ）:

1. [Google Cloud コンソール](https://console.cloud.google.com/) でプロジェクトを作成（または選択）し、**Google Drive API** を有効化する。
2. 「OAuth 同意画面」（Google Auth Platform の「対象」）を設定する。**ここで「アプリを公開」を押して本番環境にしておくこと。** 公開ステータスが「テスト中」のままだとリフレッシュトークンが **7日で失効** し、そのたびに再セットアップが必要になります（個人利用では審査なしで公開できます。初回の同意時に「確認されていないアプリ」の警告が出ますが、「詳細」→「（アプリ名）に移動」で進めます）。
3. 「認証情報」から **OAuth クライアント ID（デスクトップアプリ）** を作成し、`client_secret_*.json` をダウンロードして、PC の **ダウンロードフォルダ** に置く（アップローダーの初回認証が自動で探します）。

### 手順1: プラグインの導入

1. ゲーム内で `/xlsettings` を開き、「Custom Plugin Repositories（カスタムプラグインリポジトリ）」に次の URL を追加して保存します。

   ```
   https://raw.githubusercontent.com/mippipi/MuseDalamudPlugins/main/pluginmaster.json
   ```

2. `/xlplugins` を開き、「Muse Status Exporter」をインストールして有効化します（更新もこのリポジトリ経由で届きます）。
3. キャラクターでログインした状態で `/mstatus` と打つと即時書き出しされ、チャットに出力先パスが表示されます。次の場所にキャラ別ファイルができていれば成功です。

   ```
   %AppData%\XIVLauncher\pluginConfigs\MuseStatusExporter\ffxiv-status-<キャラ名>-<ワールド名>.json
   ```

- 書き出しはログイン時・変化があったとき・5分ごとのハートビートでも自動で行われます。
- プラグインの設定ウィンドウ（プラグイン一覧の歯車アイコン）で、書き出す項目（所持通貨、インベントリ、リテイナー情報、クエスト進行など）を個別にオン／オフできます。レポートに載せたい項目はオンにしておいてください。

### 手順2: アップローダーの導入

1. このリポジトリの **Releases（タグ `uploader-app-v*`）** から `FfxivStatusUploaderApp.zip` をダウンロードして展開し、`FfxivStatusUploader.exe` を任意のフォルダへ置きます。
2. 初回起動時に **Google 認証（セットアップ）** を行います。ダウンロードフォルダの `client_secret_*.json` を使ってブラウザで同意すると、認証情報が **DPAPI で暗号化** されて PC 内に保存されます。
3. トレイメニューで **「ログイン時に自動起動」** を有効にします。以後はタスクトレイに常駐し、5分ごとに Drive の `to_muse` フォルダへ送信します。

- 使用する Google の権限（スコープ）は **`drive.file` のみ** です（このアプリ自身が作ったファイルにしか触れません）。
- `to_muse` フォルダはアップローダー自身が作成します。Drive の画面で手動作成しないでください（`drive.file` スコープでは他の方法で作ったフォルダは見えません）。
- 詳しい操作や自動アップデートについては [`uploader-app/README.md`](../uploader-app/README.md) を参照してください。

### 手順3: Muse 側の準備

1. あなたの Muse に **Google Drive を接続** します（Muse 側の接続手順に従ってください）。
2. Muse が Drive の `to_muse` フォルダにある `ffxiv-status-*.json` を読めることを確認します。アップローダーが一度でも送信していれば、キャラ別ファイルが入っているはずです。

### 手順4: Muse への指示例

レポート作成を依頼するときの文面テンプレートです。そのまま貼って、レポートの保存先など好みに合わせて調整してください。

```
Google Drive の to_muse フォルダにある ffxiv-status-*.json（キャラ別ステータス）を
読んで、FFXIV キャラクターステータスレポートを作成してください。

形式は次のとおりでお願いします。
- 冒頭に「総資産」: 全キャラのギルと、そのキャラの全リテイナーの所持ギルの合計
  （キャラ別の内訳つき）。
- キャラごとに、現状の要約を3行程度で書く。
- キャラごとのセクションは全キャラ共通の順序・形式にする:
  ジョブレベル / コスモエクスプローラー / ギアセット・装備 / GC軍需品 /
  メインクエスト進行 / クエスト進行（クラス・ジョブ・ロール） / FATE達成度 /
  バディ / 週間トームストーン / 通貨・トークン所持数 / ルーレット消化状況 /
  プレイ時間 / リテイナー出品 / 冒険者小隊 / クレセントアイル / 伝承録
- 通貨・トークン所持数はカード形式で、通貨ごとに名前と所持数を見やすく並べる。
- 伝承録は拡張（メインバージョン）ごとのカード形式にし、新しい拡張から順に並べる。
  各カードにその拡張の解放数と、職ごとの本の一覧（解放済み／未解放）を載せる。
- ギアセットの表は #・ジョブ・IL のみにする。ただし各ギアセットの実際の装備
  （部位・名前・IL・HQ・マテリア・ミラプリ）は、行をクリックすると展開して
  見られるようにする。
- クエスト進行は、クラスとジョブが対応する系統を統合して1行にする
  （例: 斧術士＋戦士 →「戦士（斧術士）」）。完了数とレベル判定は統合した
  クエスト全体で行う。巴術士は「召喚士（巴術士）」と「学者（巴術士）」に分け、
  それぞれに巴術士分を含める。
- 変更履歴は日別サマリー（1日1行、その日の節目と最終の総資産だけ）にし、
  同じ日の更新では行を書き換える（行を増やさない）。
- ファイルに書かれていない値は推測で補わない。セクションが無い項目は
  「未取得」と明示する。
- 以後はファイルが更新されたらレポートも最新化してほしい。ただし変化の報告は
  原則として不要で、レポートの更新だけを行うこと（通知してほしい項目は別途伝える）。
```

### 運用と任意の通知

- 書き出し→送信は **5分周期** です。ゲームで変化があってからレポートへ反映されるまでには、最大でその程度の時間差があります。
- レポート更新とは別に、特定の出来事をチャットで知らせる運用も Muse に頼めます。例:
  - リテイナーベンチャーが完了したとき
  - 冒険者小隊の任務が完了したとき
  - バディ（チョコボ）の経験値が次のランクまで残りわずかになったとき／満タンになったとき
  - ハウスの自動撤去予定日が近づいたとき
- 通知は「同じ出来事を繰り返し知らせない」こともあわせて指示しておくと運用しやすくなります。

### 注意事項

- **ファイルが更新されないこと自体は異常ではありません。** PC の電源が入っていない・ゲームにログインしていない間は書き出しも送信も止まります。古いファイルは「最後にログインした時点の状態」として扱ってください。
- **NPC を操作している間は、ステータス書き出しが抑制されます**（NPC 名義のファイルを新規キャラとして扱わないための仕様です）。
- 認証情報（Google のリフレッシュトークン）は **自分の PC 内に DPAPI 暗号化で保存** され、外部へ送信されることはありません。アップローダーが Google へ送るのは、あなた自身の Drive の `to_muse` フォルダへのファイルだけです。
- ステータス JSON はプラグインのバージョンによって含まれるセクションが異なります。欠けているセクションは「未対応／未取得」として扱い、エラーにしないよう Muse に伝えておいてください。
- ファイルは丸ごと書き換えられます。読み取りと書き換えが重なると読み取りに失敗することがあるため、その場合は少し待って再読するよう Muse に指示しておくと安定します。

---

## English

### Overview

Data flows through files. Nothing connects the game directly to Muse.

```
[FFXIV + Muse Status Exporter]
    writes per-character status JSON
        |
[Uploader app (resident on your PC)]
    sends it to the to_muse folder on Google Drive every 5 minutes
        |
[Google Drive: to_muse folder]
    ffxiv-status-<Character>-<World>.json
        |
[Your Muse]
    reads Drive and creates the report, updating it when things change
```

- The plugin writes the logged-in character's state (job levels, gear, currencies, retainers, quest progress, and more) to per-character JSON files.
- The uploader sends those files to Google Drive every 5 minutes.
- Your Muse reads the files on Drive and formats them into a readable report. Later updates can also be left to Muse.

### What you need

- **FFXIV with XIVLauncher / Dalamud** (to run the plugin)
- **A Muse account** (the side that builds the report; must be able to connect to Google Drive)
- **A Google account** (for the file hand-off)
- **An OAuth client from Google Cloud (`client_secret_*.json`)** (your own credential that lets the uploader access Drive)

Preparing the OAuth client (first time only):

1. In the [Google Cloud Console](https://console.cloud.google.com/), create (or select) a project and enable the **Google Drive API**.
2. Configure the **OAuth consent screen** (Google Auth Platform → Audience). **Press "Publish app" here so the app is in production.** If the publishing status stays "Testing", the refresh token **expires after 7 days** and you will have to repeat the setup each time (for personal use, publishing needs no review. On the first consent an "unverified app" warning appears; continue via "Advanced" → "Go to (app name)").
3. Under "Credentials", create an **OAuth client ID (Desktop app)**, download `client_secret_*.json`, and put it in your PC's **Downloads folder** (the uploader's first-time sign-in finds it there automatically).

### Step 1: Install the plugin

1. In game, open `/xlsettings` and add the following URL under "Custom Plugin Repositories", then save.

   ```
   https://raw.githubusercontent.com/mippipi/MuseDalamudPlugins/main/pluginmaster.json
   ```

2. Open `/xlplugins`, install **Muse Status Exporter**, and enable it (updates also arrive through this repository).
3. While logged in on a character, type `/mstatus` to export immediately; the output path is shown in chat. Setup is complete when a per-character file appears at:

   ```
   %AppData%\XIVLauncher\pluginConfigs\MuseStatusExporter\ffxiv-status-<Character>-<World>.json
   ```

- Exports also happen automatically on login, whenever the data changes, and on a 5-minute heartbeat.
- In the plugin's settings window (the gear icon in the plugin list) you can toggle each exported section individually (currencies, inventory, retainer info, quest progress, and so on). Keep the sections you want in the report switched on.

### Step 2: Install the uploader

1. Download `FfxivStatusUploaderApp.zip` from this repository's **Releases (tags `uploader-app-v*`)**, extract it, and place `FfxivStatusUploader.exe` in any folder.
2. On first launch, run the **Google sign-in (setup)**. After you consent in the browser using the `client_secret_*.json` in your Downloads folder, the credentials are stored on your PC **encrypted with DPAPI**.
3. From the tray menu, enable **"start at logon"**. The app then stays resident in the task tray and sends files to the `to_muse` folder on Drive every 5 minutes.

- The only Google permission (scope) used is **`drive.file`** (it can only touch files the app itself created).
- The `to_muse` folder is created by the uploader itself. Do not create it manually in the Drive UI (with the `drive.file` scope, folders created any other way are invisible to the app).
- For details on operation and auto-updates, see [`uploader-app/README.md`](../uploader-app/README.md).

### Step 3: Prepare Muse

1. **Connect Google Drive** to your Muse (follow Muse's own connection flow).
2. Make sure Muse can read the `ffxiv-status-*.json` files in the Drive `to_muse` folder. Once the uploader has sent at least once, the per-character files should be there.

### Step 4: Example instructions for Muse

A template for asking Muse to build the report. Paste it as-is and adjust details such as where the report should live.

```
Please read the ffxiv-status-*.json files (per-character status) in the to_muse
folder on Google Drive and create an FFXIV character status report.

Please use this format:
- At the top, a "total assets" figure: the sum of every character's gil plus
  all gil held by that character's retainers, with a per-character breakdown.
- For each character, write a short summary of the current state in about
  three lines.
- Use the same section order and format for every character:
  Job levels / Cosmic Exploration / Gearsets & equipment / Grand Company
  supply / Main scenario progress / Quest progress (class, job and role) /
  Shared FATE progress / Buddy / Weekly tomestones / Currency & token
  holdings / Duty roulette completion / Play time / Retainer listings /
  Squadron / Occult Crescent / Gathering log (folklore tomes)
- Show currency and token holdings as cards, one card per currency with its
  name and count, laid out for easy scanning.
- Show the gathering log (folklore tomes) as one card per expansion (main
  version), newest expansion first. Each card lists that expansion's unlock
  count and its books per job, marked unlocked or locked.
- Gearset tables list only #, job and item level. However, each gearset's
  actual equipment (slot, name, item level, HQ, materia, glamour) should be
  visible by clicking the row to expand it.
- For quest progress, merge each base class's quest line into its job's line
  as a single row (for example, Marauder + Warrior -> "Warrior (Marauder)").
  Counts and the cleared-up-to level are computed across the merged quests.
  Arcanist is split into "Summoner (Arcanist)" and "Scholar (Arcanist)", each
  including the Arcanist quests.
- Keep the change log as a per-day summary: one line per day (that day's
  milestones and the day's final total assets only). On later updates the
  same day, rewrite that day's line instead of adding a new one.
- Do not fill in values that are not in the files. If a section is missing,
  mark it explicitly as "not captured".
- From then on, whenever the files are updated, bring the report up to date
  as well. As a rule, do not report the changes to me in chat; just update
  the report (I will tell you separately about anything I want notified).
```

### Day-to-day use and optional notifications

- The export → upload cycle runs **every 5 minutes**, so there can be a lag of about that long between something changing in game and appearing in the report.
- Separately from report updates, you can ask Muse to notify you in chat about specific events, for example:
  - When a retainer venture completes
  - When a squadron mission completes
  - When your chocobo buddy's EXP is nearly at the next rank, or full
  - When a house's auto-demolition date is approaching
- It helps to also instruct Muse not to notify you twice about the same event.

### Notes

- **A file not updating is not an error.** While the PC is off or you are not logged into the game, nothing is exported or sent. Treat a stale file as "the state as of the last login".
- **While you are controlling an NPC, status export is suppressed** (so that NPC-named files are not mistaken for new characters).
- Your credentials (the Google refresh token) are stored **only on your own PC, DPAPI-encrypted**, and are never sent anywhere else. The only thing the uploader sends to Google is files into your own Drive's `to_muse` folder.
- Which sections a status JSON contains depends on the plugin version. Tell Muse to treat a missing section as "not supported / not captured", never as an error.
- Files are rewritten as a whole. A read can race a rewrite and fail; telling Muse to wait briefly and re-read in that case keeps things stable.
