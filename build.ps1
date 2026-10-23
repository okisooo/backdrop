# Publishes Backdrop.exe (single file, needs the .NET 9 Desktop Runtime) to artifacts\publish.
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$RuntimeVersion,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts\publish' }
$selfContainedValue = $SelfContained.IsPresent.ToString().ToLowerInvariant()
[string[]]$runtimeOptions = if ($RuntimeVersion) { @("-p:BundledRuntimeVersion=$RuntimeVersion") } else { @() }

dotnet publish (Join-Path $root 'src\Backdrop\Backdrop.csproj') `
    -c $Configuration -r win-x64 --self-contained $selfContainedValue `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $out -nologo @runtimeOptions
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Get-Item (Join-Path $out 'Backdrop.exe')
