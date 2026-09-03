$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
$ttyd = Join-Path $repoRoot ".tools\ttyd\1.7.7\ttyd.exe"
$previousOptIn = $env:TREEMON_RUN_EMBEDDED_LAUNCH_E2E

Push-Location $repoRoot

try {
    if (-not (Test-Path -LiteralPath $ttyd -PathType Leaf)) {
        & (Join-Path $PSScriptRoot "setup-ttyd.ps1")
    }

    dotnet build treemon.slnx --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }

    $env:TREEMON_RUN_EMBEDDED_LAUNCH_E2E = "1"

    dotnet test src/Tests/Tests.fsproj `
        --configuration Release `
        --no-build `
        --filter "Category=EmbeddedLaunchE2E" `
        --logger "console;verbosity=detailed"

    if ($LASTEXITCODE -ne 0) {
        throw "Embedded launch E2E verification failed with exit code $LASTEXITCODE."
    }
} finally {
    if ($null -eq $previousOptIn) {
        Remove-Item Env:TREEMON_RUN_EMBEDDED_LAUNCH_E2E -ErrorAction SilentlyContinue
    } else {
        $env:TREEMON_RUN_EMBEDDED_LAUNCH_E2E = $previousOptIn
    }

    Pop-Location
}
