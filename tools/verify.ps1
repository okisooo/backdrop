[CmdletBinding()]
param([string]$GifPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

dotnet build (Join-Path $root 'src\Backdrop\Backdrop.csproj') -c Release -nologo -v q --disable-build-servers -p:UseSharedCompilation=false -m:1
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

dotnet run --project (Join-Path $root 'tests\Backdrop.DetectorTests') -c Release --disable-build-servers -p:UseSharedCompilation=false -p:BuildInParallel=false
if ($LASTEXITCODE -ne 0) { throw 'detector checks failed' }

$extra = if ($GifPath) { @('--', '--gif', $GifPath) } else { @() }
dotnet run --project (Join-Path $root 'tests\Backdrop.EngineTests') -c Release --disable-build-servers -p:UseSharedCompilation=false -p:BuildInParallel=false @extra
if ($LASTEXITCODE -ne 0) { throw 'engine checks failed' }