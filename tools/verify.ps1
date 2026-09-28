# Offline checks: builds the app, runs the .NET policy and behavior suites and the
# headless-browser checks of the injected page code. Needs the .NET 9 SDK, Node.js 22
# and Chrome or Edge (or CHROME_PATH). Nothing here touches a running Codex.
[CmdletBinding()]
param([string]$ScratchRoot = (Join-Path ([IO.Path]::GetTempPath()) 'codex-backdrop-verification'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$run = Join-Path $ScratchRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$succeeded = $false

try {
    Push-Location $root
    $manifest = Get-Content -Raw -LiteralPath 'src\CodexBackdrop\app.manifest'
    if ($manifest -notmatch 'requestedExecutionLevel\s+level="asInvoker"') {
        throw 'the app must run as the current user without elevation'
    }
    $css = Get-Content -Raw -LiteralPath 'src\CodexBackdrop\Runtime\codex-selectors.css'
    if ($css -notmatch '(?s)\[data-embedded-frame-host-root\]\s*\{[^}]*z-index:\s*2\s*!important;[^}]*pointer-events:\s*none\s*!important;') {
        throw 'embedded previews must stay above the app root and pass pointer input through'
    }

    $buildRoot = Join-Path $run 'build'
    dotnet build 'src\CodexBackdrop\CodexBackdrop.csproj' -c Release --artifacts-path $buildRoot -warnaserror --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'build failed: CodexBackdrop' }

    $projects = @(
        'CodexBackdrop.PolicyTests',
        'CodexBackdrop.OwnershipPolicyTests',
        'CodexBackdrop.SettingsDurabilityTests',
        'CodexBackdrop.InjectorFenceTests',
        'CodexBackdrop.SupervisorBehaviorTests'
    )
    foreach ($name in $projects) {
        dotnet build "tests\$name\$name.csproj" -c Release --artifacts-path $buildRoot -warnaserror --nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw "build failed: $name" }
        dotnet (Join-Path $buildRoot "bin\$name\release\$name.dll")
        if ($LASTEXITCODE -ne 0) { throw "checks failed: $name" }
    }

    foreach ($test in @(
        'verify-protected-css.mjs',
        'verify-sentry-scheduling.mjs',
        'verify-sentry-route-scope.mjs',
        'verify-surface-frame.mjs',
        'verify-injection-continuity.mjs'
    )) {
        node "tests\$test"
        if ($LASTEXITCODE -ne 0) { throw "checks failed: $test" }
    }
    $succeeded = $true
    Write-Host 'all 10 offline verification suites passed'
}
finally {
    Pop-Location
    if ($succeeded) { Remove-Item -LiteralPath $run -Recurse -Force }
    else { Write-Warning "verification scratch kept at $run" }
}
