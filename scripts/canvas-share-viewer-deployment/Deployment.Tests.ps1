#requires -Version 7.4

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$viewerProject = Join-Path $repoRoot 'src' 'CanvasShareViewer' 'CanvasShareViewer.fsproj'
$lifecyclePolicyPath = Join-Path $repoRoot 'scripts' 'canvas-share-lifecycle-policy.json'
$minimumLifecycleDays = 31
$viewerBaseUrl = 'https://treemon.azurewebsites.net'

. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'SubscriptionGuard.ps1')

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

Invoke-TestCase 'deployment entry point loads the subscription guard after Common and before Azure' {
    $entryPoint =
        [IO.File]::ReadAllText(
            (Join-Path $repoRoot 'scripts' 'deploy-canvas-share-viewer.ps1'))
    $commonIndex =
        $entryPoint.IndexOf("'Common.ps1'", [StringComparison]::Ordinal)
    $subscriptionGuardIndex =
        $entryPoint.IndexOf("'SubscriptionGuard.ps1'", [StringComparison]::Ordinal)
    $azureIndex =
        $entryPoint.IndexOf("'Azure.ps1'", [StringComparison]::Ordinal)

    Assert-True `
        -Condition ($commonIndex -ge 0 -and
            $subscriptionGuardIndex -gt $commonIndex -and
            $azureIndex -gt $subscriptionGuardIndex) `
        -Because 'the subscription guard depends on Common and must be available before Azure deployment orchestration'
}

function dotnet {
    $arguments = @($args)
    $outputIndex = [Array]::IndexOf($arguments, '--output')
    if ($outputIndex -lt 0 -or $outputIndex + 1 -ge $arguments.Count) {
        throw 'Mock dotnet publish did not receive --output.'
    }

    $publishDirectory = [string] $arguments[$outputIndex + 1]
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $publishDirectory 'CanvasShareViewer.dll'),
        'deployment fixture',
        [Text.UTF8Encoding]::new($false))
    Write-Output 'mock dotnet publish output'
    $global:LASTEXITCODE = 0
}

Invoke-TestCase 'viewer package returns only its ZIP path' {
    $workingDirectory =
        Join-Path ([IO.Path]::GetTempPath()) "treemon-deployment-test-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $workingDirectory | Out-Null

    try {
        $results = @(New-ViewerPackage -WorkingDirectory $workingDirectory)

        Assert-Equal `
            -Actual $results.Count `
            -Expected 1 `
            -Because 'dotnet publish output must not flow into the PackagePath value'
        Assert-True `
            -Condition ($results[0] -is [string]) `
            -Because 'Deploy-Viewer requires one string PackagePath'
        Assert-True `
            -Condition (Test-Path -LiteralPath $results[0] -PathType Leaf) `
            -Because 'the returned ZIP package must exist'
    } finally {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Remove-Item Function:dotnet

$appName = 'treemon'
$callbackUrl = 'https://treemon.azurewebsites.net/.auth/login/aad/callback'
$federatedCredentialName = 'treemon-easy-auth'
$managedIdentityAssertionSetting = 'OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID'
$readerRole = 'Storage Blob Data Reader'
$contributorRole = 'Storage Blob Data Contributor'
$ResourceGroup = 'viewer-rg'
$Plan = 'viewer-plan'
$Identity = 'viewer-identity'
$Registration = 'viewer-registration'

. (Join-Path $PSScriptRoot 'Azure.ps1')
. (Join-Path $PSScriptRoot 'CrossSubscriptionCorrection.ps1')

$script:scenario = ''
$script:azNoneCalls = @()
$script:azJsonCalls = @()
$script:azTryCalls = @()
$script:blobAccessAudits = @()
$script:subscriptionAccounts = @{}
$script:subscriptionLookupFailures = @{}
$script:selectedAccount = $null
$script:selectedAccountFailure = ''
$script:publisherResult = $null
$script:createCallCount = 0
$script:nameAvailabilityCallCount = 0
$script:serviceManagementReference = '11111111-2222-3333-4444-555555555555'
$script:planResourceId =
    '/subscriptions/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/resourceGroups/viewer-rg/providers/Microsoft.Web/serverfarms/viewer-plan'
$script:correctionSubscriptionId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
$script:correctionStorageAccount =
    [pscustomobject]@{
        id = "/subscriptions/$script:correctionSubscriptionId/resourceGroups/storage-rg/providers/Microsoft.Storage/storageAccounts/fixturestorage"
        name = 'fixturestorage'
        location = 'westeurope'
        resourceGroup = 'storage-rg'
        allowBlobPublicAccess = $false
    }
$script:correctionGroup =
    [pscustomobject]@{
        id = "/subscriptions/$script:correctionSubscriptionId/resourceGroups/$ResourceGroup"
        name = $ResourceGroup
        location = 'westeurope'
        tags = [pscustomobject]@{ environment = 'nonproduction' }
    }
$script:correctionIdentity =
    [pscustomobject]@{
        id = "/subscriptions/$script:correctionSubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.ManagedIdentity/userAssignedIdentities/$Identity"
        name = $Identity
        principalId = '22222222-2222-2222-2222-222222222222'
        clientId = '33333333-3333-3333-3333-333333333333'
    }
$script:correctionPlan =
    [pscustomobject]@{
        id = $script:planResourceId
        name = $Plan
        reserved = $true
    }
$script:correctionWebApp =
    [pscustomobject]@{
        id = "/subscriptions/$script:correctionSubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/$appName"
        name = $appName
        defaultHostName = 'treemon.azurewebsites.net'
        kind = 'app,linux'
        appServicePlanId = $script:planResourceId
        httpsOnly = $true
    }
$script:webAppResult =
    [pscustomobject]@{
        appServicePlanId = $script:planResourceId
        identity = $null
    }
$script:registrationResult =
    [pscustomobject]@{
        appId = '99999999-8888-7777-6666-555555555555'
        displayName = $Registration
        signInAudience = 'AzureADMyOrg'
        passwordCredentials = @()
        serviceManagementReference = $script:serviceManagementReference
        web = [pscustomobject]@{
            redirectUris = @($callbackUrl)
            implicitGrantSettings = [pscustomobject]@{
                enableAccessTokenIssuance = $false
                enableIdTokenIssuance = $true
            }
        }
    }

function Write-Step {
    param([Parameter(Mandatory)][string] $Message)
}

function Invoke-AzNone {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $script:azNoneCalls +=
        [pscustomobject]@{ Arguments = @($Arguments) }
}

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $script:azJsonCalls +=
        [pscustomobject]@{ Arguments = @($Arguments) }

    if ($script:scenario -eq 'azure-context') {
        $command = $Arguments[0..([Math]::Min(2, $Arguments.Count - 1))] -join ' '

        switch ($command) {
            'cloud show' {
                return [pscustomobject]@{ name = 'AzureCloud' }
            }
            'account show --subscription' {
                $subscriptionIndex = [Array]::IndexOf($Arguments, '--subscription')
                $subscription = [string] $Arguments[$subscriptionIndex + 1]

                if ($script:subscriptionLookupFailures.ContainsKey($subscription)) {
                    throw [string] $script:subscriptionLookupFailures[$subscription]
                }

                if (-not $script:subscriptionAccounts.ContainsKey($subscription)) {
                    throw 'Synthetic subscription is unavailable.'
                }

                return $script:subscriptionAccounts[$subscription]
            }
            'account show' {
                if (-not [string]::IsNullOrWhiteSpace($script:selectedAccountFailure)) {
                    throw $script:selectedAccountFailure
                }

                return $script:selectedAccount
            }
            'ad signed-in-user show' {
                return $script:publisherResult
            }
            default {
                throw 'Unexpected Azure CLI command in subscription-guard test.'
            }
        }
    }

    if ($script:scenario -eq 'global-name-unavailable') {
        if ($Arguments[0] -eq 'rest' -and
            ($Arguments -join ' ') -match 'checknameavailability') {
            $script:nameAvailabilityCallCount++
            return [pscustomobject]@{ nameAvailable = $false }
        }

        throw 'Unexpected Azure CLI command in global-name-availability test.'
    }

    if ($script:scenario -eq 'correction-preparation') {
        if ($Arguments[0] -eq 'rest' -and
            ($Arguments -join ' ') -match 'checknameavailability') {
            $script:nameAvailabilityCallCount++
            return [pscustomobject]@{ nameAvailable = $false }
        }

        $command = $Arguments[0..2] -join ' '

        switch ($command) {
            'group show --name' {
                return $script:correctionGroup
            }
            'identity show --name' {
                return $script:correctionIdentity
            }
            'role assignment list' {
                return @()
            }
            default {
                throw 'Unexpected Azure CLI command in correction-preparation test.'
            }
        }
    }

    if ($script:scenario -eq 'correction-destination') {
        $command = $Arguments[0..2] -join ' '

        switch ($command) {
            'ad app list' {
                return @($script:registrationResult)
            }
            'ad app show' {
                return $script:registrationResult
            }
            'webapp config appsettings' {
                return @()
            }
            default {
                throw 'Unexpected Azure CLI command in correction-destination test.'
            }
        }
    }

    $command = $Arguments[0..2] -join ' '

    switch ($script:scenario) {
        'webapp' {
            if ($command -eq 'webapp show --name') {
                return $script:webAppResult
            }
        }
        'registration' {
            switch ($command) {
                'ad app create' {
                    $script:createCallCount++

                    if ($Arguments -notcontains '--service-management-reference') {
                        throw 'ServiceManagementReference field is required for Create, but is missing in the request.'
                    }

                    return $script:registrationResult
                }
                'ad app list' {
                    if ($Arguments -notcontains '--show-mine') {
                        throw 'Service-management reference discovery must be limited to apps owned by the current user.'
                    }

                    return @(
                        [pscustomobject]@{
                            serviceManagementReference = $script:serviceManagementReference
                        }
                    )
                }
                'ad sp list' {
                    return @()
                }
                'ad app show' {
                    return $script:registrationResult
                }
            }
        }
    }

    throw "Unexpected mocked Azure CLI command: $($Arguments -join ' ')"
}

function Try-AzJson {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $script:azTryCalls +=
        [pscustomobject]@{ Arguments = @($Arguments) }
    $command = $Arguments[0..2] -join ' '

    if ($script:scenario -eq 'correction-preparation') {
        switch ($command) {
            'storage container-rm show' {
                return [pscustomobject]@{ publicAccess = 'None' }
            }
            'group show --name' {
                return $null
            }
            default {
                throw 'Unexpected optional Azure CLI command in correction-preparation test.'
            }
        }
    }

    if ($script:scenario -eq 'correction-destination') {
        switch ($command) {
            'group show --name' {
                return $script:correctionGroup
            }
            'identity show --name' {
                return $script:correctionIdentity
            }
            'appservice plan show' {
                return $script:correctionPlan
            }
            'rest --method get' {
                if (($Arguments -join ' ') -match '/sites/treemon\?api-version=') {
                    return $script:correctionWebApp
                }

                return $null
            }
            default {
                throw 'Unexpected optional Azure CLI command in correction-destination test.'
            }
        }
    }

    throw "Unexpected mocked optional Azure CLI command: $($Arguments -join ' ')"
}

function Assert-ViewerBlobAccessIsContainerOnly {
    param(
        [Parameter(Mandatory)][string] $PrincipalObjectId,
        [Parameter(Mandatory)][string] $ContainerScope,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    $script:blobAccessAudits +=
        [pscustomobject]@{
            PrincipalObjectId = $PrincipalObjectId
            ContainerScope = $ContainerScope
            SubscriptionId = $SubscriptionId
        }
}

function Reset-AzureContextMocks {
    $script:scenario = 'azure-context'
    $script:azJsonCalls = @()
    $script:subscriptionAccounts = @{}
    $script:subscriptionLookupFailures = @{}
    $script:selectedAccount = $null
    $script:selectedAccountFailure = ''
    $script:publisherResult =
        [pscustomobject]@{
            id = 'dddddddd-dddd-dddd-dddd-dddddddddddd'
        }
}

function Reset-CorrectionMocks {
    param([Parameter(Mandatory)][string] $Scenario)

    $script:scenario = $Scenario
    $script:azNoneCalls = @()
    $script:azJsonCalls = @()
    $script:azTryCalls = @()
    $script:blobAccessAudits = @()
    $script:nameAvailabilityCallCount = 0
}

function New-SyntheticAccount {
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $TenantId
    )

    [pscustomobject]@{
        id = $Id
        tenantId = $TenantId
        state = 'Enabled'
        user = [pscustomobject]@{ type = 'user' }
    }
}

function Assert-NoPrivilegedAzureCalls {
    $privilegedCalls =
        @(
            $script:azJsonCalls
            | Where-Object {
                $_.Arguments[0] -notin @('cloud', 'account')
            }
        )

    Assert-Equal `
        -Actual $privilegedCalls.Count `
        -Expected 0 `
        -Because 'the subscription guard must fail before any resource-provider or Entra call'
}

function Assert-SubscriptionValuesRedacted {
    param(
        [Parameter(Mandatory)][string] $Message,
        [Parameter(Mandatory)][string[]] $Values
    )

    $revealed =
        @(
            $Values
            | Where-Object {
                $Message.Contains($_, [StringComparison]::OrdinalIgnoreCase)
            }
        )

    Assert-Equal `
        -Actual $revealed.Count `
        -Expected 0 `
        -Because 'subscription guard diagnostics must not reveal configured names or IDs'
}

$approvedSubscription = 'approved-subscription'
$requestedSubscription = 'requested-subscription'
$approvedSubscriptionId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
$otherSubscriptionId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
$tenantId = 'cccccccc-cccc-cccc-cccc-cccccccccccc'

Invoke-TestCase 'requested subscription mismatch fails before privileged Azure calls' {
    Reset-AzureContextMocks
    $approvedAccount =
        New-SyntheticAccount `
            -Id $approvedSubscriptionId `
            -TenantId $tenantId
    $script:subscriptionAccounts[$approvedSubscription] = $approvedAccount
    $script:subscriptionAccounts[$requestedSubscription] =
        New-SyntheticAccount `
            -Id $otherSubscriptionId `
            -TenantId $tenantId
    $script:selectedAccount = $approvedAccount
    $message = ''

    try {
        Get-AzureContext `
            -ApprovedSubscription $approvedSubscription `
            -RequestedSubscription $requestedSubscription `
            -RequestedTenant $tenantId |
            Out-Null
    } catch {
        $message = $_.Exception.Message
    }

    Assert-True `
        -Condition ($message -match 'mismatch' -and
            $message -match 'canvasShare\.approvedSubscription') `
        -Because 'the requested-subscription failure must identify the protected config key and mismatch'
    Assert-NoPrivilegedAzureCalls
    Assert-SubscriptionValuesRedacted `
        -Message $message `
        -Values @(
            $approvedSubscription,
            $requestedSubscription,
            $approvedSubscriptionId,
            $otherSubscriptionId)
}

Invoke-TestCase 'selected-account authentication failure preserves the Azure CLI diagnostic' {
    Reset-AzureContextMocks
    $script:selectedAccountFailure =
        "az account show failed: Please run 'az login' to set up an account."
    $message = ''

    try {
        Get-AzureContext `
            -ApprovedSubscription $approvedSubscription `
            -RequestedSubscription $requestedSubscription `
            -RequestedTenant $tenantId |
            Out-Null
    } catch {
        $message = $_.Exception.Message
    }

    Assert-True `
        -Condition ($message -match "Please run 'az login'") `
        -Because 'an unavailable selected account must retain the actionable Azure CLI authentication diagnostic'
    $subscriptionLookups =
        @(
            $script:azJsonCalls
            | Where-Object { $_.Arguments -contains '--subscription' }
        )
    Assert-Equal `
        -Actual $subscriptionLookups.Count `
        -Expected 0 `
        -Because 'the selected account must resolve before either private subscription value is used'
    Assert-NoPrivilegedAzureCalls
    Assert-SubscriptionValuesRedacted `
        -Message $message `
        -Values @($approvedSubscription, $requestedSubscription)
}

Invoke-TestCase 'private subscription lookup failures are sanitized' {
    Reset-AzureContextMocks
    $approvedAccount =
        New-SyntheticAccount `
            -Id $approvedSubscriptionId `
            -TenantId $tenantId
    $script:selectedAccount = $approvedAccount
    $privateFailureMarker = 'private-lookup-stderr'
    $script:subscriptionLookupFailures[$approvedSubscription] =
        "$privateFailureMarker $approvedSubscription $approvedSubscriptionId"
    $message = ''

    try {
        Get-AzureContext `
            -ApprovedSubscription $approvedSubscription `
            -RequestedSubscription $requestedSubscription `
            -RequestedTenant $tenantId |
            Out-Null
    } catch {
        $message = $_.Exception.Message
    }

    Assert-True `
        -Condition ($message -match 'canvasShare\.approvedSubscription' -and
            $message -match 'could not be resolved') `
        -Because 'private-value lookup failures must identify only the protected configuration source'
    Assert-SubscriptionValuesRedacted `
        -Message $message `
        -Values @(
            $approvedSubscription,
            $approvedSubscriptionId,
            $privateFailureMarker)
    Assert-NoPrivilegedAzureCalls
}

Invoke-TestCase 'missing approved subscription fails closed with a sanitized diagnostic' {
    Reset-AzureContextMocks
    $script:selectedAccount =
        New-SyntheticAccount `
            -Id $approvedSubscriptionId `
            -TenantId $tenantId
    $message = ''

    try {
        Get-AzureContext `
            -ApprovedSubscription $approvedSubscription `
            -RequestedSubscription $requestedSubscription `
            -RequestedTenant $tenantId |
            Out-Null
    } catch {
        $message = $_.Exception.Message
    }

    Assert-True `
        -Condition ($message -match 'canvasShare\.approvedSubscription' -and
            $message -match 'could not be resolved') `
        -Because 'a missing approved subscription must fail closed'
    Assert-SubscriptionValuesRedacted `
        -Message $message `
        -Values @($approvedSubscription, $approvedSubscriptionId)
    Assert-NoPrivilegedAzureCalls
}

Invoke-TestCase 'disabled requested subscription fails closed with a sanitized diagnostic' {
    Reset-AzureContextMocks
    $approvedAccount =
        New-SyntheticAccount `
            -Id $approvedSubscriptionId `
            -TenantId $tenantId
    $disabledRequestedAccount =
        New-SyntheticAccount `
            -Id $approvedSubscriptionId `
            -TenantId $tenantId
    $disabledRequestedAccount.state = 'Disabled'
    $script:selectedAccount = $approvedAccount
    $script:subscriptionAccounts[$approvedSubscription] = $approvedAccount
    $script:subscriptionAccounts[$requestedSubscription] = $disabledRequestedAccount
    $message = ''

    try {
        Get-AzureContext `
            -ApprovedSubscription $approvedSubscription `
            -RequestedSubscription $requestedSubscription `
            -RequestedTenant $tenantId |
            Out-Null
    } catch {
        $message = $_.Exception.Message
    }

    Assert-True `
        -Condition ($message -match 'The -Subscription input' -and
            $message -match 'could not be resolved') `
        -Because 'a disabled requested subscription must fail closed'
    Assert-SubscriptionValuesRedacted `
        -Message $message `
        -Values @($requestedSubscription, $approvedSubscriptionId)
    Assert-NoPrivilegedAzureCalls
}

Invoke-TestCase 'selected Azure account mismatch fails before privileged Azure calls' {
    Reset-AzureContextMocks
    $approvedAccount =
        New-SyntheticAccount `
            -Id $approvedSubscriptionId `
            -TenantId $tenantId
    $script:subscriptionAccounts[$approvedSubscription] = $approvedAccount
    $script:subscriptionAccounts[$requestedSubscription] = $approvedAccount
    $script:selectedAccount =
        New-SyntheticAccount `
            -Id $otherSubscriptionId `
            -TenantId $tenantId
    $message = ''

    try {
        Get-AzureContext `
            -ApprovedSubscription $approvedSubscription `
            -RequestedSubscription $requestedSubscription `
            -RequestedTenant $tenantId |
            Out-Null
    } catch {
        $message = $_.Exception.Message
    }

    Assert-True `
        -Condition ($message -match 'mismatch' -and
            $message -match 'canvasShare\.approvedSubscription') `
        -Because 'the selected-account failure must identify the protected config key and mismatch'
    Assert-NoPrivilegedAzureCalls
    Assert-SubscriptionValuesRedacted `
        -Message $message `
        -Values @(
            $approvedSubscription,
            $requestedSubscription,
            $approvedSubscriptionId,
            $otherSubscriptionId)
}

Invoke-TestCase 'absent approved-subscription config fails before any Azure call' {
    Reset-AzureContextMocks
    $configDirectory =
        Join-Path ([IO.Path]::GetTempPath()) "treemon-config-test-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $configDirectory | Out-Null
    $previousConfigDirectory = $env:TREEMON_CONFIG_DIR

    try {
        $env:TREEMON_CONFIG_DIR = $configDirectory
        [IO.File]::WriteAllText(
            (Join-Path $configDirectory 'config.json'),
            '{"canvasShare":{"accountName":"fixture-account","container":"fixture-container"}}',
            [Text.UTF8Encoding]::new($false))
        $configuration = Read-TreemonCanvasShareConfig
        $message = ''

        try {
            Get-AzureContext `
                -ApprovedSubscription $configuration.ApprovedSubscription `
                -RequestedSubscription $requestedSubscription `
                -RequestedTenant $tenantId |
                Out-Null
        } catch {
            $message = $_.Exception.Message
        }

        Assert-True `
            -Condition ($message -match 'canvasShare\.approvedSubscription') `
            -Because 'an absent key must fail closed and identify the missing configuration'
        Assert-Equal `
            -Actual $script:azJsonCalls.Count `
            -Expected 0 `
            -Because 'an absent key must stop before Azure CLI is invoked'
    } finally {
        $env:TREEMON_CONFIG_DIR = $previousConfigDirectory
        Remove-Item -LiteralPath $configDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-TestCase 'approved subscription context proceeds to publisher lookup' {
    Reset-AzureContextMocks
    $approvedAccount =
        New-SyntheticAccount `
            -Id $approvedSubscriptionId `
            -TenantId $tenantId
    $script:subscriptionAccounts[$approvedSubscription] = $approvedAccount
    $script:subscriptionAccounts[$requestedSubscription] = $approvedAccount
    $script:selectedAccount = $approvedAccount

    $context =
        Get-AzureContext `
            -ApprovedSubscription $approvedSubscription `
            -RequestedSubscription $requestedSubscription `
            -RequestedTenant $tenantId

    Assert-Equal `
        -Actual $context.SubscriptionId `
        -Expected $approvedSubscriptionId `
        -Because 'the approved context must return the resolved subscription ID'
    Assert-Equal `
        -Actual $context.TenantId `
        -Expected $tenantId `
        -Because 'the approved context must retain the requested tenant'
    $publisherCalls =
        @(
            $script:azJsonCalls
            | Where-Object {
                ($_.Arguments -join ' ') -ceq 'ad signed-in-user show'
            }
        )
    Assert-Equal `
        -Actual $publisherCalls.Count `
        -Expected 1 `
        -Because 'publisher lookup may proceed only after every subscription identity agrees'
}

Invoke-TestCase 'pre-move preparation bypasses the unavailable global app name safely' {
    Reset-CorrectionMocks -Scenario 'correction-preparation'
    $container = 'fixture-container'
    $containerScope =
        "$($script:correctionStorageAccount.id)/blobServices/default/containers/$container"
    $output = @(
        & {
            Invoke-CrossSubscriptionMovePreparation `
                -StorageAccount $script:correctionStorageAccount `
                -Container $container `
                -ContainerScope $containerScope `
                -SubscriptionId $script:correctionSubscriptionId
        } 6>&1
    ) | ForEach-Object { [string] $_ }
    $outputText = $output -join [Environment]::NewLine
    $mutatingCommands =
        @(
            $script:azNoneCalls
            | ForEach-Object { $_.Arguments[0..2] -join ' ' }
        )

    Assert-Equal `
        -Actual $script:nameAvailabilityCallCount `
        -Expected 0 `
        -Because 'pre-move preparation must not check or weaken global App Service name availability'
    Assert-Equal `
        -Actual $script:azNoneCalls.Count `
        -Expected 3 `
        -Because 'preparation may create only the destination group, replacement identity, and reader grant'
    Assert-True `
        -Condition ($mutatingCommands -contains 'group create --name') `
        -Because 'the approved destination resource group must be prepared'
    Assert-True `
        -Condition ($mutatingCommands -contains 'identity create --name') `
        -Because 'the replacement user-assigned identity must be prepared'
    Assert-True `
        -Condition ($mutatingCommands -contains 'role assignment create') `
        -Because 'the replacement identity must receive its container-scoped reader grant'
    Assert-True `
        -Condition (-not ($mutatingCommands | Where-Object {
            $_ -match '^(appservice|storage|webapp|ad)\b'
        })) `
        -Because 'preparation must not mutate the plan, app, storage, or Entra registration'
    $wrongSubscriptionCalls =
        @(
            @($script:azJsonCalls) + @($script:azTryCalls) + @($script:azNoneCalls)
            | Where-Object {
                $subscriptionIndex =
                    [Array]::IndexOf($_.Arguments, '--subscription')
                $subscriptionIndex -ge 0 -and
                    [string] $_.Arguments[$subscriptionIndex + 1] -cne
                        $script:correctionSubscriptionId
            }
        )
    Assert-Equal `
        -Actual $wrongSubscriptionCalls.Count `
        -Expected 0 `
        -Because 'preparation must issue every resource call only to the approved destination'

    $roleAssignmentCall =
        @(
            $script:azNoneCalls
            | Where-Object {
                ($_.Arguments[0..2] -join ' ') -eq 'role assignment create'
            }
        )[0]
    Assert-True `
        -Condition ($roleAssignmentCall.Arguments -contains $readerRole -and
            $roleAssignmentCall.Arguments -notcontains $contributorRole) `
        -Because 'preparation grants only Storage Blob Data Reader to the replacement identity'
    Assert-Equal `
        -Actual $script:blobAccessAudits.Count `
        -Expected 1 `
        -Because 'the newly prepared identity must be audited after its reader grant'
    Assert-True `
        -Condition ($outputText -match 'Destination preparation complete' -and
            $outputText -match 'ConfirmPortalMoveCompleted') `
        -Because 'preparation must emit the portal handoff and the explicit reconciliation command'
    Assert-True `
        -Condition ($outputText -notmatch '(?i)/subscriptions/|tenant-id|source-rg|source-identity|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}') `
        -Because 'the emitted checklist must not contain source subscription, tenant, group, identity, or resource IDs'

    $entrypoint =
        [IO.File]::ReadAllText(
            (Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy-canvas-share-viewer.ps1'))
    $preparationIndex =
        $entrypoint.IndexOf(
            'Invoke-CrossSubscriptionMovePreparation',
            [StringComparison]::Ordinal)
    $preparationReturnIndex =
        $entrypoint.IndexOf(
            'return',
            $preparationIndex,
            [StringComparison]::Ordinal)
    $ordinaryDiscoveryIndex =
        $entrypoint.IndexOf(
            '$existingResources =',
            $preparationIndex,
            [StringComparison]::Ordinal)
    Assert-True `
        -Condition ($preparationIndex -ge 0 -and
            $preparationReturnIndex -gt $preparationIndex -and
            $preparationReturnIndex -lt $ordinaryDiscoveryIndex) `
        -Because 'the entry point must return from preparation before ordinary app discovery and its global-name guard'
}

Invoke-TestCase 'ordinary creation and ValidateOnly retain the global-name failure' {
    Reset-CorrectionMocks -Scenario 'global-name-unavailable'
    $existing =
        [pscustomobject]@{
            Group = $null
            Plan = $null
            Identity = $null
            WebApp = $null
            Registration = $null
        }
    $messages =
        @(
            'ordinary apply', 'ordinary ValidateOnly'
            | ForEach-Object {
                try {
                    Assert-ExistingResourceSafety `
                        -Existing $existing `
                        -SubscriptionId $script:correctionSubscriptionId
                    ''
                } catch {
                    $_.Exception.Message
                }
            }
        )

    Assert-Equal `
        -Actual $script:nameAvailabilityCallCount `
        -Expected 2 `
        -Because 'both ordinary paths share the unchanged global-name safety check'
    Assert-Equal `
        -Actual @($messages | Where-Object {
            $_ -match "Global App Service name '$appName' is unavailable"
        }).Count `
        -Expected 2 `
        -Because 'ordinary creation and ValidateOnly must still fail while the source app owns the name'

    $entrypoint =
        [IO.File]::ReadAllText(
            (Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy-canvas-share-viewer.ps1'))
    Assert-True `
        -Condition ($entrypoint.IndexOf(
                'Assert-ExistingResourceSafety',
                [StringComparison]::Ordinal) -lt
            $entrypoint.IndexOf(
                'if ($ValidateOnly)',
                [StringComparison]::Ordinal)) `
        -Because 'ordinary validation must run the existing-resource safety guard before its read-only success exit'
}

Invoke-TestCase 'post-move reconciliation requires explicit operator confirmation before tools run' {
    Reset-CorrectionMocks -Scenario 'confirmation-gate'
    $message = ''

    try {
        Assert-CrossSubscriptionCorrectionParameters `
            -ReconcileCrossSubscriptionMove
    } catch {
        $message = $_.Exception.Message
    }

    Assert-True `
        -Condition ($message -match 'ConfirmPortalMoveCompleted') `
        -Because 'reconciliation must name the explicit confirmation switch when it is absent'
    Assert-Equal `
        -Actual ($script:azJsonCalls.Count + $script:azTryCalls.Count + $script:azNoneCalls.Count) `
        -Expected 0 `
        -Because 'the confirmation gate must not perform any Azure operation'

    Assert-CrossSubscriptionCorrectionParameters `
        -ReconcileCrossSubscriptionMove `
        -ConfirmPortalMoveCompleted

    $entrypoint =
        [IO.File]::ReadAllText(
            (Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy-canvas-share-viewer.ps1'))
    Assert-True `
        -Condition ($entrypoint.IndexOf(
                'Assert-CrossSubscriptionCorrectionParameters',
                [StringComparison]::Ordinal) -lt
            $entrypoint.IndexOf(
                'Assert-Prerequisites',
                [StringComparison]::Ordinal)) `
        -Because 'the entry point must enforce confirmation before local tools or Azure are consulted'
}

Invoke-TestCase 'post-move discovery and identity replacement use approved destination state only' {
    Reset-CorrectionMocks -Scenario 'correction-destination'
    $resources =
        Get-CrossSubscriptionMoveDestinationResources `
            -SubscriptionId $script:correctionSubscriptionId
    Assert-CrossSubscriptionMovedResources `
        -Existing $resources `
        -SubscriptionId $script:correctionSubscriptionId

    $resourceCalls = @($script:azTryCalls)
    $unguardedCalls =
        @(
            $resourceCalls
            | Where-Object {
                $subscriptionIndex =
                    [Array]::IndexOf($_.Arguments, '--subscription')
                $subscriptionIndex -lt 0 -or
                    [string] $_.Arguments[$subscriptionIndex + 1] -cne
                        $script:correctionSubscriptionId
            }
        )
    Assert-Equal `
        -Actual $unguardedCalls.Count `
        -Expected 0 `
        -Because 'post-move resource discovery must be scoped to the approved destination subscription'

    $webAppCall =
        @(
            $resourceCalls
            | Where-Object {
                ($_.Arguments[0..2] -join ' ') -eq 'rest --method get' -and
                    ($_.Arguments -join ' ') -match '/sites/treemon\?api-version='
            }
        )[0]
    $queryIndex = [Array]::IndexOf($webAppCall.Arguments, '--query')
    Assert-True `
        -Condition ($queryIndex -ge 0 -and
            [string] $webAppCall.Arguments[$queryIndex + 1] -notmatch '(?i)identity') `
        -Because 'initial destination discovery must not retrieve a source identity attachment'
    Assert-Equal `
        -Actual $script:nameAvailabilityCallCount `
        -Expected 0 `
        -Because 'post-move discovery resolves the moved app and never checks global name availability'

    $script:azNoneCalls = @()

    Set-CrossSubscriptionReplacementIdentity `
        -ManagedIdentity $script:correctionIdentity `
        -SubscriptionId $script:correctionSubscriptionId

    $assignmentCall = @($script:azNoneCalls)[0]
    $identitiesIndex = [Array]::IndexOf($assignmentCall.Arguments, '--identities')
    Assert-Equal `
        -Actual ([string] $assignmentCall.Arguments[$identitiesIndex + 1]) `
        -Expected ([string] $script:correctionIdentity.id) `
        -Because 'the moved app identity set must be replaced with the prepared destination identity'
    Assert-True `
        -Condition (($assignmentCall.Arguments -join ' ') -notmatch 'source-subscription|source-rg|source-identity') `
        -Because 'the identity assignment must not carry a source resource identifier'
}

Invoke-TestCase 'every Azure resource-plane call selects its subscription explicitly' {
    $resourceFamilies =
        @(
            'appservice',
            'group',
            'identity',
            'resource',
            'role',
            'storage',
            'webapp'
        )
    $unguardedCalls =
        @(
            'SubscriptionGuard.ps1',
            'Azure.ps1',
            'CrossSubscriptionCorrection.ps1',
            'ViewerBlobAccess.ps1'
            | ForEach-Object {
                $path = Join-Path $PSScriptRoot $_
                $tokens = $null
                $parseErrors = $null
                $ast =
                    [Management.Automation.Language.Parser]::ParseFile(
                        $path,
                        [ref] $tokens,
                        [ref] $parseErrors)

                Assert-Equal `
                    -Actual $parseErrors.Count `
                    -Expected 0 `
                    -Because 'deployment helpers must parse before their Azure calls can be audited'

                $ast.FindAll(
                    {
                        param($node)
                        $node -is [Management.Automation.Language.CommandAst] -and
                            $node.GetCommandName() -match '^(Invoke-AzJson|Try-AzJson|Invoke-AzNone)$'
                    },
                    $true)
                | Where-Object {
                    $literals =
                        @(
                            $_.FindAll(
                                {
                                    param($node)
                                    $node -is [Management.Automation.Language.StringConstantExpressionAst]
                                },
                                $true)
                            | ForEach-Object Value
                        )
                    $family =
                        if ($literals.Count -gt 1) {
                            $literals[1]
                        } else {
                            ''
                        }

                    ($family -in $resourceFamilies -or
                        ($family -eq 'rest' -and
                            $_.Extent.Text.Contains(
                                '/subscriptions/',
                                [StringComparison]::OrdinalIgnoreCase))) -and
                        $literals -notcontains '--subscription'
                }
            }
        )

    Assert-Equal `
        -Actual $unguardedCalls.Count `
        -Expected 0 `
        -Because 'resource-plane az calls must never inherit the ambient Azure CLI subscription'
}

Invoke-TestCase 'Azure CLI resource shapes resolve without external projections' {
    $nestedPlan =
        [pscustomobject]@{
            properties = [pscustomobject]@{
                reserved = $true
            }
        }
    $nestedWebApp =
        [pscustomobject]@{
            properties = [pscustomobject]@{
                serverFarmId = $script:planResourceId
            }
        }

    Assert-Equal `
        -Actual (Get-AzureResourcePropertyValue -Resource $nestedPlan -Name 'reserved') `
        -Expected $true `
        -Because 'Azure CLI 2.84 nests the Linux-plan flag under properties'
    Assert-Equal `
        -Actual (Get-WebAppPlanResourceId -WebApp $script:webAppResult) `
        -Expected $script:planResourceId `
        -Because 'current Azure CLI uses appServicePlanId'
    Assert-Equal `
        -Actual (Get-WebAppPlanResourceId -WebApp $nestedWebApp) `
        -Expected $script:planResourceId `
        -Because 'ARM-shaped responses can use nested serverFarmId'
}

Invoke-TestCase 'clean and existing web apps use file-backed runtime configuration' {
    $script:scenario = 'webapp'
    $script:azNoneCalls = @()
    $workingDirectory =
        Join-Path ([IO.Path]::GetTempPath()) "treemon-webapp-test-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $workingDirectory | Out-Null

    try {
        $planResource = [pscustomobject]@{ id = $script:planResourceId }
        $managedIdentity =
            [pscustomobject]@{
                id = '/subscriptions/test/resourceGroups/viewer-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/viewer-identity'
            }

        Ensure-WebApp `
            -ExistingWebApp $null `
            -PlanResource $planResource `
            -ManagedIdentity $managedIdentity `
            -SubscriptionId 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' `
            -WorkingDirectory $workingDirectory |
            Out-Null
        Ensure-WebApp `
            -ExistingWebApp $script:webAppResult `
            -PlanResource $planResource `
            -ManagedIdentity $managedIdentity `
            -SubscriptionId 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' `
            -WorkingDirectory $workingDirectory |
            Out-Null

        $createCalls = @(
            $script:azNoneCalls
            | Where-Object { ($_.Arguments[0..2] -join ' ') -eq 'webapp create --name' }
        )
        $configurationCalls = @(
            $script:azNoneCalls
            | Where-Object { ($_.Arguments[0..2] -join ' ') -eq 'webapp config set' }
        )

        Assert-Equal `
            -Actual $createCalls.Count `
            -Expected 1 `
            -Because 'only the clean-state invocation creates the App Service'
        Assert-Equal `
            -Actual $configurationCalls.Count `
            -Expected 2 `
            -Because 'both clean and existing state reconcile runtime configuration'

        foreach ($call in $configurationCalls) {
            Assert-True `
                -Condition (-not ($call.Arguments | Where-Object { $_ -match '\|' })) `
                -Because 'az.cmd must never receive the pipe-bearing Linux runtime as an argument'

            $configurationIndex =
                [Array]::IndexOf($call.Arguments, '--generic-configurations')
            Assert-True `
                -Condition ($configurationIndex -ge 0) `
                -Because 'runtime configuration must use the Azure CLI JSON-file option'

            $configurationPath =
                ([string] $call.Arguments[$configurationIndex + 1]).TrimStart('@')
            $configuration =
                Get-Content -LiteralPath $configurationPath -Raw |
                ConvertFrom-Json
            Assert-Equal `
                -Actual ([string] $configuration.linuxFxVersion) `
                -Expected 'DOTNETCORE|10.0' `
                -Because 'the file must retain the exact Linux runtime value'
        }
    } finally {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-TestCase 'restricted-tenant registration creation converges on existing state' {
    $script:scenario = 'registration'
    $script:azNoneCalls = @()
    $script:createCallCount = 0

    $created = Ensure-AppRegistration -ExistingRegistration $null
    $existing = Ensure-AppRegistration -ExistingRegistration $created

    Assert-Equal `
        -Actual $script:createCallCount `
        -Expected 2 `
        -Because 'the clean path retries once with the owned tenant service reference'
    Assert-Equal `
        -Actual ([string] $existing.appId) `
        -Expected ([string] $script:registrationResult.appId) `
        -Because 'the existing-state path reuses the same registration'

    $updateCalls = @(
        $script:azNoneCalls
        | Where-Object { ($_.Arguments[0..2] -join ' ') -eq 'ad app update' }
    )
    Assert-Equal `
        -Actual $updateCalls.Count `
        -Expected 2 `
        -Because 'both clean and existing state reconcile the registration'

    foreach ($call in $updateCalls) {
        $referenceIndex =
            [Array]::IndexOf($call.Arguments, '--service-management-reference')
        Assert-True `
            -Condition ($referenceIndex -ge 0) `
            -Because 'restricted-tenant updates must preserve serviceManagementReference'
        Assert-Equal `
            -Actual ([string] $call.Arguments[$referenceIndex + 1]) `
            -Expected $script:serviceManagementReference `
            -Because 'registration mutations must use the one unambiguous owned reference'

        $accessTokenIndex =
            [Array]::IndexOf($call.Arguments, '--enable-access-token-issuance')
        Assert-True `
            -Condition ($accessTokenIndex -ge 0) `
            -Because 'registration updates must set browser access-token issuance explicitly'
        Assert-Equal `
            -Actual ([string] $call.Arguments[$accessTokenIndex + 1]) `
            -Expected 'false' `
            -Because 'Easy Auth does not need browser-issued access tokens'

        $idTokenIndex =
            [Array]::IndexOf($call.Arguments, '--enable-id-token-issuance')
        Assert-True `
            -Condition ($idTokenIndex -ge 0) `
            -Because 'registration updates must set ID-token issuance explicitly'
        Assert-Equal `
            -Actual ([string] $call.Arguments[$idTokenIndex + 1]) `
            -Expected 'true' `
            -Because 'Easy Auth requests code and id_token at its form-post callback'
    }
}

Invoke-TestCase 'deployed registration requires ID tokens without browser access tokens' {
    Assert-AppRegistrationAuthenticationFlow `
        -AppRegistration $script:registrationResult

    $invalidRegistration =
        [pscustomobject]@{
            appId = $script:registrationResult.appId
            displayName = $Registration
            passwordCredentials = @()
            signInAudience = 'AzureADMyOrg'
            web = [pscustomobject]@{
                redirectUris = @($callbackUrl)
                implicitGrantSettings = [pscustomobject]@{
                    enableAccessTokenIssuance = $false
                    enableIdTokenIssuance = $false
                }
            }
        }
    $rejectedMissingIdToken = $false

    try {
        Assert-AppRegistrationAuthenticationFlow `
            -AppRegistration $invalidRegistration
    } catch {
        $rejectedMissingIdToken =
            $_.Exception.Message -match 'code/id_token callback'
    }

    Assert-True `
        -Condition $rejectedMissingIdToken `
        -Because 'deployed-state verification must reject a registration that breaks the Easy Auth callback'

    $invalidRegistration.web.implicitGrantSettings.enableIdTokenIssuance = $true
    $invalidRegistration.web.implicitGrantSettings.enableAccessTokenIssuance = $true
    $rejectedBrowserAccessToken = $false

    try {
        Assert-AppRegistrationAuthenticationFlow `
            -AppRegistration $invalidRegistration
    } catch {
        $rejectedBrowserAccessToken =
            $_.Exception.Message -match 'code/id_token callback'
    }

    Assert-True `
        -Condition $rejectedBrowserAccessToken `
        -Because 'deployed-state verification must keep browser access-token issuance disabled'
}

Write-Host 'Canvas share deployment regression tests passed.'
