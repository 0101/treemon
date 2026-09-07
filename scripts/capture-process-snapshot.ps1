param(
    [int]$RootPid = 0,
    [string]$ProcessIds = ""
)

$ErrorActionPreference = "Stop"

function Convert-ProcessRow($Process) {
    [pscustomobject]@{
        pid = [int]$Process.ProcessId
        parentPid = [int]$Process.ParentProcessId
        startTicks =
            if ($Process.CreationDate) {
                [int64]$Process.CreationDate.ToUniversalTime().Ticks
            } else {
                0
            }
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
