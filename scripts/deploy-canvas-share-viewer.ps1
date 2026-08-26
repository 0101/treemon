#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Subscription,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$')]
    [string] $Tenant,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroup,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Plan,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Identity,

    [switch] $ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$appName = 'treemon'
$viewerBaseUrl = 'https://treemon.azurewebsites.net'
$callbackUrl = "$viewerBaseUrl/.auth/login/aad/callback"
$federatedCredentialName = 'treemon-easy-auth'
$managedIdentityAssertionSetting = 'OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID'
$readerRole = 'Storage Blob Data Reader'
$contributorRole = 'Storage Blob Data Contributor'
$minimumLifecycleDays = 31
$repoRoot = Split-Path -Parent $PSScriptRoot
$viewerProject = Join-Path $repoRoot 'src' 'CanvasShareViewer' 'CanvasShareViewer.fsproj'
$lifecyclePolicyPath = Join-Path $PSScriptRoot 'canvas-share-lifecycle-policy.json'
$deploymentSupportDirectory = Join-Path $PSScriptRoot 'canvas-share-viewer-deployment'

. (Join-Path $deploymentSupportDirectory 'Common.ps1')
. (Join-Path $deploymentSupportDirectory 'SubscriptionGuard.ps1')
. (Join-Path $deploymentSupportDirectory 'ViewerBlobAccess.ps1')
. (Join-Path $deploymentSupportDirectory 'Azure.ps1')

Write-Step 'Validating local tools and repository inputs'
Assert-Prerequisites
$treemonConfig = Read-TreemonCanvasShareConfig
$desiredLifecycleRule = Get-LifecycleRule -Container $treemonConfig.Container

Write-Step 'Validating Azure subscription, tenant, and delegated publisher'
$azureContext = Get-AzureContext `
    -ApprovedSubscription $treemonConfig.ApprovedSubscription `
    -RequestedSubscription $Subscription `
    -RequestedTenant $Tenant
$storageAccount = Get-StorageAccount `
    -AccountName $treemonConfig.AccountName `
    -SubscriptionId $azureContext.SubscriptionId
$containerScope = Get-ShareContainerScope `
    -StorageAccount $storageAccount `
    -Container $treemonConfig.Container

$existingResources = Get-ExistingResources -SubscriptionId $azureContext.SubscriptionId
Assert-ExistingResourceSafety `
    -Existing $existingResources `
    -SubscriptionId $azureContext.SubscriptionId

if ($null -ne $existingResources.Identity) {
    Write-Step 'Verifying the existing viewer identity has container-only Blob access'
    Assert-ViewerBlobAccessIsContainerOnly `
        -PrincipalObjectId ([string] $existingResources.Identity.principalId) `
        -ContainerScope $containerScope `
        -SubscriptionId $azureContext.SubscriptionId
}

$workingDirectory = Join-Path ([IO.Path]::GetTempPath()) "treemon-viewer-$PID-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $workingDirectory | Out-Null

try {
    $packagePath = New-ViewerPackage -WorkingDirectory $workingDirectory

    if ($ValidateOnly) {
        Write-Host ''
        Write-Host 'Validation succeeded. No Azure resource or machine configuration was changed.'
        Write-Host "An apply run will ensure the fixed viewer at $viewerBaseUrl, secret-free Easy Auth, container-scoped roles, and lifecycle deletion after at least $minimumLifecycleDays days."
        return
    }

    $group = Ensure-ResourceGroup `
        -ExistingGroup $existingResources.Group `
        -StorageAccount $storageAccount `
        -SubscriptionId $azureContext.SubscriptionId
    $containerScope = Ensure-PrivateContainer `
        -StorageAccount $storageAccount `
        -Container $treemonConfig.Container `
        -SubscriptionId $azureContext.SubscriptionId
    $planResource = Ensure-AppServicePlan `
        -ExistingPlan $existingResources.Plan `
        -Location ([string] $group.location) `
        -SubscriptionId $azureContext.SubscriptionId
    $managedIdentity = Ensure-ManagedIdentity `
        -ExistingIdentity $existingResources.Identity `
        -Location ([string] $group.location) `
        -SubscriptionId $azureContext.SubscriptionId
    $webApp = Ensure-WebApp `
        -ExistingWebApp $existingResources.WebApp `
        -PlanResource $planResource `
        -ManagedIdentity $managedIdentity `
        -SubscriptionId $azureContext.SubscriptionId `
        -WorkingDirectory $workingDirectory
    $appRegistration = Ensure-AppRegistration `
        -ExistingRegistration $existingResources.Registration

    Ensure-FederatedCredential `
        -AppRegistration $appRegistration `
        -ManagedIdentity $managedIdentity `
        -TenantId $azureContext.TenantId `
        -WorkingDirectory $workingDirectory
    Ensure-StorageAccess `
        -ManagedIdentity $managedIdentity `
        -PublisherObjectId $azureContext.PublisherObjectId `
        -ContainerScope $containerScope `
        -SubscriptionId $azureContext.SubscriptionId
    Ensure-LifecyclePolicy `
        -StorageAccount $storageAccount `
        -DesiredRule $desiredLifecycleRule `
        -SubscriptionId $azureContext.SubscriptionId `
        -WorkingDirectory $workingDirectory
    Ensure-AppSettings `
        -StorageAccountName $treemonConfig.AccountName `
        -Container $treemonConfig.Container `
        -ManagedIdentity $managedIdentity `
        -TenantId $azureContext.TenantId `
        -SubscriptionId $azureContext.SubscriptionId
    Ensure-EasyAuth `
        -AppRegistration $appRegistration `
        -TenantId $azureContext.TenantId `
        -SubscriptionId $azureContext.SubscriptionId `
        -WorkingDirectory $workingDirectory
    Disable-BasicPublishingCredentials `
        -SubscriptionId $azureContext.SubscriptionId
    Deploy-Viewer `
        -PackagePath $packagePath `
        -SubscriptionId $azureContext.SubscriptionId
    Assert-DeployedState `
        -StorageAccount $storageAccount `
        -Container $treemonConfig.Container `
        -ContainerScope $containerScope `
        -ManagedIdentity $managedIdentity `
        -PublisherObjectId $azureContext.PublisherObjectId `
        -AppRegistration $appRegistration `
        -TenantId $azureContext.TenantId `
        -SubscriptionId $azureContext.SubscriptionId

    Write-Step 'Setting the canonical viewer URL in Treemon machine configuration'
    Set-TreemonViewerBaseUrl `
        -ExpectedAccountName $treemonConfig.AccountName `
        -ExpectedContainer $treemonConfig.Container

    $updatedConfig = Read-TreemonCanvasShareConfig
    if ([string] $updatedConfig.Root['canvasShare']['viewerBaseUrl'] -cne $viewerBaseUrl) {
        throw 'Treemon machine configuration does not contain the canonical viewerBaseUrl.'
    }

    Write-Host ''
    Write-Host "Deployment complete: $viewerBaseUrl"
    Write-Host 'The canonical App Service, identity, Entra configuration, RBAC grants, and lifecycle policy remain deployed.'
} finally {
    Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
