$ErrorActionPreference = "Stop"

# Thin wrapper. The installer itself lives in treemon.fsx, which is the cross-platform lifecycle and
# the only place the pinned version, the per-platform artifact names and their SHA-256 values are
# written down. Keeping a second copy here meant the two could silently disagree on the next upgrade,
# which for a checksum is the one kind of drift that must not happen quietly.
#
# This file remains because CI and treemon.ps1 invoke it by path.

$repoRoot = Split-Path $PSScriptRoot -Parent
& dotnet fsi (Join-Path $repoRoot "treemon.fsx") setup-ttyd

if ($LASTEXITCODE -ne 0) {
    throw "ttyd setup failed with exit code $LASTEXITCODE"
}
