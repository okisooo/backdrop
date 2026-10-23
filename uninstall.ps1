[CmdletBinding()]
param([switch]$RemoveSettings)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Backdrop'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Backdrop.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process Backdrop -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force
    $_.WaitForExit(5000) | Out-Null
}

if (Get-ItemProperty $runKey -Name Backdrop -ErrorAction SilentlyContinue) {
    Remove-ItemProperty $runKey -Name Backdrop
}
if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
if ($RemoveSettings) {
    $settingsDir = Join-Path $env:LOCALAPPDATA 'Backdrop'
    if (Test-Path $settingsDir) { Remove-Item $settingsDir -Recurse -Force }
}
Write-Host "Uninstalled Backdrop"