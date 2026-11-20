#Requires -Version 5.1
<#
.SYNOPSIS
    FFXIVステータスJSON（自作プラグイン Muse Status Exporter が書き出したもの）を
    Google Drive へアップロードするスクリプト。

.DESCRIPTION
    初回セットアップ（-Setup）:
      ダウンロード済みの OAuth クライアント JSON を読み、ブラウザで Google の同意を
      受けてリフレッシュトークンを取得し、クライアント情報とトークンを
      Windows DPAPI で暗号化して ffxiv-uploader-cred.xml に保存します（平文では保存しません）。

    通常実行（引数なし）:
      暗号化ファイルからトークンを復号してアクセストークンを取得し、
      プラグインが出力したキャラ別ファイル（ffxiv-status-<キャラ名>-<ワールド>.json）を
      すべて、Drive 上の同名ファイルへ上書きアップロードします（無ければ新規作成します）。
      キャラが増えてもファイルが自動で増えるため、設定変更は不要です。
      テル記録ファイル（ffxiv-tells-<キャラ名>-<ワールド>.json、プラグイン v1.4.0 以降）も
      同じフォルダからあわせてアップロードします。
      タスクスケジューラから1時間ごとに実行される想定です（テル転送用に5分間隔の
      タスク「FFXIV Status Uploader 5min」からも同じスクリプトが実行されます）。

    v5 の追加分（設定ダウンリンク）:
      プラグイン v1.8.0 の適用結果ファイル（ffxiv-config-results.json）も
      あわせてアップロードします。さらに、Drive 上の設定リクエストファイル
      （ffxiv-config-requests.json、無ければ空で新規作成します）を読み、
      Status が "pending" のリクエストをプラグインの受信フォルダ
      （pluginConfigs\MuseStatusExporter\config-inbox）へ配信し、
      Status を "delivered" に更新して Drive へ書き戻します。
      実際の適用（検証・バックアップ・プラグイン再読み込み・結果記録）は
      ゲーム内のプラグイン側が行います。

    v6 の追加分（to_muse フォルダへの集約）:
      送信ファイルが増えてきたため、Drive のルートではなく「to_muse」フォルダへ
      集約します（ユーザーの指示、2026-11-20）。フォルダはこのスクリプト自身が
      作成します（drive.file スコープでは、他のアプリが作ったフォルダは見えません）。
      フォルダ ID は to-muse-folder.json に保存して再利用します。
      新規アップロードは最初からフォルダ内へ作成し、既存ファイルはフォルダへ
      移動します。過去にルートへ送ったファイル（ffxiv-* / FF14-pluginConfigs-light-*）も
      毎回の実行で走査して、フォルダ外に残っていれば移動します。
      フォルダの解決に失敗した場合は、従来どおりルートへのアップロードに
      フォールバックします（アップロード自体は止めません）。

    使用スコープは https://www.googleapis.com/auth/drive.file のみ
    （このアプリ自身が作成したファイルにしかアクセスできません）。

.PARAMETER Setup
    初回セットアップモードで実行します。

.PARAMETER Manual
    セットアップ時に、ループバックでのコード受信ではなく、許可後にブラウザの
    アドレスバーへ表示されるリダイレクト先 URL をコンソールへ貼り付ける方式を
    使います。同意画面が進まない場合や、スマートフォン等の別ブラウザで
    許可したい場合に -Setup と併せて指定します。

.PARAMETER ClientJson
    セットアップ時に使う、Google Cloud からダウンロードしたクライアント JSON のパス。
    省略時はダウンロードフォルダから client_secret_*.json の最新を自動で探します。

.PARAMETER StatusFile
    アップロードする JSON のパス（単体指定、テスト用）。省略時はプラグインの
    標準出力フォルダにある ffxiv-status-*.json をすべてアップロードします。

.PARAMETER ConfigDir
    Collect-DalamudConfig.ps1 の出力先フォルダ。省略時は
    ドキュメント\FF14-ConfigAsCode。このフォルダにある軽量zip
    （FF14-pluginConfigs-light-*.zip）の最新1件もあわせてアップロードします
    （同名が Drive にあれば送信済みとしてスキップ）。

.PARAMETER ApiBase
    テスト・診断用に API の接続先を差し替えるための内部パラメータ。通常は指定しません。
#>
[CmdletBinding()]
param(
    [switch]$Setup,
    [switch]$Manual,
    [string]$ClientJson = '',
    [string]$StatusFile = '',
    [string]$ConfigDir = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'FF14-ConfigAsCode'),
    [string]$ApiBase = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:CredPath  = Join-Path $script:ScriptDir 'ffxiv-uploader-cred.xml'
$script:LogPath   = Join-Path $script:ScriptDir 'uploader.log'
$script:Scope = 'https://www.googleapis.com/auth/drive.file'
$script:PluginDir = Join-Path $env:APPDATA 'XIVLauncher\pluginConfigs\MuseStatusExporter'
$script:RequestsFileName = 'ffxiv-config-requests.json'
$script:ToMuseFolderName = 'to_muse'
$script:FolderStatePath = Join-Path $script:ScriptDir 'to-muse-folder.json'
$script:ToMuseFolderId = $null

if ($ApiBase) {
    $script:TokenUri  = "$ApiBase/token"
    $script:FilesUri  = "$ApiBase/drive/v3/files"
    $script:UploadUri = "$ApiBase/upload/drive/v3/files"
} else {
    $script:TokenUri  = 'https://oauth2.googleapis.com/token'
    $script:FilesUri  = 'https://www.googleapis.com/drive/v3/files'
    $script:UploadUri = 'https://www.googleapis.com/upload/drive/v3/files'
}

function Write-Log {
    param([string]$Message)
    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line
    try { Add-Content -Path $script:LogPath -Value $line -Encoding UTF8 } catch { }
}

function ConvertTo-PlainText {
    param([System.Security.SecureString]$Secure)
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Save-Credentials {
    param([string]$ClientId, [string]$ClientSecret, [string]$RefreshToken)
    $data = @{
        ClientId     = $ClientId
        ClientSecret = (ConvertTo-SecureString $ClientSecret -AsPlainText -Force)
        RefreshToken = (ConvertTo-SecureString $RefreshToken -AsPlainText -Force)
    }
    # Export-Clixml は SecureString を現在の Windows ユーザーに紐づく DPAPI で暗号化して保存する
    $data | Export-Clixml -Path $script:CredPath
}

function Load-Credentials {
    $data = Import-Clixml -Path $script:CredPath
    return @{
        ClientId     = [string]$data.ClientId
        ClientSecret = (ConvertTo-PlainText $data.ClientSecret)
        RefreshToken = (ConvertTo-PlainText $data.RefreshToken)
    }
}

function Invoke-TokenRequest {
    param([hashtable]$Body)
    return Invoke-RestMethod -Method Post -Uri $script:TokenUri -Body $Body `
        -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 60
}

function Get-ErrorDetail {
    param($ErrorRecord)
    $detail = ''
    try {
        if ($ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) {
            $detail = [string]$ErrorRecord.ErrorDetails.Message
        }
    } catch { }
    if ($detail) { return $detail }
    return [string]$ErrorRecord.Exception.Message
}

# ---------------------------------------------------------------------------
# 初回セットアップ
# ---------------------------------------------------------------------------
function Invoke-Setup {
    # 1) クライアント JSON を特定する
    $jsonPath = $ClientJson
    if (-not $jsonPath) {
        $downloads = Join-Path $env:USERPROFILE 'Downloads'
        $found = Get-ChildItem -Path $downloads -Filter 'client_secret_*.json' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($found) { $jsonPath = $found.FullName }
    }
    if (-not $jsonPath -or -not (Test-Path $jsonPath)) {
        throw 'クライアント JSON が見つかりません。-ClientJson でパスを指定してください。'
    }
    Write-Log "クライアント JSON: $jsonPath"

    $parsed = Get-Content -Path $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $client = $null
    if ($parsed.PSObject.Properties.Name -contains 'installed') { $client = $parsed.installed }
    elseif ($parsed.PSObject.Properties.Name -contains 'web') { $client = $parsed.web }
    if (-not $client) { throw 'クライアント JSON の形式が想定と違います（installed / web キーがありません）。' }
    $clientId = [string]$client.client_id
    $clientSecret = [string]$client.client_secret

    # 2) ループバック待受ポートを決める
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $port = $probe.LocalEndpoint.Port
    $probe.Stop()
    $redirectUri = "http://127.0.0.1:$port/"

    # 3) 認可 URL を組み立ててブラウザで開く
    $query = @(
        'client_id='     + [uri]::EscapeDataString($clientId)
        'redirect_uri='  + [uri]::EscapeDataString($redirectUri)
        'response_type=code'
        'scope='         + [uri]::EscapeDataString($script:Scope)
        'access_type=offline'
        'prompt=consent'
    ) -join '&'
    $authUrl = 'https://accounts.google.com/o/oauth2/v2/auth?' + $query

    Write-Host ''
    Write-Host 'ブラウザで Google の同意画面を開きます。対象アカウントでログインし、許可してください。'
    Write-Host '自動で開かない場合は、次の URL をブラウザに貼り付けてください:'
    Write-Host $authUrl
    Write-Host ''
    try { Start-Process $authUrl } catch { Write-Host '（ブラウザの自動起動に失敗しました。上記 URL を手動で開いてください）' }

    $code = $null
    $authError = $null
    if ($Manual) {
        # 4a) 手動モード: 許可後にアドレスバーへ表示されるリダイレクト先 URL を貼ってもらう
        Write-Host '【手動モード】'
        Write-Host 'スマートフォンなど別のブラウザで上の URL を開いて許可しても構いません（対象の Google アカウントでログインしてください）。'
        Write-Host '許可すると、ブラウザは 127.0.0.1 のページを開こうとして「接続できません」と表示されますが、問題ありません。'
        Write-Host 'そのときアドレスバーに表示されている URL 全体をコピーして、下に貼り付けてください。'
        $pasted = Read-Host 'リダイレクト先 URL を貼り付け'
        if ($pasted -match '[?&]error=([^&#\s]+)') {
            $authError = [uri]::UnescapeDataString($Matches[1])
        } elseif ($pasted -match '[?&]code=([^&#\s]+)') {
            $code = [uri]::UnescapeDataString($Matches[1])
        } elseif ($pasted.Trim().Length -ge 10 -and $pasted -notmatch '\s') {
            $code = $pasted.Trim()
        }
        if (-not $code -and -not $authError) {
            throw '貼り付けた内容から認可コードを取り出せませんでした。URL 全体を貼り付けて、もう一度 -Setup -Manual を実行してください。'
        }
    } else {
    # 4b) ループバックで認可コードを受け取る（関係ない接続は読み飛ばす）
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
    $listener.Start()
    try {
        $deadline = (Get-Date).AddSeconds(900)
        while ((Get-Date) -lt $deadline -and -not $code -and -not $authError) {
            $acceptTask = $listener.AcceptTcpClientAsync()
            if (-not $acceptTask.Wait(5000)) { continue }
            $tcp = $acceptTask.Result
            try {
                $stream = $tcp.GetStream()
                $reader = [System.IO.StreamReader]::new($stream)
                $requestLine = $reader.ReadLine()
                while ($true) {
                    $headerLine = $reader.ReadLine()
                    if ([string]::IsNullOrEmpty($headerLine)) { break }
                }
                if ($requestLine -match '^[A-Z]+ ([^ ]+) ') {
                    $target = $Matches[1]
                    if ($target -match '[?&]code=([^&]+)') {
                        $code = [uri]::UnescapeDataString($Matches[1])
                    } elseif ($target -match '[?&]error=([^&]+)') {
                        $authError = [uri]::UnescapeDataString($Matches[1])
                    }
                }
                $html = '<html><head><meta charset="utf-8"></head><body><p>認証が完了しました。このタブを閉じて、コンソールに戻ってください。</p></body></html>'
                $htmlBytes = [System.Text.Encoding]::UTF8.GetBytes($html)
                $header = "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($htmlBytes.Length)`r`nConnection: close`r`n`r`n"
                $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($header)
                $stream.Write($headerBytes, 0, $headerBytes.Length)
                $stream.Write($htmlBytes, 0, $htmlBytes.Length)
                $stream.Flush()
            } finally {
                $tcp.Close()
            }
        }
    } finally {
        $listener.Stop()
    }
    }

    if ($authError) { throw "Google 側で認可が拒否されました: $authError" }
    if (-not $code) { throw '認可コードを受け取れませんでした（タイムアウト）。もう一度 -Setup を実行してください。' }

    # 5) コードをトークンに交換する
    $token = Invoke-TokenRequest -Body @{
        code          = $code
        client_id     = $clientId
        client_secret = $clientSecret
        redirect_uri  = $redirectUri
        grant_type    = 'authorization_code'
    }
    if ($token.PSObject.Properties.Name -notcontains 'refresh_token') {
        throw 'リフレッシュトークンが発行されませんでした。同意画面で許可し直してから、もう一度 -Setup を実行してください。'
    }

    # 6) DPAPI で暗号化して保存する
    Save-Credentials -ClientId $clientId -ClientSecret $clientSecret -RefreshToken ([string]$token.refresh_token)
    Write-Log "セットアップ完了。暗号化した認証情報を保存しました: $script:CredPath"
    Write-Host ''
    Write-Host '次の手順:'
    Write-Host '  1) ダウンロードしたクライアント JSON は削除して構いません（認証情報は暗号化ファイル側に保存済み）。'
    Write-Host '  2) Register-UploaderTask.ps1 を実行すると、1時間ごとの自動アップロードが登録されます。'
    Write-Host '  3) まず一度、このスクリプトを引数なしで実行して初回アップロードを確認してください。'
}

# ---------------------------------------------------------------------------
# 通常実行（アップロード）
# ---------------------------------------------------------------------------
function Get-AccessHeaders {
    param([hashtable]$Cred)

    # アクセストークンを取得
    try {
        $token = Invoke-TokenRequest -Body @{
            client_id     = $Cred.ClientId
            client_secret = $Cred.ClientSecret
            refresh_token = $Cred.RefreshToken
            grant_type    = 'refresh_token'
        }
    } catch {
        Write-Log ('アクセストークンの取得に失敗しました: ' + (Get-ErrorDetail $_))
        Write-Log 'リフレッシュトークンが失効している可能性があります。その場合は -Setup を再実行してください。'
        exit 1
    }
    if ($token.PSObject.Properties.Name -notcontains 'access_token') {
        Write-Log 'アクセストークンの取得に失敗しました（応答に access_token がありません）。-Setup を再実行してください。'
        exit 1
    }
    return @{ Authorization = "Bearer $($token.access_token)" }
}

# ---------------------------------------------------------------------------
# to_muse フォルダ（v6: 送信ファイルの集約先）
# ---------------------------------------------------------------------------
function Save-ToMuseFolderId {
    param([string]$FolderId)
    try {
        $json = @{ FolderId = $FolderId } | ConvertTo-Json -Compress
        [System.IO.File]::WriteAllText($script:FolderStatePath, $json, [System.Text.UTF8Encoding]::new($false))
    } catch { }
}

function Get-ToMuseFolderId {
    param([hashtable]$Headers)

    if ($script:ToMuseFolderId) { return $script:ToMuseFolderId }

    try {
        # 1) 保存済みのフォルダ ID が生きていれば使う
        if (Test-Path $script:FolderStatePath) {
            $saved = Get-Content -Path $script:FolderStatePath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($saved -and ($saved.PSObject.Properties.Name -contains 'FolderId') -and [string]$saved.FolderId) {
                $savedId = [string]$saved.FolderId
                try {
                    $fields = [uri]::EscapeDataString('id,mimeType,trashed')
                    $info = Invoke-RestMethod -Method Get -Uri "$($script:FilesUri)/$savedId`?fields=$fields" -Headers $Headers -TimeoutSec 60
                    if ($info -and [string]$info.mimeType -eq 'application/vnd.google-apps.folder' -and -not $info.trashed) {
                        $script:ToMuseFolderId = $savedId
                        return $savedId
                    }
                } catch { }
            }
        }

        # 2) 同名フォルダを探す（drive.file スコープなので自アプリ作成分だけが見える）
        $q = [uri]::EscapeDataString("name = '$($script:ToMuseFolderName)' and mimeType = 'application/vnd.google-apps.folder' and trashed = false")
        $fields = [uri]::EscapeDataString('files(id)')
        $listUri = "$($script:FilesUri)?q=$q&fields=$fields&pageSize=1"
        $list = Invoke-RestMethod -Method Get -Uri $listUri -Headers $Headers -TimeoutSec 60
        if ($list.files -and $list.files.Count -gt 0) {
            $foundId = [string]$list.files[0].id
            $script:ToMuseFolderId = $foundId
            Save-ToMuseFolderId -FolderId $foundId
            return $foundId
        }

        # 3) 無ければ作成する
        $meta = @{ name = $script:ToMuseFolderName; mimeType = 'application/vnd.google-apps.folder' } | ConvertTo-Json -Compress
        $created = Invoke-RestMethod -Method Post -Uri $script:FilesUri -Headers $Headers `
            -Body $meta -ContentType 'application/json; charset=UTF-8' -TimeoutSec 60
        $newId = [string]$created.id
        $script:ToMuseFolderId = $newId
        Save-ToMuseFolderId -FolderId $newId
        Write-Log "集約先フォルダを作成しました: $($script:ToMuseFolderName) id=$newId"
        return $newId
    } catch {
        Write-Log ('集約先フォルダの解決に失敗したため、ルートへアップロードします: ' + (Get-ErrorDetail $_))
        return $null
    }
}

function Ensure-InToMuseFolder {
    param([string]$FileId, [hashtable]$Headers)

    if (-not $script:ToMuseFolderId) { return }
    try {
        $fields = [uri]::EscapeDataString('parents')
        $info = Invoke-RestMethod -Method Get -Uri "$($script:FilesUri)/$FileId`?fields=$fields" -Headers $Headers -TimeoutSec 60
        $parents = @()
        if ($info -and ($info.PSObject.Properties.Name -contains 'parents') -and $info.parents) {
            $parents = @($info.parents | ForEach-Object { [string]$_ })
        }
        if ($parents -contains $script:ToMuseFolderId) { return }
        $moveUri = "$($script:FilesUri)/$FileId`?addParents=$($script:ToMuseFolderId)"
        if ($parents.Count -gt 0) {
            $moveUri += '&removeParents=' + ($parents -join ',')
        }
        Invoke-RestMethod -Method Patch -Uri $moveUri -Headers $Headers `
            -Body '{}' -ContentType 'application/json' -TimeoutSec 60 | Out-Null
    } catch {
        Write-Log ('フォルダへの移動に失敗しました（次回再試行します）: ' + (Get-ErrorDetail $_))
    }
}

function Invoke-MoveStrayFiles {
    param([hashtable]$Headers)

    if (-not $script:ToMuseFolderId) { return }
    try {
        $q = [uri]::EscapeDataString("(name contains 'ffxiv-' or name contains 'FF14-pluginConfigs-light') and trashed = false")
        $fields = [uri]::EscapeDataString('files(id,name,parents)')
        $listUri = "$($script:FilesUri)?q=$q&fields=$fields&pageSize=100"
        $list = Invoke-RestMethod -Method Get -Uri $listUri -Headers $Headers -TimeoutSec 60
        if (-not $list.files) { return }
        foreach ($f in @($list.files)) {
            $parents = @()
            if ($f.PSObject.Properties.Name -contains 'parents' -and $f.parents) {
                $parents = @($f.parents | ForEach-Object { [string]$_ })
            }
            if ($parents -notcontains $script:ToMuseFolderId) {
                Ensure-InToMuseFolder -FileId ([string]$f.id) -Headers $Headers
                Write-Log "フォルダへ移動しました: $([string]$f.name)"
            }
        }
    } catch {
        Write-Log ('フォルダ外ファイルの走査に失敗しました: ' + (Get-ErrorDetail $_))
    }
}

function Invoke-UploadOneFile {
    param([string]$LocalPath, [string]$RemoteName, [hashtable]$Headers,
          [string]$ContentType = 'application/json', [switch]$SkipIfExists)

    # 既存ファイルを探す（drive.file スコープなので、このアプリが作ったファイルだけが見える）
    $q = [uri]::EscapeDataString("name = '$RemoteName' and trashed = false")
    $fields = [uri]::EscapeDataString('files(id,modifiedTime)')
    $order = [uri]::EscapeDataString('modifiedTime desc')
    $listUri = "$($script:FilesUri)?q=$q&fields=$fields&orderBy=$order&pageSize=1"
    $list = Invoke-RestMethod -Method Get -Uri $listUri -Headers $Headers -TimeoutSec 60

    $bytes = [System.IO.File]::ReadAllBytes($LocalPath)

    if ($list.files -and $list.files.Count -gt 0) {
        $fileId = [string]$list.files[0].id
        if ($SkipIfExists) {
            Write-Log "送信済みのためスキップします: $RemoteName"
        } else {
            $updateUri = "$($script:UploadUri)/$fileId`?uploadType=media"
            $result = Invoke-RestMethod -Method Patch -Uri $updateUri -Headers $Headers `
                -Body $bytes -ContentType $ContentType -TimeoutSec 120
            Write-Log "アップロード完了（更新）: $RemoteName id=$($result.id)"
        }
        Ensure-InToMuseFolder -FileId $fileId -Headers $Headers
        return
    } else {
        $boundary = 'ffxiv-' + [guid]::NewGuid().ToString('N')
        $metaHash = @{ name = $RemoteName; mimeType = $ContentType }
        if ($script:ToMuseFolderId) {
            $metaHash['parents'] = @($script:ToMuseFolderId)
        }
        $meta = $metaHash | ConvertTo-Json -Compress
        $ms = [System.IO.MemoryStream]::new()
        $crlf = "`r`n"
        $pre = "--$boundary$crlf" +
               "Content-Type: application/json; charset=UTF-8$crlf$crlf" +
               "$meta$crlf" +
               "--$boundary$crlf" +
               "Content-Type: $ContentType$crlf$crlf"
        $preBytes = [System.Text.Encoding]::UTF8.GetBytes($pre)
        $ms.Write($preBytes, 0, $preBytes.Length)
        $ms.Write($bytes, 0, $bytes.Length)
        $postBytes = [System.Text.Encoding]::UTF8.GetBytes("$crlf--$boundary--$crlf")
        $ms.Write($postBytes, 0, $postBytes.Length)

        $createUri = "$($script:UploadUri)?uploadType=multipart"
        $result = Invoke-RestMethod -Method Post -Uri $createUri -Headers $Headers `
            -Body $ms.ToArray() -ContentType "multipart/related; boundary=$boundary" -TimeoutSec 120
        Write-Log "アップロード完了（新規作成）: $RemoteName id=$($result.id)"
    }
}

function Invoke-Upload {
    if (-not (Test-Path $script:CredPath)) {
        Write-Log '認証情報ファイルがありません。先に -Setup を実行してください。'
        exit 1
    }

    $cred = Load-Credentials
    $headers = Get-AccessHeaders -Cred $cred

    # v6: 集約先フォルダを解決する（失敗時は $null のままでルート運用にフォールバック）
    $script:ToMuseFolderId = Get-ToMuseFolderId -Headers $headers

    Invoke-UploadFiles -Headers $headers
    Invoke-ConfigRequests -Headers $headers
    Invoke-MoveStrayFiles -Headers $headers
}

function Invoke-UploadFiles {
    param([hashtable]$Headers)

    # アップロード対象を決める（キャラ別ファイルをすべて対象にするので、
    # キャラが増えて新しいファイルができても設定変更なしで自動対応する）
    $targets = @()
    if ($StatusFile) {
        if (Test-Path $StatusFile) {
            $targets += Get-Item $StatusFile
        } else {
            Write-Log "ステータスファイルが無いためスキップします: $StatusFile"
            return
        }
    } else {
        $statusDir = $script:PluginDir
        $targets = @(Get-ChildItem -Path $statusDir -Filter 'ffxiv-status-*.json' -File -ErrorAction SilentlyContinue) +
                   @(Get-ChildItem -Path $statusDir -Filter 'ffxiv-tells-*.json' -File -ErrorAction SilentlyContinue)
        $targets = @($targets | Sort-Object Name)
        # 設定ダウンリンクの適用結果（プラグイン v1.8.0 以降）も対象にする
        $resultsFile = Join-Path $statusDir 'ffxiv-config-results.json'
        if (Test-Path $resultsFile) {
            $targets += Get-Item $resultsFile
        }
        if ($targets.Count -eq 0) {
            Write-Log "対象ファイルが無いためスキップします: $statusDir\ffxiv-status-*.json / ffxiv-tells-*.json"
        }
    }

    # Dalamud設定の軽量zip（Collect-DalamudConfig.ps1 の出力）の最新1件も対象にする。
    # タイムスタンプ付きで作られた後は変わらないため、同名が Drive にあれば送信済みとして扱う。
    # フル共有zip（FF14-pluginConfigs-share-*.zip）は対象にしない。
    $lightZip = $null
    if (-not $StatusFile) {
        $lightZip = Get-ChildItem -Path $ConfigDir -Filter 'FF14-pluginConfigs-light-*.zip' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $lightZip) {
            Write-Log "設定の軽量zipが無いためスキップします: $ConfigDir\FF14-pluginConfigs-light-*.zip"
        }
    }

    if ($targets.Count -eq 0 -and -not $lightZip) {
        return
    }

    foreach ($file in $targets) {
        Invoke-UploadOneFile -LocalPath $file.FullName -RemoteName $file.Name -Headers $headers
    }
    if ($lightZip) {
        Invoke-UploadOneFile -LocalPath $lightZip.FullName -RemoteName $lightZip.Name -Headers $headers `
            -ContentType 'application/zip' -SkipIfExists
    }
}

# ---------------------------------------------------------------------------
# 設定ダウンリンク（Drive → プラグインの受信フォルダ）
# ---------------------------------------------------------------------------
function Set-ObjectProperty {
    param($Obj, [string]$Name, [string]$Value)
    if ($Obj.PSObject.Properties.Name -contains $Name) {
        $Obj.$Name = $Value
    } else {
        $Obj | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Invoke-ConfigRequests {
    param([hashtable]$Headers)

    if ($StatusFile) {
        # 単体テスト指定時はダウンリンクを行わない
        return
    }

    $remoteName = $script:RequestsFileName

    # 既存のリクエストファイルを探す（drive.file スコープなので、このアプリが作ったファイルだけが見える）
    $q = [uri]::EscapeDataString("name = '$remoteName' and trashed = false")
    $fields = [uri]::EscapeDataString('files(id,modifiedTime)')
    $order = [uri]::EscapeDataString('modifiedTime desc')
    $listUri = "$($script:FilesUri)?q=$q&fields=$fields&orderBy=$order&pageSize=1"
    $list = Invoke-RestMethod -Method Get -Uri $listUri -Headers $Headers -TimeoutSec 60

    if (-not $list.files -or $list.files.Count -eq 0) {
        # 無ければ空のリクエストファイルを作る（Muse 側がこのファイルへリクエストを書く）
        $empty = @{ Version = 1; Requests = @() } | ConvertTo-Json -Depth 5
        $tmpCreate = Join-Path $script:ScriptDir 'ffxiv-config-requests.tmp.json'
        [System.IO.File]::WriteAllText($tmpCreate, $empty, [System.Text.UTF8Encoding]::new($false))
        try {
            Invoke-UploadOneFile -LocalPath $tmpCreate -RemoteName $remoteName -Headers $Headers
        } finally {
            Remove-Item $tmpCreate -ErrorAction SilentlyContinue
        }
        Write-Log '設定リクエストファイルが無かったため、空で作成しました。'
        return
    }

    $fileId = [string]$list.files[0].id
    $downloadUri = "$($script:FilesUri)/$fileId`?alt=media"
    $doc = Invoke-RestMethod -Method Get -Uri $downloadUri -Headers $Headers -TimeoutSec 60
    if ($null -eq $doc -or $doc.PSObject.Properties.Name -notcontains 'Requests') {
        Write-Log '設定リクエストファイルの形式が想定と違うため、ダウンリンクをスキップします。'
        return
    }

    $inboxDir = Join-Path $script:PluginDir 'config-inbox'
    $changed = $false
    $delivered = 0
    foreach ($req in @($doc.Requests)) {
        if ($null -eq $req) { continue }
        $status = ''
        if ($req.PSObject.Properties.Name -contains 'Status') { $status = [string]$req.Status }
        if ($status -ne 'pending') { continue }

        $id = ''
        if ($req.PSObject.Properties.Name -contains 'Id') { $id = [string]$req.Id }
        $valid = ($id -match '^[A-Za-z0-9][A-Za-z0-9-]{7,63}$') -and
                 ($req.PSObject.Properties.Name -contains 'Target') -and
                 ($req.PSObject.Properties.Name -contains 'ContentBase64')
        if (-not $valid) {
            Set-ObjectProperty $req 'Status' 'invalid'
            Set-ObjectProperty $req 'Note' 'Id / Target / ContentBase64 が不足しているため配信できませんでした。'
            $changed = $true
            Write-Log "設定リクエストが不正なため invalid にしました: id=$id"
            continue
        }

        if (-not (Test-Path $inboxDir)) {
            New-Item -ItemType Directory -Path $inboxDir -Force | Out-Null
        }
        $envelopeJson = $req | ConvertTo-Json -Depth 10
        $tmpPath = Join-Path $inboxDir ($id + '.tmp')
        $finalPath = Join-Path $inboxDir ($id + '.json')
        [System.IO.File]::WriteAllText($tmpPath, $envelopeJson, [System.Text.UTF8Encoding]::new($false))
        Move-Item -Path $tmpPath -Destination $finalPath -Force

        Set-ObjectProperty $req 'Status' 'delivered'
        Set-ObjectProperty $req 'DeliveredAt' (Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz')
        $changed = $true
        $delivered++
        Write-Log "設定リクエストを配信しました: id=$id target=$([string]$req.Target)"
    }

    if ($changed) {
        $outJson = $doc | ConvertTo-Json -Depth 10
        $tmpUpdate = Join-Path $script:ScriptDir 'ffxiv-config-requests.tmp.json'
        [System.IO.File]::WriteAllText($tmpUpdate, $outJson, [System.Text.UTF8Encoding]::new($false))
        try {
            Invoke-UploadOneFile -LocalPath $tmpUpdate -RemoteName $remoteName -Headers $Headers
        } finally {
            Remove-Item $tmpUpdate -ErrorAction SilentlyContinue
        }
    }
    if ($delivered -gt 0) {
        Write-Log "設定リクエスト配信: 合計 $delivered 件"
    }
}

# ---------------------------------------------------------------------------
try {
    if ($Setup) { Invoke-Setup } else { Invoke-Upload }
} catch {
    Write-Log ('エラー: ' + (Get-ErrorDetail $_))
    exit 1
}
