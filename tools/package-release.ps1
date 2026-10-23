[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$RuntimeVersion = '9.0.21'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$bundle = Join-Path $destination 'Backdrop-win-x64'
New-Item -ItemType Directory -Path $bundle -Force | Out-Null

& (Join-Path $root 'build.ps1') -SelfContained -OutputDirectory $bundle -RuntimeVersion $RuntimeVersion
if ($LASTEXITCODE -ne 0) { throw 'release build failed' }
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $bundle 'LICENSE.txt') -Force
@'
Backdrop for Windows x64

1. Extract this ZIP to a folder you want to keep.
2. Run Backdrop.exe.
3. Choose an image or animated GIF when prompted.
4. Right-click the tray icon to adjust wash, blur, calm, or enabled apps.

The .NET runtime is included. No SDK, separate runtime installation, or administrator rights are required.
For automatic startup, enable Start with Windows in the tray menu.
Existing Codex/Claude/T3 Backdrop settings are imported on first use.
In old standalone Backdrop apps, disable Start with Windows and quit before running this version.

Backdrop is unsigned. Source, documentation, and updates:
https://github.com/okisooo/backdrop

This build bundles its runtime; keep Backdrop updated through GitHub Releases.
Do not report issues using private chat screenshots unless you have removed sensitive details.
'@ | Set-Content -LiteralPath (Join-Path $bundle 'START-HERE.txt') -Encoding utf8
$sample = Join-Path $root 'docs\media\sample-wallpaper.gif'
if (Test-Path -LiteralPath $sample) { Copy-Item -LiteralPath $sample -Destination $bundle -Force }
$archive = Join-Path $destination 'Backdrop-win-x64.zip'
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  Backdrop-win-x64.zip" | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding ascii
Get-Item -LiteralPath $archive
