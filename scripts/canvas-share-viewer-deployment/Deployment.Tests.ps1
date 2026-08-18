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
        passwordCredentials = @()
        serviceManagementReference = $script:serviceManagementReference
        web = [pscustomobject]@{
            redirectUris = @($callbackUrl)
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
    }
}

Write-Host 'Canvas share deployment regression tests passed.'
