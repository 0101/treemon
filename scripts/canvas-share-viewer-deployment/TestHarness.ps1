function Assert-Equal {
    param(
        [AllowNull()][object] $Actual,
        [AllowNull()][object] $Expected,
        [Parameter(Mandatory)][string] $Because
    )

    if ($Actual -ne $Expected) {
        throw "Expected '$Expected' but got '$Actual': $Because"
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory)][bool] $Condition,
        [Parameter(Mandatory)][string] $Because
    )

    if (-not $Condition) {
        throw "Expected true: $Because"
    }
}

function Invoke-TestCase {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][scriptblock] $Body
    )

    & $Body
    Write-Host "PASS: $Name"
}
