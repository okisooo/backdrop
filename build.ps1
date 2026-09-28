# Publishes CodexBackdrop.exe (single file, needs the .NET 9 Desktop Runtime) to artifacts\publish.
[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'artifacts\publish'

dotnet publish (Join-Path $root 'src\CodexBackdrop\CodexBackdrop.csproj') `
    -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Get-Item (Join-Path $out 'CodexBackdrop.exe')
