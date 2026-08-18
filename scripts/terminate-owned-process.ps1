param(
    [Parameter(Mandatory = $true)]
    [int]$ProcessId,

    [Parameter(Mandatory = $true)]
    [long]$StartTimeUtcTicks,

    [Parameter(Mandatory = $true)]
    [int]$TimeoutMilliseconds
)

$ErrorActionPreference = "Stop"

try {
    $ownedProcess = [System.Diagnostics.Process]::GetProcessById($ProcessId)
} catch [System.ArgumentException] {
    exit 3
}

try {
    try {
        $processHandle = $ownedProcess.SafeHandle

        if ($ownedProcess.HasExited -or $ownedProcess.StartTime.ToUniversalTime().Ticks -ne $StartTimeUtcTicks) {
            exit 3
        }
    } catch [System.InvalidOperationException] {
        exit 3
    }

    try {
        $ownedProcess.Kill($true)
    } catch [System.InvalidOperationException] {
        if ($ownedProcess.HasExited) {
            exit 0
        }

        throw
    }

    if (-not $ownedProcess.WaitForExit($TimeoutMilliseconds)) {
        throw "Timed out waiting for the identity-bound process tree to exit"
    }
} finally {
    $ownedProcess.Dispose()
}
