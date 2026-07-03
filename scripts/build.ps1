#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build, test, and optionally run SemanticPortrait.
.DESCRIPTION
    Wraps the dotnet CLI for the desktop app (.NET MAUI Blazor Hybrid, .NET 10, win-arm64).
.PARAMETER Configuration
    Debug (default) or Release.
.PARAMETER Test
    Run the test suite after building.
.PARAMETER Run
    Launch the app after a successful build.
.PARAMETER Clean
    Remove bin/ and obj/ before building.
.EXAMPLE
    ./scripts/build.ps1 -Test
    ./scripts/build.ps1 -Configuration Release -Run
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Test,
    [switch]$Run,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$RepoRoot  = Split-Path -Parent $PSScriptRoot
$AppProj   = Join-Path $RepoRoot 'src\SemanticPortrait.App\SemanticPortrait.App.csproj'
$TestProj  = Join-Path $RepoRoot 'tests\SemanticPortrait.Tests\SemanticPortrait.Tests.csproj'
$Framework = 'net10.0-windows10.0.19041.0'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

if ($Clean) {
    Write-Step 'Cleaning bin/ and obj/'
    Get-ChildItem -Path (Join-Path $RepoRoot 'src'), (Join-Path $RepoRoot 'tests') `
        -Include bin, obj -Recurse -Directory -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force
}

Write-Step "Building app ($Configuration, $Framework)"
dotnet build $AppProj -c $Configuration -f $Framework
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

if ($Test) {
    Write-Step "Running tests ($Configuration)"
    dotnet test $TestProj -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE)" }
}

if ($Run) {
    Write-Step 'Launching app'
    dotnet run --project $AppProj -c $Configuration -f $Framework
}

Write-Step 'Done.'
