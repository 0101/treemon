$ErrorActionPreference = "Stop"

$version = "1.7.7"

function Get-PinnedArtifact {
    # Pinned from the upstream release https://github.com/tsl0922/ttyd/releases/tag/1.7.7.
    # The Windows build is 32-bit and runs on every Windows architecture, so only Linux varies by CPU.
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        return @{
            Name           = "ttyd.win32.exe"
            Sha256         = "e33a27501b10b96981335bcba938b1145c7f52551a343e72160f00ab71832b37"
            ExecutableName = "ttyd.exe"
        }
    }

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
        switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
            "X64" {
                return @{
                    Name           = "ttyd.x86_64"
                    Sha256         = "8a217c968aba172e0dbf3f34447218dc015bc4d5e59bf51db2f2cd12b7be4f55"
                    ExecutableName = "ttyd"
                }
            }
            "Arm64" {
                return @{
                    Name           = "ttyd.aarch64"
                    Sha256         = "b38acadd89d1d396a0f5649aa52c539edbad07f4bc7348b27b4f4b7219dd4165"
                    ExecutableName = "ttyd"
                }
            }
        }

        throw "No ttyd $version artifact is pinned for Linux $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture). Embedded terminals require x64 or arm64."
    }

    throw "Embedded terminals are only supported on Windows and Linux; ttyd $version is not pinned for $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)."
}

$artifact = Get-PinnedArtifact
$downloadUri = "https://github.com/tsl0922/ttyd/releases/download/$version/$($artifact.Name)"
$repoRoot = Split-Path $PSScriptRoot -Parent
$installDirectory = Join-Path (Join-Path (Join-Path $repoRoot ".tools") "ttyd") $version
$executablePath = Join-Path $installDirectory $artifact.ExecutableName
$licenseSource = Join-Path (Join-Path $PSScriptRoot "third-party") "ttyd-LICENSE.txt"
$licenseDestination = Join-Path $installDirectory "LICENSE.txt"

function Get-Sha256 {
    param([string]$Path)

    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-ExpectedArtifact {
    param([string]$Path)

    (Test-Path -LiteralPath $Path -PathType Leaf) -and
        ((Get-Sha256 $Path) -eq $artifact.Sha256)
}

function Grant-ExecutePermission {
    param([string]$Path)

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        return
    }

    # A release asset arrives without the execute bit, and ttyd is spawned directly rather than
    # through a shell, so the bit has to be set here or every terminal launch fails with EACCES.
    & chmod "+x" $Path

    if ($LASTEXITCODE -ne 0) {
        throw "Could not mark '$Path' executable (chmod exited with $LASTEXITCODE)."
    }
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null

if (Test-ExpectedArtifact $executablePath) {
    Grant-ExecutePermission $executablePath
    Copy-Item -LiteralPath $licenseSource -Destination $licenseDestination -Force
    Write-Host "ttyd $version is already installed at $executablePath" -ForegroundColor Green
    exit 0
}

if (Test-Path -LiteralPath $executablePath) {
    Write-Host "Replacing $($artifact.ExecutableName) because its SHA-256 checksum does not match the pinned artifact." -ForegroundColor Yellow
}

$temporaryPath = Join-Path $installDirectory "$($artifact.Name).download"

try {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    Write-Host "Downloading ttyd $version from $downloadUri"
    Invoke-WebRequest -Uri $downloadUri -OutFile $temporaryPath

    $actualSha256 = Get-Sha256 $temporaryPath

    if ($actualSha256 -ne $artifact.Sha256) {
        throw "Downloaded ttyd checksum mismatch. Expected $($artifact.Sha256) but received $actualSha256. Delete '$temporaryPath' and retry; if it repeats, verify the $version release at https://github.com/tsl0922/ttyd/releases/tag/$version."
    }

    Move-Item -LiteralPath $temporaryPath -Destination $executablePath -Force
    Grant-ExecutePermission $executablePath
    Copy-Item -LiteralPath $licenseSource -Destination $licenseDestination -Force
} catch {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    throw "Unable to install ttyd $version. Check access to GitHub, then rerun 'treemon.ps1 setup-ttyd'. $($_.Exception.Message)"
}

Write-Host "Installed ttyd $version at $executablePath" -ForegroundColor Green
