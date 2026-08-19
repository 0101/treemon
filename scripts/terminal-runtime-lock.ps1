param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDirectory,
    [Parameter(Mandatory = $true)]
    [string]$CompatibilityBundleHash,
    [Parameter(Mandatory = $true)]
    [string]$ExtendedBundleHash,
    [Parameter(Mandatory = $true)]
    [string]$LaunchRequestPath,
    [Parameter(Mandatory = $true)]
    [string]$ReadyPath,
    [Parameter(Mandatory = $true)]
    [string]$StatusPath
)

$ErrorActionPreference = "Stop"

function Get-Sha256 {
    param([IO.FileStream]$Stream)

    $Stream.Position = 0
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        ([Convert]::ToHexString($sha.ComputeHash($Stream))).ToLowerInvariant()
    } finally {
        $sha.Dispose()
        $Stream.Position = 0
    }
}

function Get-ContainedPath {
    param(
        [string]$Root,
        [string]$RelativePath
    )

    if (
        [string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains(":")
    ) {
        throw "Runtime bundle contains an invalid relative path"
    }

    $rootPath = [IO.Path]::TrimEndingDirectorySeparator(
        [IO.Path]::GetFullPath($Root)
    )
    $candidate = [IO.Path]::GetFullPath(
        [IO.Path]::Combine(
            $rootPath,
            $RelativePath.Replace("/", [IO.Path]::DirectorySeparatorChar)
        )
    )
    $prefix = "$rootPath$([IO.Path]::DirectorySeparatorChar)"
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime bundle path escaped its root"
    }
    $candidate
}

function Assert-NoReparsePoint {
    param(
        [string]$Root,
        [string]$Path
    )

    $rootPath = [IO.Path]::GetFullPath($Root)
    $relative = [IO.Path]::GetRelativePath($rootPath, $Path)
    $segments = $relative.Split(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar
        ),
        [StringSplitOptions]::RemoveEmptyEntries
    )
    $paths = @($rootPath)
    $current = $rootPath
    foreach ($segment in $segments) {
        $current = [IO.Path]::Combine($current, $segment)
        $paths += $current
    }
    foreach ($candidate in $paths) {
        if (
            [IO.File]::Exists($candidate) -or
            [IO.Directory]::Exists($candidate)
        ) {
            $attributes = [IO.File]::GetAttributes($candidate)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Runtime bundle path crosses a reparse point"
            }
        }
    }
}

function Write-State {
    param(
        [string]$Path,
        [object]$Value
    )

    $directory = [IO.Path]::GetDirectoryName($Path)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporary = "$Path.$PID.tmp"
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(
            ($Value | ConvertTo-Json -Compress -Depth 5)
        )
        $stream = [IO.FileStream]::new(
            $temporary,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough
        )
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }
        [IO.File]::Move($temporary, $Path, $true)
    } finally {
        [IO.File]::Delete($temporary)
    }
}

function Write-Ready {
    param([object]$Value)
    Write-State $ReadyPath $Value
}

function Test-Fault {
    param([string]$Stage)

    if (
        $env:TREEMON_TERMINAL_LOCK_TEST_MODE -eq "1" -and
        $env:TREEMON_TERMINAL_LOCK_TEST_STAGE -eq $Stage
    ) {
        throw "Injected runtime lock failure at $Stage"
    }
}

$locks = [Collections.Generic.List[IO.FileStream]]::new()
$child = $null
$request = $null
$hostError = ""

try {
    if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Windows
    )) {
        throw "Durable runtime file locking is supported only on Windows"
    }

    $bundle = [IO.Path]::GetFullPath($BundleDirectory)
    Assert-NoReparsePoint $bundle $bundle
    Test-Fault "before-manifest-lock"

    $manifestPath = Get-ContainedPath $bundle "bundle.json"
    $manifestStream = [IO.FileStream]::new(
        $manifestPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read
    )
    $locks.Add($manifestStream)
    $reader = [IO.StreamReader]::new(
        $manifestStream,
        [Text.Encoding]::UTF8,
        $true,
        4096,
        $true
    )
    try {
        $manifest = $reader.ReadToEnd() | ConvertFrom-Json
    } finally {
        $reader.Dispose()
        $manifestStream.Position = 0
    }

    if (
        $manifest.version -ne 1 -or
        [string]$manifest.bundleHash -cne $CompatibilityBundleHash -or
        $manifest.extendedRuntime.version -ne 3 -or
        [string]$manifest.extendedRuntime.bundleHash -cne $ExtendedBundleHash
    ) {
        throw "Runtime bundle manifest identity changed"
    }

    $entries = @($manifest.extendedRuntime.files)
    $names = @($entries | ForEach-Object { [string]$_.name })
    if (
        $entries.Count -eq 0 -or
        (@($names | Sort-Object -Unique)).Count -ne $entries.Count
    ) {
        throw "Runtime bundle manifest file identity changed"
    }

    $expectedNames = @("bundle.json") + $names
    $actualNames = @(
        [IO.Directory]::GetFiles(
            $bundle,
            "*",
            [IO.SearchOption]::AllDirectories
        ) | ForEach-Object {
            [IO.Path]::GetRelativePath($bundle, $_).Replace("\", "/")
        }
    )
    $differences = Compare-Object `
        -ReferenceObject ($expectedNames | Sort-Object) `
        -DifferenceObject ($actualNames | Sort-Object)
    if (@($differences).Count -ne 0) {
        throw "Runtime bundle contains unexpected files"
    }

    foreach ($entry in $entries) {
        $name = [string]$entry.name
        $expectedHash = [string]$entry.sha256
        if ($expectedHash -notmatch '^[0-9a-f]{64}$') {
            throw "Runtime bundle manifest contains an invalid hash"
        }
        $path = Get-ContainedPath $bundle $name
        Assert-NoReparsePoint $bundle $path
        $stream = [IO.FileStream]::new(
            $path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read
        )
        $locks.Add($stream)
        if ((Get-Sha256 $stream) -cne $expectedHash) {
            throw "Runtime bundle hash mismatch for $name"
        }
    }

    Test-Fault "after-file-locks"

    $request = Get-Content -LiteralPath $LaunchRequestPath -Raw | ConvertFrom-Json
    if (
        [string]::IsNullOrWhiteSpace([string]$request.token) -or
        [string]::IsNullOrWhiteSpace([string]$request.nodeExecutable) -or
        @($request.arguments).Count -eq 0
    ) {
        throw "Runtime lock launch request is invalid"
    }
    $expectedHost = Get-ContainedPath $bundle "durable-terminal-host.mjs"
    if (
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$request.arguments[0]),
            $expectedHost,
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        throw "Runtime lock launch request selected an unverified host"
    }

    Test-Fault "before-host-spawn"
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = [string]$request.nodeExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardError = $true
    @($request.arguments) | ForEach-Object {
        $startInfo.ArgumentList.Add([string]$_)
    }
    $lockOwnerStartTicks =
        [Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString()
    $startInfo.ArgumentList.Add("--runtime-lock-owner-pid")
    $startInfo.ArgumentList.Add($PID.ToString())
    $startInfo.ArgumentList.Add("--runtime-lock-owner-start-ticks")
    $startInfo.ArgumentList.Add($lockOwnerStartTicks)
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $startInfo
    if (-not $child.Start()) {
        throw "Node did not start the durable terminal host"
    }
    $child.add_ErrorDataReceived({
        if ($null -ne $_.Data -and $script:hostError.Length -lt 4000) {
            $script:hostError =
                ($script:hostError + " " + [string]$_.Data).Trim()
        }
    })
    $child.BeginErrorReadLine()
    $childStartTicks = $child.StartTime.ToUniversalTime().Ticks
    Test-Fault "after-host-spawn"

    Write-Ready ([ordered]@{
        ok = $true
        token = [string]$request.token
        runtimeLockOwnerPid = $PID
        runtimeLockOwnerProcessStartTicks = $lockOwnerStartTicks
        hostPid = $child.Id
        hostProcessStartTicks = $childStartTicks.ToString()
    })
    [IO.File]::Delete($LaunchRequestPath)
    Test-Fault "after-ready"

    $child.WaitForExit()
    $child.WaitForExit()
    if ($child.ExitCode -ne 0) {
        Write-State $StatusPath ([ordered]@{
            hostPid = $child.Id
            exitCode = $child.ExitCode
            error = $hostError.Substring(
                0,
                [Math]::Min(500, $hostError.Length)
            )
        })
    }
    exit $child.ExitCode
} catch {
    $message = [string]$_.Exception.Message
    if ($null -ne $child -and -not $child.HasExited) {
        try {
            $child.StandardInput.Close()
            $child.Kill($true)
            $child.WaitForExit(5000) | Out-Null
        } catch {
        }
    }
    try {
        Write-Ready ([ordered]@{
            ok = $false
            token = [string]$request.token
            error = $message.Substring(0, [Math]::Min(500, $message.Length))
        })
    } catch {
    }
    exit 1
} finally {
    if ($null -ne $child) {
        try {
            $child.StandardInput.Close()
        } catch {
        }
        $child.Dispose()
    }
    $locks | ForEach-Object { $_.Dispose() }
    [IO.File]::Delete($LaunchRequestPath)
}
