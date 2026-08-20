function Get-ExactAppRegistration {
    $registrations = @(
        Invoke-AzJson -Arguments @(
            'ad', 'app', 'list',
            '--display-name', $Registration)
        | Where-Object displayName -CEQ $Registration
    )

    if ($registrations.Count -gt 1) {
        throw "More than one Entra app registration is named '$Registration'. Use a unique dedicated registration name."
    }

    if ($registrations.Count -eq 1) {
        return Invoke-AzJson -Arguments @('ad', 'app', 'show', '--id', $registrations[0].appId)
    }

    $null
}

function Assert-RegistrationIsDedicated {
    param([Parameter(Mandatory)][pscustomobject] $AppRegistration)

    if (@($AppRegistration.passwordCredentials).Count -gt 0) {
        throw "Entra app registration '$Registration' has a client secret. Use a dedicated secret-free registration."
    }

    $redirectUris = @($AppRegistration.web.redirectUris)
    $unexpectedRedirectUris = @($redirectUris | Where-Object { $_ -cne $callbackUrl })

    if ($unexpectedRedirectUris.Count -gt 0) {
        throw "Entra app registration '$Registration' has redirect URIs other than the canonical App Service callback. Use a dedicated registration."
    }
}

function Get-AzureResourcePropertyValue {
    param(
        [Parameter(Mandatory)][object] $Resource,
        [Parameter(Mandatory)][string] $Name
    )

    $directProperty = $Resource.PSObject.Properties[$Name]
    if ($null -ne $directProperty) {
        return $directProperty.Value
    }

    $propertiesProperty = $Resource.PSObject.Properties['properties']
    if ($null -eq $propertiesProperty -or $null -eq $propertiesProperty.Value) {
        return $null
    }

    $nestedProperty = $propertiesProperty.Value.PSObject.Properties[$Name]
    if ($null -ne $nestedProperty) {
        return $nestedProperty.Value
    }

    $null
}

function Assert-AppRegistrationAuthenticationFlow {
    param([Parameter(Mandatory)][pscustomobject] $AppRegistration)

    Assert-RegistrationIsDedicated -AppRegistration $AppRegistration

    $redirectUris = @($AppRegistration.web.redirectUris)
    $implicitGrantSettingsProperty =
        $AppRegistration.web.PSObject.Properties['implicitGrantSettings']
    $implicitGrantSettings =
        if ($null -eq $implicitGrantSettingsProperty) {
            $null
        } else {
            $implicitGrantSettingsProperty.Value
        }
    $idTokenIssuanceEnabled =
        if ($null -eq $implicitGrantSettings) {
            $false
        } else {
            [bool] (Get-AzureResourcePropertyValue `
                -Resource $implicitGrantSettings `
                -Name 'enableIdTokenIssuance')
        }
    $accessTokenIssuanceEnabled =
        if ($null -eq $implicitGrantSettings) {
            $false
        } else {
            [bool] (Get-AzureResourcePropertyValue `
                -Resource $implicitGrantSettings `
                -Name 'enableAccessTokenIssuance')
        }

    if ([string] $AppRegistration.signInAudience -cne 'AzureADMyOrg' -or
        $redirectUris.Count -ne 1 -or
        [string] $redirectUris[0] -cne $callbackUrl -or
        -not $idTokenIssuanceEnabled -or
        $accessTokenIssuanceEnabled) {
        throw "Entra app registration '$Registration' is not configured for Easy Auth's single-tenant code/id_token callback."
    }
}

function Get-WebAppPlanResourceId {
    param([Parameter(Mandatory)][pscustomobject] $WebApp)

    $planResourceId =
        [string] (Get-AzureResourcePropertyValue `
            -Resource $WebApp `
            -Name 'appServicePlanId')

    if ([string]::IsNullOrWhiteSpace($planResourceId)) {
        $planResourceId =
            [string] (Get-AzureResourcePropertyValue `
                -Resource $WebApp `
                -Name 'serverFarmId')
    }

    $planResourceId
}

function Get-ExistingResources {
    param([Parameter(Mandatory)][string] $SubscriptionId)

    $group = Try-AzJson -Arguments @(
        'group', 'show',
        '--name', $ResourceGroup,
        '--subscription', $SubscriptionId)

    $planResource =
        if ($null -eq $group) {
            $null
        } else {
            Try-AzJson -Arguments @(
                'appservice', 'plan', 'show',
                '--name', $Plan,
                '--resource-group', $ResourceGroup,
                '--subscription', $SubscriptionId)
        }

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

    $webApp =
        if ($null -eq $group) {
            $null
        } else {
            Try-AzJson -Arguments @(
                'webapp', 'show',
                '--name', $appName,
                '--resource-group', $ResourceGroup,
                '--subscription', $SubscriptionId)
        }

    [pscustomobject]@{
        Group = $group
        Plan = $planResource
        Identity = $identityResource
        WebApp = $webApp
        Registration = Get-ExactAppRegistration
    }
}

function Assert-ExistingResourceSafety {
    param(
        [Parameter(Mandatory)][pscustomobject] $Existing,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    $environmentTag =
        if ($null -ne $Existing.Group -and
            $null -ne $Existing.Group.tags -and
            $null -ne $Existing.Group.tags.PSObject.Properties['environment']) {
            [string] $Existing.Group.tags.PSObject.Properties['environment'].Value
        } else {
            ''
        }

    if ($environmentTag -match '^(?i:prod|production)$') {
        throw "Resource group '$ResourceGroup' is tagged as production. This automation is non-production only."
    }

    if ($null -ne $Existing.Plan -and
        -not [bool] (Get-AzureResourcePropertyValue `
            -Resource $Existing.Plan `
            -Name 'reserved')) {
        throw "Existing App Service plan '$Plan' is not a Linux plan."
    }

    if ($null -ne $Existing.WebApp) {
        if ($null -eq $Existing.Plan) {
            throw "Existing app '$appName' does not use the requested plan '$Plan'."
        }

        if ([string] $Existing.WebApp.defaultHostName -cne 'treemon.azurewebsites.net') {
            throw "Existing app '$appName' does not have the canonical hostname."
        }

        if ([string] $Existing.WebApp.kind -notmatch '(^|,)linux($|,)') {
            throw "Existing app '$appName' is not a Linux App Service."
        }

        if (-not [string]::Equals(
                (Get-WebAppPlanResourceId -WebApp $Existing.WebApp),
                [string] $Existing.Plan.id,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Existing app '$appName' does not use plan '$Plan'."
        }

        if ($null -ne $Existing.Identity) {
            Assert-WebAppIdentityIsDedicated `
                -WebApp $Existing.WebApp `
                -IdentityResourceId ([string] $Existing.Identity.id)
        } elseif ($null -ne $Existing.WebApp.identity) {
            throw "Existing app '$appName' has an identity, but the requested dedicated identity '$Identity' does not exist."
        }

        Assert-NoClientSecretConfiguration -SubscriptionId $SubscriptionId
    } else {
        $availabilityBodyPath = [IO.Path]::GetTempFileName()

        try {
            Write-JsonFile `
                -Value ([ordered]@{
                    name = $appName
                    type = 'Microsoft.Web/sites'
                }) `
                -Path $availabilityBodyPath
            $availability = Invoke-AzJson -Arguments @(
                'rest',
                '--method', 'post',
                '--uri', "/subscriptions/$SubscriptionId/providers/Microsoft.Web/checknameavailability?api-version=2023-12-01",
                '--body', "@$availabilityBodyPath",
                '--subscription', $SubscriptionId)
        } finally {
            Remove-Item -LiteralPath $availabilityBodyPath -Force -ErrorAction SilentlyContinue
        }

        if (-not [bool] $availability.nameAvailable) {
            throw "Global App Service name '$appName' is unavailable. The deployment will not append a suffix or use another hostname."
        }
    }

    if ($null -ne $Existing.Registration) {
        Assert-RegistrationIsDedicated -AppRegistration $Existing.Registration
    }
}

function Ensure-ResourceGroup {
    param(
        [AllowNull()][pscustomobject] $ExistingGroup,
        [Parameter(Mandatory)][pscustomobject] $StorageAccount,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    if ($null -eq $ExistingGroup) {
        Write-Step "Creating non-production resource group '$ResourceGroup'"
        Invoke-AzNone -Arguments @(
            'group', 'create',
            '--name', $ResourceGroup,
            '--location', [string] $StorageAccount.location,
            '--tags', 'environment=nonproduction', 'purpose=treemon-canvas-share',
            '--subscription', $SubscriptionId)
    }

    Invoke-AzJson -Arguments @(
        'group', 'show',
        '--name', $ResourceGroup,
        '--subscription', $SubscriptionId)
}

function Get-ShareContainerScope {
    param(
        [Parameter(Mandatory)][pscustomobject] $StorageAccount,
        [Parameter(Mandatory)][string] $Container
    )

    "$($StorageAccount.id)/blobServices/default/containers/$Container"
}

function Ensure-PrivateContainer {
    param(
        [Parameter(Mandatory)][pscustomobject] $StorageAccount,
        [Parameter(Mandatory)][string] $Container,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    Write-Step "Ensuring private Blob container '$Container'"
    Invoke-AzNone -Arguments @(
        'storage', 'account', 'update',
        '--ids', [string] $StorageAccount.id,
        '--allow-blob-public-access', 'false',
        '--subscription', $SubscriptionId)

    $existingContainer = Try-AzJson -Arguments @(
        'storage', 'container-rm', 'show',
        '--storage-account', [string] $StorageAccount.id,
        '--name', $Container,
        '--subscription', $SubscriptionId)

    if ($null -eq $existingContainer) {
        Invoke-AzNone -Arguments @(
            'storage', 'container-rm', 'create',
            '--storage-account', [string] $StorageAccount.id,
            '--name', $Container,
            '--public-access', 'off',
            '--subscription', $SubscriptionId)
    } else {
        Invoke-AzNone -Arguments @(
            'storage', 'container-rm', 'update',
            '--storage-account', [string] $StorageAccount.id,
            '--name', $Container,
            '--public-access', 'off',
            '--subscription', $SubscriptionId)
    }

    Get-ShareContainerScope `
        -StorageAccount $StorageAccount `
        -Container $Container
}

function Ensure-AppServicePlan {
    param(
        [AllowNull()][pscustomobject] $ExistingPlan,
        [Parameter(Mandatory)][string] $Location,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    if ($null -eq $ExistingPlan) {
        Write-Step "Creating Linux App Service plan '$Plan'"
        Invoke-AzNone -Arguments @(
            'appservice', 'plan', 'create',
            '--name', $Plan,
            '--resource-group', $ResourceGroup,
            '--location', $Location,
            '--sku', 'B1',
            '--is-linux',
            '--subscription', $SubscriptionId)
    }

    Invoke-AzJson -Arguments @(
        'appservice', 'plan', 'show',
        '--name', $Plan,
        '--resource-group', $ResourceGroup,
        '--subscription', $SubscriptionId)
}

function Ensure-ManagedIdentity {
    param(
        [AllowNull()][pscustomobject] $ExistingIdentity,
        [Parameter(Mandatory)][string] $Location,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    if ($null -eq $ExistingIdentity) {
        Write-Step "Creating user-assigned managed identity '$Identity'"
        Invoke-AzNone -Arguments @(
            'identity', 'create',
            '--name', $Identity,
            '--resource-group', $ResourceGroup,
            '--location', $Location,
            '--subscription', $SubscriptionId)
    }

    Invoke-AzJson -Arguments @(
        'identity', 'show',
        '--name', $Identity,
        '--resource-group', $ResourceGroup,
        '--subscription', $SubscriptionId)
}

function Get-AssignedIdentityIds {
    param([AllowNull()][object] $UserAssignedIdentities)

    if ($null -eq $UserAssignedIdentities) {
        return @()
    }

    @($UserAssignedIdentities.PSObject.Properties.Name)
}

function Assert-WebAppIdentityIsDedicated {
    param(
        [Parameter(Mandatory)][pscustomobject] $WebApp,
        [Parameter(Mandatory)][string] $IdentityResourceId
    )

    if ($null -eq $WebApp.identity) {
        return
    }

    if ([string] $WebApp.identity.type -match 'SystemAssigned') {
        throw "Existing app '$appName' has a system-assigned identity. Use the dedicated user-assigned identity only."
    }

    $unexpectedIdentities = @(
        Get-AssignedIdentityIds $WebApp.identity.userAssignedIdentities
        | Where-Object {
            -not [string]::Equals(
                $_,
                $IdentityResourceId,
                [StringComparison]::OrdinalIgnoreCase)
        }
    )

    if ($unexpectedIdentities.Count -gt 0) {
        throw "Existing app '$appName' has a user-assigned identity other than '$Identity'."
    }
}

function Ensure-WebApp {
    param(
        [AllowNull()][pscustomobject] $ExistingWebApp,
        [Parameter(Mandatory)][pscustomobject] $PlanResource,
        [Parameter(Mandatory)][pscustomobject] $ManagedIdentity,
        [Parameter(Mandatory)][string] $SubscriptionId,
        [Parameter(Mandatory)][string] $WorkingDirectory
    )

    if ($null -eq $ExistingWebApp) {
        Write-Step "Creating fixed-name App Service '$appName'"
        Invoke-AzNone -Arguments @(
            'webapp', 'create',
            '--name', $appName,
            '--resource-group', $ResourceGroup,
            '--plan', $Plan,
            '--runtime', 'DOTNETCORE:10.0',
            '--assign-identity', [string] $ManagedIdentity.id,
            '--basic-auth', 'Disabled',
            '--subscription', $SubscriptionId)
    } else {
        Assert-WebAppIdentityIsDedicated `
            -WebApp $ExistingWebApp `
            -IdentityResourceId ([string] $ManagedIdentity.id)
    }

    Invoke-AzNone -Arguments @(
        'webapp', 'identity', 'assign',
        '--name', $appName,
        '--resource-group', $ResourceGroup,
        '--identities', [string] $ManagedIdentity.id,
        '--subscription', $SubscriptionId)

    Invoke-AzNone -Arguments @(
        'webapp', 'update',
        '--name', $appName,
        '--resource-group', $ResourceGroup,
        '--https-only', 'true',
        '--subscription', $SubscriptionId)

    $siteConfigurationPath = Join-Path $WorkingDirectory 'webapp-site-config.json'
    Write-JsonFile `
        -Value ([ordered]@{ linuxFxVersion = 'DOTNETCORE|10.0' }) `
        -Path $siteConfigurationPath

    Invoke-AzNone -Arguments @(
        'webapp', 'config', 'set',
        '--name', $appName,
        '--resource-group', $ResourceGroup,
        '--generic-configurations', "@$siteConfigurationPath",
        '--startup-file', 'dotnet CanvasShareViewer.dll',
        '--ftps-state', 'Disabled',
        '--http20-enabled', 'true',
        '--min-tls-version', '1.2',
        '--subscription', $SubscriptionId)

    $webApp = Invoke-AzJson -Arguments @(
        'webapp', 'show',
        '--name', $appName,
        '--resource-group', $ResourceGroup,
        '--subscription', $SubscriptionId)

    if (-not [string]::Equals(
        (Get-WebAppPlanResourceId -WebApp $webApp),
        [string] $PlanResource.id,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "App '$appName' is not attached to plan '$Plan'."
    }

    $webApp
}

function Test-ServiceManagementReferenceRequired {
    param([Parameter(Mandatory)][Management.Automation.ErrorRecord] $ErrorRecord)

    $ErrorRecord.Exception.Message -match '(?i)ServiceManagementReference field is required'
}

function Get-OwnedServiceManagementReference {
    $ownedApplications = @(
        Invoke-AzJson -Arguments @('ad', 'app', 'list', '--show-mine')
    )
    $references = @(
        $ownedApplications
        | ForEach-Object {
            [string] (Get-AzureResourcePropertyValue `
                -Resource $_ `
                -Name 'serviceManagementReference')
        }
        | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        | Sort-Object -Unique
    )

    if ($references.Count -ne 1) {
        throw "The tenant requires serviceManagementReference for app registration changes, but the current Azure CLI user owns $($references.Count) distinct reference values. Exactly one is required for an unambiguous secret-free deployment."
    }

    [string] $references[0]
}

function New-ViewerAppRegistration {
    param([AllowEmptyString()][string] $ServiceManagementReference)

    $referenceArguments =
        if ([string]::IsNullOrWhiteSpace($ServiceManagementReference)) {
            @()
        } else {
            @('--service-management-reference', $ServiceManagementReference)
        }

    Invoke-AzJson -Arguments (@(
        'ad', 'app', 'create',
        '--display-name', $Registration,
        '--sign-in-audience', 'AzureADMyOrg',
        '--web-redirect-uris', $callbackUrl
    ) + $referenceArguments)
}

function Update-ViewerAppRegistration {
    param(
        [Parameter(Mandatory)][string] $AppId,
        [AllowEmptyString()][string] $ServiceManagementReference
    )

    $referenceArguments =
        if ([string]::IsNullOrWhiteSpace($ServiceManagementReference)) {
            @()
        } else {
            @('--service-management-reference', $ServiceManagementReference)
        }

    Invoke-AzNone -Arguments (@(
        'ad', 'app', 'update',
        '--id', $AppId,
        '--sign-in-audience', 'AzureADMyOrg',
        '--web-redirect-uris', $callbackUrl,
        '--enable-access-token-issuance', 'false',
        '--enable-id-token-issuance', 'true'
    ) + $referenceArguments)
}

function Ensure-AppRegistration {
    param([AllowNull()][pscustomobject] $ExistingRegistration)

    $serviceManagementReference = ''
    $appRegistration =
        if ($null -eq $ExistingRegistration) {
            Write-Step "Creating single-tenant Entra app registration '$Registration'"

            try {
                New-ViewerAppRegistration -ServiceManagementReference ''
            } catch {
                if (-not (Test-ServiceManagementReferenceRequired -ErrorRecord $_)) {
                    throw
                }

                $serviceManagementReference = Get-OwnedServiceManagementReference
                New-ViewerAppRegistration `
                    -ServiceManagementReference $serviceManagementReference
            }
        } else {
            $ExistingRegistration
        }

    Assert-RegistrationIsDedicated -AppRegistration $appRegistration

    if ([string]::IsNullOrWhiteSpace($serviceManagementReference)) {
        $serviceManagementReference =
            [string] (Get-AzureResourcePropertyValue `
                -Resource $appRegistration `
                -Name 'serviceManagementReference')
    }

    try {
        Update-ViewerAppRegistration `
            -AppId ([string] $appRegistration.appId) `
            -ServiceManagementReference $serviceManagementReference
    } catch {
        if (-not [string]::IsNullOrWhiteSpace($serviceManagementReference) -or
            -not (Test-ServiceManagementReferenceRequired -ErrorRecord $_)) {
            throw
        }

        $serviceManagementReference = Get-OwnedServiceManagementReference
        Update-ViewerAppRegistration `
            -AppId ([string] $appRegistration.appId) `
            -ServiceManagementReference $serviceManagementReference
    }

    $servicePrincipals = @(
        Invoke-AzJson -Arguments @(
            'ad', 'sp', 'list',
            '--filter', "appId eq '$($appRegistration.appId)'")
    )

    if ($servicePrincipals.Count -eq 0) {
        Invoke-AzNone -Arguments @(
            'ad', 'sp', 'create',
            '--id', [string] $appRegistration.appId)
    } elseif ($servicePrincipals.Count -gt 1) {
        throw "More than one service principal exists for Entra app registration '$Registration'."
    }

    Invoke-AzJson -Arguments @('ad', 'app', 'show', '--id', [string] $appRegistration.appId)
}

function Ensure-FederatedCredential {
    param(
        [Parameter(Mandatory)][pscustomobject] $AppRegistration,
        [Parameter(Mandatory)][pscustomobject] $ManagedIdentity,
        [Parameter(Mandatory)][string] $TenantId,
        [Parameter(Mandatory)][string] $WorkingDirectory
    )

    Write-Step 'Ensuring managed-identity federation for Easy Auth'
    $issuer = "https://login.microsoftonline.com/$TenantId/v2.0"
    $credentialPath = Join-Path $WorkingDirectory 'federated-credential.json'
    $existingCredentials = @(
        Invoke-AzJson -Arguments @(
            'ad', 'app', 'federated-credential', 'list',
            '--id', [string] $AppRegistration.appId)
    )
    $matchingCredentials = @(
        $existingCredentials | Where-Object name -CEQ $federatedCredentialName
    )

    if ($matchingCredentials.Count -gt 1) {
        throw "More than one '$federatedCredentialName' federated credential exists."
    }

    $credentialProperties = [ordered]@{
        issuer = $issuer
        subject = [string] $ManagedIdentity.principalId
        description = 'Trust the viewer managed identity as the Easy Auth client assertion.'
        audiences = @('api://AzureADTokenExchange')
    }

    if ($matchingCredentials.Count -eq 0) {
        $createProperties = [ordered]@{ name = $federatedCredentialName }
        foreach ($entry in $credentialProperties.GetEnumerator()) {
            $createProperties[$entry.Key] = $entry.Value
        }

        Write-JsonFile -Value $createProperties -Path $credentialPath
        Invoke-AzNone -Arguments @(
            'ad', 'app', 'federated-credential', 'create',
            '--id', [string] $AppRegistration.appId,
            '--parameters', "@$credentialPath")
    } else {
        Write-JsonFile -Value $credentialProperties -Path $credentialPath
        Invoke-AzNone -Arguments @(
            'ad', 'app', 'federated-credential', 'update',
            '--id', [string] $AppRegistration.appId,
            '--federated-credential-id', [string] $matchingCredentials[0].id,
            '--parameters', "@$credentialPath")
    }
}

function Test-ExactRoleAssignment {
    param(
        [Parameter(Mandatory)][string] $PrincipalObjectId,
        [Parameter(Mandatory)][string] $Role,
        [Parameter(Mandatory)][string] $Scope,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    $assignments = @(
        Invoke-AzJson -Arguments @(
            'role', 'assignment', 'list',
            '--assignee-object-id', $PrincipalObjectId,
            '--role', $Role,
            '--scope', $Scope,
            '--fill-principal-name', 'false',
            '--subscription', $SubscriptionId)
        | Where-Object {
            [string]::Equals(
                [string] $_.scope,
                $Scope,
                [StringComparison]::OrdinalIgnoreCase)
        }
    )

    $assignments.Count -gt 0
}

function Ensure-RoleAssignment {
    param(
        [Parameter(Mandatory)][string] $PrincipalObjectId,
        [Parameter(Mandatory)][ValidateSet('User', 'ServicePrincipal')][string] $PrincipalType,
        [Parameter(Mandatory)][string] $Role,
        [Parameter(Mandatory)][string] $Scope,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    if (-not (Test-ExactRoleAssignment `
        -PrincipalObjectId $PrincipalObjectId `
        -Role $Role `
        -Scope $Scope `
        -SubscriptionId $SubscriptionId)) {
        Invoke-AzNone -Arguments @(
            'role', 'assignment', 'create',
            '--assignee-object-id', $PrincipalObjectId,
            '--assignee-principal-type', $PrincipalType,
            '--role', $Role,
            '--scope', $Scope,
            '--subscription', $SubscriptionId)
    }
}

function Ensure-StorageAccess {
    param(
        [Parameter(Mandatory)][pscustomobject] $ManagedIdentity,
        [Parameter(Mandatory)][string] $PublisherObjectId,
        [Parameter(Mandatory)][string] $ContainerScope,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    Write-Step 'Ensuring container-scoped Blob data roles'
    Ensure-RoleAssignment `
        -PrincipalObjectId ([string] $ManagedIdentity.principalId) `
        -PrincipalType ServicePrincipal `
        -Role $readerRole `
        -Scope $ContainerScope `
        -SubscriptionId $SubscriptionId

    Ensure-RoleAssignment `
        -PrincipalObjectId $PublisherObjectId `
        -PrincipalType User `
        -Role $contributorRole `
        -Scope $ContainerScope `
        -SubscriptionId $SubscriptionId
}

function Get-CurrentManagementPolicy {
    param(
        [Parameter(Mandatory)][pscustomobject] $StorageAccount,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    Try-AzJson -Arguments @(
        'storage', 'account', 'management-policy', 'show',
        '--account-name', [string] $StorageAccount.name,
        '--resource-group', [string] $StorageAccount.resourceGroup,
        '--subscription', $SubscriptionId)
}

function Ensure-LifecyclePolicy {
    param(
        [Parameter(Mandatory)][pscustomobject] $StorageAccount,
        [Parameter(Mandatory)][pscustomobject] $DesiredRule,
        [Parameter(Mandatory)][string] $SubscriptionId,
        [Parameter(Mandatory)][string] $WorkingDirectory
    )

    Write-Step 'Ensuring Blob lifecycle cleanup after the maximum share lifetime'
    $currentPolicy = Get-CurrentManagementPolicy `
        -StorageAccount $StorageAccount `
        -SubscriptionId $SubscriptionId
    $currentRules = @(
        if ($null -eq $currentPolicy -or $null -eq $currentPolicy.policy) {
            @()
        } else {
            @($currentPolicy.policy.rules)
        }
    )
    $unrelatedRules = @(
        $currentRules | Where-Object name -CNE 'expire-shared-canvas-docs'
    )
    $policy = [ordered]@{
        rules = @($unrelatedRules) + @($DesiredRule)
    }
    $policyPath = Join-Path $WorkingDirectory 'merged-lifecycle-policy.json'
    Write-JsonFile -Value $policy -Path $policyPath

    Invoke-AzNone -Arguments @(
        'storage', 'account', 'management-policy', 'create',
        '--account-name', [string] $StorageAccount.name,
        '--resource-group', [string] $StorageAccount.resourceGroup,
        '--policy', "@$policyPath",
        '--subscription', $SubscriptionId)
}

function Assert-NoClientSecretConfiguration {
    param([Parameter(Mandatory)][string] $SubscriptionId)

    $settings = @(
        Invoke-AzJson -Arguments @(
            'webapp', 'config', 'appsettings', 'list',
            '--name', $appName,
            '--resource-group', $ResourceGroup,
            '--subscription', $SubscriptionId)
    )

    if ($settings | Where-Object name -CEQ 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET') {
        throw "App '$appName' already contains an Easy Auth client-secret setting. Remove it before using the secret-free deployment."
    }

    $configuredCredentialSetting = Try-AzJson -Arguments @(
        'rest',
        '--method', 'get',
        '--uri', "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/$appName/config/authsettingsV2?api-version=2023-12-01",
        '--query', 'properties.identityProviders.azureActiveDirectory.registration.clientSecretSettingName',
        '--subscription', $SubscriptionId)

    if (-not [string]::IsNullOrWhiteSpace([string] $configuredCredentialSetting) -and
        [string] $configuredCredentialSetting -cne $managedIdentityAssertionSetting) {
        throw "App '$appName' is configured with an Easy Auth client secret. Remove it before using managed-identity federation."
    }
}

function Ensure-AppSettings {
    param(
        [Parameter(Mandatory)][string] $StorageAccountName,
        [Parameter(Mandatory)][string] $Container,
        [Parameter(Mandatory)][pscustomobject] $ManagedIdentity,
        [Parameter(Mandatory)][string] $TenantId,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    Assert-NoClientSecretConfiguration -SubscriptionId $SubscriptionId
    Write-Step 'Configuring non-secret viewer settings'

    Invoke-AzNone -Arguments @(
        'webapp', 'config', 'appsettings', 'set',
        '--name', $appName,
        '--resource-group', $ResourceGroup,
        '--settings',
        "CanvasShareViewer__StorageAccountName=$StorageAccountName",
        "CanvasShareViewer__ShareContainer=$Container",
        'DOTNET_ENVIRONMENT=Production',
        'ASPNETCORE_ENVIRONMENT=Production',
        "AZURE_CLIENT_ID=$($ManagedIdentity.clientId)",
        "WEBSITE_AUTH_AAD_ALLOWED_TENANTS=$TenantId",
        '--subscription', $SubscriptionId)

    Invoke-AzNone -Arguments @(
        'webapp', 'config', 'appsettings', 'set',
        '--name', $appName,
        '--resource-group', $ResourceGroup,
        '--slot-settings',
        "$managedIdentityAssertionSetting=$($ManagedIdentity.clientId)",
        '--subscription', $SubscriptionId)
}

function Ensure-EasyAuth {
    param(
        [Parameter(Mandatory)][pscustomobject] $AppRegistration,
        [Parameter(Mandatory)][string] $TenantId,
        [Parameter(Mandatory)][string] $SubscriptionId,
        [Parameter(Mandatory)][string] $WorkingDirectory
    )

    Write-Step 'Configuring required single-tenant Easy Auth'
    $authSettings = [ordered]@{
        properties = [ordered]@{
            platform = [ordered]@{
                enabled = $true
                runtimeVersion = '~2'
            }
            globalValidation = [ordered]@{
                requireAuthentication = $true
                unauthenticatedClientAction = 'RedirectToLoginPage'
                redirectToProvider = 'azureactivedirectory'
            }
            httpSettings = [ordered]@{
                requireHttps = $true
            }
            identityProviders = [ordered]@{
                azureActiveDirectory = [ordered]@{
                    enabled = $true
                    registration = [ordered]@{
                        clientId = [string] $AppRegistration.appId
                        clientSecretSettingName = $managedIdentityAssertionSetting
                        openIdIssuer = "https://login.microsoftonline.com/$TenantId/v2.0"
                    }
                    login = [ordered]@{
                        loginParameters = @()
                    }
                }
            }
            login = [ordered]@{
                tokenStore = [ordered]@{
                    enabled = $false
                }
            }
        }
    }
    $authSettingsPath = Join-Path $WorkingDirectory 'authsettings-v2.json'
    Write-JsonFile -Value $authSettings -Path $authSettingsPath

    Invoke-AzNone -Arguments @(
        'rest',
        '--method', 'put',
        '--uri', "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/$appName/config/authsettingsV2?api-version=2023-12-01",
        '--body', "@$authSettingsPath",
        '--subscription', $SubscriptionId)
}

function Disable-BasicPublishingCredentials {
    param([Parameter(Mandatory)][string] $SubscriptionId)

    Write-Step 'Disabling FTP and SCM basic publishing credentials'

    @('ftp', 'scm') | ForEach-Object {
        Invoke-AzNone -Arguments @(
            'resource', 'update',
            '--resource-group', $ResourceGroup,
            '--name', $_,
            '--namespace', 'Microsoft.Web',
            '--resource-type', 'basicPublishingCredentialsPolicies',
            '--parent', "sites/$appName",
            '--set', 'properties.allow=false',
            '--subscription', $SubscriptionId)
    }
}

function Deploy-Viewer {
    param(
        [Parameter(Mandatory)][string] $PackagePath,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    Write-Step 'Deploying the viewer with Microsoft Entra authentication'
    Invoke-AzNone -Arguments @(
        'webapp', 'deploy',
        '--name', $appName,
        '--resource-group', $ResourceGroup,
        '--src-path', $PackagePath,
        '--type', 'zip',
        '--clean', 'true',
        '--restart', 'true',
        '--async', 'false',
        '--subscription', $SubscriptionId)
}

function Assert-DeployedState {
    param(
        [Parameter(Mandatory)][pscustomobject] $StorageAccount,
        [Parameter(Mandatory)][string] $Container,
        [Parameter(Mandatory)][string] $ContainerScope,
        [Parameter(Mandatory)][pscustomobject] $ManagedIdentity,
        [Parameter(Mandatory)][string] $PublisherObjectId,
        [Parameter(Mandatory)][pscustomobject] $AppRegistration,
        [Parameter(Mandatory)][string] $TenantId,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    Write-Step 'Verifying deployed control-plane configuration'
    Assert-ViewerBlobAccessIsContainerOnly `
        -PrincipalObjectId ([string] $ManagedIdentity.principalId) `
        -ContainerScope $ContainerScope `
        -SubscriptionId $SubscriptionId

    $webApp = Invoke-AzJson -Arguments @(
        'webapp', 'show',
        '--name', $appName,
        '--resource-group', $ResourceGroup,
        '--subscription', $SubscriptionId)

    if ([string] $webApp.defaultHostName -cne 'treemon.azurewebsites.net' -or
        -not [bool] $webApp.httpsOnly) {
        throw 'App Service hostname or HTTPS-only configuration is incorrect.'
    }

    $currentStorageAccount = Invoke-AzJson -Arguments @(
        'storage', 'account', 'show',
        '--ids', [string] $StorageAccount.id,
        '--subscription', $SubscriptionId)
    if ([bool] $currentStorageAccount.allowBlobPublicAccess) {
        throw 'Storage account still permits public Blob access.'
    }

    $currentContainer = Invoke-AzJson -Arguments @(
        'storage', 'container-rm', 'show',
        '--storage-account', [string] $StorageAccount.id,
        '--name', $Container,
        '--subscription', $SubscriptionId)
    $publicAccess =
        if ($null -ne $currentContainer.PSObject.Properties['publicAccess']) {
            [string] $currentContainer.PSObject.Properties['publicAccess'].Value
        } else {
            ''
        }
    if (-not [string]::IsNullOrWhiteSpace($publicAccess) -and
        $publicAccess -notmatch '^(?i:none|off)$') {
        throw "Blob container '$Container' still permits public access."
    }

    Assert-WebAppIdentityIsDedicated `
        -WebApp $webApp `
        -IdentityResourceId ([string] $ManagedIdentity.id)
    $assignedIdentityIds = Get-AssignedIdentityIds $webApp.identity.userAssignedIdentities

    if (-not ($assignedIdentityIds | Where-Object {
        [string]::Equals(
            $_,
            [string] $ManagedIdentity.id,
            [StringComparison]::OrdinalIgnoreCase)
    })) {
        throw "App '$appName' is missing managed identity '$Identity'."
    }

    $settings = @(
        Invoke-AzJson -Arguments @(
            'webapp', 'config', 'appsettings', 'list',
            '--name', $appName,
            '--resource-group', $ResourceGroup,
            '--subscription', $SubscriptionId)
    )
    $expectedSettings = [ordered]@{
        CanvasShareViewer__StorageAccountName = [string] $StorageAccount.name
        CanvasShareViewer__ShareContainer = $Container
        DOTNET_ENVIRONMENT = 'Production'
        ASPNETCORE_ENVIRONMENT = 'Production'
        AZURE_CLIENT_ID = [string] $ManagedIdentity.clientId
        WEBSITE_AUTH_AAD_ALLOWED_TENANTS = $TenantId
        $managedIdentityAssertionSetting = [string] $ManagedIdentity.clientId
    }

    foreach ($setting in $expectedSettings.GetEnumerator()) {
        if ((Get-AppSettingValue -Settings $settings -Name $setting.Key) -cne $setting.Value) {
            throw "App setting '$($setting.Key)' is missing or incorrect."
        }
    }

    if ($settings | Where-Object name -CEQ 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET') {
        throw 'An Easy Auth client-secret setting is present.'
    }

    $currentAppRegistration = Invoke-AzJson -Arguments @(
        'ad', 'app', 'show',
        '--id', [string] $AppRegistration.appId)
    Assert-AppRegistrationAuthenticationFlow `
        -AppRegistration $currentAppRegistration

    $authSettings = Invoke-AzJson -Arguments @(
        'rest',
        '--method', 'get',
        '--uri', "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/$appName/config/authsettingsV2?api-version=2023-12-01",
        '--subscription', $SubscriptionId)
    $azureAd = $authSettings.properties.identityProviders.azureActiveDirectory

    if (-not [bool] $authSettings.properties.platform.enabled -or
        -not [bool] $authSettings.properties.globalValidation.requireAuthentication -or
        [string] $authSettings.properties.globalValidation.unauthenticatedClientAction -cne 'RedirectToLoginPage' -or
        -not [bool] $azureAd.enabled -or
        [string] $azureAd.registration.clientId -cne [string] $AppRegistration.appId -or
        [string] $azureAd.registration.clientSecretSettingName -cne $managedIdentityAssertionSetting -or
        [string] $azureAd.registration.openIdIssuer -cne "https://login.microsoftonline.com/$TenantId/v2.0" -or
        @($azureAd.login.loginParameters).Count -ne 0 -or
        [bool] $authSettings.properties.login.tokenStore.enabled) {
        throw 'Easy Auth is not configured for required, secret-free, single-tenant authentication with the token store disabled.'
    }

    foreach ($policyName in @('ftp', 'scm')) {
        $policy = Invoke-AzJson -Arguments @(
            'resource', 'show',
            '--resource-group', $ResourceGroup,
            '--name', $policyName,
            '--namespace', 'Microsoft.Web',
            '--resource-type', 'basicPublishingCredentialsPolicies',
            '--parent', "sites/$appName",
            '--subscription', $SubscriptionId)

        if ([bool] $policy.properties.allow) {
            throw "$policyName basic publishing credentials are still enabled."
        }
    }

    if (-not (Test-ExactRoleAssignment `
        -PrincipalObjectId ([string] $ManagedIdentity.principalId) `
        -Role $readerRole `
        -Scope $ContainerScope `
        -SubscriptionId $SubscriptionId)) {
        throw "Viewer identity is missing container-scoped '$readerRole'."
    }

    if (-not (Test-ExactRoleAssignment `
        -PrincipalObjectId $PublisherObjectId `
        -Role $contributorRole `
        -Scope $ContainerScope `
        -SubscriptionId $SubscriptionId)) {
        throw "Publisher identity is missing container-scoped '$contributorRole'."
    }

    $managementPolicy = Get-CurrentManagementPolicy `
        -StorageAccount $StorageAccount `
        -SubscriptionId $SubscriptionId
    $lifecycleRules = @(
        $managementPolicy.policy.rules
        | Where-Object name -CEQ 'expire-shared-canvas-docs'
    )

    if ($lifecycleRules.Count -ne 1 -or
        [double] $lifecycleRules[0].definition.actions.baseBlob.delete.daysAfterModificationGreaterThan -lt $minimumLifecycleDays -or
        @($lifecycleRules[0].definition.filters.prefixMatch).Count -ne 1 -or
        [string] $lifecycleRules[0].definition.filters.prefixMatch[0] -cne "$Container/") {
        throw 'Blob lifecycle policy does not preserve the full 30-day share lifetime.'
    }

    $federatedCredentials = @(
        Invoke-AzJson -Arguments @(
            'ad', 'app', 'federated-credential', 'list',
            '--id', [string] $AppRegistration.appId)
        | Where-Object name -CEQ $federatedCredentialName
    )

    if ($federatedCredentials.Count -ne 1 -or
        [string] $federatedCredentials[0].issuer -cne "https://login.microsoftonline.com/$TenantId/v2.0" -or
        [string] $federatedCredentials[0].subject -cne [string] $ManagedIdentity.principalId -or
        @($federatedCredentials[0].audiences).Count -ne 1 -or
        [string] $federatedCredentials[0].audiences[0] -cne 'api://AzureADTokenExchange') {
        throw 'Managed-identity federated credential is missing or incorrect.'
    }
}
