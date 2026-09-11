$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$ttyd = Join-Path $repoRoot ".tools\ttyd\1.7.7\ttyd.exe"
$hadConfiguration = Test-Path Env:\TREEMON_VERIFY_CONFIGURATION
$previousConfiguration = $env:TREEMON_VERIFY_CONFIGURATION

Push-Location $repoRoot

try {
    if (-not (Test-Path -LiteralPath $ttyd -PathType Leaf)) {
        & (Join-Path $PSScriptRoot "setup-ttyd.ps1")
    }

    dotnet build "src\TerminalHost\TerminalHost.fsproj" `
        --configuration Release `
        --nologo `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "TerminalHost build failed with exit code $LASTEXITCODE."
    }

    dotnet build "src\Server\Server.fsproj" `
        --configuration Release `
        --nologo `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Treemon server build failed with exit code $LASTEXITCODE."
    }

    $env:TREEMON_VERIFY_CONFIGURATION = "Release"
    node --no-warnings (Join-Path $PSScriptRoot "verify-real-cli-reporting.mjs")
    if ($LASTEXITCODE -ne 0) {
        throw "Real CLI reporting verification failed with exit code $LASTEXITCODE."
    }
} finally {
    if ($hadConfiguration) {
        $env:TREEMON_VERIFY_CONFIGURATION = $previousConfiguration
    } else {
        Remove-Item Env:\TREEMON_VERIFY_CONFIGURATION -ErrorAction SilentlyContinue
    }
    Pop-Location
}
