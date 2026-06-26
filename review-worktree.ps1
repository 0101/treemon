# Creates a worktree for reviewing someone else's contribution: fetches a remote
# and forks a `review/<branch>` worktree that tracks `<remote>/<branch>`, then runs
# the standard post-fork setup. This is a manual workflow — Treemon's own
# "create worktree" feature no longer forks from arbitrary remotes.
#
# Usage:
#   pwsh -File review-worktree.ps1 <branch> <remote>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Branch,
    [Parameter(Mandatory = $true, Position = 1)]
    [string]$Remote
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$dirSafeBranch = $Branch -replace '/', '-'
$worktreePath = Join-Path (Split-Path $repoRoot -Parent) "tm-review-$dirSafeBranch"
$localBranch = "review/$Branch"

Write-Host "Fetching from $Remote..."
git fetch $Remote

Write-Host "Creating review worktree at $worktreePath (branch $localBranch tracking $Remote/$Branch)..."
git worktree add -b $localBranch $worktreePath "$Remote/$Branch"

# Reuse the same setup Treemon runs after creating a worktree.
& (Join-Path $repoRoot "post-fork.ps1") $worktreePath $repoRoot "$Remote/$Branch" $localBranch
