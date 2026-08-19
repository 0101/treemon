param(
    [ValidateSet("Inspect", "Terminate")]
    [string]$Operation = "Terminate",

    [Parameter(Mandatory = $true)]
    [int]$ProcessId,

    [long]$StartTimeUtcTicks = 0,

    [int]$TimeoutMilliseconds = 5000
)

$ErrorActionPreference = "Stop"

if ($Operation -ne "Inspect" -and $StartTimeUtcTicks -le 0) {
    throw "$Operation requires an exact process creation time"
}

if ($TimeoutMilliseconds -le 0) {
    throw "TimeoutMilliseconds must be positive"
}

function Open-ProcessHandle([int]$Id, [long]$ExpectedStartTimeUtcTicks) {
    try {
        $candidate = [System.Diagnostics.Process]::GetProcessById($Id)
    } catch [System.ArgumentException] {
        return $null
    }

    try {
        $retainedHandle = $candidate.SafeHandle
        if ($candidate.HasExited) {
            $candidate.Dispose()
            return $null
        }

        $actualStartTimeUtcTicks = $candidate.StartTime.ToUniversalTime().Ticks
        if ($ExpectedStartTimeUtcTicks -gt 0 -and $actualStartTimeUtcTicks -ne $ExpectedStartTimeUtcTicks) {
            $candidate.Dispose()
            return $null
        }

        return $candidate
    } catch [System.InvalidOperationException] {
        $candidate.Dispose()
        return $null
    }
}

function Read-ProcessIdentity([System.Diagnostics.Process]$Process) {
    try {
        if ($Process.HasExited) {
            return $null
        }

        $item = Get-CimInstance Win32_Process -Filter "ProcessId = $($Process.Id)"
        if ($null -eq $item -or $Process.HasExited) {
            return $null
        }

        $exactStartTimeUtcTicks = $Process.StartTime.ToUniversalTime().Ticks
        $reportedStartTimeUtcTicks = $item.CreationDate.ToUniversalTime().Ticks
        if ([Math]::Abs($exactStartTimeUtcTicks - $reportedStartTimeUtcTicks) -gt 9) {
            return $null
        }

        [PSCustomObject]@{
            ProcessId = [int]$item.ProcessId
            ParentProcessId = [int]$item.ParentProcessId
            StartTimeUtcTicks = $exactStartTimeUtcTicks
        }
    } catch [System.InvalidOperationException] {
        return $null
    }
}

$expectedStartTimeUtcTicks =
    if ($Operation -eq "Inspect") { 0 } else { $StartTimeUtcTicks }
$ownedProcess = Open-ProcessHandle $ProcessId $expectedStartTimeUtcTicks
if ($null -eq $ownedProcess) {
    exit 3
}

try {
    switch ($Operation) {
        "Inspect" {
            $identity = Read-ProcessIdentity $ownedProcess
            if ($null -eq $identity) {
                exit 3
            }

            "{0}|{1}|{2}" -f $identity.ProcessId, $identity.ParentProcessId, $identity.StartTimeUtcTicks
        }

        "Terminate" {
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
        }
    }
} finally {
    $ownedProcess.Dispose()
}
