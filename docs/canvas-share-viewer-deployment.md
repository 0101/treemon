# Canvas Share Viewer Deployment

This is a local operator workflow for an isolated, non-production Azure subscription. It creates
or reconciles the canonical viewer at exactly:

```text
https://treemon.azurewebsites.net
```

The App Service name is fixed as `treemon`. The automation checks global name availability before
the first creation and stops if the name is unavailable; it never chooses a suffix or a custom
domain.

## Prerequisites

- PowerShell 7.4 or later, Azure CLI 2.72.0 or later, and .NET SDK 10 or later.
- Azure CLI signed in as the delegated publisher user, with the requested personal development
  subscription selected:

  ```powershell
  az login --tenant '<tenant-id>'
  az account set --subscription '<subscription-name-or-id>'
  ```

- Azure permissions to create resources, update the storage account, assign roles at the Blob
  container scope, and disable App Service basic publishing credentials. Entra permissions must
  allow creation or ownership of the dedicated app registration and its federated credential.
  The operator must also be able to list the viewer identity's role assignments throughout the
  subscription and inherited parent scopes and read their role definitions.
- If the tenant enforces `serviceManagementReference` on app-registration changes, the signed-in
  Azure CLI user must own applications that expose exactly one distinct non-empty reference value.
  The script discovers that value only after Entra returns the specific required-field error; it
  never invents, prints, or asks for a service reference, and fails when publisher-owned state is
  absent or ambiguous.
- An existing storage account configured in the machine-level Treemon config. The container may be
  omitted to use `canvas-shared`; the deployment creates it through the ARM control plane when
  needed:

  ```json
  {
    "canvasShare": {
      "approvedSubscription": "<personal-development-subscription>",
      "accountName": "<storage-account>",
      "container": "canvas-shared",
      "defaultExpiryDays": 7
    }
  }
  ```

The script reads the approved subscription, storage account, and container from
`~/.treemon/config.json` (or `$TREEMON_CONFIG_DIR/config.json`) and uses the current Azure CLI user
as the publisher. The `-Subscription` argument and selected Azure CLI account must both exactly
match the machine-private approved value before the script performs any resource-provider or Entra
application operation. Exact subscription and tenant identifiers remain local and are never
committed. Storage and publisher identifiers are therefore not additional command-line inputs.

## Validate without changing Azure

Run the read-only validation first:

```powershell
.\scripts\deploy-canvas-share-viewer.ps1 `
  -Subscription '<personal-development-subscription>' `
  -Tenant '<tenant-id>' `
  -ResourceGroup '<non-production-resource-group>' `
  -Plan '<app-service-plan>' `
  -Identity '<viewer-managed-identity>' `
  -Registration '<viewer-app-registration>' `
  -ValidateOnly
```

Validation first checks the requested and selected subscription against the machine-private
approved target. A missing or mismatched value fails before any resource-provider or Entra
application operation. It then checks the tenant, storage configuration, existing resources,
global availability of `treemon` when the app does not yet exist, lifecycle-policy invariant, and
a local Release publish of the viewer. If the requested managed identity already exists, validation
also resolves its direct and inherited role assignments and fails when any effective Blob-read
data action is scoped outside the configured share container. Group-derived assignments are
included. It performs no Azure mutation and does not write machine configuration.

## Correct a deployment from another subscription

The correction workflow never accepts a source subscription, tenant, resource group, identity, or
resource ID. Automation operates only in the machine-approved destination; the operator performs
the App Service move in the Azure portal.

First prepare the destination:

```powershell
.\scripts\deploy-canvas-share-viewer.ps1 `
  -Subscription '<approved-personal-subscription>' `
  -Tenant '<tenant-id>' `
  -ResourceGroup '<destination-resource-group>' `
  -Plan '<app-service-plan-being-moved>' `
  -Identity '<replacement-viewer-identity>' `
  -Registration '<retained-viewer-registration>' `
  -PrepareCrossSubscriptionMove
```

Preparation confirms that the configured storage account and private share container already
exist in the approved subscription. It creates or reconciles only the destination resource group,
replacement user-assigned identity, and that identity's container-scoped `Storage Blob Data
Reader` assignment. It does not check global availability of `treemon`, create or read the App
Service or plan, mutate the app registration, change storage configuration, build or deploy the
viewer, or write Treemon configuration. It finishes by printing a redacted portal checklist.

Follow that checklist in the Azure portal. First detach the App Service's current user-assigned
identity attachment and confirm that its system-assigned identity is off; leave the detached
identity resource, its role assignments, storage, and resource groups in place for rollback. Then
move the canonical `treemon` App Service and its App Service plan together into the prepared
destination. Do not substitute an ordinary `-ValidateOnly` or apply run for preparation; before
the portal move those modes correctly stop because the existing app still owns the global name.
The viewer is expected to be unavailable from detachment until reconciliation finishes. If the
portal move fails before the app reaches the destination, manually reattach the preserved former
identity before retrying or restoring service.

After the portal reports success, use an isolated `TREEMON_CONFIG_DIR` seeded with the approved
subscription, storage account, and container from the private machine configuration, then run:

```powershell
.\scripts\deploy-canvas-share-viewer.ps1 `
  -Subscription '<approved-personal-subscription>' `
  -Tenant '<tenant-id>' `
  -ResourceGroup '<destination-resource-group>' `
  -Plan '<moved-app-service-plan>' `
  -Identity '<replacement-viewer-identity>' `
  -Registration '<retained-viewer-registration>' `
  -ReconcileCrossSubscriptionMove `
  -ConfirmPortalMoveCompleted
```

The confirmation switch is mandatory and is checked before local tools or Azure are consulted.
Reconciliation requires the moved canonical app, its plan, the prepared identity, and the retained
registration to exist. Before changing the app, it reads only a sanitized count and
prepared-identity match from the moved app's destination-side identity state. Any other
user-assigned attachment or a system-assigned identity stops reconciliation with portal guidance
and no App Service mutation; detach it in the portal and rerun. Once that preflight passes,
reconciliation attaches the prepared destination identity, then reconciles federation,
private-container RBAC, settings, lifecycle policy, package, Easy Auth, and deployed state through
approved-subscription-only calls. The isolated config receives the canonical `viewerBaseUrl`; the
production config remains unchanged.

If the app must be moved back for rollback, move the app and plan together and manually reattach
the preserved former identity afterwards.

## Provision and deploy

Remove `-ValidateOnly` to apply the same plan:

```powershell
.\scripts\deploy-canvas-share-viewer.ps1 `
  -Subscription '<personal-development-subscription>' `
  -Tenant '<tenant-id>' `
  -ResourceGroup '<non-production-resource-group>' `
  -Plan '<app-service-plan>' `
  -Identity '<viewer-managed-identity>' `
  -Registration '<viewer-app-registration>'
```

The script is idempotent and is intended to be run a second time with the same values. It:

1. Creates or reuses the non-production resource group, a B1 Linux App Service plan, the
   user-assigned managed identity, and the fixed-name App Service.
2. Disables account-level Blob public access, creates the configured private container, and grants
   the viewer `Storage Blob Data Reader` and the current publisher
   `Storage Blob Data Contributor`, both at that container's exact ARM scope. Before mutating an
   existing deployment and again during final verification, it rejects broader viewer Blob-read
   assignments discovered anywhere in the subscription or inherited from a parent scope.
3. Creates or reuses one uniquely named, secret-free, current-tenant `AzureADMyOrg` app
   registration and service principal. The registration accepts only the canonical App Service
   callback. On a restricted tenant's specific `serviceManagementReference` error, creation or
   update retries with the one unambiguous reference already carried by applications the delegated
   publisher owns. It enables ID-token issuance for Easy Auth's `code id_token` form-post callback
   while leaving browser access-token issuance disabled.
4. Adds a federated credential whose subject is the managed identity principal. Easy Auth uses the
   slot-sticky `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID` setting and its
   `clientSecretSettingName` sentinel, so no client secret is created.
5. Requires Easy Auth before requests reach the viewer, uses the tenant's v2 issuer, requests no
   extra login scopes, requires HTTPS, disables the token store, and pins both the .NET host and
   ASP.NET Core environments to `Production`.
6. Merges `expire-shared-canvas-docs` into the storage account's complete lifecycle policy while
   preserving unrelated rules. Deletion starts only after more than 31 days, beyond the 30-day
   maximum share lifetime.
7. Supplies the pipe-bearing Linux runtime through Azure CLI's JSON-file configuration input,
   avoiding command reinterpretation by the Windows `az.cmd` launcher. It then disables FTP and SCM
   basic publishing credentials, builds a ZIP locally, and deploys it with `az webapp deploy`.
   Azure CLI therefore uses Microsoft Entra authentication rather than a deployment credential.
8. Verifies the resulting control-plane configuration and then atomically sets:

   ```json
   {
     "canvasShare": {
       "viewerBaseUrl": "https://treemon.azurewebsites.net"
     }
   }
   ```

   Existing `canvasShare` fields and unrelated machine-level settings are preserved.

   Run the apply step while no Treemon instance is writing machine configuration -- the script
   reads, updates, and atomically replaces `config.json` itself rather than going through a
   running server, so a settings change made in the UI at the same moment could be overwritten.

The automation does not invoke Treemon server lifecycle commands and does not bind any local
Treemon port. Temporary build and JSON files are deleted on exit. It does not request or print
access tokens, create deployment credentials, or read a publishing profile.

## After deployment

Federated credentials and Blob role assignments can take several minutes to propagate. A first
sign-in or Blob read that fails immediately after provisioning may return the viewer's empty 503
response and should be retried after propagation; do not replace the managed-identity federation
with a client secret.

If the containment check reports an assignment ID, role, and scope, remove that assignment or
select a truly dedicated viewer identity. The script deliberately does not delete it because it
may authorize another workload. Conditions on broader assignments are not accepted as proof of
container-only access.

By default, every identity in the current workforce tenant can authenticate. If a smaller audience
is required, enable enterprise-application assignment and assign the intended users or groups in
Entra; keep the app registration single-tenant.

The canonical App Service, plan, identity, app registration/service principal, federated
credential, RBAC assignments, and lifecycle policy are durable non-production resources. Do not
tear them down after verification. Verification cleanup is limited to uploaded document fixtures
and auxiliary resources created solely for permission-boundary probes.

Useful read-only checks are:

```powershell
az webapp show --name treemon --resource-group '<non-production-resource-group>'

az rest --method get `
  --uri '/subscriptions/<subscription-id>/resourceGroups/<non-production-resource-group>/providers/Microsoft.Web/sites/treemon/config/authsettingsV2?api-version=2023-12-01'

az storage account management-policy show `
  --account-name '<storage-account>' `
  --resource-group '<storage-resource-group>'
```
