$ErrorActionPreference = "Stop"

$version = "1.7.7"
$artifactName = "ttyd.win32.exe"
$expectedSha256 = "e33a27501b10b96981335bcba938b1145c7f52551a343e72160f00ab71832b37"
$downloadUri = "https://github.com/tsl0922/ttyd/releases/download/$version/$artifactName"
$repoRoot = Split-Path $PSScriptRoot -Parent
$installDirectory = Join-Path $repoRoot ".tools\ttyd\$version"
$executablePath = Join-Path $installDirectory "ttyd.exe"
$licenseSource = Join-Path $PSScriptRoot "third-party\ttyd-LICENSE.txt"
$licenseDestination = Join-Path $installDirectory "LICENSE.txt"

function Get-Sha256 {
    param([string]$Path)

    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-ExpectedArtifact {
    param([string]$Path)

    (Test-Path -LiteralPath $Path -PathType Leaf) -and
        ((Get-Sha256 $Path) -eq $expectedSha256)
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null

if (Test-ExpectedArtifact $executablePath) {
    Copy-Item -LiteralPath $licenseSource -Destination $licenseDestination -Force
    Write-Host "ttyd $version is already installed at $executablePath" -ForegroundColor Green
    exit 0
}

if (Test-Path -LiteralPath $executablePath) {
    Write-Host "Replacing ttyd.exe because its SHA-256 checksum does not match the pinned artifact." -ForegroundColor Yellow
}

$temporaryPath = Join-Path $installDirectory "$artifactName.download"

try {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    Write-Host "Downloading ttyd $version from $downloadUri"
    Invoke-WebRequest -Uri $downloadUri -OutFile $temporaryPath

    $actualSha256 = Get-Sha256 $temporaryPath

    if ($actualSha256 -ne $expectedSha256) {
        throw "Downloaded ttyd checksum mismatch. Expected $expectedSha256 but received $actualSha256. Delete '$temporaryPath' and retry; if it repeats, verify the 1.7.7 release at https://github.com/tsl0922/ttyd/releases/tag/1.7.7."
    }

    Move-Item -LiteralPath $temporaryPath -Destination $executablePath -Force
    Copy-Item -LiteralPath $licenseSource -Destination $licenseDestination -Force
} catch {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    throw "Unable to install ttyd $version. Check access to GitHub, then rerun '.\treemon.ps1 setup-ttyd'. $($_.Exception.Message)"
}

Write-Host "Installed ttyd $version at $executablePath" -ForegroundColor Green
