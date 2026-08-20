function Resolve-EnabledAzureSubscription {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Value,

        [Parameter(Mandatory)]
        [string] $Source
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Source is absent or blank."
    }

    try {
        $account = Invoke-AzJson -Arguments @(
            'account', 'show',
            '--subscription', $Value)
    } catch {
        throw "$Source could not be resolved uniquely to an enabled Azure subscription."
    }

    if ($null -eq $account -or $account -is [array]) {
        throw "$Source could not be resolved uniquely to an enabled Azure subscription."
    }

    $idProperty = $account.PSObject.Properties['id']
    $stateProperty = $account.PSObject.Properties['state']

    if ($null -eq $idProperty -or
        $null -eq $stateProperty -or
        [string]::IsNullOrWhiteSpace([string] $idProperty.Value) -or
        -not [string]::Equals(
            [string] $stateProperty.Value,
            'Enabled',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Source could not be resolved uniquely to an enabled Azure subscription."
    }

    $account
}

function Get-AzureContext {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $ApprovedSubscription,

        [Parameter(Mandatory)]
        [string] $RequestedSubscription,

        [Parameter(Mandatory)]
        [string] $RequestedTenant
    )

    if ([string]::IsNullOrWhiteSpace($ApprovedSubscription)) {
        throw 'Treemon machine configuration must set canvasShare.approvedSubscription before viewer deployment.'
    }

    $cloud = Invoke-AzJson -Arguments @('cloud', 'show')
    if ([string] $cloud.name -cne 'AzureCloud') {
        throw "The canonical azurewebsites.net deployment requires the AzureCloud environment. Current cloud: $($cloud.name)."
    }

    $currentAccount = Invoke-AzJson -Arguments @('account', 'show')

    if ([string]::IsNullOrWhiteSpace([string] $currentAccount.id) -or
        -not [string]::Equals(
            [string] $currentAccount.state,
            'Enabled',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The selected Azure CLI account does not identify an enabled subscription.'
    }

    $approvedAccount = Resolve-EnabledAzureSubscription `
        -Value $ApprovedSubscription `
        -Source 'canvasShare.approvedSubscription'
    $requestedAccount = Resolve-EnabledAzureSubscription `
        -Value $RequestedSubscription `
        -Source 'The -Subscription input'

    if (-not [string]::Equals(
        [string] $approvedAccount.id,
        [string] $requestedAccount.id,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Azure subscription mismatch: -Subscription must resolve to canvasShare.approvedSubscription.'
    }

    if (-not [string]::Equals(
        [string] $approvedAccount.id,
        [string] $currentAccount.id,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Azure subscription mismatch: the selected Azure CLI account must resolve to canvasShare.approvedSubscription.'
    }

    if (-not [string]::Equals(
        [string] $approvedAccount.tenantId,
        $RequestedTenant,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The requested tenant does not own the selected subscription.'
    }

    if ([string] $currentAccount.user.type -cne 'user') {
        throw 'Sign in to Azure CLI with the delegated publisher user before running this script.'
    }

    $publisher = Invoke-AzJson -Arguments @('ad', 'signed-in-user', 'show')

    [pscustomobject]@{
        SubscriptionId = [string] $approvedAccount.id
        TenantId = [string] $approvedAccount.tenantId
        PublisherObjectId = [string] $publisher.id
    }
}
