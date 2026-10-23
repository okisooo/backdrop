# Builds Backdrop and installs it for the current user:
#   %LOCALAPPDATA%\Programs\Backdrop\Backdrop.exe plus a Start menu shortcut.
# Automatically migrates and replaces standalone ClaudeBackdrop, CodexBackdrop, and T3Backdrop instances.
[CmdletBinding()]
param([switch]$NoStart)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Backdrop'
$exe = Join-Path $installDir 'Backdrop.exe'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Backdrop.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

& (Join-Path $root 'build.ps1') | Out-Null

# Stop unified or legacy processes
foreach ($procName in @('Backdrop', 'ClaudeBackdrop', 'CodexBackdrop', 'T3Backdrop')) {
    Get-Process $procName -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        $_.WaitForExit(5000) | Out-Null
    }
}

# Remove legacy Run entries and Start Menu shortcuts
foreach ($legacy in @('ClaudeBackdrop', 'CodexBackdrop', 'T3Backdrop')) {
    if (Get-ItemProperty $runKey -Name $legacy -ErrorAction SilentlyContinue) {
        Remove-ItemProperty $runKey -Name $legacy -ErrorAction SilentlyContinue
    }
}
foreach ($legacyLnk in @('Claude Backdrop.lnk', 'Codex Backdrop.lnk', 'T3 Backdrop.lnk')) {
    $lnkPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$legacyLnk"
    if (Test-Path $lnkPath) { Remove-Item $lnkPath -Force -ErrorAction SilentlyContinue }
}

New-Item -ItemType Directory -Force $installDir | Out-Null
for ($attempt = 1; ; $attempt++) {
    try { Copy-Item (Join-Path $root 'artifacts\publish\Backdrop.exe') $exe -Force; break }
    catch { if ($attempt -ge 10) { throw }; Start-Sleep -Milliseconds 300 }
}

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $installDir
$link.Description = 'Wallpaper behind Claude, Codex, and T3 Code'
$link.Save()

Set-ItemProperty -Path $runKey -Name 'Backdrop' -Value "`"$exe`""

if (-not $NoStart) {
    try {
        $service = New-Object -ComObject "Schedule.Service"
        $service.Connect()
        $rootFolder = $service.GetFolder("\")
        $taskDef = $service.NewTask(0)
        $taskDef.Settings.Enabled = $true
        $taskDef.Settings.AllowDemandStart = $true
        $taskDef.Principal.LogonType = 3
        $action = $taskDef.Actions.Create(0)
        $action.Path = $exe
        $action.WorkingDirectory = $installDir
        $regTask = $rootFolder.RegisterTaskDefinition("LaunchBackdrop", $taskDef, 6, $null, $null, 3)
        $regTask.Run($null) | Out-Null
        Start-Sleep -Milliseconds 700
        $rootFolder.DeleteTask("LaunchBackdrop", 0)
    } catch {
        Start-Process explorer.exe "`"$exe`""
    }
}
Write-Host "Installed to $exe"