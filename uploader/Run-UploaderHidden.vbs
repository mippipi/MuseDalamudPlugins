' Run-UploaderHidden.vbs
' Launches Upload-FfxivStatus.ps1 (in the same folder) without showing any
' console window. Register the scheduled task with wscript.exe pointing at
' this file (Register-UploaderTask.ps1 does that) so the hourly upload no
' longer flashes a PowerShell window. The uploader writes its own log to
' uploader.log in this folder, so results stay checkable while hidden.
Option Explicit

Dim shell, fso, dir, ps1, cmd, rc
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

dir = fso.GetParentFolderName(WScript.ScriptFullName)
ps1 = dir & "\Upload-FfxivStatus.ps1"
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & ps1 & """"

' 0 = hidden window, True = wait for completion. Pass the exit code through
' so Task Scheduler reports the real result of the upload.
rc = shell.Run(cmd, 0, True)
WScript.Quit rc
