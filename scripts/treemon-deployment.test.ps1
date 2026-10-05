param([switch]$StartupOnly, [switch]$ShutdownOnly)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot "treemon.ps1")

function Assert-True($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Publish-TestProject([string]$Project, [string]$Destination, [string]$Version) {
    $buildRoot = "$Destination-build"
    dotnet publish $Project `
        -c Release `
        -o $Destination `
        "-p:InformationalVersion=$Version" `
        "-p:UseArtifactsOutput=true" `
        "-p:ArtifactsPath=$buildRoot" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Test project publish failed" }
}

function Copy-HostPublishSources([string]$Destination) {
    $paths = @(
        git -C $repoRoot -c core.quotePath=false ls-files -- `
            src\Server src\Shared src\TerminalHost src\TerminalHostLayout src\Extension global.json
    )
    if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) {
        throw "Could not list tracked host publish sources"
    }

    $destinationRoot = [IO.Path]::GetFullPath($Destination)
    $destinationPrefix = $destinationRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($relative in $paths) {
        $source = Join-Path $repoRoot $relative.Replace('/', '\')
        $target = [IO.Path]::GetFullPath((Join-Path $destinationRoot $relative.Replace('/', '\')))
        if (-not $target.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Tracked host publish source escaped the temporary checkout"
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }

    $runtime = ".tools\ttyd\1.7.7"
    $runtimeDestination = Join-Path $destinationRoot $runtime
    New-Item -ItemType Directory -Path $runtimeDestination -Force | Out-Null
    foreach ($name in @("ttyd.exe", "LICENSE.txt")) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $repoRoot $runtime) $name) `
            -Destination $runtimeDestination
    }
}

function Get-TestPort {
    do {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
        $listener.Stop()
    } while ($port -in @(5000, 5002))
    return $port
}

function Start-TestHost([string]$Executable, [string]$StateDirectory, [int]$Port) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new($Executable)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add("--state-dir")
    $startInfo.ArgumentList.Add($StateDirectory)
    $startInfo.ArgumentList.Add("--port")
    $startInfo.ArgumentList.Add("$Port")
    $process = [Diagnostics.Process]::Start($startInfo)
    if (-not $process) { throw "Could not start the test TerminalHost" }
    return $process
}

function Wait-Manifest([string]$StateDirectory) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $manifest =
                Get-Content -LiteralPath (Join-Path $StateDirectory "host.json") -Raw |
                ConvertFrom-Json
            if ($manifest) { return $manifest }
        } catch {
            # The host atomically publishes the manifest; retry while it starts.
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Test TerminalHost did not publish its manifest"
}

function Invoke-TestHostRequest($Manifest, [string]$Method, [string]$Path, [string]$Body = "") {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler, $true)
    $request = [Net.Http.HttpRequestMessage]::new(
        [Net.Http.HttpMethod]::new($Method),
        [Uri]::new([Uri]$Manifest.endpoint, $Path))
    $request.Headers.Authorization =
        [Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $Manifest.bearerToken)
    if ($Body) {
        $request.Content = [Net.Http.StringContent]::new(
            $Body,
            [Text.Encoding]::UTF8,
            "application/json")
    }

    try {
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) {
                throw "Test TerminalHost returned HTTP $([int]$response.StatusCode)"
            }
            $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if (-not $json) { return $null }
            return $json | ConvertFrom-Json -Depth 16
        } finally {
            $response.Dispose()
        }
    } finally {
        $request.Dispose()
        $client.Dispose()
    }
}

function Get-DirectorySnapshot([string]$Directory) {
    $files = @(Get-ChildItem -LiteralPath $Directory -File -Recurse -Force)
    return @(Get-FileFingerprintEntries $Directory $files) -join "`n"
}

function Stop-TestHost($Process, $Manifest) {
    if (-not $Process -or $Process.HasExited) { return }
    try {
        Invoke-TestHostRequest $Manifest "POST" "/api/v2/shutdown" | Out-Null
        if (-not $Process.WaitForExit(10000)) {
            throw "Test TerminalHost did not stop after shutdown"
        }
    } catch {
        if (-not $Process.HasExited -and
            $Process.StartTime.ToUniversalTime().Ticks -eq [long]$Manifest.processStartTimeUtcTicks) {
            $Process.Kill($true)
            if (-not $Process.WaitForExit(5000)) {
                throw "Fixture-owned TerminalHost survived cleanup"
            }
        }
    }
}

function Test-IsolatedProductionShutdown([string]$Executable, [string]$Directory) {
    $DefaultPort = Get-TestPort
    $PublishDir = Split-Path -Parent $Executable
    $config = Join-Path $Directory "config"
    New-Item -ItemType Directory -Path $config -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $Directory "wwwroot") | Out-Null
    Set-Content -LiteralPath (Join-Path $config "config.json") -Value '{"worktreeRoots":[]}'
    $startInfo = [Diagnostics.ProcessStartInfo]::new($Executable)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = $Directory
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment["TREEMON_CONFIG_DIR"] = $config
    $startInfo.Environment["TREEMON_TERMINAL_HOST_STATE_DIR"] = Join-Path $Directory "host-state"
    $startInfo.Environment["TREEMON_TERMINAL_HOST_EXECUTABLE"] = Join-Path $PublishDir "terminal-host\TerminalHost.exe"
    foreach ($argument in @("--port", "$DefaultPort", "--no-canvas", "--log-dir", (Join-Path $Directory "logs"))) {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        Assert-True (Wait-ProductionListening $process $DefaultPort 30) "Isolated shutdown fixture did not listen"
        $wrongProcessRefused = $false
        try { Request-ProductionShutdown 0 | Out-Null } catch {
            $wrongProcessRefused = $_.Exception.Message -ceq
                "Production shutdown was rejected (HTTP 409); the server was not force-stopped"
        }
        Assert-True (
            $wrongProcessRefused -and -not $process.HasExited
        ) "Wrong-process shutdown was not refused without stopping the fixture"

        Stop-ProductionProcess $process
        Assert-True (
            $process.ExitCode -eq 0
        ) "Graceful shutdown fixture failed: $($stderr.GetAwaiter().GetResult())"
        $database = Join-Path $Directory "data\session-activity-$DefaultPort.db"
        Assert-True (
            (Test-Path -LiteralPath $database) -and
            -not (Test-Path -LiteralPath "$database-wal") -and
            -not (Test-Path -LiteralPath "$database-shm")
        ) "Graceful shutdown did not checkpoint WAL and release shared memory"
        $guard = [IO.FileStream]::new($database, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $guard.Dispose()
        $log = Get-ChildItem -LiteralPath (Join-Path $Directory "logs") -Filter "server-*.log" |
            Select-Object -First 1 |
            Get-Content -Raw
        Assert-True (
            $log.Contains("[Shutdown] Stopping session activity")
        ) "The graceful HTTP shutdown did not drain the activity runtime"
        Write-Host "PASS: published server accepts exact-process shutdown, drains SQLite, and leaves no WAL/SHM"
    } finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            Assert-True ($process.WaitForExit(10000)) "Fixture-owned server survived cleanup"
        }
        Set-Content -LiteralPath (Join-Path $Directory "stdout.log") -Value $stdout.GetAwaiter().GetResult()
        Set-Content -LiteralPath (Join-Path $Directory "stderr.log") -Value $stderr.GetAwaiter().GetResult()
        $process.Dispose()
    }
}

$root = Join-Path ([IO.Path]::GetTempPath()) "treemon-deploy-test-$([Guid]::NewGuid().ToString('N'))"
$legacyPublish = Join-Path $root "legacy-active"
$baseline = Join-Path $legacyPublish "terminal-host"
$candidateServer = $null
$state = Join-Path $root "state"
$emptyState = Join-Path $root "empty-state"
$worktree = Join-Path $root "worktree"
$originalPublishDir = $PublishDir
$originalPidFile = $PidFile
$originalWwwRoot = $WwwRoot
$originalLogDir = $LogDir
$originalLogFile = $LogFile
$originalDefaultPort = $DefaultPort
$originalCanvasPort = $CanvasPort
$hadStateOverride = Test-Path Env:\TREEMON_TERMINAL_HOST_STATE_DIR
$previousStateOverride = $env:TREEMON_TERMINAL_HOST_STATE_DIR
$hadTerminalSessionId = Test-Path Env:\TREEMON_TERMINAL_SESSION_ID
$previousTerminalSessionId = $env:TREEMON_TERMINAL_SESSION_ID
$hostProcess = $null
$manifest = $null
$embeddedListener = $null

try {
    Remove-Item Env:\TREEMON_TERMINAL_SESSION_ID -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $root | Out-Null

    foreach ($address in @([Net.IPAddress]::Loopback, [Net.IPAddress]::IPv6Loopback)) {
        $portListener = [Net.Sockets.TcpListener]::new($address, 0)
        $client = [Net.Sockets.TcpClient]::new($address.AddressFamily)
        $accepted = $null
        try {
            $portListener.Start()
            $port = $portListener.LocalEndpoint.Port
            Assert-True ($port -notin @(5000, 5001, 5002, 5174)) "Port fixture selected a reserved port"
            Assert-True (-not (Wait-PortFree $port 0)) "An active $address listener counted as released"
            $client.Connect($address, $port)
            $accepted = $portListener.AcceptTcpClient()
            $client.SendTimeout = 5000
            $accepted.ReceiveTimeout = 5000
            $portListener.Stop()
            Assert-True (Wait-PortFree $port 0) "A lingering $address connection counted as a listener"
            Assert-True (
                $client.Client.Send([byte[]]@(42)) -eq 1 -and
                $accepted.Client.Receive([byte[]]::new(1)) -eq 1
            ) "The lingering connection fixture was not active"
            $replacement = [Net.Sockets.TcpListener]::new($address, $port)
            try {
                $replacement.Start()
                Assert-True (
                    Wait-ProductionListening ([pscustomobject]@{ Id = $PID; HasExited = $false }) $port 5
                ) "The replacement listener was not recognized while the old connection lingered"
            } finally {
                $replacement.Stop()
            }
        } finally {
            if ($accepted) { $accepted.Dispose() }
            $client.Dispose()
            $portListener.Stop()
        }
        Assert-True (Wait-PortFree $port 0) "A closed $address connection counted as a listener"
    }
    Write-Host "PASS: port release observes IPv4/IPv6 listeners without counting lingering connections"

    & {
        $DefaultPort = Get-TestPort
        $ScriptDir = $root
        $PidFile = Join-Path $root "listener.pid"
        $listenerConnections = @(
            [pscustomobject]@{ LocalAddress = "127.0.0.1"; OwningProcess = 123 },
            [pscustomobject]@{ LocalAddress = "::1"; OwningProcess = 123 }
        )
        $portReleased = $false
        $script:listenerProcessPresent = $true
        $script:stoppedListenerPids = @()

        function Get-NetTCPConnection {
            [CmdletBinding()]
            param([int]$LocalPort, [string]$State)
            Assert-True (
                $LocalPort -eq $DefaultPort -and $State -ceq "Listen"
            ) "Deployment queried connections other than the fixture's listeners"
            return $listenerConnections
        }
        function Stop-Process {
            [CmdletBinding()]
            param([int]$Id, [switch]$Force)
            $script:stoppedListenerPids += $Id
        }
        function Get-Process {
            [CmdletBinding()]
            param([int]$Id)
            if (-not $script:listenerProcessPresent) { return $null }
            $process = [pscustomobject]@{ Id = $Id; HasExited = $false }
            $process | Add-Member -MemberType ScriptMethod -Name Dispose -Value {}
            return $process
        }
        function Stop-ProductionProcess($Process) {
            Stop-Process -Id $Process.Id -Force
        }
        function Wait-PortFree([int]$Port, [int]$TimeoutSec) {
            Assert-True (
                $Port -eq $DefaultPort -and $TimeoutSec -eq 10
            ) "Deployment did not wait for its listener shutdown"
            return $portReleased
        }

        Set-Content -LiteralPath $PidFile -Value "123" -NoNewline
        $shutdownError = $null
        try {
            Stop-ProductionPortListeners
        } catch {
            $shutdownError = $_.Exception.Message
        }
        Assert-True (
            $shutdownError -ceq
            "Production port $DefaultPort still has a TCP listener after 10s: 127.0.0.1 (PID: 123), ::1 (PID: 123)"
        ) "Listener timeout omitted the remaining addresses or owning process"
        Assert-True (
            (Get-Content -LiteralPath $PidFile -Raw) -ceq "123" -and
            ($script:stoppedListenerPids -join ",") -ceq "123"
        ) "Failed shutdown removed the PID file or stopped a duplicate listener process"

        $portReleased = $true
        $script:stoppedListenerPids = @()
        Stop-ProductionPortListeners
        Assert-True (
            -not (Test-Path -LiteralPath $PidFile) -and
            ($script:stoppedListenerPids -join ",") -ceq "123"
        ) "Successful shutdown did not remove the PID file after stopping the listener"

        $listenerConnections = @()
        $script:listenerProcessPresent = $false
        $script:stoppedListenerPids = @()
        Set-Content -LiteralPath $PidFile -Value "123" -NoNewline
        Stop-ProductionPortListeners
        Assert-True (
            -not (Test-Path -LiteralPath $PidFile) -and
            $script:stoppedListenerPids.Count -eq 0
        ) "An already-stopped server triggered another process stop or kept its stale PID file"
    }
    Write-Host "PASS: listener shutdown reports remaining owners and preserves failed-shutdown state"

    & {
        $PublishDir = Join-Path $root "process-stop"
        $process = [pscustomobject]@{
            Id = 123
            Path = Join-Path $PublishDir "Treemon.exe"
            HasExited = $false
        }
        $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
            param($timeout)
            $script:shutdownEvents += "wait-$timeout"
            return $script:shutdownExitConfirmed
        }
        function Request-ProductionShutdown([int]$ProcessId) {
            $script:shutdownEvents += "request-$ProcessId"
            return $script:shutdownAccepted
        }
        function Stop-Process {
            [CmdletBinding()]
            param([int]$Id, [switch]$Force)
            $script:shutdownEvents += "force-$Id"
        }

        $script:shutdownAccepted = $true
        $script:shutdownExitConfirmed = $true
        $script:shutdownEvents = @()
        Stop-ProductionProcess $process
        Assert-True (
            ($script:shutdownEvents -join "|") -ceq "request-123|wait-30000"
        ) "Accepted graceful shutdown forced the process or did not wait for its exit"

        $script:shutdownExitConfirmed = $false
        $script:shutdownEvents = @()
        $timeoutError = $null
        try { Stop-ProductionProcess $process } catch { $timeoutError = $_.Exception.Message }
        Assert-True (
            $timeoutError -ceq "Production PID 123 did not exit within 30s after accepting shutdown; deployment was stopped" -and
            ($script:shutdownEvents -join "|") -ceq "request-123|wait-30000"
        ) "A graceful shutdown timeout triggered a forced stop or lost its diagnostic"

        $script:shutdownAccepted = $false
        $script:shutdownExitConfirmed = $true
        $script:shutdownEvents = @()
        Stop-ProductionProcess $process
        Assert-True (
            ($script:shutdownEvents -join "|") -ceq "request-123|force-123|wait-10000"
        ) "Legacy shutdown did not force only the exact process and await its exit"

        $process.Path = Join-Path $root "unrelated.exe"
        $script:shutdownEvents = @()
        $unrelatedError = $null
        try { Stop-ProductionProcess $process } catch { $unrelatedError = $_.Exception.Message }
        Assert-True (
            $unrelatedError -ceq "Refusing to stop non-production process PID 123 on port $DefaultPort" -and
            $script:shutdownEvents.Count -eq 0
        ) "An unrelated port owner was sent a shutdown request or force-stopped"
    }
    Write-Host "PASS: exact-process shutdown waits for graceful exit and refuses unrelated processes"

    & {
        $PublishDir = Join-Path $root "shutdown-response"
        $process = [pscustomobject]@{
            Id = 123
            Handle = 0
            Path = Join-Path $PublishDir "Treemon.exe"
            HasExited = $false
        }
        $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
            param($timeout)
            $script:shutdownResponseEvents += "wait-$timeout"
            return $true
        }
        function Stop-Process {
            [CmdletBinding()]
            param([int]$Id, [switch]$Force)
            $script:shutdownResponseEvents += "force-$Id"
        }

        $scenarios = @(
            @{
                Name = "incomplete shutdown acceptance"
                Response = "HTTP/1.1 202 Accepted`r`nContent-Length: 17`r`nConnection: close`r`n`r`nShutdown"
                ExpectedError = "Production shutdown response could not be confirmed; the server was not force-stopped"
                ExpectedEvents = ""
            },
            @{
                Name = "explicit legacy endpoint absence"
                Response = "HTTP/1.1 404 Not Found`r`nContent-Length: 0`r`nConnection: close`r`n`r`n"
                ExpectedError = $null
                ExpectedEvents = "force-123|wait-10000"
            }
        )
        foreach ($scenario in $scenarios) {
            $DefaultPort = Get-TestPort
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $DefaultPort)
            $listener.Start()
            $responder = [PowerShell]::Create()
            try {
                $null = $responder.AddScript({
                    param($listener, $response)
                    $ErrorActionPreference = "Stop"
                    $client = $listener.AcceptTcpClientAsync().
                        WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
                    try {
                        $client.ReceiveTimeout = 5000
                        $client.SendTimeout = 5000
                        $stream = $client.GetStream()
                        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII)
                        try {
                            $headers = @()
                            do {
                                $line = $reader.ReadLine()
                                if ($null -eq $line) { throw "Shutdown fixture did not receive complete request headers" }
                                $headers += $line
                            } while ($line.Length -gt 0)
                            $bytes = [Text.Encoding]::ASCII.GetBytes($response)
                            $stream.Write($bytes, 0, $bytes.Length)
                            return $headers
                        } finally {
                            $reader.Dispose()
                        }
                    } finally {
                        $client.Dispose()
                    }
                }).AddArgument($listener).AddArgument($scenario.Response)
                $pending = $responder.BeginInvoke()
                $script:shutdownResponseEvents = @()
                $shutdownError = $null
                try { Stop-ProductionProcess $process } catch { $shutdownError = $_.Exception.Message }
                $headers = @($responder.EndInvoke($pending))
                Assert-True (
                    -not $responder.HadErrors -and
                    $headers[0] -ceq "POST /api/server/shutdown HTTP/1.1" -and
                    $headers -contains "X-Treemon-Process-Id: 123"
                ) "The $($scenario.Name) fixture did not receive the exact-process shutdown request"
                Assert-True (
                    $shutdownError -ceq $scenario.ExpectedError -and
                    ($script:shutdownResponseEvents -join "|") -ceq $scenario.ExpectedEvents
                ) "Unexpected process-stop behavior for $($scenario.Name): error='$shutdownError', events='$($script:shutdownResponseEvents -join "|")'"
            } finally {
                $listener.Stop()
                $responder.Dispose()
            }
        }
    }
    Write-Host "PASS: incomplete shutdown acceptance aborts without force, while explicit 404 retains the legacy stop"

    & {
        $ScriptDir = Join-Path $root "sqlite-recovery"
        $DefaultPort = Get-TestPort
        $dataDirectory = Join-Path $ScriptDir "data"
        New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
        $database = Join-Path $dataDirectory "session-activity-$DefaultPort.db"
        $sharedMemory = "$database-shm"
        [IO.File]::WriteAllBytes($database, [byte[]]@(1, 2, 3))
        [IO.File]::WriteAllBytes("$database-wal", [byte[]]@(4, 5, 6))
        [IO.File]::WriteAllBytes($sharedMemory, [byte[]]@(7, 8, 9))
        $reader = [IO.FileStream]::new($database, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $inUseRefused = $false
            try { Repair-ProductionSqliteSharedMemory } catch { $inUseRefused = $true }
            Assert-True (
                $inUseRefused -and (Test-Path -LiteralPath $sharedMemory) -and
                @(Get-ChildItem -LiteralPath $dataDirectory -Filter "*.stale-*").Count -eq 0
            ) "SQLite recovery changed shared memory while a database reader was open"
        } finally {
            $reader.Dispose()
        }

        $walReader = [IO.FileStream]::new("$database-wal", [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $inUseRefused = $false
            try { Repair-ProductionSqliteSharedMemory } catch { $inUseRefused = $true }
            Assert-True (
                $inUseRefused -and (Test-Path -LiteralPath $sharedMemory)
            ) "SQLite recovery changed shared memory while the WAL was open"
        } finally {
            $walReader.Dispose()
        }

        $stream = [IO.FileStream]::new(
            $sharedMemory, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $mapping = [IO.MemoryMappedFiles.MemoryMappedFile]::CreateFromFile(
            $stream, [NullString]::Value, 0, [IO.MemoryMappedFiles.MemoryMappedFileAccess]::ReadWrite,
            [IO.HandleInheritability]::None, $true)
        $view = $mapping.CreateViewAccessor()
        $stream.Dispose()
        try {
            if ([OperatingSystem]::IsWindows()) {
                $truncate = [IO.FileStream]::new(
                    $sharedMemory, [IO.FileMode]::Open, [IO.FileAccess]::Write,
                    ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                try {
                    $truncateRefused = $false
                    try { $truncate.SetLength(0) } catch { $truncateRefused = $true }
                    Assert-True $truncateRefused "The mapped shared-memory fixture did not reproduce the truncate failure"
                } finally {
                    $truncate.Dispose()
                }
            }
            Repair-ProductionSqliteSharedMemory
            Assert-True ($view.ReadByte(0) -eq 7) "Recovery invalidated the old shared-memory mapping"
        } finally {
            $view.Dispose()
            $mapping.Dispose()
        }
        $preserved = @(Get-ChildItem -LiteralPath $dataDirectory -Filter "*.stale-*")
        Assert-True (
            -not (Test-Path -LiteralPath $sharedMemory) -and $preserved.Count -eq 1 -and
            ([IO.File]::ReadAllBytes($preserved[0].FullName) -join ",") -ceq "7,8,9" -and
            ([IO.File]::ReadAllBytes($database) -join ",") -ceq "1,2,3" -and
            ([IO.File]::ReadAllBytes("$database-wal") -join ",") -ceq "4,5,6"
        ) "SQLite recovery did not preserve shared memory or modified the database/WAL"
        Repair-ProductionSqliteSharedMemory
        Assert-True (
            @(Get-ChildItem -LiteralPath $dataDirectory -Filter "*.stale-*").Count -eq 1
        ) "SQLite recovery was not idempotent when shared memory was absent"
    }
    Write-Host "PASS: offline SQLite recovery preserves shared memory and refuses live database/WAL readers"

    & {
        $destination = Join-Path $root "directory-swap"
        $candidate = Join-Path $root "directory-candidate"
        New-Item -ItemType Directory -Path $destination, $candidate | Out-Null
        Set-Content -LiteralPath (Join-Path $destination "old.txt") -Value "old"
        Set-Content -LiteralPath (Join-Path $candidate "new.txt") -Value "new"
        $originalMove = (Get-Item Function:\Move-DeploymentDirectory).ScriptBlock
        function Move-DeploymentDirectory([string]$Source, [string]$Destination) {
            & $originalMove $Source $Destination 0
        }
        if ([OperatingSystem]::IsWindows()) {
            $lockedFile = [IO.FileStream]::new(
                (Join-Path $candidate "new.txt"), [IO.FileMode]::Open,
                [IO.FileAccess]::Read, [IO.FileShare]::None)
            try {
                $swapRefused = $false
                try { Install-PreparedDirectory $candidate $destination } catch { $swapRefused = $true }
                Assert-True (
                    $swapRefused -and
                    (Test-Path -LiteralPath (Join-Path $destination "old.txt")) -and
                    (Test-Path -LiteralPath (Join-Path $candidate "new.txt"))
                ) "A locked candidate swap did not restore the previous directory"
            } finally {
                $lockedFile.Dispose()
            }
        }
        Install-PreparedDirectory $candidate $destination
        Assert-True (
            (Test-Path -LiteralPath (Join-Path $destination "new.txt")) -and
            -not (Test-Path -LiteralPath (Join-Path $destination "old.txt")) -and
            -not (Test-Path -LiteralPath $candidate)
        ) "Directory installation did not succeed after the candidate lock was released"
    }
    Write-Host "PASS: a locked candidate preserves the previous deployment and installs after release"

    $testPort = Get-TestPort
    $testListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $testPort)
    $testListener.Start()
    try {
        $liveProcess = [pscustomobject]@{ Id = $PID; HasExited = $false }
        $otherProcess = [pscustomobject]@{ Id = 0; HasExited = $false }
        $exitedProcess = [pscustomobject]@{ Id = $PID; HasExited = $true }
        Assert-True (Wait-ProductionListening $liveProcess $testPort 5) "The server's own listener was not recognized"
        Assert-True (-not (Wait-ProductionListening $otherProcess $testPort 0)) "Another process's listener counted as readiness"
        Assert-True (-not (Wait-ProductionListening $exitedProcess $testPort 0)) "An exited server counted as ready"
    } finally {
        $testListener.Stop()
    }
    Assert-True (-not (Wait-ProductionListening $liveProcess (Get-TestPort) 0)) "A server without a listener counted as ready"
    $script:exitProbeCount = 0
    $delayedExit = [pscustomobject]@{ Id = 0 }
    $delayedExit | Add-Member -MemberType ScriptProperty -Name HasExited -Value {
        $script:exitProbeCount++
        return $script:exitProbeCount -ge 3
    }
    Assert-True (
        -not (Wait-ProductionListening $delayedExit (Get-TestPort) 10) -and
        $script:exitProbeCount -ge 3
    ) "A server that exits after the first readiness check counted as ready"

    $stderrFixture = Join-Path $root "startup-stderr.log"
    $stdoutFixture = Join-Path $root "startup-stdout.log"
    Set-Content -LiteralPath $stderrFixture -Value @(
        "Unhandled exception. Microsoft.Data.Sqlite.SqliteException: SQLite Error 10: 'disk I/O error'.",
        "   at Example.openConnection()"
    )
    $startupOutput = Show-StartupLogs $stdoutFixture $stderrFixture 6>&1 | Out-String
    Assert-True (
        $startupOutput.Contains("SQLite Error 10: 'disk I/O error'") -and
        $startupOutput.Contains("Stderr log: $stderrFixture") -and
        $startupOutput.Contains("preserve the database and WAL/SHM files")
    ) "Startup diagnostics omitted the SQLite error or its log paths"
    Write-Host "PASS: startup waits for its own listener and reports SQLite failures"

    if ($StartupOnly) { return }

    $script:publishSetupEvents = @()
    $PublishDir = Join-Path $root "setup-order"
    $originalInstallTtydRuntime = (Get-Item Function:\Install-TtydRuntime).ScriptBlock
    try {
        function Install-TtydRuntime {
            $script:publishSetupEvents += "setup-ttyd"
        }
        function dotnet {
            $script:publishSetupEvents += "dotnet-publish"
            $outputIndex = [Array]::IndexOf($args, "-o")
            $outputPath = [string]$args[$outputIndex + 1]
            New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $outputPath "Treemon.exe") `
                -Value "fixture" -NoNewline
            $global:LASTEXITCODE = 0
        }

        $setupOrderCandidate = Publish-ServerCandidate
        Assert-True (
            ($script:publishSetupEvents -join "|") -ceq "setup-ttyd|dotnet-publish"
        ) "Server publication did not install ttyd before dotnet publish"
        Write-Host "PASS: server publication installs ttyd before dotnet publish"
    } finally {
        Set-Item Function:\Install-TtydRuntime $originalInstallTtydRuntime
        Remove-Item Function:\dotnet -ErrorAction SilentlyContinue
        if ($setupOrderCandidate -and (Test-Path -LiteralPath $setupOrderCandidate)) {
            Remove-Item -LiteralPath $setupOrderCandidate -Recurse -Force
        }
    }

    $PublishDir = Join-Path $root "candidate-active"
    $candidateBuildRoot = Join-Path $root "candidate-build"
    $candidateServer = Publish-ServerCandidate -AdditionalPublishArguments @(
        "-p:UseArtifactsOutput=true"
        "-p:ArtifactsPath=$candidateBuildRoot"
    )
    Assert-True ($candidateServer -is [string]) "Server candidate path was not scalar"
    Test-IsolatedProductionShutdown (Join-Path $candidateServer "Treemon.exe") (Join-Path $root "shutdown-server")
    if ($ShutdownOnly) { return }
    $candidateHost = Join-Path $candidateServer "terminal-host"
    Assert-True (
        -not (Test-Path -LiteralPath (Join-Path $candidateHost "Shared.dll") -PathType Leaf)
    ) "Published TerminalHost bundle still contained Shared.dll"
    $revisionStampedHostAssemblies = @(
        @("TerminalHost.exe", "TerminalHost.dll", "TerminalHostLayout.dll") |
            Where-Object {
                $version = [Diagnostics.FileVersionInfo]::GetVersionInfo(
                    (Join-Path $candidateHost $_)
                ).ProductVersion
                $version -match "\+[0-9a-fA-F]{40}$"
            }
    )
    Assert-True (
        $revisionStampedHostAssemblies.Count -eq 0
    ) "Published TerminalHost assemblies included repository revision metadata"
    Write-Host "PASS: published host identity excludes repository revision metadata"

    $env:TREEMON_TERMINAL_HOST_STATE_DIR = $emptyState
    $layoutProbe = Test-TerminalHostDeployment $candidateServer
    Assert-True (-not $layoutProbe.HasLiveHost) "Layout preflight unexpectedly found a live host"
    Assert-True (
        $layoutProbe.Layout.StateDirectory -ceq [IO.Path]::GetFullPath($emptyState)
    ) "Candidate layout ignored the non-default state directory"
    Assert-True (
        $layoutProbe.Layout.StagingDirectory -ceq
        [IO.Path]::Combine([IO.Path]::GetFullPath($emptyState), "staged")
    ) "Candidate layout did not derive staging from the non-default state directory"
    Assert-True (
        $layoutProbe.Layout.ManifestPath -ceq
        [IO.Path]::Combine([IO.Path]::GetFullPath($emptyState), "host.json")
    ) "Candidate layout did not derive the manifest from the non-default state directory"
    Assert-True (
        (Get-TerminalHostStateDirectory $layoutProbe.Layout) -ceq
        [IO.Path]::GetFullPath($emptyState)
    ) "PowerShell did not consume the candidate's state-directory authority"
    Write-Host "PASS: candidate layout owns the non-default state and staging paths"

    $repeatCandidateBuildRoot = Join-Path $root "repeat-candidate-build"
    $repeatCandidateServer = Publish-ServerCandidate -AdditionalPublishArguments @(
        "-p:UseArtifactsOutput=true"
        "-p:ArtifactsPath=$repeatCandidateBuildRoot"
    )
    $repeatCandidateHost = Join-Path $repeatCandidateServer "terminal-host"
    Assert-True (
        -not (Test-Path -LiteralPath (Join-Path $repeatCandidateHost "Shared.dll") -PathType Leaf)
    ) "Repeated TerminalHost publish contained Shared.dll"
    Assert-True (
        (Get-TerminalHostBundleDigest $candidateHost $layoutProbe.Layout) -ceq
        (Get-TerminalHostBundleDigest $repeatCandidateHost $layoutProbe.Layout)
    ) "Repeated identical nested TerminalHost publications produced different bundle digests"
    Write-Host "PASS: identical nested TerminalHost publications have a stable bundle digest"

    $alternateSource = Join-Path $root "alternate-source"
    Copy-HostPublishSources $alternateSource
    $alternatePublish = Join-Path $root "alternate-publish"
    dotnet publish (Join-Path $alternateSource "src\Server\Server.fsproj") `
        -c Release `
        -o $alternatePublish `
        "-p:UseArtifactsOutput=true" `
        "-p:ArtifactsPath=$(Join-Path $root 'alternate-build')" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Alternate-source server publish failed" }
    $alternateHost = Join-Path $alternatePublish "terminal-host"
    foreach ($name in @("TerminalHost.dll", "TerminalHostLayout.dll")) {
        Assert-True (
            (Get-FileHash -LiteralPath (Join-Path $candidateHost $name) -Algorithm SHA256).Hash -ceq
            (Get-FileHash -LiteralPath (Join-Path $alternateHost $name) -Algorithm SHA256).Hash
        ) "$name changed when published from a different source directory"
    }
    Assert-True (
        (Get-TerminalHostBundleDigest $candidateHost $layoutProbe.Layout) -ceq
        (Get-TerminalHostBundleDigest $alternateHost $layoutProbe.Layout)
    ) "Identical source published from another source root changed the TerminalHost bundle"
    $sameSourceLive = [pscustomobject]@{
        HasLiveHost = $true
        ExecutablePath = Join-Path $candidateHost $layoutProbe.Layout.HostExecutableName
        Layout = $layoutProbe.Layout
    }
    $unchangedAcrossSources = Stage-TerminalHost $alternateHost $sameSourceLive
    Assert-True (
        -not $unchangedAcrossSources.Changed -and
        -not (Test-Path -LiteralPath $layoutProbe.Layout.StagingDirectory)
    ) "An identical host bundle published from another source root was staged"
    Write-Host "PASS: cross-root nested publish produces the same host bundle and skips staging"

    Publish-TestProject (
        Join-Path $repoRoot "src\TerminalHost\TerminalHost.fsproj"
    ) $baseline "1.0.0-deployment-test"
    Assert-True (
        -not (Test-Path -LiteralPath (Join-Path $baseline "Shared.dll") -PathType Leaf)
    ) "Direct TerminalHost publish still contained Shared.dll"
    Write-Host "PASS: TerminalHost publish excludes Shared.dll"

    $fingerprintDirectory = Join-Path $root "fingerprints"
    $fingerprintNestedDirectory = Join-Path $fingerprintDirectory "nested"
    New-Item -ItemType Directory -Path $fingerprintNestedDirectory -Force | Out-Null
    $layoutProbe.Layout.RequiredBundleFileNames | ForEach-Object {
        Set-Content -LiteralPath (Join-Path $fingerprintDirectory $_) -Value $_ -NoNewline
    }
    $fingerprintPdb = Join-Path $fingerprintNestedDirectory "TerminalHost.PDB"
    Set-Content -LiteralPath $fingerprintPdb -Value "symbols-v1" -NoNewline
    Set-Content -LiteralPath (
        Join-Path $fingerprintNestedDirectory "runtime.json"
    ) -Value "runtime" -NoNewline

    $fingerprintFiles = @(
        Get-ChildItem -LiteralPath $fingerprintDirectory -File -Recurse -Force
    )
    $allFingerprintEntries = @(
        Get-FileFingerprintEntries $fingerprintDirectory $fingerprintFiles
    )
    $bundleFingerprintEntries = @(
        Get-FileFingerprintEntries $fingerprintDirectory $fingerprintFiles -ExcludePdb
    )
    Assert-True (
        ($allFingerprintEntries -join "`n") -ceq
        ((@($allFingerprintEntries | Sort-Object)) -join "`n")
    ) "Fingerprint entries were not sorted"
    Assert-True (
        $allFingerprintEntries.Count -eq ($bundleFingerprintEntries.Count + 1)
    ) "PDB fingerprint mode did not exclude exactly the fixture PDB"

    $bundleDigestBeforePdbChange =
        Get-TerminalHostBundleDigest $fingerprintDirectory $layoutProbe.Layout
    $directorySnapshotBeforePdbChange = Get-DirectorySnapshot $fingerprintDirectory
    Set-Content -LiteralPath $fingerprintPdb -Value "symbols-v2" -NoNewline
    Assert-True (
        (Get-TerminalHostBundleDigest $fingerprintDirectory $layoutProbe.Layout) -ceq
        $bundleDigestBeforePdbChange
    ) "TerminalHost bundle digest included a PDB"
    Assert-True (
        (Get-DirectorySnapshot $fingerprintDirectory) -cne
        $directorySnapshotBeforePdbChange
    ) "Directory snapshot excluded a PDB"
    Write-Host "PASS: shared fingerprint entries support bundle and snapshot modes"

    $fingerprintRuntime = Join-Path $fingerprintNestedDirectory "runtime.json"
    Set-Content -LiteralPath $fingerprintRuntime -Value "runtime-v2" -NoNewline
    Assert-True (
        (Get-TerminalHostBundleDigest $fingerprintDirectory $layoutProbe.Layout) -cne
        $bundleDigestBeforePdbChange
    ) "TerminalHost bundle digest ignored a changed runtime file"
    Set-Content -LiteralPath $fingerprintRuntime -Value "runtime" -NoNewline
    Write-Host "PASS: host bundle digest detects runtime content changes"

    $missingBundleMember =
        Join-Path $fingerprintDirectory $layoutProbe.Layout.TtydExecutableName
    Remove-Item -LiteralPath $missingBundleMember
    $missingBundleRejected = $false
    try {
        Get-TerminalHostBundleDigest $fingerprintDirectory $layoutProbe.Layout | Out-Null
    } catch {
        $missingBundleRejected = $_.Exception.Message -like
            "Published TerminalHost output is incomplete*"
    }
    Assert-True $missingBundleRejected "A missing authoritative bundle member was accepted"
    Set-Content -LiteralPath $missingBundleMember -Value "restored" -NoNewline
    Write-Host "PASS: authoritative bundle membership rejects missing binaries"

    Assert-True (
        (Get-TerminalHostBundleDigest $baseline $layoutProbe.Layout) -cne
        (Get-TerminalHostBundleDigest $candidateHost $layoutProbe.Layout)
    ) "The test host publications must differ"

    New-Item -ItemType Directory -Path $worktree | Out-Null
    git -C $worktree init --quiet
    if ($LASTEXITCODE -ne 0) { throw "Could not initialize the fixture worktree" }

    $hostProcess = Start-TestHost (Join-Path $baseline "TerminalHost.exe") $state (Get-TestPort)
    $manifest = Wait-Manifest $state
    $canonicalWorktree = [IO.Path]::GetFullPath($worktree).TrimEnd('\', '/')
    $started = Invoke-TestHostRequest $manifest "POST" "/api/v2/terminals" (
        @{ worktreePath = $canonicalWorktree } | ConvertTo-Json -Compress)
    Assert-True (@($started.terminals).Count -eq 1) "Fixture terminal did not start"
    $terminalSessionId = $started.terminals[0].sessionId
    $hostIdentity = "$($manifest.pid):$($manifest.processStartTimeUtcTicks)"
    $env:TREEMON_TERMINAL_HOST_STATE_DIR = $state

    $compatible = Test-TerminalHostDeployment $candidateServer
    Assert-True $compatible.HasLiveHost "Compatible preflight did not find the live host"
    Assert-True ($compatible.TerminalCount -eq 1) "Compatible preflight lost the terminal"
    Assert-True (
        "$($compatible.Pid):$($compatible.ProcessStartTimeUtcTicks)" -ceq
        $hostIdentity
    ) "Compatible preflight changed the host identity"
    Assert-True (
        $compatible.Layout.StateDirectory -ceq [IO.Path]::GetFullPath($state)
    ) "Live-host preflight did not report the configured state directory"
    $unchanged = Stage-TerminalHost $baseline $compatible
    Assert-True (-not $unchanged.Changed) "Unchanged live host was staged for replacement"

    $staged = Stage-TerminalHost $candidateHost $compatible
    Assert-True $staged.Changed "Changed TerminalHost publication was not staged"
    Assert-True (Test-Path -LiteralPath $staged.ExecutablePath) "Staged executable is missing"
    Assert-True (
        (Get-Content -LiteralPath (Join-Path $state "host.json") -Raw |
            ConvertFrom-Json).stagedExecutableVersion -ceq $staged.Version
    ) "Live host did not report the staged executable version"
    $afterStage = Test-TerminalHostDeployment $candidateServer
    Assert-True (
        "$($afterStage.Pid):$($afterStage.ProcessStartTimeUtcTicks)" -ceq
        $hostIdentity
    ) "Staging replaced the live host"
    Assert-True (
        @((Invoke-TestHostRequest $manifest "GET" "/api/v2/terminals").terminals)[0].sessionId -ceq
        $terminalSessionId
    ) "Staging replaced the live terminal"
    Write-Host "PASS: staging leaves the exact live host and terminal untouched"

    $PublishDir = $legacyPublish
    Set-Content -LiteralPath (Join-Path $PublishDir "old.txt") -Value "old"
    $compatibleServer = Join-Path $root "compatible-server"
    New-Item -ItemType Directory -Path $compatibleServer | Out-Null
    Set-Content -LiteralPath (Join-Path $compatibleServer "Treemon.exe") -Value "candidate"
    Install-ServerPublish $compatibleServer $compatible.ExecutablePath
    Assert-True (Test-Path -LiteralPath (Join-Path $PublishDir "Treemon.exe")) "Compatible server was not installed"
    Assert-True (Test-Path -LiteralPath $compatible.ExecutablePath) "Live host publication was overwritten"
    Assert-True (-not $hostProcess.HasExited) "Compatible server install stopped the live host"
    Assert-True (
        @((Invoke-TestHostRequest $manifest "GET" "/api/v2/terminals").terminals)[0].sessionId -ceq
        $terminalSessionId
    ) "Compatible server install restarted the terminal"
    Write-Host "PASS: compatible deployment reuses the exact host"

    $manifestPath = Join-Path $state "host.json"
    # Corrupting only the manifest exercises fail-closed identity handling. The genuine compatible
    # / incompatible and empty / non-empty matrix is covered by EmbeddedTerminalControlClientTests.
    $mismatchedManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $mismatchedManifest.controlApiVersion = 3
    $mismatchedManifest | ConvertTo-Json -Compress | Set-Content -LiteralPath $manifestPath -NoNewline
    $beforeRefusal = Get-DirectorySnapshot $root
    $refused = $false
    try {
        Test-TerminalHostDeployment $candidateServer | Out-Null
    } catch {
        $refused = $_.Exception.Message -like "Deployment refused:*"
    }
    Assert-True $refused "Mismatched host identity was not refused"
    Assert-True (
        (Get-DirectorySnapshot $root) -ceq $beforeRefusal
    ) "Mismatched-identity preflight changed deployment files"
    Assert-True (-not $hostProcess.HasExited) "Mismatched-identity preflight stopped the live host"
    Assert-True (
        @((Invoke-TestHostRequest $manifest "GET" "/api/v2/terminals").terminals)[0].sessionId -ceq
        $terminalSessionId
    ) "Mismatched-identity preflight changed the terminal"
    $mismatchedManifest.controlApiVersion = 2
    $mismatchedManifest | ConvertTo-Json -Compress | Set-Content -LiteralPath $manifestPath -NoNewline
    Write-Host "PASS: manifest and health identity mismatch is refused without side effects"

    $closed = Invoke-TestHostRequest $manifest "DELETE" "/api/v2/terminals/$terminalSessionId"
    Assert-True (@($closed.terminals).Count -eq 0) "Fixture terminal did not close"

    $compatibleEmpty = Test-TerminalHostDeployment $candidateServer
    Assert-True $compatibleEmpty.HasLiveHost "Compatible empty preflight lost the live host"
    Assert-True ($compatibleEmpty.TerminalCount -eq 0) "Compatible empty preflight reported terminals"
    Assert-True (
        "$($compatibleEmpty.Pid):$($compatibleEmpty.ProcessStartTimeUtcTicks)" -ceq
        $hostIdentity
    ) "Compatible empty preflight changed the host identity"

    $mismatchedManifest.controlApiVersion = 3
    $mismatchedManifest | ConvertTo-Json -Compress | Set-Content -LiteralPath $manifestPath -NoNewline
    $unverifiedEmptyRefused = $false
    try {
        Test-TerminalHostDeployment $candidateServer | Out-Null
    } catch {
        $unverifiedEmptyRefused = $_.Exception.Message -like "Deployment refused:*"
    }
    Assert-True $unverifiedEmptyRefused "Unverified empty-host identity was not refused"
    Assert-True (-not $hostProcess.HasExited) "Unverified preflight stopped the live host"
    $mismatchedManifest.controlApiVersion = 2
    $mismatchedManifest | ConvertTo-Json -Compress | Set-Content -LiteralPath $manifestPath -NoNewline
    Write-Host "PASS: compatible empty host proceeds and unverified identity fails closed"

    $env:TREEMON_TERMINAL_HOST_STATE_DIR = $emptyState
    $noHost = Test-TerminalHostDeployment $candidateServer
    Assert-True (-not $noHost.HasLiveHost) "Empty state unexpectedly reported a live host"

    @("../escape", "invalid+metadata", ("x" * 129)) | ForEach-Object {
        $invalidVersionRejected = $false
        try {
            Stage-TerminalHost $candidateHost $noHost $_ | Out-Null
        } catch {
            $invalidVersionRejected = $_.Exception.Message -like
                "TerminalHost staged executable version*"
        }
        Assert-True $invalidVersionRejected "Invalid staged version '$_' was accepted"
    }
    Write-Host "PASS: candidate version grammar rejects invalid direct directories"

    $mismatchedVersion = "mismatched-bundle"
    $mismatchedStage = Join-Path $noHost.Layout.StagingDirectory $mismatchedVersion
    New-Item -ItemType Directory -Path $mismatchedStage -Force | Out-Null
    Get-ChildItem -LiteralPath $candidateHost -Force |
        Copy-Item -Destination $mismatchedStage -Recurse -Force
    Set-Content -LiteralPath (
        Join-Path $mismatchedStage $noHost.Layout.TtydExecutableName
    ) -Value "mismatched ttyd" -NoNewline

    $mismatchedBundleRejected = $false
    try {
        Stage-TerminalHost $candidateHost $noHost $mismatchedVersion | Out-Null
    } catch {
        $mismatchedBundleRejected = $_.Exception.Message -like
            "Existing TerminalHost stage*does not match the candidate"
    }
    Assert-True $mismatchedBundleRejected "A mismatched staged bundle member was accepted"
    Remove-Item -LiteralPath $mismatchedStage -Recurse -Force
    Write-Host "PASS: mismatched staged bundle members fail closed"

    $noHostStage = Stage-TerminalHost $candidateHost $noHost
    $PublishDir = Join-Path $root "no-host-active"
    $noHostServer = Join-Path $root "no-host-server"
    New-Item -ItemType Directory -Path $noHostServer | Out-Null
    Set-Content -LiteralPath (Join-Path $noHostServer "Treemon.exe") -Value "candidate"
    Install-ServerPublish $noHostServer
    Assert-True (Test-Path -LiteralPath $noHostStage.ExecutablePath) "No-host deployment did not stage TerminalHost"
    Assert-True (Test-Path -LiteralPath (Join-Path $PublishDir "Treemon.exe")) "No-host deployment did not install Treemon"
    Write-Host "PASS: deployment with no live host proceeds normally"

    $script:mockDeploymentCandidate = $candidateServer
    $script:mockDeploymentScenario = ""
    $script:deploymentWorkflowEvents = @()
    $script:mockRunningPid = $null
    $script:startedHostExecutable = $null
    $script:startedRoots = @()
    $script:mockStagedExecutable = Join-Path $root "staged\TerminalHost.exe"
    $script:mockLiveExecutable = Join-Path $root "live\TerminalHost.exe"
    $script:mockDeploymentLayout = [pscustomobject]@{
        StateDirectory = Join-Path $root "mock-state"
        StagingDirectory = Join-Path $root "mock-state\staged"
        ManifestPath = Join-Path $root "mock-state\host.json"
        HostExecutableName = "TerminalHost.exe"
        TtydExecutableName = "ttyd.exe"
        VersionDirectoryPattern = "\A[A-Za-z0-9._-]{1,128}\z"
        RequiredBundleFileNames = @("TerminalHost.exe", "ttyd.exe")
    }
    $PidFile = Join-Path $root "workflow\.treemon.pid"
    $WwwRoot = Join-Path $root "workflow-wwwroot"
    $LogDir = Join-Path $root "workflow-logs"
    $LogFile = Join-Path $LogDir "treemon.log"
    $DefaultPort = Get-TestPort
    do {
        $CanvasPort = Get-TestPort
    } while ($CanvasPort -eq $DefaultPort)

    function Get-RunningPid { return $script:mockRunningPid }
    function Stop-ProductionServer {
        $script:deploymentWorkflowEvents += "stop-server"
        $script:mockRunningPid = $null
    }
    function Ensure-WwwRoot {
        $script:deploymentWorkflowEvents += "ensure-frontend"
    }
    function Build-Frontend([string]$Destination) {
        $script:builtFrontendCandidate = $Destination
        $script:deploymentWorkflowEvents += "build-frontend"
    }
    function Publish-ServerCandidate {
        $script:deploymentWorkflowEvents += "publish-server"
        return $script:mockDeploymentCandidate
    }
    function Test-TerminalHostDeployment([string]$PublishedServerDirectory) {
        $script:preflightPublishedServerDirectory = $PublishedServerDirectory
        $script:deploymentWorkflowEvents += "preflight"
        if ($script:mockDeploymentScenario -ceq "start") {
            return [pscustomobject]@{
                HasLiveHost = $true
                Pid = 123
                ProcessStartTimeUtcTicks = 456L
                TerminalCount = 2
                ExecutablePath = $script:mockLiveExecutable
                Layout = $script:mockDeploymentLayout
            }
        }
        return [pscustomobject]@{
            HasLiveHost = $false
            Pid = $null
            ProcessStartTimeUtcTicks = $null
            TerminalCount = 0
            ExecutablePath = $null
            Layout = $script:mockDeploymentLayout
        }
    }
    function Stop-ProductionPortListeners {
        $script:deploymentWorkflowEvents += "stop-listeners"
    }
    function Stage-TerminalHost([string]$PublishedHostDirectory, $LiveHost) {
        $script:stagedPublishedHostDirectory = $PublishedHostDirectory
        $script:deploymentWorkflowEvents += "stage-host"
        if ($script:mockDeploymentScenario -ceq "start") {
            return [pscustomobject]@{
                Changed = $false
                Version = $null
                ExecutablePath = $LiveHost.ExecutablePath
            }
        }
        return [pscustomobject]@{
            Changed = $true
            Version = "fixture-stage"
            ExecutablePath = $script:mockStagedExecutable
        }
    }
    function Install-ServerPublish([string]$Candidate, [string]$LiveHostExecutable = "") {
        $script:installedServerCandidate = $Candidate
        $script:installedLiveHostExecutable = $LiveHostExecutable
        $script:deploymentWorkflowEvents += "install-server"
    }
    function Install-PreparedDirectory([string]$Candidate, [string]$Destination) {
        $script:installedFrontendCandidate = $Candidate
        $script:installedFrontendDestination = $Destination
        $script:deploymentWorkflowEvents += "install-frontend"
    }
    function Install-TmCommand {
        $script:deploymentWorkflowEvents += "install-tm"
    }
    function Install-Skill {
        $script:deploymentWorkflowEvents += "install-skill"
    }
    function Install-Extension {
        $script:deploymentWorkflowEvents += "install-extension"
    }
    function Install-ReportingExtension {
        $script:deploymentWorkflowEvents += "install-reporting"
    }
    function Start-ProductionProcess(
        [string[]]$Roots,
        [string]$TerminalHostExecutable
    ) {
        $script:startedRoots = @($Roots)
        $script:startedHostExecutable = $TerminalHostExecutable
        $script:deploymentWorkflowEvents += "start-server"
    }

    New-Item -ItemType Directory -Path $LogDir | Out-Null
    $runStdout = Join-Path $LogDir "treemon-prod.20260925-000000.log"
    $runStderr = Join-Path $LogDir "treemon-prod-stderr.20260925-000000.log"
    Set-Content -LiteralPath $runStdout -Value "Server startup"
    Copy-Item -LiteralPath $stderrFixture -Destination $runStderr
    $script:mockRunningPid = $null
    $stoppedStatus = Show-Status 6>&1 | Out-String
    Assert-True (
        $stoppedStatus.Contains("Production server is not running") -and
        $stoppedStatus.Contains("SQLite Error 10: 'disk I/O error'")
    ) "Status did not report the latest failed run's SQLite error"
    $script:mockRunningPid = $PID
    $unreadyStatus = Show-Status 6>&1 | Out-String
    Assert-True (
        $unreadyStatus.Contains("running but not listening on port $DefaultPort") -and
        $unreadyStatus.Contains("Monitor: (server not listening)") -and
        $unreadyStatus.Contains("Stdout log: $runStdout") -and
        $unreadyStatus.Contains("Stderr log: $runStderr") -and
        $unreadyStatus.Contains("SQLite Error 10: 'disk I/O error'")
    ) "Status omitted startup diagnostics for an unready process"
    Clear-Content -LiteralPath $runStderr
    $unreadyWithoutStderr = Show-Status 6>&1 | Out-String
    Assert-True (
        $unreadyWithoutStderr.Contains("Stderr log: $runStderr") -and
        -not $unreadyWithoutStderr.Contains("SQLite Error 10:")
    ) "Status omitted the stderr path for an unready process without an error yet"
    $startUnreadyRefused = $false
    try {
        Start-ProductionServer @()
    } catch {
        $startUnreadyRefused = $_.Exception.Message -like
            "Production server process PID $PID is not listening on port $DefaultPort*"
    }
    Assert-True $startUnreadyRefused "Start accepted an existing process with no dashboard listener"
    $script:mockRunningPid = $null
    Write-Host "PASS: status distinguishes failed and unready production runs"

    $script:mockDeploymentScenario = "start"
    $script:deploymentWorkflowEvents = @()
    Start-ProductionServer @("Q:\fixture-worktree")
    Assert-True (
        ($script:deploymentWorkflowEvents -join "|") -ceq
        "ensure-frontend|publish-server|preflight|stage-host|install-server|start-server"
    ) "Start-ProductionServer did not use the shared deployment workflow"
    Assert-True (
        $script:startedHostExecutable -ceq
        (Join-Path $PublishDir "terminal-host\TerminalHost.exe")
    ) "Start-ProductionServer did not resolve the installed unchanged host executable"
    Assert-True (
        $script:preflightPublishedServerDirectory -ceq $script:mockDeploymentCandidate -and
        $script:stagedPublishedHostDirectory -ceq
        (Join-Path $script:mockDeploymentCandidate "terminal-host") -and
        $script:installedServerCandidate -ceq $script:mockDeploymentCandidate -and
        $script:installedLiveHostExecutable -ceq $script:mockLiveExecutable
    ) "Start-ProductionServer did not thread the candidate and live host through deployment"
    Assert-True (
        $script:startedRoots.Count -eq 1 -and
        $script:startedRoots[0] -ceq "Q:\fixture-worktree"
    ) "Start-ProductionServer did not preserve its roots"
    Write-Host "PASS: start uses the shared server deployment workflow"

    $script:mockDeploymentScenario = "deploy"
    $script:deploymentWorkflowEvents = @()
    $script:startedHostExecutable = $null
    Deploy-Frontend
    Assert-True (
        ($script:deploymentWorkflowEvents -join "|") -ceq
        "build-frontend|publish-server|preflight|stop-listeners|stage-host|install-server|install-frontend|install-tm|install-skill|install-extension|install-reporting|start-server"
    ) "Deploy-Frontend did not preserve candidate-first deployment ordering"
    Assert-True (
        $script:installedFrontendCandidate -ceq $script:builtFrontendCandidate -and
        $script:installedFrontendDestination -ceq $WwwRoot
    ) "Deploy-Frontend did not install its prepared frontend candidate"
    Assert-True (
        $script:startedHostExecutable -ceq $script:mockStagedExecutable
    ) "Deploy-Frontend did not use the changed staged host executable"
    Assert-True (
        $script:preflightPublishedServerDirectory -ceq $script:mockDeploymentCandidate -and
        $script:stagedPublishedHostDirectory -ceq
        (Join-Path $script:mockDeploymentCandidate "terminal-host") -and
        $script:installedServerCandidate -ceq $script:mockDeploymentCandidate -and
        [string]::IsNullOrEmpty($script:installedLiveHostExecutable)
    ) "Deploy-Frontend did not thread its candidate through deployment"
    Assert-True (
        $script:startedRoots.Count -eq 0
    ) "Deploy-Frontend unexpectedly supplied explicit roots"
    Write-Host "PASS: deploy uses the shared candidate-first server deployment workflow"

    $env:TREEMON_TERMINAL_SESSION_ID = "embedded-deployment-fixture"

    $script:mockRunningPid = $null
    $script:deploymentWorkflowEvents = @()
    $deployRefused = $false
    try {
        Deploy-Frontend
    } catch {
        $deployRefused = $_.Exception.Message -like
            "Cannot deploy Treemon production from a Treemon embedded terminal*"
    }
    Assert-True $deployRefused "Embedded-terminal deploy was not refused"
    Assert-True (
        $script:deploymentWorkflowEvents.Count -eq 0
    ) "Embedded-terminal deploy performed work before refusal"

    $embeddedListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $DefaultPort)
    $embeddedListener.Start()
    $script:mockRunningPid = $PID
    $script:deploymentWorkflowEvents = @()
    Start-ProductionServer @()
    Assert-True (
        $script:deploymentWorkflowEvents.Count -eq 0 -and
        $script:mockRunningPid -eq $PID
    ) "Embedded-terminal start did not preserve the already-running no-op"

    $script:mockRunningPid = $null
    $script:deploymentWorkflowEvents = @()
    $startRefused = $false
    try {
        Start-ProductionServer @()
    } catch {
        $startRefused = $_.Exception.Message -like
            "Cannot start Treemon production from a Treemon embedded terminal*"
    }
    Assert-True $startRefused "Embedded-terminal production start was not refused"
    Assert-True (
        $script:deploymentWorkflowEvents.Count -eq 0
    ) "Embedded-terminal production start performed work before refusal"

    $script:mockRunningPid = $PID
    $script:deploymentWorkflowEvents = @()
    $restartRefused = $false
    try {
        Restart-ProductionServer @()
    } catch {
        $restartRefused = $_.Exception.Message -like
            "Cannot restart Treemon production from a Treemon embedded terminal*"
    }
    Assert-True $restartRefused "Embedded-terminal production restart was not refused"
    Assert-True (
        $script:deploymentWorkflowEvents.Count -eq 0 -and
        $script:mockRunningPid -eq $PID
    ) "Embedded-terminal production restart stopped or replaced the running server"

    $script:deploymentWorkflowEvents = @()
    Restart-ServerIfRunning
    Assert-True (
        $script:deploymentWorkflowEvents.Count -eq 0 -and
        $script:mockRunningPid -eq $PID
    ) "Embedded-terminal automatic config restart stopped or replaced the running server"
    Write-Host "PASS: production lifecycle refuses embedded-terminal ownership before side effects"
} finally {
    if ($embeddedListener) { $embeddedListener.Stop() }
    $PublishDir = $originalPublishDir
    $PidFile = $originalPidFile
    $WwwRoot = $originalWwwRoot
    $LogDir = $originalLogDir
    $LogFile = $originalLogFile
    $DefaultPort = $originalDefaultPort
    $CanvasPort = $originalCanvasPort
    if ($hadStateOverride) {
        $env:TREEMON_TERMINAL_HOST_STATE_DIR = $previousStateOverride
    } else {
        Remove-Item Env:\TREEMON_TERMINAL_HOST_STATE_DIR -ErrorAction SilentlyContinue
    }
    if ($hadTerminalSessionId) {
        $env:TREEMON_TERMINAL_SESSION_ID = $previousTerminalSessionId
    } else {
        Remove-Item Env:\TREEMON_TERMINAL_SESSION_ID -ErrorAction SilentlyContinue
    }
    if ($hostProcess -and $manifest) { Stop-TestHost $hostProcess $manifest }
    if ($hostProcess) { $hostProcess.Dispose() }
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
