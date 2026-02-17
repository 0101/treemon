param(
    [Parameter(Position = 0)]
    [ValidateSet("start", "stop", "restart", "status", "log", "dev", "deploy")]
    [string]$Command,

    [Parameter(Position = 1)]
    [string]$WorktreeRoot
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$PidFile = Join-Path $ScriptDir ".treemon.pid"
$ConfigFile = Join-Path $ScriptDir ".treemon.config"
$LogDir = Join-Path $ScriptDir "logs"
$LogFile = Join-Path $LogDir "treemon-prod.log"
$WwwRoot = Join-Path $ScriptDir "wwwroot"
$DefaultPort = 5000

if (-not $Command) {
    Write-Host "Usage: .\treemon.ps1 <command> [worktree-root]" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Commands:" -ForegroundColor White
    Write-Host "  start <path>   Start production server (auto-builds if wwwroot/ is empty)"
    Write-Host "  stop           Stop the production server"
    Write-Host "  restart        Stop + start (reuses previously configured worktree root)"
    Write-Host "  status         Show production server status"
    Write-Host "  log            Tail the production server log"
    Write-Host "  dev <path>     Start dev mode (server :5001 + Vite :5174), Ctrl+C to stop"
    Write-Host "  deploy         Build frontend and deploy to wwwroot/ (restarts prod if running)"
    exit 0
}

function Get-SavedConfig {
    if (Test-Path $ConfigFile) {
        $config = Get-Content $ConfigFile -Raw | ConvertFrom-Json
        return $config
    }
    return $null
}

function Save-Config([string]$Root) {
    @{ WorktreeRoot = $Root.TrimEnd('\', '/') } | ConvertTo-Json | Set-Content $ConfigFile
}

function Get-RunningPid {
    if (-not (Test-Path $PidFile)) { return $null }
    $savedPid = (Get-Content $PidFile -Raw).Trim()
    if (-not $savedPid) { return $null }
    $process = Get-Process -Id $savedPid -ErrorAction SilentlyContinue
    if ($process -and -not $process.HasExited) { return [int]$savedPid }
    return $null
}

function Resolve-WorktreeRoot([string]$Cmd) {
    if ($WorktreeRoot) { return $WorktreeRoot }

    $config = Get-SavedConfig
    if ($config) {
        Write-Host "Using previously configured worktree root: $($config.WorktreeRoot)" -ForegroundColor Gray
        return $config.WorktreeRoot
    }

    Write-Host "Error: worktree root path is required for first $Cmd" -ForegroundColor Red
    Write-Host "Usage: .\treemon.ps1 $Cmd <worktree-root-path>" -ForegroundColor Gray
    exit 1
}

function Build-Frontend {
    Push-Location $ScriptDir
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }

        $distDir = Join-Path $ScriptDir "dist"
        if (-not (Test-Path $distDir)) { throw "dist/ not found after build" }

        if (-not (Test-Path $WwwRoot)) { New-Item -ItemType Directory -Path $WwwRoot | Out-Null }
        Get-ChildItem $WwwRoot -Recurse | Remove-Item -Recurse -Force
        Copy-Item -Path (Join-Path $distDir "*") -Destination $WwwRoot -Recurse -Force
    } finally {
        Pop-Location
    }
}

function Ensure-WwwRoot {
    $hasContent = (Test-Path $WwwRoot) -and @(Get-ChildItem $WwwRoot -File -Recurse).Count -gt 0
    if ($hasContent) { return }

    Write-Host "wwwroot/ is empty, building frontend..." -ForegroundColor Yellow
    Build-Frontend
    Write-Host "Frontend built and copied to wwwroot/" -ForegroundColor Green
}

function Start-ProductionServer([string]$Root) {
    $runningPid = Get-RunningPid
    if ($runningPid) {
        Write-Host "Production server is already running (PID: $runningPid)" -ForegroundColor Yellow
        Write-Host "  URL: http://localhost:$DefaultPort" -ForegroundColor Gray
        Write-Host "Use '.\treemon.ps1 stop' first or '.\treemon.ps1 restart'" -ForegroundColor Gray
        return
    }

    Ensure-WwwRoot

    if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }
    "" | Set-Content $LogFile

    Save-Config $Root

    Write-Host "Starting production server on port $DefaultPort..." -ForegroundColor Cyan
    $process = Start-Process -FilePath "dotnet" `
        -ArgumentList "run", "-c", "Release", "--project", (Join-Path $ScriptDir "src/Server"), "--", $Root, "--port", $DefaultPort `
        -WorkingDirectory $ScriptDir `
        -RedirectStandardOutput $LogFile `
        -RedirectStandardError (Join-Path $LogDir "treemon-prod-stderr.log") `
        -NoNewWindow:$false `
        -WindowStyle Hidden `
        -PassThru

    $process.Id | Set-Content $PidFile
    Write-Host "Production server started (PID: $($process.Id))" -ForegroundColor Green
    Write-Host "Monitoring: $Root" -ForegroundColor Gray
    Write-Host "URL: http://localhost:$DefaultPort" -ForegroundColor Gray
    Write-Host "Log: $LogFile" -ForegroundColor Gray
}

function Stop-ProductionServer {
    $runningPid = Get-RunningPid
    if (-not $runningPid) {
        Write-Host "Production server is not running" -ForegroundColor Yellow
        if (Test-Path $PidFile) { Remove-Item $PidFile }
        return
    }

    Write-Host "Stopping production server (PID: $runningPid)..." -ForegroundColor Yellow
    Stop-Process -Id $runningPid -Force -ErrorAction SilentlyContinue
    Remove-Item $PidFile -ErrorAction SilentlyContinue
    Write-Host "Production server stopped" -ForegroundColor Green
}

function Show-Status {
    $runningPid = Get-RunningPid
    if (-not $runningPid) {
        Write-Host "Production server is not running" -ForegroundColor Yellow
        return
    }

    $process = Get-Process -Id $runningPid -ErrorAction SilentlyContinue
    $uptime = (Get-Date) - $process.StartTime
    $uptimeStr = if ($uptime.Days -gt 0) { "$($uptime.Days)d $($uptime.Hours)h $($uptime.Minutes)m" }
                 elseif ($uptime.Hours -gt 0) { "$($uptime.Hours)h $($uptime.Minutes)m" }
                 else { "$($uptime.Minutes)m $($uptime.Seconds)s" }

    $config = Get-SavedConfig

    Write-Host "Production server is running" -ForegroundColor Green
    Write-Host "  PID:     $runningPid"
    Write-Host "  Port:    $DefaultPort"
    Write-Host "  Uptime:  $uptimeStr"
    Write-Host "  URL:     http://localhost:$DefaultPort"
    if ($config) {
        Write-Host "  Monitor: $($config.WorktreeRoot)"
    }
    Write-Host "  Log:     $LogFile"
}

function Show-Log {
    if (-not (Test-Path $LogFile)) {
        Write-Host "No log file found at $LogFile" -ForegroundColor Yellow
        return
    }
    Write-Host "Tailing $LogFile (Ctrl+C to stop)..." -ForegroundColor Gray
    Get-Content $LogFile -Tail 50 -Wait
}

function Start-DevMode([string]$Root) {
    $devApiPort = 5001
    $devVitePort = 5174

    Write-Host "Starting dev mode..." -ForegroundColor Cyan
    Write-Host "  Server:  http://localhost:$devApiPort (dotnet watch)" -ForegroundColor Gray
    Write-Host "  Vite:    http://localhost:$devVitePort" -ForegroundColor Gray
    Write-Host "  Press Ctrl+C to stop both processes" -ForegroundColor Gray
    Write-Host ""

    $env:VITE_PORT = $devVitePort
    $env:API_PORT = $devApiPort

    $serverProcess = $null
    $viteProcess = $null

    try {
        $serverProcess = Start-Process -FilePath "dotnet" `
            -ArgumentList "watch", "run", "--project", (Join-Path $ScriptDir "src/Server"), "--", $Root, "--port", $devApiPort `
            -WorkingDirectory $ScriptDir `
            -PassThru `
            -NoNewWindow

        $viteProcess = Start-Process -FilePath "npx" `
            -ArgumentList "vite", "--port", $devVitePort `
            -WorkingDirectory (Join-Path $ScriptDir "src/Client") `
            -PassThru `
            -NoNewWindow

        Write-Host "Dev server started (PID: $($serverProcess.Id)), Vite started (PID: $($viteProcess.Id))" -ForegroundColor Green

        while (-not $serverProcess.HasExited -and -not $viteProcess.HasExited) {
            Start-Sleep -Milliseconds 500
        }
    } finally {
        Write-Host ""
        Write-Host "Shutting down dev processes..." -ForegroundColor Yellow

        if ($serverProcess -and -not $serverProcess.HasExited) {
            Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        }
        if ($viteProcess -and -not $viteProcess.HasExited) {
            Stop-Process -Id $viteProcess.Id -Force -ErrorAction SilentlyContinue
        }

        Remove-Item Env:\VITE_PORT -ErrorAction SilentlyContinue
        Remove-Item Env:\API_PORT -ErrorAction SilentlyContinue

        Write-Host "Dev mode stopped" -ForegroundColor Green
    }
}

function Deploy-Frontend {
    Write-Host "Building frontend..." -ForegroundColor Cyan
    Build-Frontend
    Write-Host "Frontend deployed to wwwroot/" -ForegroundColor Green

    $runningPid = Get-RunningPid
    if ($runningPid) {
        Write-Host "Restarting production server..." -ForegroundColor Cyan
        $config = Get-SavedConfig
        $root = if ($config) { $config.WorktreeRoot } else { $null }

        if (-not $root) {
            Write-Host "Warning: could not find saved worktree root, skipping restart" -ForegroundColor Yellow
            return
        }

        Stop-ProductionServer
        Start-Sleep -Seconds 1
        Start-ProductionServer $root
    } else {
        Write-Host "Production server is not running, skipping restart" -ForegroundColor Gray
    }
}

switch ($Command) {
    "start" {
        $root = Resolve-WorktreeRoot "start"
        if (-not (Test-Path $root)) {
            Write-Host "Error: worktree root path does not exist: $root" -ForegroundColor Red
            exit 1
        }
        Start-ProductionServer $root
    }
    "stop" {
        Stop-ProductionServer
    }
    "restart" {
        $config = Get-SavedConfig
        $root = if ($WorktreeRoot) { $WorktreeRoot }
                elseif ($config) { $config.WorktreeRoot }
                else { $null }

        if (-not $root) {
            Write-Host "Error: no worktree root configured. Run 'start' first." -ForegroundColor Red
            exit 1
        }

        Stop-ProductionServer
        Start-Sleep -Seconds 1
        Start-ProductionServer $root
    }
    "status" {
        Show-Status
    }
    "log" {
        Show-Log
    }
    "dev" {
        $root = Resolve-WorktreeRoot "dev"
        if (-not (Test-Path $root)) {
            Write-Host "Error: worktree root path does not exist: $root" -ForegroundColor Red
            exit 1
        }
        Start-DevMode $root
    }
    "deploy" {
        Deploy-Frontend
    }
}
