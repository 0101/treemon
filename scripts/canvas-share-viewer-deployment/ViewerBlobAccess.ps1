function Get-RolePermissionValues {
    param(
        [Parameter(Mandatory)][pscustomobject] $Permission,
        [Parameter(Mandatory)][ValidateSet('dataActions', 'notDataActions')][string] $PropertyName
    )

    $property = $Permission.PSObject.Properties[$PropertyName]

    if ($null -eq $property -or $null -eq $property.Value) {
        return @()
    }

    @($property.Value)
}

function Test-DataActionPatternMatches {
    param(
        [Parameter(Mandatory)][string] $Pattern,
        [Parameter(Mandatory)][string] $DataAction
    )

    $expression = '^' + [Regex]::Escape($Pattern).Replace('\*', '.*') + '$'
    [Regex]::IsMatch(
        $DataAction,
        $expression,
        [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)
}

function Test-RoleDefinitionGrantsBlobRead {
    param([Parameter(Mandatory)][pscustomobject] $RoleDefinition)

    $permissionsProperty = $RoleDefinition.PSObject.Properties['permissions']

    if ($null -eq $permissionsProperty -or $null -eq $permissionsProperty.Value) {
        throw "Role definition '$($RoleDefinition.id)' did not expose permissions. Viewer Blob access cannot be proven container-only."
    }

    $blobReadDataAction = 'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'

    foreach ($permission in @($permissionsProperty.Value)) {
        $grantsBlobRead = @(
            Get-RolePermissionValues -Permission $permission -PropertyName dataActions
            | Where-Object {
                Test-DataActionPatternMatches `
                    -Pattern ([string] $_) `
                    -DataAction $blobReadDataAction
            }
        ).Count -gt 0

        $excludesBlobRead = @(
            Get-RolePermissionValues -Permission $permission -PropertyName notDataActions
            | Where-Object {
                Test-DataActionPatternMatches `
                    -Pattern ([string] $_) `
                    -DataAction $blobReadDataAction
            }
        ).Count -gt 0

        if ($grantsBlobRead -and -not $excludesBlobRead) {
            return $true
        }
    }

    $false
}

function Test-ScopeIsWithinShareContainer {
    param(
        [Parameter(Mandatory)][string] $Scope,
        [Parameter(Mandatory)][string] $ContainerScope
    )

    $normalizedScope = $Scope.TrimEnd('/')
    $normalizedContainerScope = $ContainerScope.TrimEnd('/')

    [string]::Equals(
        $normalizedScope,
        $normalizedContainerScope,
        [StringComparison]::OrdinalIgnoreCase) -or
        $normalizedScope.StartsWith(
            "$normalizedContainerScope/",
            [StringComparison]::OrdinalIgnoreCase)
}

function Get-ViewerRoleAssignments {
    param(
        [Parameter(Mandatory)][string] $PrincipalObjectId,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    $directAssignments = @(
        Invoke-AzJson -Arguments @(
            'role', 'assignment', 'list',
            '--assignee-object-id', $PrincipalObjectId,
            '--all',
            '--include-groups',
            '--fill-principal-name', 'false',
            '--fill-role-definition-name', 'false',
            '--subscription', $SubscriptionId)
    )
    $inheritedAssignments = @(
        Invoke-AzJson -Arguments @(
            'role', 'assignment', 'list',
            '--assignee-object-id', $PrincipalObjectId,
            '--scope', "/subscriptions/$SubscriptionId",
            '--include-inherited',
            '--include-groups',
            '--fill-principal-name', 'false',
            '--fill-role-definition-name', 'false',
            '--subscription', $SubscriptionId)
    )
    $assignmentsById =
        [Collections.Generic.Dictionary[string, object]]::new(
            [StringComparer]::OrdinalIgnoreCase)

    foreach ($assignment in @($directAssignments) + @($inheritedAssignments)) {
        if ($null -eq $assignment) {
            throw 'Azure returned an empty viewer role assignment. Viewer Blob access cannot be proven container-only.'
        }

        $idProperty = $assignment.PSObject.Properties['id']
        $assignmentId =
            if ($null -eq $idProperty) {
                ''
            } else {
                [string] $idProperty.Value
            }

        if ([string]::IsNullOrWhiteSpace($assignmentId)) {
            throw 'Azure returned a viewer role assignment without an ID. Viewer Blob access cannot be proven container-only.'
        }

        $assignmentsById[$assignmentId] = $assignment
    }

    @($assignmentsById.Values)
}

function Get-RequiredRoleAssignmentValue {
    param(
        [Parameter(Mandatory)][pscustomobject] $Assignment,
        [Parameter(Mandatory)][ValidateSet('id', 'roleDefinitionId', 'scope')][string] $PropertyName
    )

    $property = $Assignment.PSObject.Properties[$PropertyName]
    $value =
        if ($null -eq $property) {
            ''
        } else {
            [string] $property.Value
        }

    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Azure returned a viewer role assignment without '$PropertyName'. Viewer Blob access cannot be proven container-only."
    }

    $value
}

function Assert-ViewerBlobAccessIsContainerOnly {
    param(
        [Parameter(Mandatory)][string] $PrincipalObjectId,
        [Parameter(Mandatory)][string] $ContainerScope,
        [Parameter(Mandatory)][string] $SubscriptionId
    )

    $assignments = @(
        Get-ViewerRoleAssignments `
            -PrincipalObjectId $PrincipalObjectId `
            -SubscriptionId $SubscriptionId
    )
    $roleDefinitions =
        [Collections.Generic.Dictionary[string, object]]::new(
            [StringComparer]::OrdinalIgnoreCase)

    foreach ($assignment in $assignments) {
        $assignmentId = Get-RequiredRoleAssignmentValue -Assignment $assignment -PropertyName id
        $roleDefinitionId =
            Get-RequiredRoleAssignmentValue -Assignment $assignment -PropertyName roleDefinitionId
        $scope = Get-RequiredRoleAssignmentValue -Assignment $assignment -PropertyName scope

        if (-not $roleDefinitions.ContainsKey($roleDefinitionId)) {
            try {
                $roleDefinition = Invoke-AzJson -Arguments @(
                    'role', 'definition', 'show',
                    '--id', $roleDefinitionId,
                    '--subscription', $SubscriptionId)
            } catch {
                throw "Could not resolve role definition '$roleDefinitionId' for viewer assignment '$assignmentId' at scope '$scope'. Viewer Blob access cannot be proven container-only. $($_.Exception.Message)"
            }

            if ($null -eq $roleDefinition) {
                throw "Role definition '$roleDefinitionId' for viewer assignment '$assignmentId' at scope '$scope' was not found. Viewer Blob access cannot be proven container-only."
            }

            $roleDefinitions[$roleDefinitionId] = $roleDefinition
        }

        $resolvedRoleDefinition = $roleDefinitions[$roleDefinitionId]

        if ((Test-RoleDefinitionGrantsBlobRead -RoleDefinition $resolvedRoleDefinition) -and
            -not (Test-ScopeIsWithinShareContainer `
                -Scope $scope `
                -ContainerScope $ContainerScope)) {
            $roleNameProperty = $resolvedRoleDefinition.PSObject.Properties['roleName']
            $roleName =
                if ($null -eq $roleNameProperty -or
                    [string]::IsNullOrWhiteSpace([string] $roleNameProperty.Value)) {
                    $roleDefinitionId
                } else {
                    [string] $roleNameProperty.Value
                }
            $conditionProperty = $assignment.PSObject.Properties['condition']
            $conditionNote =
                if ($null -ne $conditionProperty -and
                    -not [string]::IsNullOrWhiteSpace([string] $conditionProperty.Value)) {
                    ' A condition on a broader assignment is not accepted as proof of container-only access.'
                } else {
                    ''
                }

            throw "Viewer Blob-read access is not confined to '$ContainerScope'. Offending assignment: id='$assignmentId'; role='$roleName' ($roleDefinitionId); scope='$scope'.$conditionNote The deployment will not delete this assignment because it may belong to another workload. Remove it or use a truly dedicated viewer identity."
        }
    }
}
