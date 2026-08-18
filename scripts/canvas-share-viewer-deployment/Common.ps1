function Write-Step {
    param([Parameter(Mandatory)][string] $Message)

    Write-Host "==> $Message"
}

function Format-AzCommandName {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $argumentCount = [Math]::Min(3, $Arguments.Count)
    if ($argumentCount -eq 0) {
        return 'az'
    }

    "az $($Arguments[0..($argumentCount - 1)] -join ' ')"
}

function Invoke-AzRaw {
    param(
        [Parameter(Mandatory)][string[]] $Arguments,
        [switch] $AllowFailure
    )

    $errorPath = [IO.Path]::GetTempFileName()

    try {
        $output = @(& az @Arguments 2> $errorPath)
        $exitCode = $LASTEXITCODE
        $errorText =
            if (Test-Path -LiteralPath $errorPath) {
                [IO.File]::ReadAllText($errorPath).Trim()
            } else {
                ''
            }
    } finally {
        Remove-Item -LiteralPath $errorPath -Force -ErrorAction SilentlyContinue
    }

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        $detail =
            if ([string]::IsNullOrWhiteSpace($errorText)) {
                "exit code $exitCode"
            } else {
                $errorText
            }

        throw "$(Format-AzCommandName $Arguments) failed: $detail"
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Output = ($output -join [Environment]::NewLine)
        Error = $errorText
    }
}

function ConvertFrom-AzJson {
    param(
        [Parameter(Mandatory)][pscustomobject] $Result,
        [Parameter(Mandatory)][string[]] $Arguments
    )

    if ([string]::IsNullOrWhiteSpace($Result.Output)) {
        return $null
    }

    try {
        ConvertFrom-Json -InputObject $Result.Output -Depth 100
    } catch {
        throw "$(Format-AzCommandName $Arguments) returned invalid JSON."
    }
}

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $jsonArguments = @($Arguments) + @('--only-show-errors', '--output', 'json')
    $result = Invoke-AzRaw -Arguments $jsonArguments
    ConvertFrom-AzJson -Result $result -Arguments $Arguments
}

function Try-AzJson {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $jsonArguments = @($Arguments) + @('--only-show-errors', '--output', 'json')
    $result = Invoke-AzRaw -Arguments $jsonArguments -AllowFailure

    if ($result.ExitCode -eq 0) {
        return ConvertFrom-AzJson -Result $result -Arguments $Arguments
    }

    if ($result.Error -match '(?i)(not found|does not exist|could not be found|ResourceNotFound|ParentResourceNotFound|ManagementPolicyNotFound)') {
        return $null
    }

    $detail =
        if ([string]::IsNullOrWhiteSpace($result.Error)) {
            "exit code $($result.ExitCode)"
        } else {
            $result.Error
        }

    throw "$(Format-AzCommandName $Arguments) failed: $detail"
}

function Invoke-AzNone {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $noneArguments = @($Arguments) + @('--only-show-errors', '--output', 'none')
    Invoke-AzRaw -Arguments $noneArguments | Out-Null
}

function Get-TreemonConfigPath {
    $configuredDirectory = [Environment]::GetEnvironmentVariable('TREEMON_CONFIG_DIR')
    $configDirectory =
        if ([string]::IsNullOrWhiteSpace($configuredDirectory)) {
            Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.treemon'
        } else {
            $configuredDirectory
        }

    Join-Path $configDirectory 'config.json'
}

function Read-TreemonCanvasShareConfig {
    $path = Get-TreemonConfigPath

    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Treemon machine configuration was not found at '$path'. Configure canvasShare.accountName first."
    }

    $raw = [IO.File]::ReadAllText($path)

    try {
        $root = ConvertFrom-Json -InputObject $raw -AsHashtable -Depth 100
    } catch {
        throw "Treemon machine configuration at '$path' is not valid JSON."
    }

    if ($root -isnot [Collections.IDictionary] -or
        -not $root.Contains('canvasShare') -or
        $root['canvasShare'] -isnot [Collections.IDictionary]) {
        throw "Treemon machine configuration must contain a canvasShare object with accountName."
    }

    $canvasShare = $root['canvasShare']
    $accountName =
        if ($canvasShare.Contains('accountName')) {
            [string] $canvasShare['accountName']
        } else {
            ''
        }

    if ([string]::IsNullOrWhiteSpace($accountName)) {
        throw "Treemon machine configuration must set canvasShare.accountName before viewer deployment."
    }

    $container =
        if ($canvasShare.Contains('container') -and
            -not [string]::IsNullOrWhiteSpace([string] $canvasShare['container'])) {
            ([string] $canvasShare['container']).Trim()
        } else {
            'canvas-shared'
        }

    [pscustomobject]@{
        Path = $path
        Raw = $raw
        Root = $root
        AccountName = $accountName.Trim()
        Container = $container
    }
}

function Set-TreemonViewerBaseUrl {
    param(
        [Parameter(Mandatory)][string] $ExpectedAccountName,
        [Parameter(Mandatory)][string] $ExpectedContainer
    )

    $configuration = Read-TreemonCanvasShareConfig

    if ($configuration.AccountName -cne $ExpectedAccountName -or
        $configuration.Container -cne $ExpectedContainer) {
        throw 'Treemon canvasShare storage configuration changed during deployment. The viewer URL was not written.'
    }

    $configuration.Root['canvasShare']['viewerBaseUrl'] = $viewerBaseUrl
    $serialized = ConvertTo-Json -InputObject $configuration.Root -Depth 100
    $serialized = "$serialized$([Environment]::NewLine)"
    $directory = Split-Path -Parent $configuration.Path
    $temporaryPath = Join-Path $directory "config.json.viewer-$PID-$([Guid]::NewGuid().ToString('N')).tmp"
    $backupPath = "$temporaryPath.bak"

    try {
        [IO.File]::WriteAllText(
            $temporaryPath,
            $serialized,
            [Text.UTF8Encoding]::new($false))

        if ([IO.File]::ReadAllText($configuration.Path) -cne $configuration.Raw) {
            throw 'Treemon machine configuration changed while it was being updated. Retry deployment to set viewerBaseUrl.'
        }

        [IO.File]::Replace($temporaryPath, $configuration.Path, $backupPath, $true)
    } finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
    }
}

function Assert-Prerequisites {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw 'Azure CLI (az) is required.'
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET SDK 10 or later is required.'
    }

    if (-not (Test-Path -LiteralPath $viewerProject -PathType Leaf)) {
        throw "Viewer project was not found at '$viewerProject'."
    }

    if (-not (Test-Path -LiteralPath $lifecyclePolicyPath -PathType Leaf)) {
        throw "Lifecycle policy was not found at '$lifecyclePolicyPath'."
    }

    $azVersions = Invoke-AzJson -Arguments @('version')
    $azVersion = [version] $azVersions.'azure-cli'

    if ($azVersion -lt [version] '2.72.0') {
        throw "Azure CLI 2.72.0 or later is required for viewer RBAC inspection and Microsoft Entra-authenticated App Service deployment. Found $azVersion."
    }

    $dotnetVersionText = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not read the installed .NET SDK version.'
    }

    $dotnetVersion = [version] $dotnetVersionText.Split('-')[0]
    if ($dotnetVersion.Major -lt 10) {
        throw ".NET SDK 10 or later is required. Found $dotnetVersionText."
    }
}

function Get-LifecycleRule {
    param([Parameter(Mandatory)][string] $Container)

    try {
        $policy = Get-Content -LiteralPath $lifecyclePolicyPath -Raw | ConvertFrom-Json -Depth 100
    } catch {
        throw "Lifecycle policy at '$lifecyclePolicyPath' is not valid JSON."
    }

    $rules = @($policy.rules)
    $matchingRules = @($rules | Where-Object name -CEQ 'expire-shared-canvas-docs')

    if ($matchingRules.Count -ne 1) {
        throw "Lifecycle policy must contain exactly one 'expire-shared-canvas-docs' rule."
    }

    $rule = $matchingRules[0]
    $deleteDays = [double] $rule.definition.actions.baseBlob.delete.daysAfterModificationGreaterThan

    if ($deleteDays -lt $minimumLifecycleDays) {
        throw "Lifecycle deletion must start after at least $minimumLifecycleDays days."
    }

    $rule.definition.filters.prefixMatch = @("$Container/")
    $rule
}

function Get-StorageAccount {
    param(
        [Parameter(Mandatory)][string] $AccountName,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    $account = Try-AzJson -Arguments @(
        'storage', 'account', 'show',
        '--name', $AccountName,
        '--subscription', $SubscriptionId)

    if ($null -eq $account) {
        throw "Storage account '$AccountName' was not found in the selected subscription."
    }

    $account
}

function New-ViewerPackage {
    param([Parameter(Mandatory)][string] $WorkingDirectory)

    $publishDirectory = Join-Path $WorkingDirectory 'publish'
    $packagePath = Join-Path $WorkingDirectory 'canvas-share-viewer.zip'

    Write-Step 'Building the viewer deployment package'
    & dotnet publish $viewerProject `
        --configuration Release `
        --output $publishDirectory `
        --nologo `
        --verbosity minimal

    if ($LASTEXITCODE -ne 0) {
        throw 'CanvasShareViewer publish failed.'
    }

    Compress-Archive `
        -Path (Join-Path $publishDirectory '*') `
        -DestinationPath $packagePath `
        -CompressionLevel Optimal

    $packagePath
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory)][object] $Value,
        [Parameter(Mandatory)][string] $Path
    )

    $json = ConvertTo-Json -InputObject $Value -Depth 100
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Get-AppSettingValue {
    param(
        [Parameter(Mandatory)][object[]] $Settings,
        [Parameter(Mandatory)][string] $Name
    )

    $matches = @($Settings | Where-Object name -CEQ $Name)
    if ($matches.Count -ne 1) {
        return $null
    }

    [string] $matches[0].value
}
