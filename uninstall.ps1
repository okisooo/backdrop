# Removes the per-user install. Settings in %LOCALAPPDATA%\ClaudeBackdrop are kept
# unless -RemoveSettings is given.
[CmdletBinding()]
param([switch]$RemoveSettings)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeBackdrop'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Claude Backdrop.lnk'

Get-Process ClaudeBackdrop -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ClaudeBackdrop -ErrorAction SilentlyContinue
if (Test-Path $shortcut) { Remove-Item $shortcut }
if (Test-Path $installDir) { Remove-Item $installDir -Recurse }
if ($RemoveSettings) {
    $settings = Join-Path $env:LOCALAPPDATA 'ClaudeBackdrop'
    if (Test-Path $settings) { Remove-Item $settings -Recurse }
}
Write-Host 'Claude Backdrop removed.'
