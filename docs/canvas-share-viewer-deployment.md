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
- An existing storage account configured in the machine-level Treemon config. The container may be
  omitted to use `canvas-shared`; the deployment creates it through the ARM control plane when
  needed:

  ```json
  {
    "canvasShare": {
      "accountName": "<storage-account>",
      "container": "canvas-shared",
      "defaultExpiryDays": 7
    }
  }
  ```

The script reads the storage account and container from `~/.treemon/config.json` (or
`$TREEMON_CONFIG_DIR/config.json`) and uses the current Azure CLI user as the publisher. Storage
and publisher identifiers are therefore not additional command-line inputs.

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

Validation checks the selected subscription and tenant, the storage configuration, any existing
resources, global availability of `treemon` when the app does not yet exist, the lifecycle-policy
invariant, and a local Release publish of the viewer. If the requested managed identity already
exists, validation also resolves its direct and inherited role assignments and fails when any
effective Blob-read data action is scoped outside the configured share container. Group-derived
assignments are included. It performs no Azure mutation and does not write machine configuration.

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
   callback.
4. Adds a federated credential whose subject is the managed identity principal. Easy Auth uses the
   slot-sticky `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID` setting and its
   `clientSecretSettingName` sentinel, so no client secret is created.
5. Requires Easy Auth before requests reach the viewer, uses the tenant's v2 issuer, requests no
   extra login scopes, requires HTTPS, and disables the token store.
6. Merges `expire-shared-canvas-docs` into the storage account's complete lifecycle policy while
   preserving unrelated rules. Deletion starts only after more than 31 days, beyond the 30-day
   maximum share lifetime.
7. Disables FTP and SCM basic publishing credentials, builds a ZIP locally, and deploys it with
   `az webapp deploy`. Azure CLI therefore uses Microsoft Entra authentication rather than a
   deployment credential.
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
sign-in or Blob read that fails immediately after provisioning should be retried after propagation;
do not replace the managed-identity federation with a client secret.

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
