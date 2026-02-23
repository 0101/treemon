param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Branch
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$worktreePath = Join-Path (Split-Path $repoRoot -Parent) "tm-$Branch"

if (-not (Test-Path $worktreePath)) {
    Write-Error "Worktree not found at $worktreePath"
    exit 1
}

Write-Host "This will remove:"
Write-Host "  - Worktree: $worktreePath"
Write-Host "  - Branch: $Branch"
Write-Host ""

$confirmation = Read-Host "Are you sure? (y/N)"
if ($confirmation -ne 'y' -and $confirmation -ne 'Y') {
    Write-Host "Aborted."
    exit 0
}

Write-Host "Removing worktree..."
git worktree remove --force $worktreePath

Write-Host "Deleting branch..."
git branch -D $Branch

Write-Host "Done!"
