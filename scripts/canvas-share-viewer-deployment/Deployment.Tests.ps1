#requires -Version 7.4

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$viewerProject = Join-Path $repoRoot 'src' 'CanvasShareViewer' 'CanvasShareViewer.fsproj'
$lifecyclePolicyPath = Join-Path $repoRoot 'scripts' 'canvas-share-lifecycle-policy.json'
$minimumLifecycleDays = 31
$viewerBaseUrl = 'https://treemon.azurewebsites.net'

. (Join-Path $PSScriptRoot 'Common.ps1')

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

$script:scenario = ''
$script:azNoneCalls = @()
$script:azJsonCalls = @()
$script:subscriptionAccounts = @{}
$script:selectedAccount = $null
$script:publisherResult = $null
$script:createCallCount = 0
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

    if ($script:scenario -eq 'azure-context') {
        $script:azJsonCalls +=
            [pscustomobject]@{ Arguments = @($Arguments) }
        $command = $Arguments[0..([Math]::Min(2, $Arguments.Count - 1))] -join ' '

        switch ($command) {
            'cloud show' {
                return [pscustomobject]@{ name = 'AzureCloud' }
            }
            'account show --subscription' {
                $subscriptionIndex = [Array]::IndexOf($Arguments, '--subscription')
                $subscription = [string] $Arguments[$subscriptionIndex + 1]

                if (-not $script:subscriptionAccounts.ContainsKey($subscription)) {
                    throw 'Synthetic subscription is unavailable.'
                }

                return $script:subscriptionAccounts[$subscription]
            }
            'account show' {
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

function Reset-AzureContextMocks {
    $script:scenario = 'azure-context'
    $script:azJsonCalls = @()
    $script:subscriptionAccounts = @{}
    $script:selectedAccount = $null
    $script:publisherResult =
        [pscustomobject]@{
            id = 'dddddddd-dddd-dddd-dddd-dddddddddddd'
        }
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
            'Azure.ps1', 'ViewerBlobAccess.ps1'
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
