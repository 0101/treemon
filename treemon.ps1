param(
    [Parameter(Position = 0)]
    [ValidateSet("start", "stop", "restart", "status", "log", "dev", "deploy", "demo", "add", "remove", "install-skill", "setup-ttyd")]
    [string]$Command,

    [Parameter(Position = 1, ValueFromRemainingArguments)]
    [string[]]$WorktreeRoots,

    [string]$Upstream = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$PidFile = Join-Path $ScriptDir ".treemon.pid"
$ConfigFile = Join-Path $ScriptDir ".treemon.config"
$TmScript = Join-Path $ScriptDir "tm.ps1"
$LogDir = Join-Path $ScriptDir "logs"
$LogFile = Join-Path $LogDir "treemon-prod.log"
$PublishDir = Join-Path $ScriptDir ".publish"
$WwwRoot = Join-Path $ScriptDir "wwwroot"
$DefaultPort = 5000
if ($env:TREEMON_PORT) {
    $parsed = 0
    if ([int]::TryParse($env:TREEMON_PORT, [ref]$parsed)) { $DefaultPort = $parsed }
}
# Canvas doc server port. Must match Program.fs `defaultCanvasPort` — treemon.ps1 never passes
# --canvas-port, so the server always binds this. It runs as a SEPARATE Kestrel host; a silent
# bind failure (e.g. the port still held after a restart) leaves the dashboard up on $DefaultPort
# while every canvas doc fails to load.
$CanvasPort = 5002

if (-not $Command -and $MyInvocation.InvocationName -ne ".") {
    Write-Host "Usage: .\treemon.ps1 <command> [worktree-root] [additional-roots...]" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Commands:" -ForegroundColor White
    Write-Host "  start [<path>...]          Start production server (auto-builds if wwwroot/ is empty)"
    Write-Host "                             No path uses the global config roots (~/.treemon/config.json)"
    Write-Host "  stop                       Stop the production server"
    Write-Host "  restart                    Stop + start (uses the global config roots)"
    Write-Host "  status                     Show production server status (lists roots via 'tm roots')"
    Write-Host "  log                        Tail the production server log"
    Write-Host "  dev [<path>...]            Start dev mode (server :5001 + Vite :5174), Ctrl+C to stop"
    Write-Host "  demo                       Start demo mode with fixture data (server :5001 + Vite :5174)"
    Write-Host "  deploy                     Build frontend, replace the app on the production port, and start this checkout"
    Write-Host "  add <path> [<path>...]     Add watched root(s) via 'tm add' (restarts prod if running)"
    Write-Host "    -Upstream <remote>         Set the upstream remote for PR/diff (written to .treemon.json)"
    Write-Host "  remove <path> [<path>...]  Remove watched root(s) via 'tm remove' (restarts prod if running)"
    Write-Host "  install-skill              Install the tm CLI skill for AI coding agents"
    Write-Host "  setup-ttyd                 Install the pinned ttyd executable for embedded terminals"
    exit 0
}

function Get-LegacyConfig {
    # Parses the legacy .treemon.config and extracts its watched roots, accepting BOTH the current
    # plural `WorktreeRoots` array and the pre-multi-repo singular `WorktreeRoot` string (older
    # versions wrote the singular key). Returns a result object { Parsed; Roots } so callers can tell
    # a parse failure (Parsed=$false) apart from a successfully-parsed file that simply declares no
    # roots (Parsed=$true, empty Roots) — a distinction PowerShell's empty-array collapse erases if
    # you signal it through the return value. The object property keeps Roots a real array.
    if (-not (Test-Path $ConfigFile)) {
        return [pscustomobject]@{ Parsed = $true; Roots = @() }
    }

    try {
        $config = Get-Content $ConfigFile -Raw | ConvertFrom-Json
    } catch {
        return [pscustomobject]@{ Parsed = $false; Roots = @() }
    }

    $roots = @()
    if ($config.PSObject.Properties.Name -contains "WorktreeRoots") {
        $roots = @($config.WorktreeRoots | Where-Object { $_ })
    } elseif ($config.PSObject.Properties.Name -contains "WorktreeRoot") {
        $roots = @($config.WorktreeRoot | Where-Object { $_ })
    }
    return [pscustomobject]@{ Parsed = $true; Roots = @($roots) }
}

function Read-LegacyRoots {
    # One-time migration of the legacy PowerShell-managed .treemon.config. Returns its roots (or
    # @()). Does NOT delete the file — Start-ProductionServer removes it only after the server has
    # started (and thus persisted the roots into ~/.treemon/config.json) AND only when every root it
    # declared was actually migrated, so a publish/start failure or an unrecognized config can't
    # silently lose roots.
    $legacy = Get-LegacyConfig
    if (-not $legacy.Parsed) {
        Write-Host "Warning: could not parse legacy .treemon.config; leaving it in place to avoid data loss" -ForegroundColor Yellow
        return @()
    }

    if ($legacy.Roots.Count -gt 0) {
        Write-Host "Migrating worktree roots from .treemon.config into global config" -ForegroundColor Gray
    }
    return @($legacy.Roots)
}

function Invoke-Tm([string[]]$TmArgs) {
    # Thin wrapper around the local tm CLI (src/Cli via tm.ps1). Returns ONLY the
    # CLI's integer exit code; the CLI's own stdout is routed to the host via Out-Host
    # so it is not captured into the return value (which would both hide the CLI's
    # messages and turn the returned exit code into an array). Wrapped in try/catch so
    # a non-zero CLI exit can never abort treemon.ps1.
    try {
        & $TmScript @TmArgs | Out-Host
        if ($null -eq $LASTEXITCODE) { return 0 }
        return [int]$LASTEXITCODE
    } catch {
        Write-Host $_.Exception.Message -ForegroundColor Red
        if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { return [int]$LASTEXITCODE }
        return 1
    }
}

function Get-RunningPid {
    if (-not (Test-Path $PidFile)) { return $null }
    $savedPid = (Get-Content $PidFile -Raw).Trim()
    if (-not $savedPid) { return $null }
    $process = Get-Process -Id $savedPid -ErrorAction SilentlyContinue
    if ($process -and -not $process.HasExited) { return [int]$savedPid }
    return $null
}

function Build-Frontend([string]$Destination = $WwwRoot) {
    Push-Location $ScriptDir
    try {
        # Ensure npm is available
        if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
            Write-Host "npm is not installed or not in PATH" -ForegroundColor Red
            $answer = Read-Host "Install Node.js via winget? (Y/n)"
            if ($answer -eq "" -or $answer -match "^[Yy]") {
                winget install OpenJS.NodeJS.LTS
                if ($LASTEXITCODE -ne 0) { throw "winget install failed" }
                # Refresh PATH so npm is available in this session
                $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
                if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
                    throw "npm still not found after install — restart your shell and try again"
                }
            } else {
                throw "npm is required — install Node.js from https://nodejs.org or run: winget install OpenJS.NodeJS.LTS"
            }
        }

        # Restore dotnet local tools (Fable compiler)
        dotnet tool restore
        if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

        npm install --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm install failed" }

        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }

        $distDir = Join-Path $ScriptDir "dist"
        if (-not (Test-Path $distDir)) { throw "dist/ not found after build" }

        if (-not (Test-Path $Destination)) {
            New-Item -ItemType Directory -Path $Destination | Out-Null
        }
        Get-ChildItem $Destination -Force | Remove-Item -Recurse -Force
        Copy-Item -Path (Join-Path $distDir "*") -Destination $Destination -Recurse -Force
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

function Get-TerminalHostStateDirectory {
    if (-not [string]::IsNullOrWhiteSpace($env:TREEMON_TERMINAL_HOST_STATE_DIR)) {
        return [IO.Path]::GetFullPath($env:TREEMON_TERMINAL_HOST_STATE_DIR)
    }

    $localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not $localData) {
        return [IO.Path]::Combine(
            [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
            ".treemon",
            "TerminalHost")
    }
    return [IO.Path]::Combine($localData, "Treemon", "TerminalHost")
}

function Test-TerminalHostDeployment([string]$PublishedServerDirectory) {
    $server = Join-Path $PublishedServerDirectory "Treemon.exe"
    if (-not (Test-Path -LiteralPath $server -PathType Leaf)) {
        throw "Published Treemon executable was not found at '$server'"
    }

    # The candidate's own control client is the compatibility authority.
    $output = @(& $server --terminal-host-deployment-preflight 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $detail = (($output -join " ") -replace '\p{C}', ' ').Trim()
        if (-not $detail -or $detail.Length -gt 1000) {
            $detail = "the candidate could not control the exact live TerminalHost"
        }
        throw "Deployment refused: $detail"
    }

    try {
        $result = $output -join [Environment]::NewLine | ConvertFrom-Json -Depth 4
    } catch {
        throw "Deployment refused: candidate Treemon returned an invalid preflight result"
    }

    if ($result.hasLiveHost -eq $false) {
        return [pscustomobject]@{
            HasLiveHost = $false
            Pid = $null
            ProcessStartTimeUtcTicks = $null
            TerminalCount = 0
            ExecutablePath = $null
        }
    }

    if ($result.hasLiveHost -ne $true -or
        $result.pid -isnot [long] -or
        $result.pid -le 0 -or
        $result.pid -gt [int]::MaxValue -or
        $result.processStartTimeUtcTicks -isnot [long] -or
        $result.processStartTimeUtcTicks -le 0 -or
        $result.terminalCount -isnot [long] -or
        $result.terminalCount -lt 0 -or
        $result.terminalCount -gt [int]::MaxValue -or
        $result.executablePath -isnot [string] -or
        -not [IO.Path]::IsPathFullyQualified($result.executablePath)) {
        throw "Deployment refused: candidate Treemon returned an invalid preflight result"
    }

    return [pscustomobject]@{
        HasLiveHost = $true
        Pid = [int]$result.pid
        ProcessStartTimeUtcTicks = $result.processStartTimeUtcTicks
        TerminalCount = [int]$result.terminalCount
        ExecutablePath = [IO.Path]::GetFullPath($result.executablePath)
    }
}

function Get-TerminalHostBundleDigest([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory)
    $rootInfo = Get-Item -LiteralPath $root -Force
    if (-not $rootInfo.PSIsContainer -or
        ($rootInfo.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Published TerminalHost directory is unsafe at '$root'"
    }
    $required = @(
        "TerminalHost.exe",
        "TerminalHost.dll",
        "TerminalHost.deps.json",
        "TerminalHost.runtimeconfig.json",
        "FSharp.Core.dll")
    if ([OperatingSystem]::IsWindows()) { $required += "ttyd.exe" }
    if (@($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf) }).Count -gt 0) {
        throw "Published TerminalHost output is incomplete at '$root'"
    }

    $items = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    if (@($items | Where-Object {
        $_.Attributes -band [IO.FileAttributes]::ReparsePoint
    }).Count -gt 0) {
        throw "Published TerminalHost output contains a reparse point"
    }

    $entries = @(
        $items |
            Where-Object { -not $_.PSIsContainer -and $_.Extension -cne ".pdb" } |
            ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
                "$relative`0$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
            } |
            Sort-Object
    )
    $bytes = [Text.Encoding]::UTF8.GetBytes($entries -join "`n")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-TerminalHostStageVersion([string]$PublishedHostDirectory, [string]$Digest) {
    $executable = Get-Item -LiteralPath (Join-Path $PublishedHostDirectory "TerminalHost.exe")
    $baseVersion = $executable.VersionInfo.ProductVersion
    if (-not $baseVersion) { $baseVersion = "host" }
    $baseVersion = ($baseVersion -replace '\+', '-' -replace '[^A-Za-z0-9._-]', '-').
        Trim([char[]]".-_")
    if (-not $baseVersion) { $baseVersion = "host" }

    $suffix = $Digest.Substring(0, 16)
    $maximumBaseLength = 128 - $suffix.Length - 1
    if ($baseVersion.Length -gt $maximumBaseLength) {
        $baseVersion = $baseVersion.Substring(0, $maximumBaseLength).TrimEnd([char[]]".-_")
    }
    return "$baseVersion-$suffix"
}

function Wait-TerminalHostStageReport(
    $LiveHost,
    [string]$StateDirectory,
    [string]$Version,
    [int]$TimeoutSeconds = 15
) {
    if (-not $LiveHost.HasLiveHost) { return }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $manifestPath = Join-Path $StateDirectory "host.json"

    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 4
        } catch {
            throw "The live TerminalHost manifest disappeared while staging"
        }
        if ([int]$manifest.pid -ne [int]$LiveHost.Pid -or
            [long]$manifest.processStartTimeUtcTicks -ne [long]$LiveHost.ProcessStartTimeUtcTicks) {
            throw "The exact live TerminalHost changed while staging"
        }

        if ($manifest.stagedExecutableVersion -ceq $Version) { return }
        try {
            $process = [Diagnostics.Process]::GetProcessById($LiveHost.Pid)
            try {
                if ($process.HasExited -or
                    $process.StartTime.ToUniversalTime().Ticks -ne $LiveHost.ProcessStartTimeUtcTicks) {
                    return
                }
            } finally {
                $process.Dispose()
            }
        } catch [ArgumentException] {
            return
        }
        Start-Sleep -Milliseconds 100
    }

    throw "The live TerminalHost did not report staged executable version '$Version'"
}

function Stage-TerminalHost(
    [string]$PublishedHostDirectory,
    $LiveHost,
    [string]$StateDirectory = (Get-TerminalHostStateDirectory),
    [string]$Version = ""
) {
    $source = [IO.Path]::GetFullPath($PublishedHostDirectory)
    $digest = Get-TerminalHostBundleDigest $source

    if ($LiveHost.HasLiveHost) {
        try {
            $liveDirectory = Split-Path -Parent $LiveHost.ExecutablePath
            if ((Get-TerminalHostBundleDigest $liveDirectory) -ceq $digest) {
                return [pscustomobject]@{
                    Changed = $false
                    Version = $null
                    ExecutablePath = $LiveHost.ExecutablePath
                }
            }
        } catch {
            # A live bundle that cannot be compared is left untouched; staging the candidate is safe.
        }
    }

    if (-not $Version) { $Version = Get-TerminalHostStageVersion $source $digest }
    if ($Version -notmatch '^[A-Za-z0-9._-]{1,128}$') {
        throw "TerminalHost staged executable version '$Version' is invalid"
    }

    $stagingRoot = Join-Path $StateDirectory "staged"
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    $destination = Join-Path $stagingRoot $Version
    $temporary =
        Join-Path $StateDirectory ".terminal-host-stage-$([Guid]::NewGuid().ToString('N'))"

    try {
        if (Test-Path -LiteralPath $destination) {
            $destinationInfo = Get-Item -LiteralPath $destination -Force
            if (-not $destinationInfo.PSIsContainer -or
                ($destinationInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                (Get-TerminalHostBundleDigest $destination) -cne $digest) {
                throw "Existing TerminalHost stage '$destination' does not match the candidate"
            }
        } else {
            New-Item -ItemType Directory -Path $temporary | Out-Null
            Get-ChildItem -LiteralPath $source -Force |
                Copy-Item -Destination $temporary -Recurse -Force
            if ((Get-TerminalHostBundleDigest $temporary) -cne $digest) {
                throw "TerminalHost stage verification failed"
            }

            try {
                [IO.Directory]::Move($temporary, $destination)
            } catch {
                if (-not (Test-Path -LiteralPath $destination) -or
                    (Get-TerminalHostBundleDigest $destination) -cne $digest) {
                    throw
                }
            }
        }

        (Get-Item -LiteralPath $destination).LastWriteTimeUtc = [DateTime]::UtcNow
        Wait-TerminalHostStageReport $LiveHost $StateDirectory $Version
        return [pscustomobject]@{
            Changed = $true
            Version = $Version
            ExecutablePath = Join-Path $destination "TerminalHost.exe"
        }
    } finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Recurse -Force
        }
    }
}

# True when a TCP port can be bound on loopback (i.e. nothing is listening). Mirrors how the
# canvas doc server binds (IPAddress.Loopback), and avoids the slow first-call cost of
# Get-NetTCPConnection so it's cheap to poll on the start hot-path.
function Test-PortFree([int]$Port) {
    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        $listener.Stop()
        return $true
    } catch {
        return $false
    }
}

# Poll until a TCP port is bindable, up to $TimeoutSec. Returns $true when free, $false on timeout.
function Wait-PortFree([int]$Port, [int]$TimeoutSec = 10) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        if (Test-PortFree $Port) { return $true }
        if ((Get-Date) -ge $deadline) { return $false }
        Start-Sleep -Milliseconds 300
    }
}

function Get-RunLogs([string]$Channel) {
    # Returns this scheme's per-run log files for a channel ("" = stdout, "-stderr" = stderr), newest
    # first. Each run writes a fresh treemon-prod[-stderr].<timestamp>.log, so a new run never touches
    # a previous log — which means a leftover server or a log still open in an editor/viewer can never
    # block startup the way truncating one shared file could.
    $pattern = "^treemon-prod$([regex]::Escape($Channel))\.\d{8}-\d{6}\.log$"
    @(Get-ChildItem -Path $LogDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $pattern } |
        Sort-Object LastWriteTime -Descending)
}

function Get-CurrentLogFile {
    # Newest stdout run log — the file the running server is writing to. Falls back to the canonical
    # name (which may not exist yet) so callers always have a meaningful path to report.
    $logs = Get-RunLogs ""
    if ($logs.Count -eq 0) { return $LogFile }
    return $logs[0].FullName
}

function Remove-OldRunLogs([int]$Keep) {
    # Keep only the most recent $Keep runs per channel; older logs are best-effort deleted (a log
    # still held open elsewhere simply survives until its holder releases it).
    foreach ($channel in @("", "-stderr")) {
        Get-RunLogs $channel | Select-Object -Skip $Keep |
            ForEach-Object { Remove-Item $_.FullName -ErrorAction SilentlyContinue }
    }
}

function Publish-ServerCandidate {
    $candidate = "$PublishDir.candidate-$([Guid]::NewGuid().ToString('N'))"
    try {
        Write-Host "Publishing server candidate..." -ForegroundColor Cyan
        dotnet publish -c Release -o $candidate (Join-Path $ScriptDir "src\Server\Server.fsproj") |
            Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

        $required = @(
            (Join-Path $candidate "Treemon.exe"),
            (Join-Path $candidate "terminal-host\TerminalHost.exe"),
            (Join-Path $candidate "terminal-host\TerminalHost.dll"))
        if (@($required | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -gt 0) {
            throw "Published server candidate is missing TerminalHost artifacts"
        }
        return $candidate
    } catch {
        if (Test-Path -LiteralPath $candidate) {
            Remove-Item -LiteralPath $candidate -Recurse -Force
        }
        throw
    }
}

function Test-PathWithin([string]$Path, [string]$Directory) {
    $comparison = if ([OperatingSystem]::IsWindows()) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullDirectory = [IO.Path]::GetFullPath($Directory).
        TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $prefix = "$fullDirectory$([IO.Path]::DirectorySeparatorChar)"
    return $fullPath.StartsWith($prefix, $comparison)
}

function Install-PreparedDirectory([string]$Candidate, [string]$Destination) {
    $backup = "$Destination.backup-$([Guid]::NewGuid().ToString('N'))"
    $hadDestination = Test-Path -LiteralPath $Destination
    if ($hadDestination) {
        [IO.Directory]::Move(
            [IO.Path]::GetFullPath($Destination),
            [IO.Path]::GetFullPath($backup))
    }

    try {
        [IO.Directory]::Move(
            [IO.Path]::GetFullPath($Candidate),
            [IO.Path]::GetFullPath($Destination))
    } catch {
        if ($hadDestination -and
            -not (Test-Path -LiteralPath $Destination) -and
            (Test-Path -LiteralPath $backup)) {
            [IO.Directory]::Move(
                [IO.Path]::GetFullPath($backup),
                [IO.Path]::GetFullPath($Destination))
        }
        throw
    }

    Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
}

function Install-ServerPublish(
    [string]$Candidate,
    [string]$LiveHostExecutable = ""
) {
    $publishedHostDirectory = Join-Path $PublishDir "terminal-host"
    $preservePublishedHost =
        $LiveHostExecutable -and
        (Test-PathWithin $LiveHostExecutable $publishedHostDirectory)

    if ($LiveHostExecutable -and
        (Test-PathWithin $LiveHostExecutable $PublishDir) -and
        -not $preservePublishedHost) {
        throw "The exact live TerminalHost is inside the server publish directory at an unexpected path"
    }

    if (-not $preservePublishedHost) {
        Install-PreparedDirectory $Candidate $PublishDir
        return
    }

    New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
    $backup = "$PublishDir.server-backup-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $backup | Out-Null
    $oldMoved = $false
    try {
        Get-ChildItem -LiteralPath $PublishDir -Force |
            Where-Object { $_.Name -ne "terminal-host" } |
            Move-Item -Destination $backup
        $oldMoved = $true
        Get-ChildItem -LiteralPath $Candidate -Force |
            Where-Object { $_.Name -ne "terminal-host" } |
            Move-Item -Destination $PublishDir
    } catch {
        if ($oldMoved) {
            Get-ChildItem -LiteralPath $PublishDir -Force |
                Where-Object { $_.Name -ne "terminal-host" } |
                Remove-Item -Recurse -Force
        }
        Get-ChildItem -LiteralPath $backup -Force |
            Move-Item -Destination $PublishDir
        Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }

    Remove-Item -LiteralPath $Candidate -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
}

function Start-ProductionProcess(
    [string[]]$Roots,
    [string]$TerminalHostExecutable
) {
    if (-not (Test-Path -LiteralPath $TerminalHostExecutable -PathType Leaf)) {
        throw "Prepared TerminalHost executable was not found at '$TerminalHostExecutable'"
    }

    if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }
    $stamp = Get-Date -Format yyyyMMdd-HHmmss
    $script:LogFile = Join-Path $LogDir "treemon-prod.$stamp.log"
    $stderrLog = Join-Path $LogDir "treemon-prod-stderr.$stamp.log"

    $effectiveRoots = @($Roots | Where-Object { $_ })
    if ($effectiveRoots.Count -eq 0) { $effectiveRoots = Read-LegacyRoots }

    # The server binds two Kestrel hosts: the dashboard on $DefaultPort and the canvas doc server on
    # $CanvasPort. If a port is still held when we launch — e.g. the previous server hasn't released
    # it yet after a restart — the dashboard surfaces the failure (it exits), but the canvas doc host
    # fails SILENTLY, leaving every canvas doc unable to load. Wait for both to clear, warn if not.
    foreach ($p in @($DefaultPort, $CanvasPort)) {
        if (-not (Wait-PortFree $p 10)) {
            $holder = (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
            Write-Host "Warning: port $p is still in use (PID $holder) after 10s." -ForegroundColor Yellow
            if ($p -eq $CanvasPort) {
                Write-Host "  The canvas doc server may fail to bind $CanvasPort, so canvas docs won't load." -ForegroundColor Yellow
                Write-Host "  Stop the process/other treemon instance holding it, then restart." -ForegroundColor Yellow
            }
        }
    }

    $serverExe = Join-Path $PublishDir "Treemon.exe"
    $rootArgs = ($effectiveRoots | ForEach-Object { "`"$($_.TrimEnd('\', '/'))`"" }) -join " "
    $serverArgs = if ($rootArgs) { "$rootArgs --port $DefaultPort" } else { "--port $DefaultPort" }

    Write-Host "Starting production server on port $DefaultPort..." -ForegroundColor Cyan
    $hadHostOverride = Test-Path Env:\TREEMON_TERMINAL_HOST_EXECUTABLE
    $previousHostOverride = $env:TREEMON_TERMINAL_HOST_EXECUTABLE
    try {
        $env:TREEMON_TERMINAL_HOST_EXECUTABLE = $TerminalHostExecutable
        $process = Start-Process -FilePath $serverExe `
            -ArgumentList $serverArgs `
            -WorkingDirectory $ScriptDir `
            -RedirectStandardOutput $LogFile `
            -RedirectStandardError $stderrLog `
            -NoNewWindow:$false `
            -WindowStyle Hidden `
            -PassThru
    } finally {
        if ($hadHostOverride) {
            $env:TREEMON_TERMINAL_HOST_EXECUTABLE = $previousHostOverride
        } else {
            Remove-Item Env:\TREEMON_TERMINAL_HOST_EXECUTABLE -ErrorAction SilentlyContinue
        }
    }

    $process.Id | Set-Content $PidFile

    Start-Sleep -Seconds 3

    if ($process.HasExited) {
        Remove-Item $PidFile -ErrorAction SilentlyContinue
        Write-Host "Production server failed to start (exit code: $($process.ExitCode))" -ForegroundColor Red
        $stderrFile = $stderrLog
        if ((Test-Path $stderrFile) -and (Get-Item $stderrFile).Length -gt 0) {
            Write-Host ""
            Get-Content $stderrFile | Select-Object -Last 5 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        }
        exit 1
    }

    Remove-OldRunLogs 10

    # Server is up — it has resolved+persisted its effective roots into the global config. Retire
    # the legacy .treemon.config only when it is SAFE: every root it declared was actually migrated
    # (handed to the server). A file we couldn't parse, or one whose roots we didn't migrate (e.g. an
    # explicit-path start that ignored it), is preserved with a warning so we never silently destroy
    # unmigrated roots. Deleting only after a confirmed start also means a publish/start failure
    # never loses the migrated roots.
    if (Test-Path $ConfigFile) {
        $legacy = Get-LegacyConfig
        if (-not $legacy.Parsed) {
            Write-Host "Warning: .treemon.config could not be parsed; leaving it in place to avoid data loss" -ForegroundColor Yellow
        } else {
            $migrated = @($effectiveRoots | ForEach-Object { $_.TrimEnd('\', '/').ToLowerInvariant() })
            $unmigrated = @($legacy.Roots | Where-Object { $_.TrimEnd('\', '/').ToLowerInvariant() -notin $migrated })
            if ($unmigrated.Count -eq 0) {
                Remove-Item $ConfigFile -ErrorAction SilentlyContinue
            } else {
                Write-Host "Warning: .treemon.config has roots that were not migrated ($($unmigrated -join ', ')); leaving it in place to avoid data loss" -ForegroundColor Yellow
            }
        }
    }

    Write-Host "Production server started (PID: $($process.Id))" -ForegroundColor Green
    if ($effectiveRoots.Count -gt 0) {
        $effectiveRoots | ForEach-Object { Write-Host "Monitoring: $_" -ForegroundColor Gray }
    } else {
        Write-Host "Monitoring roots from global config (~/.treemon/config.json)" -ForegroundColor Gray
    }
    Write-Host "URL: http://localhost:$DefaultPort" -ForegroundColor Gray
    Write-Host "Log: $LogFile" -ForegroundColor Gray
}

function Start-ProductionServer([string[]]$Roots) {
    $runningPid = Get-RunningPid
    if ($runningPid) {
        Write-Host "Production server is already running (PID: $runningPid)" -ForegroundColor Yellow
        Write-Host "  URL: http://localhost:$DefaultPort" -ForegroundColor Gray
        Write-Host "Use '.\treemon.ps1 stop' first or '.\treemon.ps1 restart'" -ForegroundColor Gray
        return
    }

    Ensure-WwwRoot
    $candidate = $null
    try {
        $candidate = Publish-ServerCandidate
        $publishedHost = Join-Path $candidate "terminal-host"
        $preflight = Test-TerminalHostDeployment $candidate
        if ($preflight.HasLiveHost) {
            Write-Host "Compatible live TerminalHost found (PID: $($preflight.Pid), terminals: $($preflight.TerminalCount))" -ForegroundColor Green
        }
        $staged = Stage-TerminalHost $publishedHost $preflight
        if ($staged.Changed) {
            Write-Host "TerminalHost staged as $($staged.Version)" -ForegroundColor Green
        }

        Install-ServerPublish $candidate $preflight.ExecutablePath
        $candidate = $null
        if (-not $staged.Changed) {
            $staged.ExecutablePath = Join-Path $PublishDir "terminal-host\TerminalHost.exe"
        }
        Start-ProductionProcess $Roots $staged.ExecutablePath
    } finally {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            Remove-Item -LiteralPath $candidate -Recurse -Force
        }
    }
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

function Stop-ProductionPortListeners {
    $listenerPids = @(
        Get-NetTCPConnection -LocalPort $DefaultPort -State Listen -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique |
            Where-Object { $_ -gt 0 }
    )

    $listenerPids | ForEach-Object {
        Write-Host "Stopping process on production port $DefaultPort (PID: $_)..." -ForegroundColor Yellow
        Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue
    }

    Remove-Item $PidFile -ErrorAction SilentlyContinue

    if (-not (Wait-PortFree $DefaultPort 10)) {
        throw "Production port $DefaultPort is still in use after stopping its listener"
    }
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

    Write-Host "Production server is running" -ForegroundColor Green
    Write-Host "  PID:     $runningPid"
    Write-Host "  Port:    $DefaultPort"
    Write-Host "  Uptime:  $uptimeStr"
    Write-Host "  URL:     http://localhost:$DefaultPort"

    # Canvas doc server health: a separate Kestrel host on $CanvasPort. A silent bind failure at
    # startup leaves the dashboard up here but every canvas doc unable to load, so surface it.
    $canvasConn = Get-NetTCPConnection -LocalPort $CanvasPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($canvasConn -and $canvasConn.OwningProcess -eq $runningPid) {
        Write-Host "  Canvas:  http://127.0.0.1:$CanvasPort (doc server up)"
    } elseif ($canvasConn) {
        Write-Host "  Canvas:  WARNING - port $CanvasPort is held by PID $($canvasConn.OwningProcess), not this server (PID $runningPid)" -ForegroundColor Yellow
    } else {
        Write-Host "  Canvas:  DOWN - nothing listening on $CanvasPort; canvas docs will not load. Run '.\treemon.ps1 restart' to rebind." -ForegroundColor Red
    }

    # Watched roots come from the server (the single source of truth) via `tm roots`.
    $rootLines = @()
    try {
        $rootsOutput = & $TmScript roots --port $DefaultPort 2>$null
        if ($LASTEXITCODE -eq 0) {
            $rootLines = @($rootsOutput | Where-Object { $_ -and $_.Trim() -and $_.Trim() -ne "No worktree roots configured." })
        }
    } catch { }
    if ($rootLines.Count -gt 0) {
        $rootLines | ForEach-Object { Write-Host "  Monitor: $_" }
    } else {
        Write-Host "  Monitor: (none configured)"
    }
    Write-Host "  Log:     $(Get-CurrentLogFile)"
}

function Show-Log {
    $logToTail = Get-CurrentLogFile
    if (-not (Test-Path $logToTail)) {
        Write-Host "No log file found at $logToTail" -ForegroundColor Yellow
        return
    }
    Write-Host "Tailing $logToTail (Ctrl+C to stop)..." -ForegroundColor Gray
    Get-Content $logToTail -Tail 50 -Wait
}

function Start-DualProcess([string]$ServerArgs, [string]$ModeName, [string]$ServerLabel, [string[]]$MonitorPaths) {
    $devApiPort = 5001
    $devVitePort = 5174

    Write-Host "Starting $ModeName mode..." -ForegroundColor Cyan
    Write-Host "  Server:  http://localhost:$devApiPort ($ServerLabel)" -ForegroundColor Gray
    Write-Host "  Vite:    http://localhost:$devVitePort" -ForegroundColor Gray
    Write-Host "  Press Ctrl+C to stop both processes" -ForegroundColor Gray
    if ($MonitorPaths) {
        $MonitorPaths | ForEach-Object { Write-Host "  Monitoring: $_" -ForegroundColor Gray }
    }
    Write-Host ""

    $env:VITE_PORT = $devVitePort
    $env:API_PORT = $devApiPort

    $serverProcess = $null
    $viteProcess = $null

    try {
        $serverProcess = Start-Process -FilePath "dotnet" `
            -ArgumentList "watch run --project `"$(Join-Path $ScriptDir "src/Server")`" -- $ServerArgs --port $devApiPort" `
            -WorkingDirectory $ScriptDir `
            -PassThru `
            -NoNewWindow

        $viteProcess = Start-Process -FilePath "cmd.exe" `
            -ArgumentList "/c", "npx", "vite", "--port", $devVitePort `
            -WorkingDirectory $ScriptDir `
            -PassThru `
            -NoNewWindow

        Write-Host "$ModeName server started (PID: $($serverProcess.Id)), Vite started (PID: $($viteProcess.Id))" -ForegroundColor Green

        while (-not $serverProcess.HasExited -and -not $viteProcess.HasExited) {
            Start-Sleep -Milliseconds 500
        }
    } finally {
        Write-Host ""
        Write-Host "Shutting down $ModeName processes..." -ForegroundColor Yellow

        if ($serverProcess -and -not $serverProcess.HasExited) {
            Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        }
        if ($viteProcess -and -not $viteProcess.HasExited) {
            Stop-Process -Id $viteProcess.Id -Force -ErrorAction SilentlyContinue
        }

        Remove-Item Env:\VITE_PORT -ErrorAction SilentlyContinue
        Remove-Item Env:\API_PORT -ErrorAction SilentlyContinue

        Write-Host "$ModeName mode stopped" -ForegroundColor Green
    }
}

function Start-DevMode([string[]]$Roots) {
    # Drop any $null/empty entries: an omitted path binds $Roots to $null, and
    # @($null) is a 1-element array that would call .TrimEnd() on $null below.
    $cleanRoots = @($Roots | Where-Object { $_ })
    $rootArgs = ($cleanRoots | ForEach-Object { "`"$($_.TrimEnd('\', '/'))`"" }) -join " "
    Start-DualProcess -ServerArgs $rootArgs -ModeName "Dev" -ServerLabel "dotnet watch" -MonitorPaths $cleanRoots
}

function Start-DemoMode {
    Start-DualProcess -ServerArgs "--demo" -ModeName "Demo" -ServerLabel "demo data"
}

function Set-UpstreamRemote([string]$RepoRoot, [string]$RemoteName) {
    $configPath = Join-Path $RepoRoot ".treemon.json"
    if (Test-Path $configPath) {
        $json = Get-Content $configPath -Raw | ConvertFrom-Json
    } else {
        $json = [PSCustomObject]@{}
    }
    $json | Add-Member -NotePropertyName "upstreamRemote" -NotePropertyValue $RemoteName -Force
    $json | ConvertTo-Json -Depth 10 | Set-Content $configPath
    Write-Host "  Upstream remote set to '$RemoteName' for $RepoRoot" -ForegroundColor Green
}

function Install-TmCommand {
    $shimDir = Join-Path $env:LOCALAPPDATA "tm-cli"
    $shimFile = Join-Path $shimDir "tm.cmd"
    $tmScript = Join-Path $PSScriptRoot "tm.ps1"

    if (-not (Test-Path $shimDir)) { New-Item -ItemType Directory -Path $shimDir | Out-Null }

    @"
@echo off
pwsh -NoProfile -File "$tmScript" %*
exit /b %ERRORLEVEL%
"@ | Set-Content $shimFile

    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $entries = $userPath -split ";" | Where-Object { $_ -ne "" }

    if ($entries -contains $shimDir) { return }

    $newPath = ($entries + $shimDir) -join ";"
    [Environment]::SetEnvironmentVariable("Path", $newPath, "User")

    if ($env:Path -notlike "*$shimDir*") {
        $env:Path = "$env:Path;$shimDir"
    }

    Write-Host "'tm' command installed (restart shells to pick it up)" -ForegroundColor Green
}

function Install-Skill {
    $skillSource = Join-Path $ScriptDir "src" "Cli" "skill" "SKILL.md"

    if (-not (Test-Path $skillSource)) {
        Write-Host "Error: skill file not found at $skillSource" -ForegroundColor Red
        return
    }

    $installed = @()

    # Claude Code: ~/.claude/skills/treemon-cli/SKILL.md
    $claudeDir = Join-Path $HOME ".claude" "skills" "treemon-cli"
    if (Test-Path (Join-Path $HOME ".claude")) {
        if (-not (Test-Path $claudeDir)) { New-Item -ItemType Directory -Path $claudeDir | Out-Null }
        Copy-Item $skillSource (Join-Path $claudeDir "SKILL.md") -Force
        $installed += "Claude Code"
    }

    # GitHub Copilot CLI: ~/.copilot/skills/treemon-cli/SKILL.md
    $copilotDir = Join-Path $HOME ".copilot" "skills" "treemon-cli"
    if (Test-Path (Join-Path $HOME ".copilot")) {
        if (-not (Test-Path $copilotDir)) { New-Item -ItemType Directory -Path $copilotDir | Out-Null }
        Copy-Item $skillSource (Join-Path $copilotDir "SKILL.md") -Force
        $installed += "GitHub Copilot CLI"
    }

    if ($installed.Count -eq 0) {
        Write-Host "Warning: no supported AI tool directories found" -ForegroundColor Yellow
        Write-Host "  Claude Code: ~/.claude/skills/ not found" -ForegroundColor Gray
        Write-Host "  GitHub Copilot CLI: ~/.copilot/skills/ not found" -ForegroundColor Gray
    } else {
        $installed | ForEach-Object { Write-Host "  Installed for $_" -ForegroundColor Green }
    }
}

function Install-CopilotExtension(
    [string]$SrcDir,
    [string]$DestName,
    [string]$FriendlyName,
    [string[]]$RequiredFiles
) {
    $dest = Join-Path $env:USERPROFILE ".copilot" "extensions" $DestName
    if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
    Get-ChildItem -Path $SrcDir -Filter "*.mjs" -File |
        Where-Object { $_.Name -notlike "*.test.mjs" } |
        Copy-Item -Destination $dest -Force
    $RequiredFiles | ForEach-Object { Copy-Item (Join-Path $SrcDir $_) $dest -Force }
    Write-Host "$FriendlyName installed to $dest" -ForegroundColor Green
}

function Install-Extension {
    $src = Join-Path $PSScriptRoot "src" "Extension"
    $requiredFiles = @(
        "package.json",
        "canvas-doc-kinds.json",
        "canvas-send.js",
        "canvas-selection-context.js"
    )
    Install-CopilotExtension $src "canvas-bridge" "Canvas bridge extension" $requiredFiles

    # Install canvas authoring skill
    $skillSource = Join-Path $src "skill" "SKILL.md"
    if (Test-Path $skillSource) {
        $installed = @()

        $copilotDir = Join-Path $HOME ".copilot" "skills" "canvas"
        if (Test-Path (Join-Path $HOME ".copilot")) {
            if (-not (Test-Path $copilotDir)) { New-Item -ItemType Directory -Path $copilotDir | Out-Null }
            Copy-Item $skillSource (Join-Path $copilotDir "SKILL.md") -Force
            $installed += "GitHub Copilot CLI"
        }

        $claudeDir = Join-Path $HOME ".claude" "skills" "canvas"
        if (Test-Path (Join-Path $HOME ".claude")) {
            if (-not (Test-Path $claudeDir)) { New-Item -ItemType Directory -Path $claudeDir | Out-Null }
            Copy-Item $skillSource (Join-Path $claudeDir "SKILL.md") -Force
            $installed += "Claude Code"
        }

        if ($installed.Count -gt 0) {
            $installed | ForEach-Object { Write-Host "  Canvas skill installed for $_" -ForegroundColor Green }
        }
    }
}

function Install-ReportingExtension {
    # Phase 1 of the push status model: the passive, reporting-only extension. Installed ALONGSIDE
    # canvas-bridge (a separate extension dir), never replacing it — reporting registers no canvas
    # and no tools, so both load per session with no canvas_take_ownership collision. It forwards
    # session-activity events to POST /api/session/activity; set TREEMON_PORTS (comma-separated) to
    # fan out to several Treemon instances (side-by-side validation), else it uses TREEMON_PORT/5000.
    $src = Join-Path $PSScriptRoot "src" "Extension" "reporting"
    Install-CopilotExtension $src "treemon-reporting" "Reporting extension" @("package.json")
}

function Test-WorktreeRootPaths([string[]]$Roots) {
    # Validate that each provided worktree root exists before launching the server.
    # exit inside a function still terminates the script, so callers need no extra guard.
    if ($Roots -and $Roots.Count -gt 0) {
        $Roots | ForEach-Object {
            if (-not (Test-Path $_)) {
                Write-Host "Error: worktree root path does not exist: $_" -ForegroundColor Red
                exit 1
            }
        }
    }
}

function Restart-ServerIfRunning {
    # Restart the production server only when it is currently running, so persisted
    # config changes (added/removed roots) take effect. Roots are re-read from the
    # global config at startup, so we restart with empty args (@()).
    $runningPid = Get-RunningPid
    if ($runningPid) {
        Write-Host "Restarting server to apply changes..." -ForegroundColor Cyan
        Stop-ProductionServer
        Start-Sleep -Seconds 1
        Start-ProductionServer @()
    }
}

function Deploy-Frontend {
    $frontendCandidate = "$WwwRoot.candidate-$([Guid]::NewGuid().ToString('N'))"
    $serverCandidate = $null
    try {
        Write-Host "Building frontend candidate..." -ForegroundColor Cyan
        Build-Frontend $frontendCandidate
        $serverCandidate = Publish-ServerCandidate

        $publishedHost = Join-Path $serverCandidate "terminal-host"
        $preflight = Test-TerminalHostDeployment $serverCandidate
        if ($preflight.HasLiveHost) {
            Write-Host "Deployment preflight will reuse TerminalHost PID $($preflight.Pid) with $($preflight.TerminalCount) terminal(s)." -ForegroundColor Green
        } else {
            Write-Host "Deployment preflight found no exact live TerminalHost." -ForegroundColor Gray
        }

        # Nothing active is changed until the candidate can control the exact live host.
        Stop-ProductionPortListeners
        $staged = Stage-TerminalHost $publishedHost $preflight
        if ($staged.Changed) {
            Write-Host "TerminalHost staged as $($staged.Version)" -ForegroundColor Green
        }

        Install-ServerPublish $serverCandidate $preflight.ExecutablePath
        $serverCandidate = $null
        if (-not $staged.Changed) {
            $staged.ExecutablePath = Join-Path $PublishDir "terminal-host\TerminalHost.exe"
        }
        Install-PreparedDirectory $frontendCandidate $WwwRoot
        $frontendCandidate = $null
        Write-Host "Frontend deployed to wwwroot/" -ForegroundColor Green

        try { Install-TmCommand } catch { Write-Host "Warning: tm command install failed: $_" -ForegroundColor Yellow }
        try { Install-Skill } catch { Write-Host "Warning: skill install failed: $_" -ForegroundColor Yellow }
        try { Install-Extension } catch { Write-Host "Warning: extension install failed: $_" -ForegroundColor Yellow }
        try { Install-ReportingExtension } catch { Write-Host "Warning: reporting extension install failed: $_" -ForegroundColor Yellow }

        Start-ProductionProcess @() $staged.ExecutablePath
    } finally {
        if ($serverCandidate -and (Test-Path -LiteralPath $serverCandidate)) {
            Remove-Item -LiteralPath $serverCandidate -Recurse -Force
        }
        if ($frontendCandidate -and (Test-Path -LiteralPath $frontendCandidate)) {
            Remove-Item -LiteralPath $frontendCandidate -Recurse -Force
        }
    }
}

if ($MyInvocation.InvocationName -eq ".") { return }

switch ($Command) {
    "start" {
        Test-WorktreeRootPaths $WorktreeRoots
        Start-ProductionServer $WorktreeRoots
    }
    "stop" {
        Stop-ProductionServer
    }
    "restart" {
        Stop-ProductionServer
        Start-Sleep -Seconds 1
        Start-ProductionServer $WorktreeRoots
    }
    "status" {
        Show-Status
    }
    "log" {
        Show-Log
    }
    "dev" {
        Test-WorktreeRootPaths $WorktreeRoots
        Start-DevMode $WorktreeRoots
    }
    "demo" {
        Start-DemoMode
    }
    "deploy" {
        Deploy-Frontend
    }
    "add" {
        if (-not $WorktreeRoots -or $WorktreeRoots.Count -eq 0) {
            Write-Host "Error: specify at least one path to add" -ForegroundColor Red
            Write-Host "Usage: .\treemon.ps1 add <path> [<path>...]" -ForegroundColor Gray
            exit 1
        }

        # Thin shim: the server (single config writer) persists the roots; the change
        # applies on the next (re)start, which we trigger below if prod is running.
        $tmExit = Invoke-Tm (@("add") + $WorktreeRoots + @("--port", "$DefaultPort"))

        if ($Upstream) {
            $WorktreeRoots | ForEach-Object {
                if (Test-Path $_) {
                    Set-UpstreamRemote ((Resolve-Path $_).Path.TrimEnd('\', '/')) $Upstream
                }
            }
        }

        # Restart when at least one root actually changed. tm returns a tri-state exit
        # code: 0 = all added, 2 = partial (some paths persisted, some rejected), 1 = all
        # failed. Both 0 and 2 mean roots were persisted and need a restart to apply; exit 1
        # (e.g. bad path, server down — nothing persisted) skips the restart so we don't
        # needlessly bounce the production server.
        if ($tmExit -eq 0 -or $tmExit -eq 2) { Restart-ServerIfRunning }
        exit $tmExit
    }
    "remove" {
        if (-not $WorktreeRoots -or $WorktreeRoots.Count -eq 0) {
            Write-Host "Error: specify at least one path to remove" -ForegroundColor Red
            Write-Host "Usage: .\treemon.ps1 remove <path> [<path>...]" -ForegroundColor Gray
            exit 1
        }

        # Thin shim: the server removes the root from global config; applies on next
        # (re)start, which we trigger below if prod is running. No existence check —
        # a root whose directory was deleted must still be removable.
        $tmExit = Invoke-Tm (@("remove") + $WorktreeRoots + @("--port", "$DefaultPort"))

        # Restart on full (0) or partial (2) success — see 'add' above. Exit 1 (nothing
        # removed) skips the restart.
        if ($tmExit -eq 0 -or $tmExit -eq 2) { Restart-ServerIfRunning }
        exit $tmExit
    }
    "install-skill" {
        Install-Skill
    }
    "setup-ttyd" {
        & (Join-Path $ScriptDir "scripts\setup-ttyd.ps1")
    }
}
