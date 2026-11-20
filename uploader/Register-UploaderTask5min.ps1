#Requires -Version 5.1
<#
.SYNOPSIS
    Upload-FfxivStatus.ps1 を5分ごとに実行するタスクスケジューラのタスクを登録します。
    テル記録ファイル（ffxiv-tells-*.json）を短い間隔で Google Drive へ届けるためのものです。
    実行ユーザー: 現在ログイン中のユーザー（DPAPI の復号に必要なため「ログオン中のみ実行」が前提）。
    既存の1時間ごとのタスク「FFXIV Status Uploader」はそのまま残ります（重なっても問題ありません）。
#>
$ErrorActionPreference = 'Stop'

$uploader = Join-Path $PSScriptRoot 'Upload-FfxivStatus.ps1'
if (-not (Test-Path $uploader)) {
    throw "Upload-FfxivStatus.ps1 が同じフォルダに見つかりません: $uploader"
}

# ウィンドウを一瞬も表示しないよう、wscript.exe + VBScript ラッパー経由で起動する
$wrapper = Join-Path $PSScriptRoot 'Run-UploaderHidden.vbs'
if (-not (Test-Path $wrapper)) {
    throw "Run-UploaderHidden.vbs が同じフォルダに見つかりません: $wrapper"
}

$action = New-ScheduledTaskAction -Execute 'wscript.exe' `
    -Argument "`"$wrapper`""
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) `
    -RepetitionInterval (New-TimeSpan -Minutes 5) `
    -RepetitionDuration (New-TimeSpan -Days 3650)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

Register-ScheduledTask -TaskName 'FFXIV Status Uploader 5min' `
    -Action $action -Trigger $trigger -Settings $settings `
    -Description 'FFXIVステータスJSONとテル記録JSONを Google Drive へ5分ごとにアップロードする（テル転送用）' `
    -Force | Out-Null

Write-Host 'タスク「FFXIV Status Uploader 5min」を登録しました（5分ごと、次回は約2分後に初回実行）。'
Write-Host 'タスクスケジューラで確認できます。不要になったら Unregister-ScheduledTask -TaskName ''FFXIV Status Uploader 5min'' で削除できます。'
