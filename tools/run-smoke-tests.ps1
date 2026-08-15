#!/usr/bin/env pwsh
# Runs the JvJvMediaManager stability/performance smoke test harness.
# Usage:
#   pwsh tools/run-smoke-tests.ps1
#   pwsh tools/run-smoke-tests.ps1 -Suite db-transaction
param(
    [string]$Suite,
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tools/JvJvMediaManager.SmokeTests/JvJvMediaManager.SmokeTests.csproj'

$runArgs = @('run', '--project', $project, '-c', $Configuration)
if ($Suite) {
    $runArgs += @('--', '--suite', $Suite)
}

& dotnet @runArgs
exit $LASTEXITCODE
