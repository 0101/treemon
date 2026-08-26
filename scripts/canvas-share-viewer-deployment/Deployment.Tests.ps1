#requires -Version 7.4

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'TestHarness.ps1')

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$viewerProject = Join-Path $repoRoot 'src' 'CanvasShareViewer' 'CanvasShareViewer.fsproj'
$lifecyclePolicyPath = Join-Path $repoRoot 'scripts' 'canvas-share-lifecycle-policy.json'
$minimumLifecycleDays = 31
$viewerBaseUrl = 'https://treemon.azurewebsites.net'

. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'SubscriptionGuard.ps1')

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

. (Join-Path $PSScriptRoot 'Azure.ps1')

$script:scenario = ''
$script:azNoneCalls = @()
$script:azJsonCalls = @()
$script:restMethodCalls = @()
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
$script:webAppResult =
    [pscustomobject]@{
        appServicePlanId = $script:planResourceId
        identity = $null
    }
$script:registrationResult =
    [pscustomobject]@{
        id = '88888888-7777-6666-5555-444444444444'
        appId = '99999999-8888-7777-6666-555555555555'
        displayName = $registrationDisplayName
        description = $registrationDescription
        signInAudience = 'AzureADMyOrg'
        passwordCredentials = @()
        requiredResourceAccess = @()
        serviceManagementReference = $script:serviceManagementReference
        info = [pscustomobject]@{
            logoUrl = 'https://aadcdn.msftauthimages.net/logo.png'
        }
        web = [pscustomobject]@{
            homePageUrl = $viewerBaseUrl
            redirectUris = @($callbackUrl)
            implicitGrantSettings = [pscustomobject]@{
                enableAccessTokenIssuance = $false
                enableIdTokenIssuance = $true
            }
        }
    }
$script:legacyRegistrationResult =
    [pscustomobject]@{
        id = $script:registrationResult.id
        appId = $script:registrationResult.appId
        displayName = $legacyRegistrationDisplayName
        description = $null
        signInAudience = 'AzureADMyOrg'
        passwordCredentials = @()
        requiredResourceAccess = @()
        serviceManagementReference = $script:serviceManagementReference
        info = [pscustomobject]@{ logoUrl = $null }
        web = [pscustomobject]@{
            homePageUrl = $null
            redirectUris = @($callbackUrl)
            implicitGrantSettings = [pscustomobject]@{
                enableAccessTokenIssuance = $false
                enableIdTokenIssuance = $true
            }
        }
    }
$script:servicePrincipalResult =
    [pscustomobject]@{
        appId = $script:registrationResult.appId
        displayName = $registrationDisplayName
        description = $registrationDescription
        homepage = $viewerBaseUrl
    }

function Write-Step {
    param([Parameter(Mandatory)][string] $Message)
}

Invoke-TestCase 'registration logo is a bounded 215-pixel PNG' {
    Assert-RegistrationLogo -Path $registrationLogoPath

    $temporaryDirectory =
        Join-Path ([IO.Path]::GetTempPath()) "treemon-logo-test-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null

    try {
        $missingRejected = $false

        try {
            Assert-RegistrationLogo `
                -Path (Join-Path $temporaryDirectory 'missing.png')
        } catch {
            $missingRejected =
                $_.Exception.Message -match 'was not found'
        }

        Assert-True `
            -Condition $missingRejected `
            -Because 'a missing consent-screen logo must fail before Azure mutation'

        $wrongSizePath = Join-Path $temporaryDirectory 'wrong-size.png'
        $wrongSizeBytes = [byte[]]::new(24)
        $wrongSizeBytes[0] = 0x89
        $wrongSizeBytes[1] = 0x50
        $wrongSizeBytes[2] = 0x4E
        $wrongSizeBytes[3] = 0x47
        $wrongSizeBytes[4] = 0x0D
        $wrongSizeBytes[5] = 0x0A
        $wrongSizeBytes[6] = 0x1A
        $wrongSizeBytes[7] = 0x0A
        $wrongSizeBytes[19] = 0xC0
        $wrongSizeBytes[23] = 0xC0
        [IO.File]::WriteAllBytes($wrongSizePath, $wrongSizeBytes)
        $wrongSizeRejected = $false

        try {
            Assert-RegistrationLogo -Path $wrongSizePath
        } catch {
            $wrongSizeRejected =
                $_.Exception.Message -match '215x215 PNG'
        }

        Assert-True `
            -Condition $wrongSizeRejected `
            -Because 'a PWA icon must be derived to the exact Entra dimensions'

        $oversizedPath = Join-Path $temporaryDirectory 'oversized.png'
        [IO.File]::WriteAllBytes(
            $oversizedPath,
            [byte[]]::new(100KB + 1))
        $oversizedRejected = $false

        try {
            Assert-RegistrationLogo -Path $oversizedPath
        } catch {
            $oversizedRejected =
                $_.Exception.Message -match 'exceeds 100 KB'
        }

        Assert-True `
            -Condition $oversizedRejected `
            -Because 'an oversized logo must fail before Azure mutation'
    } finally {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
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

    $command = $Arguments[0..2] -join ' '

    switch ($script:scenario) {
        'webapp' {
            if ($command -eq 'webapp show --name') {
                return $script:webAppResult
            }
        }
        'registration' {
            switch ($command) {
                'account get-access-token --resource-type' {
                    return [pscustomobject]@{
                        accessToken = 'fixture-graph-token'
                    }
                }
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
        'registration-lookup' {
            switch ($command) {
                'ad app list' {
                    $displayNameIndex = [Array]::IndexOf($Arguments, '--display-name')
                    $displayName = [string] $Arguments[$displayNameIndex + 1]

                    if ($displayName -ceq $legacyRegistrationDisplayName) {
                        return @($script:legacyRegistrationResult)
                    }

                    return @()
                }
                'ad app show' {
                    return $script:legacyRegistrationResult
                }
            }
        }
    }

    throw "Unexpected mocked Azure CLI command: $($Arguments -join ' ')"
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

function Invoke-RestMethod {
    param(
        [Parameter(Mandatory)][string] $Uri,
        [Parameter(Mandatory)][string] $Method,
        [Parameter(Mandatory)][string] $Authentication,
        [Parameter(Mandatory)][securestring] $Token,
        [Parameter(Mandatory)][string] $ContentType,
        [Parameter(Mandatory)][string] $InFile
    )

    $script:restMethodCalls +=
        [pscustomobject]@{
            Uri = $Uri
            Method = $Method
            Authentication = $Authentication
            Token = $Token
            ContentType = $ContentType
            InFile = $InFile
        }
}

function Reset-ResourceMocks {
    param([Parameter(Mandatory)][string] $Scenario)

    $script:scenario = $Scenario
    $script:azNoneCalls = @()
    $script:azJsonCalls = @()
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

Invoke-TestCase 'ordinary creation and ValidateOnly retain the global-name failure' {
    Reset-ResourceMocks -Scenario 'global-name-unavailable'
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
                        -SubscriptionId $approvedSubscriptionId
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

Invoke-TestCase 'legacy registration lookup preserves the durable app during branding migration' {
    $script:scenario = 'registration-lookup'
    $script:azJsonCalls = @()

    $registration = Get-ExactAppRegistration

    Assert-Equal `
        -Actual ([string] $registration.appId) `
        -Expected ([string] $script:legacyRegistrationResult.appId) `
        -Because 'the existing technical-name registration must be reused rather than duplicated'

    $lookupNames =
        @(
            $script:azJsonCalls
            | Where-Object { ($_.Arguments[0..2] -join ' ') -eq 'ad app list' }
            | ForEach-Object {
                $displayNameIndex = [Array]::IndexOf($_.Arguments, '--display-name')
                [string] $_.Arguments[$displayNameIndex + 1]
            }
        )
    Assert-True `
        -Condition ($lookupNames.Count -eq 2 -and
            $lookupNames -contains $registrationDisplayName -and
            $lookupNames -contains $legacyRegistrationDisplayName) `
        -Because 'lookup must cover the canonical and one bounded legacy display name'
}

Invoke-TestCase 'restricted-tenant registration creation converges on existing state' {
    $script:scenario = 'registration'
    $script:azNoneCalls = @()
    $script:azJsonCalls = @()
    $script:restMethodCalls = @()
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

    $createCalls = @(
        $script:azJsonCalls
        | Where-Object { ($_.Arguments[0..2] -join ' ') -eq 'ad app create' }
    )
    Assert-Equal `
        -Actual $createCalls.Count `
        -Expected 2 `
        -Because 'restricted-tenant creation retries exactly once'

    foreach ($call in $createCalls) {
        $displayNameIndex = [Array]::IndexOf($call.Arguments, '--display-name')
        Assert-Equal `
            -Actual ([string] $call.Arguments[$displayNameIndex + 1]) `
            -Expected $registrationDisplayName `
            -Because 'new registrations must start with the user-facing name'

        $homepageIndex = [Array]::IndexOf($call.Arguments, '--web-home-page-url')
        Assert-Equal `
            -Actual ([string] $call.Arguments[$homepageIndex + 1]) `
            -Expected $viewerBaseUrl `
            -Because 'new registrations must start with the canonical homepage'
    }

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

        $displayNameIndex =
            [Array]::IndexOf($call.Arguments, '--display-name')
        Assert-Equal `
            -Actual ([string] $call.Arguments[$displayNameIndex + 1]) `
            -Expected $registrationDisplayName `
            -Because 'registration updates must reconcile the user-facing app name'

        $homepageIndex =
            [Array]::IndexOf($call.Arguments, '--web-home-page-url')
        Assert-Equal `
            -Actual ([string] $call.Arguments[$homepageIndex + 1]) `
            -Expected $viewerBaseUrl `
            -Because 'registration updates must reconcile the canonical homepage'

        $descriptionIndex =
            [Array]::IndexOf($call.Arguments, '--set')
        Assert-Equal `
            -Actual ([string] $call.Arguments[$descriptionIndex + 1]) `
            -Expected "description=$registrationDescription" `
            -Because 'registration updates must reconcile the plain-language description'
    }

    Assert-Equal `
        -Actual $script:restMethodCalls.Count `
        -Expected 2 `
        -Because 'both clean and existing state must reconcile the PWA logo'

    foreach ($call in $script:restMethodCalls) {
        Assert-Equal `
            -Actual ([string] $call.Uri) `
            -Expected "https://graph.microsoft.com/v1.0/applications/$($script:registrationResult.id)/logo" `
            -Because 'the logo must target the existing application object'

        Assert-Equal `
            -Actual ([string] $call.InFile) `
            -Expected $registrationLogoPath `
            -Because 'the logo upload must use the tracked Treemon PWA icon'
        Assert-True `
            -Condition ($call.Method -ceq 'Put' -and
                $call.ContentType -ceq 'image/png' -and
                $call.Authentication -ceq 'Bearer' -and
                (ConvertFrom-SecureString $call.Token -AsPlainText) -ceq 'fixture-graph-token') `
            -Because 'the logo must be uploaded as raw PNG bytes with an in-memory Graph token'
    }

    $servicePrincipalUpdates = @(
        $script:azNoneCalls
        | Where-Object { ($_.Arguments[0..2] -join ' ') -eq 'ad sp update' }
    )
    Assert-Equal `
        -Actual $servicePrincipalUpdates.Count `
        -Expected 2 `
        -Because 'both clean and existing state must reconcile Enterprise Application details'

    foreach ($call in $servicePrincipalUpdates) {
        Assert-True `
            -Condition ($call.Arguments -contains "displayName=$registrationDisplayName" -and
                $call.Arguments -contains "description=$registrationDescription" -and
                $call.Arguments -contains "homepage=$viewerBaseUrl") `
            -Because 'the Enterprise Application must match the user-facing app registration'
    }
}

Invoke-TestCase 'Easy Auth requests only openid and rejects broader deployed scopes' {
    $script:scenario = 'registration'
    $script:azNoneCalls = @()
    $workingDirectory =
        Join-Path ([IO.Path]::GetTempPath()) "treemon-easy-auth-test-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $workingDirectory | Out-Null

    try {
        Ensure-EasyAuth `
            -AppRegistration $script:registrationResult `
            -TenantId 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' `
            -SubscriptionId '11111111-2222-3333-4444-555555555555' `
            -WorkingDirectory $workingDirectory

        $authCall =
            @(
                $script:azNoneCalls
                | Where-Object {
                    ($_.Arguments[0..2] -join ' ') -eq 'rest --method put' -and
                        ($_.Arguments -join ' ') -match 'authsettingsV2'
                }
            )
            | Select-Object -First 1
        $bodyIndex = [Array]::IndexOf($authCall.Arguments, '--body')
        $authSettingsPath =
            ([string] $authCall.Arguments[$bodyIndex + 1]).TrimStart('@')
        $authSettings =
            Get-Content -LiteralPath $authSettingsPath -Raw |
            ConvertFrom-Json
        $loginParameters =
            @($authSettings.properties.identityProviders.azureActiveDirectory.login.loginParameters)

        Assert-Equal `
            -Actual ($loginParameters -join '|') `
            -Expected $easyAuthLoginParameter `
            -Because 'Easy Auth must override its profile and email defaults with openid only'
        Assert-Equal `
            -Actual ([bool] $authSettings.properties.login.tokenStore.enabled) `
            -Expected $false `
            -Because 'openid-only authentication must not enable provider token storage'

        Assert-EasyAuthConfiguration `
            -AppRegistration $script:registrationResult `
            -TenantId 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' `
            -AuthSettings $authSettings

        $authSettings.properties.identityProviders.azureActiveDirectory.login.loginParameters =
            @('scope=openid profile email')
        $rejectedDefaultScopes = $false

        try {
            Assert-EasyAuthConfiguration `
                -AppRegistration $script:registrationResult `
                -TenantId 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' `
                -AuthSettings $authSettings
        } catch {
            $rejectedDefaultScopes =
                $_.Exception.Message -match 'openid-only'
        }

        Assert-True `
            -Condition $rejectedDefaultScopes `
            -Because 'deployed-state verification must reject App Service default profile and email scopes'
    } finally {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-TestCase 'deployed registration requires ID tokens without browser access tokens' {
    Assert-AppRegistrationAuthenticationFlow `
        -AppRegistration $script:registrationResult
    Assert-AppRegistrationBranding `
        -AppRegistration $script:registrationResult
    Assert-ServicePrincipalBranding `
        -ServicePrincipal $script:servicePrincipalResult

    $invalidRegistration =
        [pscustomobject]@{
            id = $script:registrationResult.id
            appId = $script:registrationResult.appId
            displayName = $registrationDisplayName
            description = $registrationDescription
            passwordCredentials = @()
            requiredResourceAccess = @()
            signInAudience = 'AzureADMyOrg'
            info = [pscustomobject]@{
                logoUrl = 'https://aadcdn.msftauthimages.net/logo.png'
            }
            web = [pscustomobject]@{
                homePageUrl = $viewerBaseUrl
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

    $invalidRegistration.web.implicitGrantSettings.enableAccessTokenIssuance = $false
    $invalidRegistration.requiredResourceAccess =
        @([pscustomobject]@{ resourceAppId = '00000003-0000-0000-c000-000000000000' })
    $rejectedApiPermissions = $false

    try {
        Assert-AppRegistrationAuthenticationFlow `
            -AppRegistration $invalidRegistration
    } catch {
        $rejectedApiPermissions =
            $_.Exception.Message -match 'declares API permissions'
    }

    Assert-True `
        -Condition $rejectedApiPermissions `
        -Because 'the viewer registration must remain free of Microsoft Graph and other API permissions'

    $invalidRegistration.requiredResourceAccess = @()
    $invalidRegistration.description = 'technical placeholder'
    $rejectedBranding = $false

    try {
        Assert-AppRegistrationBranding `
            -AppRegistration $invalidRegistration
    } catch {
        $rejectedBranding =
            $_.Exception.Message -match 'canonical name, description, homepage, or logo'
    }

    Assert-True `
        -Condition $rejectedBranding `
        -Because 'deployed-state verification must reject incomplete user-facing app details'

    $invalidServicePrincipal =
        [pscustomobject]@{
            displayName = $legacyRegistrationDisplayName
            description = $registrationDescription
            homepage = $viewerBaseUrl
        }
    $rejectedEnterpriseAppBranding = $false

    try {
        Assert-ServicePrincipalBranding `
            -ServicePrincipal $invalidServicePrincipal
    } catch {
        $rejectedEnterpriseAppBranding =
            $_.Exception.Message -match 'Enterprise application'
    }

    Assert-True `
        -Condition $rejectedEnterpriseAppBranding `
        -Because 'deployed-state verification must reject a stale Enterprise Application name'
}

Write-Host 'Canvas share deployment regression tests passed.'
