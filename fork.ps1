param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Branch,
    [string]$Remote = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot

$dirSafeBranch = $Branch -replace '/', '-'

if ($Remote) {
    $worktreePath = Join-Path (Split-Path $repoRoot -Parent) "tm-review-$dirSafeBranch"
    Write-Host "Fetching from $Remote..."
    git fetch $Remote
    $localBranch = "review/$Branch"
    Write-Host "Creating review worktree at $worktreePath (branch $localBranch tracking $Remote/$Branch)..."
    git worktree add -b $localBranch $worktreePath "$Remote/$Branch"
} else {
    $worktreePath = Join-Path (Split-Path $repoRoot -Parent) "tm-$dirSafeBranch"
    Write-Host "Creating worktree at $worktreePath with branch $Branch..."
    git worktree add -b $Branch $worktreePath
}
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
    Start-Process pwsh -Verb RunAs -Wait -ArgumentList "-NoProfile", "-Command", "New-Item -ItemType SymbolicLink -Path '$escapedDest' -Target '$escapedTarget'"
    if (Test-Path ".claude") {
        Write-Host "  Symlink created with elevation."
    } else {
        Write-Host "  Warning: Failed to create symlink. Copy manually or enable developer mode."
    }
}

Write-Host "Creating data symlink..."
$dataTarget = Join-Path $repoRoot "data"
if (Test-Path $dataTarget) {
    try {
        New-Item -ItemType SymbolicLink -Path "data" -Target $dataTarget -ErrorAction Stop | Out-Null
        Write-Host "  Symlink created successfully."
    } catch {
        Write-Host "  Symlink creation failed (needs admin). Requesting elevation..."
        $escapedDest = (Join-Path $worktreePath "data") -replace "'", "''"
        $escapedTarget = $dataTarget -replace "'", "''"
        Start-Process pwsh -Verb RunAs -Wait -ArgumentList "-NoProfile", "-Command", "New-Item -ItemType SymbolicLink -Path '$escapedDest' -Target '$escapedTarget'"
        if (Test-Path "data") {
            Write-Host "  Symlink created with elevation."
        } else {
            Write-Host "  Warning: Failed to create symlink. Copy manually or enable developer mode."
        }
    }
} else {
    Write-Host "  No data folder found in source worktree, skipping."
}

if (Get-Command bd -ErrorAction SilentlyContinue) {
    Write-Host "Initializing beads..."
    bd init --skip-hooks --skip-merge-driver --no-daemon --quiet
} else {
    Write-Host "Skipping beads init (bd not found)."
}

Write-Host "Installing npm dependencies..."
npm install

Write-Host "Done! Worktree created at: $worktreePath"
