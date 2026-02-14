param(
    [string]$WorktreeRoot = "Q:\code\AITestAgent"
)

$ErrorActionPreference = "Stop"

Write-Host "Starting mait with worktree root: $WorktreeRoot" -ForegroundColor Cyan

$serverJob = $null
$viteJob = $null

function Test-ServerReady {
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $tcp.Connect("localhost", 5000)
        $tcp.Close()
        return $true
    } catch {
        return $false
    }
}

function Test-ViteReady {
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:5173" -TimeoutSec 1 -UseBasicParsing -ErrorAction SilentlyContinue
        return $true
    } catch {
        return $false
    }
}

function Stop-Servers {
    Write-Host "`nShutting down..." -ForegroundColor Yellow
    if ($serverJob) { Stop-Job $serverJob -ErrorAction SilentlyContinue; Remove-Job $serverJob -ErrorAction SilentlyContinue }
    if ($viteJob) { Stop-Job $viteJob -ErrorAction SilentlyContinue; Remove-Job $viteJob -ErrorAction SilentlyContinue }
    Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*Server*" } | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name "node" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*vite*" } | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "Stopped." -ForegroundColor Green
}

try {
    Write-Host "Starting Saturn server..." -ForegroundColor Gray
    $serverJob = Start-Job -ScriptBlock {
        param($root, $projectPath)
        Set-Location $projectPath
        dotnet watch run --project src/Server -- $root
    } -ArgumentList $WorktreeRoot, $PSScriptRoot

    Write-Host "Starting Vite dev server..." -ForegroundColor Gray
    $viteJob = Start-Job -ScriptBlock {
        param($projectPath)
        Set-Location $projectPath
        npx vite
    } -ArgumentList $PSScriptRoot

    Write-Host "Waiting for servers to be ready..." -ForegroundColor Gray

    $maxWait = 90
    $elapsed = 0
    $serverReady = $false
    $viteReady = $false

    while ($elapsed -lt $maxWait -and (-not $serverReady -or -not $viteReady)) {
        Start-Sleep -Seconds 1
        $elapsed++

        if (-not $serverReady) {
            $serverReady = Test-ServerReady
            if ($serverReady) { Write-Host "Saturn server ready on http://localhost:5000" -ForegroundColor Green }
        }

        if (-not $viteReady) {
            $viteReady = Test-ViteReady
            if ($viteReady) { Write-Host "Vite dev server ready on http://localhost:5173" -ForegroundColor Green }
        }
    }

    if (-not $serverReady -or -not $viteReady) {
        throw "Servers failed to start within $maxWait seconds"
    }

    Write-Host "`n================================================" -ForegroundColor Cyan
    Write-Host "  mait is running!" -ForegroundColor Green
    Write-Host "  Open: " -NoNewline; Write-Host "http://localhost:5173" -ForegroundColor Blue
    Write-Host "  Press Ctrl+C to stop" -ForegroundColor Gray
    Write-Host "================================================`n" -ForegroundColor Cyan

    while ($true) {
        Start-Sleep -Seconds 1
        if ($serverJob.State -ne "Running" -or $viteJob.State -ne "Running") {
            throw "One of the servers stopped unexpectedly"
        }
    }
} catch {
    Write-Host "Error: $_" -ForegroundColor Red
    Stop-Servers
    exit 1
} finally {
    Stop-Servers
}
