param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Branch
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$worktreePath = Join-Path (Split-Path $repoRoot -Parent) "tm-$Branch"

Write-Host "Creating worktree at $worktreePath with branch $Branch..."
git worktree add -b $Branch $worktreePath
Set-Location $worktreePath

Write-Host "Creating .claude symlink..."
$claudeTarget = Join-Path $repoRoot ".claude"
try {
    New-Item -ItemType SymbolicLink -Path ".claude" -Target $claudeTarget -ErrorAction Stop | Out-Null
    Write-Host "  Symlink created successfully."
} catch {
    Write-Host "  Symlink creation failed (needs admin). Requesting elevation..."
    $escapedDest = (Join-Path $worktreePath ".claude") -replace "'", "''"
    $escapedTarget = $claudeTarget -replace "'", "''"
    Start-Process powershell -Verb RunAs -Wait -ArgumentList "-Command", "New-Item -ItemType SymbolicLink -Path '$escapedDest' -Target '$escapedTarget'"
    if (Test-Path ".claude") {
        Write-Host "  Symlink created with elevation."
    } else {
        Write-Host "  Warning: Failed to create symlink. Copy manually or enable developer mode."
    }
}

Write-Host "Initializing beads..."
bd init --skip-hooks --skip-merge-driver --no-daemon --quiet

Write-Host "Installing npm dependencies..."
npm install

Write-Host "Done! Worktree created at: $worktreePath"
