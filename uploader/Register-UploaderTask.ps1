#Requires -Version 5.1
<#
.SYNOPSIS
    Upload-FfxivStatus.ps1 を1時間ごとに実行するタスクスケジューラのタスクを登録します。
    実行ユーザー: 現在ログイン中のユーザー（DPAPI の復号に必要なため「ログオン中のみ実行」が前提）。
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
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) `
    -RepetitionInterval (New-TimeSpan -Hours 1) `
    -RepetitionDuration (New-TimeSpan -Days 3650)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

Register-ScheduledTask -TaskName 'FFXIV Status Uploader' `
    -Action $action -Trigger $trigger -Settings $settings `
    -Description 'FFXIVステータスJSON（ffxiv-status.json）を Google Drive へ1時間ごとにアップロードする' `
    -Force | Out-Null

Write-Host 'タスク「FFXIV Status Uploader」を登録しました（1時間ごと、次回は約5分後に初回実行）。'
Write-Host 'タスクスケジューラで確認できます。不要になったら Unregister-ScheduledTask -TaskName ''FFXIV Status Uploader'' で削除できます。'
