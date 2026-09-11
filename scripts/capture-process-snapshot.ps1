param(
    [int]$RootPid = 0,
    [string]$ProcessIds = ""
)

$ErrorActionPreference = "Stop"

function Get-ProcessStartTicks($Process) {
    try {
        $runtimeProcess =
            [Diagnostics.Process]::GetProcessById([int]$Process.ProcessId)

        try {
            return [string]([int64]$runtimeProcess.StartTime.ToUniversalTime().Ticks)
        } finally {
            $runtimeProcess.Dispose()
        }
    } catch [ArgumentException] {
        return "0"
    } catch [InvalidOperationException] {
        return "0"
    }
}

function Convert-ProcessRow($Process) {
    [pscustomobject]@{
        pid = [int]$Process.ProcessId
        parentPid = [int]$Process.ParentProcessId
        startTicks = Get-ProcessStartTicks $Process
        name = [string]$Process.Name
        commandLine = [string]$Process.CommandLine
    }
}

function Get-DescendantProcesses([int[]]$ParentIds) {
    $children = @(
        $ParentIds |
            ForEach-Object {
                Get-CimInstance Win32_Process -Filter "ParentProcessId = $_"
            } |
            Where-Object { $_ }
    )

    if ($children.Count -eq 0) {
        return @()
    }

    return @($children) + @(
        Get-DescendantProcesses @($children | ForEach-Object { [int]$_.ProcessId })
    )
}

$processes =
    if (-not [string]::IsNullOrWhiteSpace($ProcessIds)) {
        @(
            $ProcessIds.Split(",", [StringSplitOptions]::RemoveEmptyEntries) |
                ForEach-Object {
                    Get-CimInstance Win32_Process -Filter "ProcessId = $([int]$_)" -ErrorAction SilentlyContinue
                } |
                Where-Object { $_ }
        )
    } elseif ($RootPid -gt 0) {
        $root =
            Get-CimInstance Win32_Process -Filter "ProcessId = $RootPid" -ErrorAction SilentlyContinue

        @($root | Where-Object { $_ }) + @(Get-DescendantProcesses @($RootPid))
    } else {
        throw "Specify RootPid or ProcessIds"
    }

ConvertTo-Json -InputObject @($processes | ForEach-Object { Convert-ProcessRow $_ }) -Compress
