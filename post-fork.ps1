# Post-fork setup hook. Treemon creates the worktree itself, then runs this
# script inside the new worktree to wire up local-only dependencies.
#
# Treemon invokes it as:
#   pwsh -NoProfile -File post-fork.ps1 <worktreePath> <sourceRepoRoot> <baseRef> <branch>
# with the working directory set to the new worktree.
param(
    [Parameter(Position = 0)]
    [string]$WorktreePath = (Get-Location).Path,
    [Parameter(Position = 1)]
    [string]$SourceRoot = $PSScriptRoot,
    [Parameter(Position = 2)]
    [string]$BaseRef = "",
    [Parameter(Position = 3)]
    [string]$Branch = ""
)

$ErrorActionPreference = "Stop"

Set-Location $WorktreePath

# Directory junctions (not symbolic links) so this works without elevation or
# Developer Mode — important because Treemon spawns this hook from the server
# process, where no one is present to approve a UAC prompt.
function New-RepoJunction {
    param([string]$Name)

    $target = Join-Path $SourceRoot $Name
    if (-not (Test-Path $target)) {
        Write-Host "  No '$Name' in source repo, skipping."
        return
    }

    Write-Host "Creating $Name junction..."
    try {
        New-Item -ItemType Junction -Path $Name -Target $target -ErrorAction Stop | Out-Null
        Write-Host "  Junction created successfully."
    } catch {
        Write-Host "  Warning: Failed to create '$Name' junction: $($_.Exception.Message). Create it manually if needed."
    }
}

New-RepoJunction ".claude"
New-RepoJunction "data"

if (Get-Command bd -ErrorAction SilentlyContinue) {
    Write-Host "Initializing beads..."
    bd init --skip-hooks --skip-merge-driver --no-daemon --quiet
} else {
    Write-Host "Skipping beads init (bd not found)."
}

Write-Host "Installing npm dependencies..."
npm install

Write-Host "Done! Worktree ready at: $WorktreePath"
