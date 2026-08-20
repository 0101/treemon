function Assert-CrossSubscriptionCorrectionParameters {
    param(
        [switch] $ReconcileCrossSubscriptionMove,
        [switch] $ConfirmPortalMoveCompleted
    )

    if ($ReconcileCrossSubscriptionMove -and -not $ConfirmPortalMoveCompleted) {
        throw 'Post-move reconciliation requires -ConfirmPortalMoveCompleted after the operator has verified that the portal move succeeded.'
    }
}

function Assert-CorrectionResourceGroupSafety {
    param([AllowNull()][pscustomobject] $ExistingGroup)

    if ($null -eq $ExistingGroup) {
        return
    }

    $environmentTag =
        if ($null -ne $ExistingGroup.tags -and
            $null -ne $ExistingGroup.tags.PSObject.Properties['environment']) {
            [string] $ExistingGroup.tags.PSObject.Properties['environment'].Value
        } else {
            ''
        }

    if ($environmentTag -match '^(?i:prod|production)$') {
        throw "Resource group '$ResourceGroup' is tagged as production. This automation is non-production only."
    }
}

function Assert-ConfiguredPrivateShareContainer {
    param(
        [Parameter(Mandatory)][pscustomobject] $StorageAccount,
        [Parameter(Mandatory)][string] $Container,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    if ([bool] (Get-AzureResourcePropertyValue `
            -Resource $StorageAccount `
            -Name 'allowBlobPublicAccess')) {
        throw 'The configured storage account permits public Blob access. Destination preparation will not change storage configuration.'
    }

    $existingContainer = Try-AzJson -Arguments @(
        'storage', 'container-rm', 'show',
        '--storage-account', [string] $StorageAccount.id,
        '--name', $Container,
        '--subscription', $SubscriptionId)

    if ($null -eq $existingContainer) {
        throw "Configured Blob container '$Container' was not found in the approved subscription. Destination preparation will not create it."
    }

    $publicAccess =
        [string] (Get-AzureResourcePropertyValue `
            -Resource $existingContainer `
            -Name 'publicAccess')

    if (-not [string]::IsNullOrWhiteSpace($publicAccess) -and
        $publicAccess -notmatch '^(?i:none|off)$') {
        throw "Configured Blob container '$Container' permits public access. Destination preparation will not change it."
    }
}

function Get-CrossSubscriptionPreparationResources {
    param([Parameter(Mandatory)][string] $SubscriptionId)

    $group = Try-AzJson -Arguments @(
        'group', 'show',
        '--name', $ResourceGroup,
        '--subscription', $SubscriptionId)
    $identityResource =
        if ($null -eq $group) {
            $null
        } else {
            Try-AzJson -Arguments @(
                'identity', 'show',
                '--name', $Identity,
                '--resource-group', $ResourceGroup,
                '--subscription', $SubscriptionId)
        }

    [pscustomobject]@{
        Group = $group
        Identity = $identityResource
    }
}

function Get-CrossSubscriptionMoveDestinationResources {
    param([Parameter(Mandatory)][string] $SubscriptionId)

    $preparationResources =
        Get-CrossSubscriptionPreparationResources -SubscriptionId $SubscriptionId
    $planResource =
        if ($null -eq $preparationResources.Group) {
            $null
        } else {
            Try-AzJson -Arguments @(
                'appservice', 'plan', 'show',
                '--name', $Plan,
                '--resource-group', $ResourceGroup,
                '--subscription', $SubscriptionId)
        }
    $webApp =
        if ($null -eq $preparationResources.Group) {
            $null
        } else {
            Try-AzJson -Arguments @(
                'rest',
                '--method', 'get',
                '--uri', "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/${appName}?api-version=2023-12-01",
                '--query', '{id:id,name:name,defaultHostName:properties.defaultHostName,kind:kind,appServicePlanId:properties.serverFarmId,httpsOnly:properties.httpsOnly}',
                '--subscription', $SubscriptionId)
        }

    [pscustomobject]@{
        Group = $preparationResources.Group
        Plan = $planResource
        Identity = $preparationResources.Identity
        WebApp = $webApp
        Registration = Get-ExactAppRegistration
    }
}

function Assert-CrossSubscriptionMovedResources {
    param(
        [Parameter(Mandatory)][pscustomobject] $Existing,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    if ($null -eq $Existing.Group) {
        throw 'The prepared destination resource group was not found in the approved subscription.'
    }

    Assert-CorrectionResourceGroupSafety -ExistingGroup $Existing.Group

    if ($null -eq $Existing.Plan) {
        throw "The moved App Service plan '$Plan' was not found in the prepared destination."
    }

    if (-not [bool] (Get-AzureResourcePropertyValue `
            -Resource $Existing.Plan `
            -Name 'reserved')) {
        throw "Moved App Service plan '$Plan' is not a Linux plan."
    }

    if ($null -eq $Existing.Identity) {
        throw "Replacement user-assigned identity '$Identity' was not found in the prepared destination."
    }

    if ($null -eq $Existing.WebApp) {
        throw "The canonical App Service '$appName' was not found in the prepared destination. Confirm the portal move before reconciliation."
    }

    if ([string] $Existing.WebApp.defaultHostName -cne 'treemon.azurewebsites.net' -or
        [string] $Existing.WebApp.kind -notmatch '(^|,)linux($|,)' -or
        -not [string]::Equals(
            (Get-WebAppPlanResourceId -WebApp $Existing.WebApp),
            [string] $Existing.Plan.id,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The moved App Service does not match the canonical hostname, Linux kind, and requested destination plan.'
    }

    if ($null -eq $Existing.Registration) {
        throw "The retained Entra app registration '$Registration' was not found."
    }

    Assert-RegistrationIsDedicated -AppRegistration $Existing.Registration
    Assert-NoClientSecretConfiguration -SubscriptionId $SubscriptionId
}

function Set-CrossSubscriptionReplacementIdentity {
    param(
        [Parameter(Mandatory)][pscustomobject] $ManagedIdentity,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    $preparedClientId = [string] $ManagedIdentity.clientId
    if ([string]::IsNullOrWhiteSpace($preparedClientId)) {
        throw 'The prepared destination identity has no client ID. No App Service change was made.'
    }

    $preparedClientIdLiteral =
        ConvertTo-Json -InputObject $preparedClientId -Compress
    $identitySummaryQuery =
        '{{identityType:identity.type,userAssignedIdentityCount:length(values(identity.userAssignedIdentities || `{{}}`)),preparedIdentityAttached:contains(values(identity.userAssignedIdentities || `{{}}`)[].clientId, `{0}`)}}' `
            -f $preparedClientIdLiteral
    $identitySummary = Invoke-AzJson -Arguments @(
        'rest',
        '--method', 'get',
        '--uri', "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/${appName}?api-version=2023-12-01",
        '--query', $identitySummaryQuery,
        '--subscription', $SubscriptionId)

    if ($null -eq $identitySummary -or
        $null -eq $identitySummary.PSObject.Properties['identityType'] -or
        $null -eq $identitySummary.PSObject.Properties['userAssignedIdentityCount'] -or
        $null -eq $identitySummary.PSObject.Properties['preparedIdentityAttached']) {
        throw 'The moved App Service identity attachment could not be verified. No App Service change was made.'
    }

    $expectedIdentityCount =
        if ([bool] $identitySummary.preparedIdentityAttached) {
            1
        } else {
            0
        }

    if ([string] $identitySummary.identityType -match 'SystemAssigned' -or
        [int] $identitySummary.userAssignedIdentityCount -ne $expectedIdentityCount) {
        throw 'The moved App Service still has an identity attachment other than the prepared destination identity. In the Azure portal, detach every other user-assigned identity and turn off its system-assigned identity, then rerun reconciliation. No App Service change was made.'
    }

    Write-Step 'Attaching the prepared identity to the moved App Service'
    Invoke-AzNone -Arguments @(
        'webapp', 'identity', 'assign',
        '--name', $appName,
        '--resource-group', $ResourceGroup,
        '--identities', [string] $ManagedIdentity.id,
        '--subscription', $SubscriptionId)
}

function Write-CrossSubscriptionMoveChecklist {
    Write-Host ''
    Write-Host 'Destination preparation complete. No source-subscription operation was issued.'
    Write-Host "1. In the Azure portal, open the canonical App Service '$appName', detach its user-assigned identity attachment, and confirm that System assigned is Off."
    Write-Host '2. Leave the detached identity resource, role assignments, storage, and resource groups unchanged for rollback.'
    Write-Host "3. Move the canonical App Service '$appName' and App Service plan '$Plan' together."
    Write-Host '4. Select the approved destination subscription and the prepared destination resource group used for this run.'
    Write-Host '5. Wait for the portal move to succeed, seed an isolated TREEMON_CONFIG_DIR, then rerun the same command with -ReconcileCrossSubscriptionMove -ConfirmPortalMoveCompleted.'
    Write-Host 'The viewer is expected to be unavailable after identity detachment until reconciliation finishes. If the move fails, reattach the preserved identity in the portal before retrying.'
    Write-Host 'The automation does not accept or print a source subscription, tenant, resource group, or resource ID.'
}

function Invoke-CrossSubscriptionMovePreparation {
    param(
        [Parameter(Mandatory)][pscustomobject] $StorageAccount,
        [Parameter(Mandatory)][string] $Container,
        [Parameter(Mandatory)][string] $ContainerScope,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    Write-Step 'Confirming the configured private destination storage'
    Assert-ConfiguredPrivateShareContainer `
        -StorageAccount $StorageAccount `
        -Container $Container `
        -SubscriptionId $SubscriptionId

    $existing =
        Get-CrossSubscriptionPreparationResources -SubscriptionId $SubscriptionId
    Assert-CorrectionResourceGroupSafety -ExistingGroup $existing.Group

    if ($null -ne $existing.Identity) {
        Assert-ViewerBlobAccessIsContainerOnly `
            -PrincipalObjectId ([string] $existing.Identity.principalId) `
            -ContainerScope $ContainerScope `
            -SubscriptionId $SubscriptionId
    }

    $group = Ensure-ResourceGroup `
        -ExistingGroup $existing.Group `
        -StorageAccount $StorageAccount `
        -SubscriptionId $SubscriptionId
    $managedIdentity = Ensure-ManagedIdentity `
        -ExistingIdentity $existing.Identity `
        -Location ([string] $group.location) `
        -SubscriptionId $SubscriptionId
    Ensure-RoleAssignment `
        -PrincipalObjectId ([string] $managedIdentity.principalId) `
        -PrincipalType ServicePrincipal `
        -Role $readerRole `
        -Scope $ContainerScope `
        -SubscriptionId $SubscriptionId
    Assert-ViewerBlobAccessIsContainerOnly `
        -PrincipalObjectId ([string] $managedIdentity.principalId) `
        -ContainerScope $ContainerScope `
        -SubscriptionId $SubscriptionId

    Write-CrossSubscriptionMoveChecklist
}
