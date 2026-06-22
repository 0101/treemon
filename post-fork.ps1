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

function New-RepoSymlink {
    param([string]$Name)

    $target = Join-Path $SourceRoot $Name
    if (-not (Test-Path $target)) {
        Write-Host "  No '$Name' in source repo, skipping."
        return
    }

    Write-Host "Creating $Name symlink..."
    try {
        New-Item -ItemType SymbolicLink -Path $Name -Target $target -ErrorAction Stop | Out-Null
        Write-Host "  Symlink created successfully."
    } catch {
        Write-Host "  Symlink creation failed (needs admin). Requesting elevation..."
        $escapedDest = (Join-Path $WorktreePath $Name) -replace "'", "''"
        $escapedTarget = $target -replace "'", "''"
        Start-Process pwsh -Verb RunAs -Wait -ArgumentList "-NoProfile", "-Command", "New-Item -ItemType SymbolicLink -Path '$escapedDest' -Target '$escapedTarget'"
        if (Test-Path $Name) {
            Write-Host "  Symlink created with elevation."
        } else {
            Write-Host "  Warning: Failed to create '$Name' symlink. Copy manually or enable developer mode."
        }
    }
}

New-RepoSymlink ".claude"
New-RepoSymlink "data"

if (Get-Command bd -ErrorAction SilentlyContinue) {
    Write-Host "Initializing beads..."
    bd init --skip-hooks --skip-merge-driver --no-daemon --quiet
} else {
    Write-Host "Skipping beads init (bd not found)."
}

Write-Host "Installing npm dependencies..."
npm install

Write-Host "Done! Worktree ready at: $WorktreePath"
