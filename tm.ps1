#!/usr/bin/env pwsh
# Treemon CLI — thin wrapper around src/Cli
param(
    [Parameter(ValueFromRemainingArguments)]
    [string[]]$Arguments
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src" "Cli"

# Pass through to dotnet run
$passArgs = @("run", "--project", $project, "--")
if ($Arguments) { $passArgs += $Arguments }
& dotnet @passArgs
exit $LASTEXITCODE
