$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\Tests\SessionIsolationVerifier\SessionIsolationVerifier.fsproj"
$ttyd = Join-Path $repoRoot ".tools\ttyd\1.7.7\ttyd.exe"
$configuration = if ($env:TREEMON_VERIFY_CONFIGURATION) {
    $env:TREEMON_VERIFY_CONFIGURATION
} else {
    "Debug"
}

Push-Location $repoRoot

try {
    if (-not (Test-Path -LiteralPath $ttyd -PathType Leaf)) {
        & (Join-Path $PSScriptRoot "setup-ttyd.ps1")
    }

    dotnet build $project `
        --configuration $configuration `
        --nologo `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Session isolation verifier build failed with exit code $LASTEXITCODE."
    }

    dotnet run `
        --no-build `
        --project $project `
        --configuration $configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Session isolation verifier failed with exit code $LASTEXITCODE."
    }
} finally {
    Pop-Location
}
