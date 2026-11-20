# Muse 連携のためのインターフェース仕様 / Interface Specification for Integrating with Muse

この文書は、他のプラグインやツールが Muse Status Exporter の仕組み（ステータスの受け渡しと設定ダウンリンク）に接続するために必要な情報をまとめたものです。前半が日本語、後半が英語で、内容は同じです。

This document describes everything another plugin or tool needs to connect to the Muse Status Exporter system (status hand-off and configuration downlink). The Japanese section comes first, followed by the English section with the same content.

---

## 日本語

### 全体像

連携は「ファイル」を境界として行われます。ネットワーク API を直接叩く必要はありません。

```
[Dalamud プラグイン] --(JSON ファイルを書く)--> [プラグイン設定フォルダ]
                                                      |
                                              [トランスポート (アップローダー等)]
                                                      |
                                              [共有先 (Google Drive 等)]
                                                      |
                                            [読み手 (Muse / 任意のツール)]

[設定の配布者 (Muse 等)] --(リクエストファイル)--> [共有先]
                                                      |
                                              [トランスポートが config-inbox へ配信]
                                                      |
                              [Muse Status Exporter が検証・適用し、結果ファイルに記録]
```

- **ステータス方向**: プラグインがキャラ別 JSON を書き、トランスポートが共有先へ運び、読み手がそれを読む。
- **設定方向（ダウンリンク）**: 配布者がリクエストファイルに依頼を書き、トランスポートがゲーム側へ届け、Muse Status Exporter が対象プラグインの設定ファイルを検証つきで差し替えて結果を返す。対象は Muse Status Exporter 自身の設定に限らず、`pluginConfigs` フォルダ配下の任意のプラグインの `.json` 設定ファイルです。

### ファイルの置き場所と名前

すべてのファイルは、対象プラグインの設定フォルダ（`%AppData%\XIVLauncher\pluginConfigs\<プラグイン内部名>\`）を起点にします。Muse Status Exporter の場合は `%AppData%\XIVLauncher\pluginConfigs\MuseStatusExporter\` です。

| ファイル | 方向 | 説明 |
|---|---|---|
| `ffxiv-status-<キャラ名>-<ワールド名>.json` | プラグイン → 読み手 | キャラ別ステータス スナップショット（後述） |
| `ffxiv-tells-<キャラ名>-<ワールド名>.json` | プラグイン → 読み手 | 受信テルの記録（後述） |
| `ffxiv-config-requests.json` | 配布者 → プラグイン | 設定ダウンリンクのリクエスト一覧（共有先に置く） |
| `ffxiv-config-results.json` | プラグイン → 配布者 | ダウンリンクの適用結果（プラグインが設定フォルダに書き、トランスポートが共有先へ運ぶ） |
| `config-inbox\<リクエストID>.json` | トランスポート → プラグイン | 配信されたリクエスト封筒（処理後は `config-inbox\done\` へ移動） |
| `FF14-pluginConfigs-<yyyyMMdd-HHmmss>.zip` | PC → 読み手 | Dalamud 設定一式の軽量スナップショット（任意。トランスポートは最新1件だけを同名で運び、同名があればスキップ） |

キャラ名・ワールド名はファイル名に使える形に整形されます（空白は除去されます。例: `ffxiv-status-RenStella-Ramuh.json`）。

### ステータス スナップショット形式

書き出しタイミング: ログイン時、内容が変わったとき（10秒間隔で変化を検出）、5分ごとのハートビート、手動コマンド `/mstatus` 実行時。

最上位フィールド:

| フィールド | 型 | 説明 |
|---|---|---|
| `Character` | string | キャラクター名 |
| `World` | string | ホームワールド名 |
| `CurrentJob` | string? | 現在のクラス／ジョブ名 |
| `DataUpdatedAt` | string | データ内容が最後に変わった時刻（ISO 8601、ローカル時刻・オフセット付き） |
| `ExportedAt` | string | このファイルを書き出した時刻（同上） |
| `PluginVersion` | string | 書き出したプラグインのバージョン（例 `1.13.0.0`） |
| `Jobs` | array | 全クラス／ジョブのレベルと経験値 |
| `Cosmic` | object? | コスモエクスプローラーの進捗 |
| `Gearsets` | array | 保存済みギアセット |
| `Equipped` | object? | 現在の装備 |
| `Currencies` | array | 通貨（ギル、トームストーン等） |
| `Inventory` | array | コンテナ別インベントリ |
| `Retainers` | array | リテイナーの状態 |
| `GrandCompanySupply` | object? | GC軍需品／調達品のリクエスト |
| `GcWindowDebug` | object? | GC取得のデバッグ情報（読み手は無視してよい） |
| `HousingVisits` | array | 自分のハウスへの入室履歴 |
| `MainScenario` | object? | メインクエスト進行 |
| `FateProgress` | array | 共有FATE達成度（全18エリア） |
| `Buddy` | object? | バディ（チョコボ）の状態 |
| `HaloneEarringEquipped` | bool? | ハルオーネイヤリングを装備中かどうか |

各セクションの形:

- `Jobs[]`: `{ Id, Abbrev, Name, Level, Exp, NeededExp }`（Id は ClassJob の行 ID、NeededExp は次レベルまでの必要経験値）
- `Cosmic.Classes[]`: `{ JobId, Abbrev, Score, ToolStage, ToolStageUnlocked, Types }`、`Types[]`: `{ Type, Current, Needed, Max }`（研究分析の進捗。ToolStage 等は取得できない場合 null）
- `Gearsets[]`: `{ Id, Name, ClassJob, ClassJobId, ItemLevel, Items }`、`Equipped.Items` と共通の `Items[]`: `{ Slot, ItemId, Name, ItemLevel, IsHq, Glamour, Stain0, Stain1, Materia }`（装備中アイテムには `Condition`、`Spiritbond` も付く。ItemId は HQ の場合も素のアイテム ID で、HQ は IsHq で表す）
- `Currencies[]`: `{ ItemId, Name, Count }`
- `Inventory[]`: `{ Container, Items }`、`Items[]`: `{ ItemId, Name, Count, IsHq }`
- `Retainers[]`: `{ Name, ClassJob, Level, Gil, ItemCount, VentureId, VentureCompleteAt }`（ベンチャー未出発時は VentureId = 0）
- `GrandCompanySupply`: `{ Supply, Provisioning }`、各要素 `{ ItemId, Name, NumRequested }`（その日のリクエスト。当日分が未取得の間は前回取得分が残る）
- `HousingVisits[]`: `{ At, World, Kind, Ward, Plot, Room }`（Ward / Plot はプラグイン内部の生値で、表示上の区・番地と 1 ずれる場合がある）
- `MainScenario`: `{ CurrentQuestId, CurrentQuestName, NextQuestId, NextQuestName, LastCompletedQuestId, LastCompletedQuestName, CurrentExpansion, CompletedQuests, TotalQuests, AllComplete, Expansions }`、`Expansions[]`: `{ ExpansionId, Name, CompletedQuests, TotalQuests }`
- `FateProgress[]`: `{ TabIndex, TerritoryTypeId, ZoneName, DisplayOrder, CurrentRank, MaxRank, FateProgress, NeededFates }`（FateProgress は最大ランクまでの累計達成数（総数 66）、NeededFates は次ランクまでの残り件数。v1.12.4 以降の正規化済みの値）
- `Buddy`: `{ Name, Rank, CurrentExp, MaxExp, SkillPoints, LevelUpReady }`（MaxExp は不明な場合 null。LevelUpReady は経験値が満タンでランク上限（20）未満のとき true ＝ 厩舎でサベネアオニオンを与えるとランクが上がる状態）

読み手への注意:

- セクションはプラグインのバージョンによって存在しないことがあります。**欠けているセクションは「未対応」として扱い、エラーにしないでください**（例: MainScenario は v1.11.0 以降、FateProgress は v1.12.0 以降、Buddy は v1.13.0 以降）。
- ファイルは丸ごと書き換えられます。読み取り中に書き換えが起きる可能性があるため、パースに失敗したら少し待って再読してください。
- 名前（Name フィールド）はゲーム内表示名で、言語設定に依存します。同一性の判定には ItemId 等の ID を使ってください。

### テル記録形式

`ffxiv-tells-<キャラ名>-<ワールド名>.json`:

```json
{
  "Character": "…",
  "World": "…",
  "UpdatedAt": "2026-11-20T12:00:00+09:00",
  "Tells": [
    { "Id": "…", "At": "…", "Sender": "…", "SenderWorld": "…", "Message": "…" }
  ]
}
```

新しい順ではなく到着順に最大 200 件を保持します。`Id` は重複排除用の識別子です。

### 設定ダウンリンク プロトコル

他のプラグインの設定を Muse 側から差し替えるための仕組みです。適用そのものは Muse Status Exporter（v1.8.0 以降）が担うため、対象側プラグインの改造は不要です。

#### リクエストファイル（共有先）

```json
{
  "Version": 1,
  "Requests": [
    {
      "Id": "20261120-example01",
      "CreatedAt": "2026-11-20T12:00:00+09:00",
      "Target": "SomePlugin.json",
      "PluginName": "Some Plugin",
      "PluginInternalName": "SomePlugin",
      "Reload": true,
      "ExpectedSha256": null,
      "ContentBase64": "<新しいファイル内容全体の base64>",
      "Status": "pending"
    }
  ]
}
```

| フィールド | 必須 | 説明 |
|---|---|---|
| `Id` | 必須 | リクエスト識別子。英数字とハイフン、8〜64 文字（先頭は英数字）。結果との対応付けに使う |
| `CreatedAt` | 任意 | 作成時刻（ISO 8601） |
| `Target` | 必須 | `pluginConfigs` フォルダからの相対パス（`.json` のみ）。`..` による外側への移動は不可。Muse Status Exporter 自身のフォルダは対象にできない |
| `PluginName` | 任意 | 表示名（結果ファイルにそのまま載る） |
| `PluginInternalName` | 任意 | 再読み込み対象プラグインの内部名。`Reload` が true のときの判定に使う |
| `Reload` | 任意 | true の場合、対象プラグインが読み込み済みなら無効化→差し替え→有効化で即時反映する。未読み込みのプラグインを勝手に有効化することはない（次回読み込み時に反映） |
| `ExpectedSha256` | 任意 | Muse が最後に見た対象ファイルの SHA-256（16進小文字）。現在のファイルと一致しない場合は上書きせず中止する（`aborted-stale`）。null なら無条件で差し替える |
| `ContentBase64` | 必須 | 差し替え後のファイル内容全体（有効な JSON でなければならない） |
| `Status` | 必須 | 配布者は `pending` で追加する。以後の遷移はトランスポートとプラグインが行う |

#### 状態の遷移

リクエスト側（リクエストファイル内の `Status`）:

- `pending` → `delivered`（トランスポートが `config-inbox\<Id>.json` へ配信した時点。`DeliveredAt` が付く）
- `pending` → `invalid`（Id / Target / ContentBase64 が不足している場合。`Note` に理由が付く）

結果側（`ffxiv-config-results.json` の `Results[]`。要素は `{ Id, Target, PluginName, Status, Detail, At }`、最大 100 件）:

- `processing` — プラグインが適用中（最終状態ではない）
- `applied` — 差し替えと再読み込みが完了し、新しい内容が有効になった
- `applied-pending-load` — 差し替えは完了したが、対象プラグインが未読み込みのため反映は次回読み込み時
- `applied-unverified` — 差し替えは行ったが、反映の確認が取れなかった
- `aborted-stale` — `ExpectedSha256` が現在のファイルと一致せず、上書きを中止した
- `failed` — 検証や書き込みに失敗した（理由は `Detail`）
- `interrupted` — 適用中にプラグインが停止したため、次回起動時に未完了として確定された

#### 安全モデル

- 対象は `pluginConfigs` 配下の `.json` のみに限定され、パスの外側参照は拒否される。
- 差し替え前に現在のファイルが `config-backups\<Target のパス>\<日時>.json` へバックアップされる（30日を過ぎたバックアップは削除される）。
- 内容が有効な JSON でないリクエストは適用されない。
- 処理済みの封筒は `config-inbox\done\` へ移動される。

### トランスポートの契約

付属アップローダーアプリ（`uploader-app/`）は次を運びます。自作のトランスポートに置き換える場合は、同じファイル契約を守ってください。

- プラグイン設定フォルダの `ffxiv-status-*.json`、`ffxiv-tells-*.json`、`ffxiv-config-results.json` を共有先へ（同名で上書き）
- 共有先の `ffxiv-config-requests.json` を読み、`pending` のリクエストを `config-inbox\` へ配信し、`Status` を更新して書き戻す（ファイルが無ければ `{ "Version": 1, "Requests": [] }` を作成する）
- 設定スナップショット zip（`FF14-pluginConfigs-*.zip`）の最新1件を同名で共有先へ（同名があればスキップ）

付属アップローダーの実装上の約束:

- Google Drive のスコープは `drive.file` のみ（自アプリが作ったファイルにしか触れない）
- 送信ファイルは Drive の `to_muse` フォルダに集約される（フォルダはアップローダー自身が作成する）
- Google のリフレッシュトークンは DPAPI 暗号化したローカルファイルにのみ保存し、スクリプトやリポジトリには含めない

### プラグイン内部ファイル（参考）

次は Muse Status Exporter の内部用サイドカーで、連携の境界ではありません（形式は予告なく変わることがあります）: `mstatus-gcsupply-<キャラ>-<ワールド>.json`（GC軍需品の最終取得分）、`mstatus-fateprogress-<キャラ>-<ワールド>.json`（FATE達成度の最終取得分）、`mstatus-buddy-<キャラ>-<ワールド>.json`（バディの最大経験値の最終既知値）、`mstatus-housing-<キャラ>-<ワールド>.json`（入室履歴）。

### 自分のプラグインを連携させるには

- **ステータスを Muse に渡したい**: 上記のスナップショット形式に沿った JSON を、自分のプラグインの設定フォルダへ `ffxiv-status-<キャラ名>-<ワールド名>.json` という名前で書けば、付属アップローダーがそのまま運びます（セクションは必要なものだけで構いません）。あるいは、読み手として既存のスナップショットを読むだけなら、共有先のファイルを参照してください。
- **自分のプラグインの設定を Muse から変更できるようにしたい**: 特別な実装は不要です。設定ファイルが `pluginConfigs\<内部名>\*.json` にある通常の Dalamud プラグインであれば、ダウンリンクの対象にできます。
- **トランスポートを自作したい**: 上記のファイル契約（対象ファイル、リクエストの配信と状態更新、結果ファイルの運搬）を満たす任意の仕組みに置き換えできます。

---

## English

### Overview

Integration happens through files. There is no network API to call directly.

```
[Dalamud plugin] --(writes JSON files)--> [plugin config folder]
                                                  |
                                     [transport (uploader, etc.)]
                                                  |
                                     [shared location (Google Drive, etc.)]
                                                  |
                                     [reader (Muse / any tool)]

[config sender (Muse, etc.)] --(requests file)--> [shared location]
                                                  |
                              [transport delivers into config-inbox]
                                                  |
              [Muse Status Exporter validates, applies, and records the result]
```

- **Status direction**: a plugin writes per-character JSON, a transport carries it to a shared location, and a reader reads it.
- **Configuration direction (downlink)**: a sender writes requests into a requests file, a transport delivers them to the game side, and Muse Status Exporter replaces the target plugin's configuration file (with validation) and reports the outcome. The target is not limited to Muse Status Exporter's own settings: it can be any plugin's `.json` configuration file under the `pluginConfigs` folder.

### File locations and names

Everything is relative to the target plugin's config folder (`%AppData%\XIVLauncher\pluginConfigs\<plugin internal name>\`). For Muse Status Exporter that is `%AppData%\XIVLauncher\pluginConfigs\MuseStatusExporter\`.

| File | Direction | Description |
|---|---|---|
| `ffxiv-status-<Character>-<World>.json` | plugin → reader | Per-character status snapshot (see below) |
| `ffxiv-tells-<Character>-<World>.json` | plugin → reader | Recorded incoming tells (see below) |
| `ffxiv-config-requests.json` | sender → plugin | Config downlink requests (kept at the shared location) |
| `ffxiv-config-results.json` | plugin → sender | Downlink outcomes (written by the plugin into its config folder, carried to the shared location by the transport) |
| `config-inbox\<request ID>.json` | transport → plugin | A delivered request envelope (moved to `config-inbox\done\` after processing) |
| `FF14-pluginConfigs-<yyyyMMdd-HHmmss>.zip` | PC → reader | Lightweight snapshot of the whole Dalamud config set (optional; the transport carries only the newest one, under the same name, and skips it if that name already exists) |

Character and world names are sanitized for file names (spaces are removed, e.g. `ffxiv-status-RenStella-Ramuh.json`).

### Status snapshot format

Written on login, whenever the content changes (change detection every 10 seconds), on a 5-minute heartbeat, and on demand via `/mstatus`.

Top-level fields:

| Field | Type | Description |
|---|---|---|
| `Character` | string | Character name |
| `World` | string | Home world name |
| `CurrentJob` | string? | Current class/job name |
| `DataUpdatedAt` | string | When the data last changed (ISO 8601, local time with offset) |
| `ExportedAt` | string | When this file was written (same format) |
| `PluginVersion` | string | Version of the writing plugin (e.g. `1.13.0.0`) |
| `Jobs` | array | Levels and EXP of every class/job |
| `Cosmic` | object? | Cosmic Exploration progress |
| `Gearsets` | array | Saved gearsets |
| `Equipped` | object? | Currently equipped items |
| `Currencies` | array | Currencies (gil, tomestones, ...) |
| `Inventory` | array | Inventory per container |
| `Retainers` | array | Retainer state |
| `GrandCompanySupply` | object? | Grand Company supply/provisioning requests |
| `GcWindowDebug` | object? | GC capture debug info (readers may ignore it) |
| `HousingVisits` | array | Visits to the player's own houses |
| `MainScenario` | object? | Main scenario progress |
| `FateProgress` | array | Shared FATE progress (all 18 zones) |
| `Buddy` | object? | Chocobo companion (buddy) state |
| `HaloneEarringEquipped` | bool? | Whether the Halone Earring is equipped |

Section shapes:

- `Jobs[]`: `{ Id, Abbrev, Name, Level, Exp, NeededExp }` (Id is the ClassJob row id; NeededExp is the EXP required for the next level)
- `Cosmic.Classes[]`: `{ JobId, Abbrev, Score, ToolStage, ToolStageUnlocked, Types }`, with `Types[]`: `{ Type, Current, Needed, Max }` (research analysis progress; ToolStage etc. are null when unavailable)
- `Gearsets[]`: `{ Id, Name, ClassJob, ClassJobId, ItemLevel, Items }`; `Items[]` (shared with `Equipped.Items`): `{ Slot, ItemId, Name, ItemLevel, IsHq, Glamour, Stain0, Stain1, Materia }` (equipped items also carry `Condition` and `Spiritbond`. ItemId is the base item id even for HQ items; HQ is expressed by IsHq)
- `Currencies[]`: `{ ItemId, Name, Count }`
- `Inventory[]`: `{ Container, Items }`, with `Items[]`: `{ ItemId, Name, Count, IsHq }`
- `Retainers[]`: `{ Name, ClassJob, Level, Gil, ItemCount, VentureId, VentureCompleteAt }` (VentureId is 0 when no venture is running)
- `GrandCompanySupply`: `{ Supply, Provisioning }`, each item `{ ItemId, Name, NumRequested }` (the current day's requests; until a new day's list is captured, the last captured list remains)
- `HousingVisits[]`: `{ At, World, Kind, Ward, Plot, Room }` (Ward / Plot are the plugin's raw values and may be off by one from the displayed ward/plot numbers)
- `MainScenario`: `{ CurrentQuestId, CurrentQuestName, NextQuestId, NextQuestName, LastCompletedQuestId, LastCompletedQuestName, CurrentExpansion, CompletedQuests, TotalQuests, AllComplete, Expansions }`, with `Expansions[]`: `{ ExpansionId, Name, CompletedQuests, TotalQuests }`
- `FateProgress[]`: `{ TabIndex, TerritoryTypeId, ZoneName, DisplayOrder, CurrentRank, MaxRank, FateProgress, NeededFates }` (FateProgress is the cumulative completed count toward the max rank, 66 total; NeededFates is the remainder to the next rank. These are the normalized values used since v1.12.4)
- `Buddy`: `{ Name, Rank, CurrentExp, MaxExp, SkillPoints, LevelUpReady }` (MaxExp is null when unknown. LevelUpReady is true when the EXP bar is full below the rank cap of 20 — feeding a Thavnairian Onion at a stable then raises the rank)

Notes for readers:

- Sections may be absent depending on the plugin version. **Treat a missing section as "not supported" and never as an error** (for example, MainScenario exists since v1.11.0, FateProgress since v1.12.0, Buddy since v1.13.0).
- Files are rewritten as a whole. A read can race a rewrite, so if parsing fails, wait briefly and read again.
- `Name` fields hold in-game display names and depend on the game language. Use ids such as ItemId for identity.

### Tell record format

`ffxiv-tells-<Character>-<World>.json`:

```json
{
  "Character": "…",
  "World": "…",
  "UpdatedAt": "2026-11-20T12:00:00+09:00",
  "Tells": [
    { "Id": "…", "At": "…", "Sender": "…", "SenderWorld": "…", "Message": "…" }
  ]
}
```

Keeps up to 200 entries in arrival order. `Id` is an identifier for de-duplication.

### Config downlink protocol

This is the mechanism for replacing another plugin's configuration from the Muse side. The application itself is performed by Muse Status Exporter (v1.8.0 or later), so the target plugin needs no changes.

#### Requests file (at the shared location)

```json
{
  "Version": 1,
  "Requests": [
    {
      "Id": "20261120-example01",
      "CreatedAt": "2026-11-20T12:00:00+09:00",
      "Target": "SomePlugin.json",
      "PluginName": "Some Plugin",
      "PluginInternalName": "SomePlugin",
      "Reload": true,
      "ExpectedSha256": null,
      "ContentBase64": "<base64 of the complete new file content>",
      "Status": "pending"
    }
  ]
}
```

| Field | Required | Description |
|---|---|---|
| `Id` | required | Request identifier: letters, digits and hyphens, 8–64 characters, starting with a letter or digit. Used to match the result |
| `CreatedAt` | optional | Creation time (ISO 8601) |
| `Target` | required | Path relative to the `pluginConfigs` folder (`.json` only). Traversal outside the folder is rejected. Muse Status Exporter's own folder cannot be targeted |
| `PluginName` | optional | Display name (carried into the results file) |
| `PluginInternalName` | optional | Internal name of the plugin to reload. Used when `Reload` is true |
| `Reload` | optional | When true and the target plugin is loaded, it is disabled, the file is replaced, and it is re-enabled so the change takes effect immediately. A plugin that is not loaded is never force-enabled (the change applies on its next load) |
| `ExpectedSha256` | optional | The SHA-256 (lowercase hex) of the target file as the sender last saw it. If the current file differs, the request is aborted instead of overwriting (`aborted-stale`). Null replaces unconditionally |
| `ContentBase64` | required | The complete replacement file content (must be valid JSON) |
| `Status` | required | The sender adds the request with `pending`. Later transitions are performed by the transport and the plugin |

#### State transitions

Request side (`Status` inside the requests file):

- `pending` → `delivered` (when the transport has delivered the envelope into `config-inbox\<Id>.json`; `DeliveredAt` is set)
- `pending` → `invalid` (when Id / Target / ContentBase64 is missing; a `Note` explains why)

Result side (`Results[]` in `ffxiv-config-results.json`; each entry is `{ Id, Target, PluginName, Status, Detail, At }`, up to 100 entries):

- `processing` — the plugin is applying the request (not a final state)
- `applied` — replaced and reloaded; the new content is in effect
- `applied-pending-load` — replaced, but the target plugin is not loaded, so it takes effect on its next load
- `applied-unverified` — replaced, but the effect could not be verified
- `aborted-stale` — `ExpectedSha256` did not match the current file, so nothing was overwritten
- `failed` — validation or writing failed (see `Detail`)
- `interrupted` — the plugin stopped mid-application; finalized as incomplete on the next start

#### Safety model

- Targets are confined to `.json` files under `pluginConfigs`; path traversal outside is rejected.
- Before replacing, the current file is backed up to `config-backups\<target path>\<timestamp>.json` (backups older than 30 days are purged).
- A request whose content is not valid JSON is never applied.
- Processed envelopes are moved to `config-inbox\done\`.

### Transport contract

The bundled uploader app (`uploader-app/`) carries the following. A replacement transport must honor the same file contract.

- Uploads `ffxiv-status-*.json`, `ffxiv-tells-*.json` and `ffxiv-config-results.json` from the plugin config folder to the shared location (same names, overwritten)
- Reads `ffxiv-config-requests.json` at the shared location, delivers `pending` requests into `config-inbox\`, updates their `Status`, and writes the file back (creates `{ "Version": 1, "Requests": [] }` when missing)
- Uploads the newest config snapshot zip (`FF14-pluginConfigs-*.zip`) under the same name (skips it if that name already exists)

Implementation commitments of the bundled uploader:

- Google Drive access uses only the `drive.file` scope (it can only touch files it created itself)
- Uploaded files are gathered into a Drive folder named `to_muse` (created by the uploader itself)
- The Google refresh token is stored only in a DPAPI-encrypted local file, never in the scripts or this repository

### Plugin-internal files (for reference)

The following are Muse Status Exporter's internal sidecars, not part of the integration boundary (their formats may change without notice): `mstatus-gcsupply-<Character>-<World>.json` (last captured GC supply list), `mstatus-fateprogress-<Character>-<World>.json` (last captured FATE progress), `mstatus-buddy-<Character>-<World>.json` (last known buddy max EXP), `mstatus-housing-<Character>-<World>.json` (housing visit history).

### Connecting your own plugin

- **To hand status data to Muse**: write JSON in the snapshot format above into your own plugin's config folder, named `ffxiv-status-<Character>-<World>.json`, and the bundled uploader will carry it as-is (include only the sections you need). Or, to consume existing snapshots, read the files at the shared location.
- **To let Muse change your plugin's settings**: no special implementation is needed. Any ordinary Dalamud plugin whose settings live in `pluginConfigs\<internal name>\*.json` can be a downlink target.
- **To build your own transport**: any mechanism that honors the file contract above (which files move, request delivery and status updates, carrying the results file back) can replace the bundled uploader.
