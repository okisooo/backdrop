# Builds Claude Backdrop and installs it for the current user:
#   %LOCALAPPDATA%\Programs\ClaudeBackdrop\ClaudeBackdrop.exe plus a Start menu shortcut.
# Settings stay in %LOCALAPPDATA%\ClaudeBackdrop. No administrator rights needed.
[CmdletBinding()]
param([switch]$NoStart)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeBackdrop'
$exe = Join-Path $installDir 'ClaudeBackdrop.exe'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Claude Backdrop.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

& (Join-Path $root 'build.ps1') | Out-Null

Get-Process ClaudeBackdrop -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force
    $_.WaitForExit(5000) | Out-Null
}

New-Item -ItemType Directory -Force $installDir | Out-Null
for ($attempt = 1; ; $attempt++) {
    try { Copy-Item (Join-Path $root 'artifacts\publish\ClaudeBackdrop.exe') $exe -Force; break }
    catch { if ($attempt -ge 10) { throw }; Start-Sleep -Milliseconds 300 }
}

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $installDir
$link.Description = 'Wallpaper behind the Claude desktop app'
$link.Save()

# Keep "Start with Windows" pointing at the installed copy if it was enabled.
if (Get-ItemProperty $runKey -Name ClaudeBackdrop -ErrorAction SilentlyContinue) {
    Set-ItemProperty $runKey -Name ClaudeBackdrop -Value "`"$exe`""
}

if (-not $NoStart) { Start-Process $exe }
Write-Host "Installed to $exe"
