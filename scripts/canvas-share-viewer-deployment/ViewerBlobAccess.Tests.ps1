#requires -Version 7.4

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:directAssignments = @()
$script:inheritedAssignments = @()
$script:roleDefinitions = @{}
$script:allQueryCount = 0
$script:inheritedQueryCount = 0
$script:roleDefinitionQueryCount = 0

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $command = $Arguments[0..2] -join ' '

    switch ($command) {
        'role assignment list' {
            if ($Arguments -notcontains '--include-groups') {
                throw "Role-assignment query omitted --include-groups: $($Arguments -join ' ')"
            }

            if ($Arguments -contains '--all') {
                $script:allQueryCount++
                return $script:directAssignments
            }

            if ($Arguments -contains '--include-inherited') {
                $script:inheritedQueryCount++
                return $script:inheritedAssignments
            }

            throw "Unexpected role-assignment query: $($Arguments -join ' ')"
        }
        'role definition show' {
            $script:roleDefinitionQueryCount++
            $idIndex = [Array]::IndexOf($Arguments, '--id')

            if ($idIndex -lt 0 -or $idIndex + 1 -ge $Arguments.Count) {
                throw "Role-definition query omitted --id: $($Arguments -join ' ')"
            }

            $roleDefinitionId = $Arguments[$idIndex + 1]

            if (-not $script:roleDefinitions.ContainsKey($roleDefinitionId)) {
                throw "No mocked role definition for '$roleDefinitionId'."
            }

            return $script:roleDefinitions[$roleDefinitionId]
        }
        default {
            throw "Unexpected Azure CLI command: $($Arguments -join ' ')"
        }
    }
}

. (Join-Path $PSScriptRoot 'ViewerBlobAccess.ps1')

function Reset-Mocks {
    $script:directAssignments = @()
    $script:inheritedAssignments = @()
    $script:roleDefinitions = @{}
    $script:allQueryCount = 0
    $script:inheritedQueryCount = 0
    $script:roleDefinitionQueryCount = 0
}

function Assert-TextContains {
    param(
        [Parameter(Mandatory)][string] $Actual,
        [Parameter(Mandatory)][string] $Expected
    )

    if (-not $Actual.Contains($Expected, [StringComparison]::Ordinal)) {
        throw "Expected '$Actual' to contain '$Expected'."
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory)][object] $Actual,
        [Parameter(Mandatory)][object] $Expected,
        [Parameter(Mandatory)][string] $Because
    )

    if ($Actual -ne $Expected) {
        throw "Expected '$Expected' but got '$Actual': $Because"
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

$subscriptionId = '11111111-1111-1111-1111-111111111111'
$principalObjectId = '22222222-2222-2222-2222-222222222222'
$storageAccountScope = "/subscriptions/$subscriptionId/resourceGroups/storage-rg/providers/Microsoft.Storage/storageAccounts/shares"
$containerScope = "$storageAccountScope/blobServices/default/containers/canvas-shared"
$readerRoleDefinitionId = "/subscriptions/$subscriptionId/providers/Microsoft.Authorization/roleDefinitions/2a2b9908-6ea1-4ae2-8e65-a410df84e7d1"
$blobReadDataAction = 'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'

Invoke-TestCase 'rejects an unconditioned account-scoped Storage Blob Data Reader assignment' {
    Reset-Mocks
    $assignmentId = "$storageAccountScope/providers/Microsoft.Authorization/roleAssignments/broad-reader"
    $script:directAssignments = @(
        [pscustomobject]@{
            id = $assignmentId
            roleDefinitionId = $readerRoleDefinitionId
            scope = $storageAccountScope
            condition = $null
        }
    )
    $script:roleDefinitions[$readerRoleDefinitionId] =
        [pscustomobject]@{
            id = $readerRoleDefinitionId
            roleName = 'Storage Blob Data Reader'
            permissions = @(
                [pscustomobject]@{
                    dataActions = @($blobReadDataAction)
                    notDataActions = @()
                }
            )
        }

    $message = ''

    try {
        Assert-ViewerBlobAccessIsContainerOnly `
            -PrincipalObjectId $principalObjectId `
            -ContainerScope $containerScope `
            -SubscriptionId $subscriptionId
    } catch {
        $message = $_.Exception.Message
    }

    if ([string]::IsNullOrWhiteSpace($message)) {
        throw 'Expected the broader Blob reader assignment to be rejected.'
    }

    Assert-TextContains -Actual $message -Expected $assignmentId
    Assert-TextContains -Actual $message -Expected 'Storage Blob Data Reader'
    Assert-TextContains -Actual $message -Expected $storageAccountScope
    Assert-TextContains -Actual $message -Expected 'will not delete this assignment'
}

Invoke-TestCase 'allows and de-duplicates a role confined to the share container' {
    Reset-Mocks
    $assignment =
        [pscustomobject]@{
            id = "$containerScope/providers/Microsoft.Authorization/roleAssignments/container-reader"
            roleDefinitionId = $readerRoleDefinitionId
            scope = $containerScope
            condition = $null
        }
    $script:directAssignments = @($assignment)
    $script:inheritedAssignments = @($assignment)
    $script:roleDefinitions[$readerRoleDefinitionId] =
        [pscustomobject]@{
            id = $readerRoleDefinitionId
            roleName = 'Storage Blob Data Reader'
            permissions = @(
                [pscustomobject]@{
                    dataActions = @($blobReadDataAction)
                    notDataActions = @()
                }
            )
        }

    Assert-ViewerBlobAccessIsContainerOnly `
        -PrincipalObjectId $principalObjectId `
        -ContainerScope $containerScope `
        -SubscriptionId $subscriptionId

    Assert-Equal -Actual $script:allQueryCount -Expected 1 -Because 'direct assignments must be enumerated across the subscription'
    Assert-Equal -Actual $script:inheritedQueryCount -Expected 1 -Because 'assignments inherited from parent scopes must be enumerated'
    Assert-Equal -Actual $script:roleDefinitionQueryCount -Expected 1 -Because 'duplicate assignments should resolve their role once'
}

Invoke-TestCase 'rejects a Blob reader assignment returned only by the inherited-scope query' {
    Reset-Mocks
    $managementGroupScope = '/providers/Microsoft.Management/managementGroups/development'
    $assignmentId = "$managementGroupScope/providers/Microsoft.Authorization/roleAssignments/inherited-reader"
    $script:inheritedAssignments = @(
        [pscustomobject]@{
            id = $assignmentId
            roleDefinitionId = $readerRoleDefinitionId
            scope = $managementGroupScope
            condition = $null
        }
    )
    $script:roleDefinitions[$readerRoleDefinitionId] =
        [pscustomobject]@{
            id = $readerRoleDefinitionId
            roleName = 'Storage Blob Data Reader'
            permissions = @(
                [pscustomobject]@{
                    dataActions = @($blobReadDataAction)
                    notDataActions = @()
                }
            )
        }

    $message = ''

    try {
        Assert-ViewerBlobAccessIsContainerOnly `
            -PrincipalObjectId $principalObjectId `
            -ContainerScope $containerScope `
            -SubscriptionId $subscriptionId
    } catch {
        $message = $_.Exception.Message
    }

    if ([string]::IsNullOrWhiteSpace($message)) {
        throw 'Expected the inherited Blob reader assignment to be rejected.'
    }

    Assert-TextContains -Actual $message -Expected $assignmentId
    Assert-TextContains -Actual $message -Expected $managementGroupScope
}

Invoke-TestCase 'fails closed on a conditioned Blob reader assignment at a broader scope' {
    Reset-Mocks
    $assignmentId = "$storageAccountScope/providers/Microsoft.Authorization/roleAssignments/conditioned-reader"
    $script:directAssignments = @(
        [pscustomobject]@{
            id = $assignmentId
            roleDefinitionId = $readerRoleDefinitionId
            scope = $storageAccountScope
            condition = "@Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringEquals 'canvas-shared'"
        }
    )
    $script:roleDefinitions[$readerRoleDefinitionId] =
        [pscustomobject]@{
            id = $readerRoleDefinitionId
            roleName = 'Storage Blob Data Reader'
            permissions = @(
                [pscustomobject]@{
                    dataActions = @($blobReadDataAction)
                    notDataActions = @()
                }
            )
        }

    $message = ''

    try {
        Assert-ViewerBlobAccessIsContainerOnly `
            -PrincipalObjectId $principalObjectId `
            -ContainerScope $containerScope `
            -SubscriptionId $subscriptionId
    } catch {
        $message = $_.Exception.Message
    }

    if ([string]::IsNullOrWhiteSpace($message)) {
        throw 'Expected the conditioned broader Blob reader assignment to be rejected.'
    }

    Assert-TextContains -Actual $message -Expected $assignmentId
    Assert-TextContains `
        -Actual $message `
        -Expected 'A condition on a broader assignment is not accepted as proof of container-only access.'
}

Invoke-TestCase 'honors notDataActions when calculating effective Blob read' {
    Reset-Mocks
    $customRoleDefinitionId = "/subscriptions/$subscriptionId/providers/Microsoft.Authorization/roleDefinitions/33333333-3333-3333-3333-333333333333"
    $script:directAssignments = @(
        [pscustomobject]@{
            id = "$storageAccountScope/providers/Microsoft.Authorization/roleAssignments/excluded-reader"
            roleDefinitionId = $customRoleDefinitionId
            scope = $storageAccountScope
            condition = $null
        }
    )
    $script:roleDefinitions[$customRoleDefinitionId] =
        [pscustomobject]@{
            id = $customRoleDefinitionId
            roleName = 'Storage data except Blob read'
            permissions = @(
                [pscustomobject]@{
                    dataActions = @('Microsoft.Storage/*')
                    notDataActions = @($blobReadDataAction)
                }
            )
        }

    Assert-ViewerBlobAccessIsContainerOnly `
        -PrincipalObjectId $principalObjectId `
        -ContainerScope $containerScope `
        -SubscriptionId $subscriptionId
}

Write-Host 'Viewer Blob access regression tests passed.'
