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
import { join, dirname } from "node:path";
const { promptForCanvasMessage } = await import(pathToFileURL(process.argv[1]).href);
const message = { action: "expand-section", section: "evidence", doc: "review.html" };
const source = { worktreePath: process.cwd(), filename: "review.html" };
const result = promptForCanvasMessage(JSON.stringify(message), source);
assert.equal(result.prompt, `[canvas] ${JSON.stringify({ source, payload: message })}`);
const systemSource = { ...source, filename: "diff.html" };
assert.equal(
  promptForCanvasMessage(JSON.stringify(message), systemSource).prompt,
  `[canvas] ${JSON.stringify({ source: systemSource, payload: message })}`,
);
const { createCanvasReadabilityHooks } = await import(pathToFileURL(join(dirname(process.argv[1]), "canvas-readability.mjs")).href);
const hooks = createCanvasReadabilityHooks({
  worktreePath: process.cwd(),
  readCanvasFile: async () => `<p>${"word ".repeat(71)}</p>`,
  log: () => {},
});
const feedback = await hooks.onPostToolUse({
  sessionId: "installed-check", workingDirectory: process.cwd(),
  toolName: "create", toolArgs: { path: join(".agents", "canvas", "review.html") },
  toolResult: { resultType: "success", textResultForLlm: "created" },
});
assert.match(feedback.additionalContext, /71 words/);
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

        foreach ($reference in Get-ChildItem -LiteralPath $skillDirectory -Filter "*.md" -File) {
            $referenceText = Get-Content -LiteralPath $reference.FullName -Raw
            foreach ($link in [regex]::Matches($referenceText, '\]\(([^)]+\.md)\)')) {
                Assert-True (
                    (Test-Path -LiteralPath (Join-Path $reference.DirectoryName $link.Groups[1].Value) -PathType Leaf)
                ) "Installed canvas skill has a broken reference: $($link.Groups[1].Value)"
            }
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
    Install-CanvasDesigner $absentConfig
    Assert-True (-not (Test-Path -LiteralPath $absentConfig)) "Skill installation created an absent agent's config"

    $designerSource = Join-Path $repoRoot "src\Extension\agents\canvas-designer.agent.md"
    $designerInstalled = Join-Path $env:COPILOT_HOME "agents\canvas-designer.agent.md"
    Install-CanvasDesigner (Get-CopilotConfigDirectory)
    Assert-True (
        (Get-FileHash -LiteralPath $designerInstalled).Hash -eq (Get-FileHash -LiteralPath $designerSource).Hash
    ) "Canvas designer was not installed under COPILOT_HOME"
    Set-Content -LiteralPath $designerInstalled -Value "Outdated agent"
    Install-CanvasDesigner (Get-CopilotConfigDirectory)
    Assert-True (
        (Get-FileHash -LiteralPath $designerInstalled).Hash -eq (Get-FileHash -LiteralPath $designerSource).Hash
    ) "Reinstall left the canvas designer outdated"
    Assert-True (
        -not (Test-Path -LiteralPath (Join-Path $claudeHome "agents\canvas-designer.agent.md"))
    ) "Canvas skill installation added a Copilot-only agent to the other host"

    Write-Host "PASS: Copilot extensions install under the active COPILOT_HOME"
    Write-Host "PASS: Canvas skill installs and updates its complete reference bundle for both agents"
    Write-Host "PASS: Canvas designer installs and updates only in the active Copilot config"
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
