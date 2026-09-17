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

    $canvasSource = Join-Path $repoRoot "src\Extension"
    Install-CopilotExtension $canvasSource "canvas-bridge" "Canvas bridge extension" @(
        "package.json", "canvas-doc-kinds.json", "canvas-filename-contract.json",
        "canvas-send.js", "canvas-selection-context.js"
    )
    $installedPrompt = Join-Path $env:COPILOT_HOME "extensions\canvas-bridge\session-prompt.mjs"
    $checkInstalledPrompt = @'
import assert from "node:assert/strict";
import { pathToFileURL } from "node:url";
const { promptForCanvasMessage } = await import(pathToFileURL(process.argv[1]).href);
const message = { action: "expand-section", section: "evidence", doc: "review.html" };
const result = promptForCanvasMessage(JSON.stringify(message));
assert.equal(typeof JSON.parse(result.prompt.slice("[canvas] ".length)).authoringReminder, "string");
const systemView = JSON.stringify({ ...message, doc: "diff.html" });
assert.equal(promptForCanvasMessage(systemView).prompt, `[canvas] ${systemView}`);
'@
    node --input-type=module -e $checkInstalledPrompt $installedPrompt
    Assert-True ($LASTEXITCODE -eq 0) "Installed canvas bridge could not load its prompt and document-kind resources"

    $skillSource = Join-Path $repoRoot "src\Extension\skill"
    $claudeHome = Join-Path $root "claude-home"
    New-Item -ItemType Directory -Path $claudeHome -Force | Out-Null
    foreach ($configDirectory in @((Get-CopilotConfigDirectory), $claudeHome)) {
        Install-CanvasSkill $configDirectory
        $skillDirectory = Join-Path $configDirectory "skills\canvas"
        foreach ($file in Get-ChildItem -LiteralPath $skillSource -Recurse -File) {
            $relativePath = [IO.Path]::GetRelativePath($skillSource, $file.FullName)
            $destination = Join-Path $skillDirectory $relativePath
            Assert-True (
                (Get-FileHash -LiteralPath $destination).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash
            ) "Installed canvas skill resource differs from its source: $relativePath"
        }

        $skillText = Get-Content -LiteralPath (Join-Path $skillDirectory "SKILL.md") -Raw
        foreach ($link in [regex]::Matches($skillText, '\]\(([^)]+\.md)\)')) {
            Assert-True (
                (Test-Path -LiteralPath (Join-Path $skillDirectory $link.Groups[1].Value) -PathType Leaf)
            ) "Installed canvas skill has a broken reference: $($link.Groups[1].Value)"
        }

        $audienceReference = Join-Path $skillDirectory "audience.md"
        Set-Content -LiteralPath $audienceReference -Value "Outdated reference"
        Install-CanvasSkill $configDirectory
        Assert-True (
            (Get-FileHash -LiteralPath $audienceReference).Hash -eq
            (Get-FileHash -LiteralPath (Join-Path $skillSource "audience.md")).Hash
        ) "Reinstall left the audience reference outdated"
    }

    $absentConfig = Join-Path $root "absent-agent"
    Install-CanvasSkill $absentConfig
    Assert-True (-not (Test-Path -LiteralPath $absentConfig)) "Skill installation created an absent agent's config"

    Write-Host "PASS: Copilot extensions install under the active COPILOT_HOME"
    Write-Host "PASS: Canvas skill installs and updates its complete reference bundle for both agents"
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
