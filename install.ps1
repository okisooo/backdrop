# Builds Backdrop, installs it to %LOCALAPPDATA%\Programs\Backdrop, adds a Start menu
# shortcut, migrates legacy ClaudeBackdrop / CodexBackdrop / T3Backdrop startup entries,
# and starts the unified tray app. No administrator rights needed.
param([switch]$NoStart)
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'build.ps1')
$builtExe = Join-Path $PSScriptRoot 'artifacts\publish\Backdrop.exe'
if (-not (Test-Path $builtExe)) { throw "Build output not found at $builtExe" }

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Backdrop'
$exe = Join-Path $installDir 'Backdrop.exe'
$stagedExe = Join-Path $installDir 'Backdrop.exe.new'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item $builtExe $stagedExe -Force

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
$runProps = Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue

# Preserve existing startup preference if already installed; otherwise default to enabled
$hadExistingInstall = Test-Path $exe
$hadStartup = ($null -ne $runProps.Backdrop) -or ($null -ne $runProps.ClaudeBackdrop) -or ($null -ne $runProps.CodexBackdrop) -or ($null -ne $runProps.T3Backdrop)
$enableStartup = (-not $hadExistingInstall) -or $hadStartup

# Stop running Backdrop or legacy standalone *Backdrop processes before swapping the executable
foreach ($procName in @('Backdrop', 'ClaudeBackdrop', 'CodexBackdrop', 'T3Backdrop')) {
    $procs = Get-Process $procName -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        $procs | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
    }
}
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    try {
        [System.IO.File]::Move($stagedExe, $exe, $true)
        break
    } catch {
        if ($attempt -eq 9) { throw }
        Start-Sleep -Milliseconds 250
    }
}

# Retire legacy standalone shortcuts and startup entries now that the new binary is in place
$programs = [Environment]::GetFolderPath('Programs')
foreach ($legacyShortcut in @('Claude Backdrop.lnk', 'Codex Backdrop.lnk', 'T3 Backdrop.lnk')) {
    Remove-Item (Join-Path $programs $legacyShortcut) -Force -ErrorAction SilentlyContinue
}
foreach ($legacyRun in @('ClaudeBackdrop', 'CodexBackdrop', 'T3Backdrop')) {
    Remove-ItemProperty -Path $runKey -Name $legacyRun -ErrorAction SilentlyContinue
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $programs 'Backdrop.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'Custom wallpaper backdrop for Claude, Codex, and T3 Code'
$shortcut.Save()

if ($enableStartup) {
    Set-ItemProperty -Path $runKey -Name 'Backdrop' -Value "`"$exe`""
}

Write-Host "Installed to $exe"
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
        try {
            $regTask.Run($null) | Out-Null
            Start-Sleep -Milliseconds 700
        } finally {
            try { $rootFolder.DeleteTask("LaunchBackdrop", 0) } catch {}
        }
    } catch {
        Start-Process explorer.exe "`"$exe`""
        Start-Sleep -Milliseconds 700
    }
    if (-not (Get-Process Backdrop -ErrorAction SilentlyContinue)) {
        Start-Process $exe -WorkingDirectory $installDir
    }
}