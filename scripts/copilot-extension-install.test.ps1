$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot "treemon.ps1")

function Assert-True($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$root = Join-Path ([IO.Path]::GetTempPath()) "treemon-extension-install-$([Guid]::NewGuid().ToString('N'))"
$hadCopilotHome = Test-Path Env:\COPILOT_HOME
$previousCopilotHome = $env:COPILOT_HOME

try {
    $env:COPILOT_HOME = Join-Path $root "copilot-home"
    $source = Join-Path $repoRoot "src\Extension\reporting"
    Install-CopilotExtension $source "treemon-reporting" "Reporting extension" @("package.json")

    $installed = Join-Path $env:COPILOT_HOME "extensions\treemon-reporting"
    Assert-True (
        (Get-CopilotConfigDirectory) -ceq [IO.Path]::GetFullPath($env:COPILOT_HOME)
    ) "Copilot config resolution ignored COPILOT_HOME"
    Assert-True (
        (Test-Path -LiteralPath (Join-Path $installed "extension.mjs") -PathType Leaf)
    ) "Reporting extension entry point was not installed under COPILOT_HOME"
    Assert-True (
        (Test-Path -LiteralPath (Join-Path $installed "reporting-runtime.mjs") -PathType Leaf)
    ) "Reporting runtime was not installed under COPILOT_HOME"
    Assert-True (
        (Test-Path -LiteralPath (Join-Path $installed "package.json") -PathType Leaf)
    ) "Reporting package metadata was not installed under COPILOT_HOME"

    Write-Host "PASS: Copilot extensions install under the active COPILOT_HOME"
} finally {
    if ($hadCopilotHome) {
        $env:COPILOT_HOME = $previousCopilotHome
    } else {
        Remove-Item Env:\COPILOT_HOME -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
