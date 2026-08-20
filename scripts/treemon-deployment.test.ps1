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

function Get-TestPort {
    do {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
        $listener.Stop()
    } while ($port -eq 5000)
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
    return @(
        Get-ChildItem -LiteralPath $Directory -File -Recurse -Force |
            ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/')
                "$relative`0$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
            } |
            Sort-Object
    ) -join "`n"
}

function Stop-TestHost($Process, $Manifest) {
    if (-not $Process -or $Process.HasExited) { return }
    try {
        Invoke-TestHostRequest $Manifest "POST" "/api/v1/shutdown" | Out-Null
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

$root = Join-Path ([IO.Path]::GetTempPath()) "treemon-deploy-test-$([Guid]::NewGuid().ToString('N'))"
$legacyPublish = Join-Path $root "legacy-active"
$baseline = Join-Path $legacyPublish "terminal-host"
$candidateServer = $null
$state = Join-Path $root "state"
$emptyState = Join-Path $root "empty-state"
$worktree = Join-Path $root "worktree"
$originalPublishDir = $PublishDir
$hadStateOverride = Test-Path Env:\TREEMON_TERMINAL_HOST_STATE_DIR
$previousStateOverride = $env:TREEMON_TERMINAL_HOST_STATE_DIR
$hostProcess = $null
$manifest = $null

try {
    New-Item -ItemType Directory -Path $root | Out-Null
    Publish-TestProject (
        Join-Path $repoRoot "src\TerminalHost\TerminalHost.fsproj"
    ) $baseline "1.0.0-deployment-test"
    $PublishDir = Join-Path $root "candidate-active"
    $candidateServer = Publish-ServerCandidate
    Assert-True ($candidateServer -is [string]) "Server candidate path was not scalar"
    $candidateHost = Join-Path $candidateServer "terminal-host"
    Assert-True (
        (Get-TerminalHostBundleDigest $baseline) -cne
        (Get-TerminalHostBundleDigest $candidateHost)
    ) "The test host publications must differ"

    New-Item -ItemType Directory -Path $worktree | Out-Null
    git -C $worktree init --quiet
    if ($LASTEXITCODE -ne 0) { throw "Could not initialize the fixture worktree" }

    $hostProcess = Start-TestHost (Join-Path $baseline "TerminalHost.exe") $state (Get-TestPort)
    $manifest = Wait-Manifest $state
    $canonicalWorktree = [IO.Path]::GetFullPath($worktree).TrimEnd('\', '/')
    $started = Invoke-TestHostRequest $manifest "POST" "/api/v1/terminals" (
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
    $unchanged = Stage-TerminalHost $baseline $compatible $state
    Assert-True (-not $unchanged.Changed) "Unchanged live host was staged for replacement"

    $staged = Stage-TerminalHost $candidateHost $compatible $state
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
        @((Invoke-TestHostRequest $manifest "GET" "/api/v1/terminals").terminals)[0].sessionId -ceq
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
        @((Invoke-TestHostRequest $manifest "GET" "/api/v1/terminals").terminals)[0].sessionId -ceq
        $terminalSessionId
    ) "Compatible server install restarted the terminal"
    Write-Host "PASS: compatible deployment reuses the exact host"

    $manifestPath = Join-Path $state "host.json"
    $incompatibleManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $incompatibleManifest.controlApiVersion = 2
    $incompatibleManifest | ConvertTo-Json -Compress | Set-Content -LiteralPath $manifestPath -NoNewline
    $beforeRefusal = Get-DirectorySnapshot $root
    $refused = $false
    try {
        Test-TerminalHostDeployment $candidateServer | Out-Null
    } catch {
        $refused = $_.Exception.Message -like "Deployment refused:*"
    }
    Assert-True $refused "Incompatible control API deployment was not refused"
    Assert-True (
        (Get-DirectorySnapshot $root) -ceq $beforeRefusal
    ) "Incompatible preflight changed deployment files"
    Assert-True (-not $hostProcess.HasExited) "Incompatible preflight stopped the live host"
    Assert-True (
        @((Invoke-TestHostRequest $manifest "GET" "/api/v1/terminals").terminals)[0].sessionId -ceq
        $terminalSessionId
    ) "Incompatible preflight changed the terminal"
    $incompatibleManifest.controlApiVersion = 1
    $incompatibleManifest | ConvertTo-Json -Compress | Set-Content -LiteralPath $manifestPath -NoNewline
    Write-Host "PASS: incompatible deployment is refused without side effects"

    $env:TREEMON_TERMINAL_HOST_STATE_DIR = $emptyState
    $noHost = Test-TerminalHostDeployment $candidateServer
    Assert-True (-not $noHost.HasLiveHost) "Empty state unexpectedly reported a live host"
    $noHostStage = Stage-TerminalHost $candidateHost $noHost $emptyState
    $PublishDir = Join-Path $root "no-host-active"
    $noHostServer = Join-Path $root "no-host-server"
    New-Item -ItemType Directory -Path $noHostServer | Out-Null
    Set-Content -LiteralPath (Join-Path $noHostServer "Treemon.exe") -Value "candidate"
    Install-ServerPublish $noHostServer
    Assert-True (Test-Path -LiteralPath $noHostStage.ExecutablePath) "No-host deployment did not stage TerminalHost"
    Assert-True (Test-Path -LiteralPath (Join-Path $PublishDir "Treemon.exe")) "No-host deployment did not install Treemon"
    Write-Host "PASS: deployment with no live host proceeds normally"
} finally {
    $PublishDir = $originalPublishDir
    if ($hadStateOverride) {
        $env:TREEMON_TERMINAL_HOST_STATE_DIR = $previousStateOverride
    } else {
        Remove-Item Env:\TREEMON_TERMINAL_HOST_STATE_DIR -ErrorAction SilentlyContinue
    }
    if ($hostProcess -and $manifest) { Stop-TestHost $hostProcess $manifest }
    if ($hostProcess) { $hostProcess.Dispose() }
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
