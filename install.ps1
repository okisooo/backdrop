# Builds Codex Backdrop and installs it for the current user:
#   %LOCALAPPDATA%\Programs\CodexBackdrop\CodexBackdrop.exe plus a Start menu shortcut.
# Settings stay in %LOCALAPPDATA%\CodexBackdrop. No administrator rights needed.
[CmdletBinding()]
param([switch]$NoStart)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$programs = Join-Path $env:LOCALAPPDATA 'Programs'
$installDir = Join-Path $programs 'CodexBackdrop'
$exe = Join-Path $installDir 'CodexBackdrop.exe'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Codex Backdrop.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

& (Join-Path $root 'build.ps1') | Out-Null

Get-Process CodexBackdrop -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force
    $_.WaitForExit(5000) | Out-Null
}

# Earlier releases shipped a self-contained folder with a rollback copy beside it.
if (Test-Path (Join-Path $installDir 'SHA256SUMS.txt')) { Remove-Item $installDir -Recurse -Force }
foreach ($legacy in 'CodexBackdrop.last-known-good', 'CodexBackdrop.install-state.json', 'CodexBackdrop.install-journal.json') {
    $path = Join-Path $programs $legacy
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

New-Item -ItemType Directory -Force $installDir | Out-Null
for ($attempt = 1; ; $attempt++) {
    try { Copy-Item (Join-Path $root 'artifacts\publish\CodexBackdrop.exe') $exe -Force; break }
    catch { if ($attempt -ge 10) { throw }; Start-Sleep -Milliseconds 300 }
}

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $installDir
$link.Description = 'Wallpaper behind the Codex desktop app'
$link.Save()

# Keep "Start with Windows" pointing at the installed copy if it was enabled.
if (Get-ItemProperty $runKey -Name CodexBackdrop -ErrorAction SilentlyContinue) {
    Set-ItemProperty $runKey -Name CodexBackdrop -Value "`"$exe`""
}

if (-not $NoStart) { Start-Process $exe }
Write-Host "Installed to $exe"
