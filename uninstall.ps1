# Removes the per-user install. Settings in %LOCALAPPDATA%\CodexBackdrop are kept
# unless -RemoveSettings is given.
[CmdletBinding()]
param([switch]$RemoveSettings)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\CodexBackdrop'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Codex Backdrop.lnk'

Get-Process CodexBackdrop -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name CodexBackdrop -ErrorAction SilentlyContinue
if (Test-Path $shortcut) { Remove-Item $shortcut }
if (Test-Path $installDir) { Remove-Item $installDir -Recurse }
if ($RemoveSettings) {
    $settings = Join-Path $env:LOCALAPPDATA 'CodexBackdrop'
    if (Test-Path $settings) { Remove-Item $settings -Recurse }
}
Write-Host 'Codex Backdrop removed.'
