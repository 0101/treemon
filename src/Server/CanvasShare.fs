/// Publishes an already-exported, standalone canvas doc to Azure Blob Storage and mints a per-doc,
/// read-only SAS URL a recipient can open in a plain browser (no login). Deliberately independent of
/// BOTH the Shared API contract and CanvasExport: `publish` takes an already-exported HTML string
/// plus the doc's filename, so the caller (`WorktreeApi.shareCanvasDocImpl`) owns the export step and
/// the assembly of the `CanvasShareResult`. That keeps this module a thin, replaceable storage
/// adapter with only three dependencies: `Azure.Storage.Blobs`, `Azure.Identity` and `GlobalConfig`.
///
/// Credential model (docs/spec/canvas-sharing.md, Decision #3): there is **no stored secret**. Links
/// are signed with a *user delegation key* fetched over Entra ID via `DefaultAzureCredential` (on a
/// dev host, the operator's `az login`), so the storage account runs with `allowSharedKeyAccess=false`
/// and no account key exists to leak, rotate, or commit. The cost is Azure's hard **7-day** ceiling on
/// a user delegation key — and therefore on every link.
///
/// Secrecy model (Decisions #2/#4/#5): the container is PRIVATE (anonymous access disabled at the
/// account), the blob lands under an unguessable `<random-prefix>/<filename>` name, and the returned
/// link is a blob-scoped, read-only, https-only SAS (`sr=b`, `sp=r`, `spr=https`). Because the SAS is
/// blob-scoped, a leaked link exposes exactly one doc; per-doc revoke is a blob delete.
///
/// The SAS is additionally bound to the *signing identity*: it carries `skoid`/`sktid`, and Azure
/// re-checks that principal's RBAC on every request. Removing the signer's `Storage Blob Data
/// Contributor` role therefore kills every outstanding link immediately, not at expiry.
module Server.CanvasShare

open System
open System.IO
open System.Text
open Azure.Identity
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Sas
open FsToolkit.ErrorHandling
open Server.GlobalConfig

/// Base62 alphabet (digits + upper + lower) for the unguessable blob prefix — URL-safe, and with no
/// `/` so it can't muddle the `<prefix>/<filename>` split.
let private base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"

/// Length of the random prefix. 22 base62 chars ≈ 131 bits of entropy — far beyond guessable; the
/// SAS signature (not the name) is the real gate anyway (Decision #5).
[<Literal>]
let internal PrefixLength = 22

/// A fresh high-entropy base62 prefix from the cryptographic RNG. `GetString` samples the alphabet
/// uniformly (no modulo bias). Impure (RNG) but shape-testable: right length, alphabet-only, and
/// distinct across calls.
let internal generatePrefix () : string =
    System.Security.Cryptography.RandomNumberGenerator.GetString(base62Alphabet.AsSpan(), PrefixLength)

/// The leaf of a filename — defends the blob name against a caller passing a doc path rather than a
/// bare name. The live caller validates first, but publishing must never silently create nested
/// blobs from `..`/subdirs. Pure.
let internal leafName (filename: string) : string =
    filename.Replace('\\', '/').Split('/') |> Array.last

/// The blob name a published doc lands at: `<random-prefix>/<filename-leaf>`. The random prefix gives
/// uniqueness + unguessability; the real filename gives the recipient a meaningful page/tab title
/// (Decision #5). Pure given the prefix, so the naming shape is unit-testable.
let internal blobName (prefix: string) (filename: string) : string =
    $"{prefix}/{leafName filename}"

/// Builds the per-doc SAS grant: blob-scoped (`sr=b`, `Resource = "b"`), read-only (`sp=r`),
/// https-only (`spr=https`), expiring at `expiresOn`. Pure — it holds no credential and touches no
/// network (the user delegation key is applied later by `ToSasQueryParameters`), so the exact
/// least-privilege grant is unit-testable in isolation. Least privilege (Decision #2): a recipient of
/// doc A's link cannot read doc B because the signature is bound to A's blob.
///
/// `expiresOn` must fall inside the delegation key's own validity window or the link is refused at
/// use time regardless of what the token says, so `publish` derives both from one start instant.
let internal buildSasBuilder (containerName: string) (blob: string) (expiresOn: DateTimeOffset) : BlobSasBuilder =
    BlobSasBuilder(
        BlobSasPermissions.Read, expiresOn,
        BlobContainerName = containerName,
        BlobName = blob,
        Resource = "b",
        Protocol = SasProtocol.Https)

/// The Azure Blob endpoint for a storage account. Pure, so URL assembly is unit-testable without a
/// credential or a network call.
let internal blobEndpoint (accountName: string) : Uri =
    Uri($"https://{accountName}.blob.core.windows.net")

/// The Azure credential, built ONCE and reused. This is a performance requirement, not a style
/// preference: `DefaultAzureCredential` caches its resolved token per instance, and on a dev host the
/// chain resolves through `AzureCliCredential`, which *spawns the `az` CLI* — a 3-5 second process
/// launch. Constructing a fresh credential per publish paid that cost every time and pushed the share
/// round-trip past the browser's transient-activation window, so the clipboard write was rejected and
/// the user got "copy it manually" instead of a copied link. Reusing the instance keeps the token
/// cached (and refreshed by the SDK) across shares.
///
/// `lazy` rather than a module-level value so merely loading this module never touches the credential
/// chain — tests reference `CanvasShare` and must not shell out to `az`.
let private credential = lazy (DefaultAzureCredential())

/// The client-facing "not configured" message. Names the config key to set; there is no secret to
/// mention because the design has none — the credential is the host's ambient Entra identity.
let internal notConfiguredMessage =
    "Canvas sharing is not configured — set canvasShare.accountName in ~/.treemon/config.json to an Azure Storage account name."

/// The message shown when the host has no usable Entra identity (e.g. `az login` has expired). This
/// is the one routine operational failure of the credential model, so it names the fix rather than
/// surfacing an SDK exception type.
let internal signInRequiredMessage =
    "Canvas sharing could not authenticate to Azure — run `az login` on this host and try again."

/// Publish an already-exported standalone HTML doc; return a per-doc read-only SAS URL string.
///
/// Uploads `html` to the PRIVATE container (created on first use if absent) at
/// `<random-prefix>/<filename>` with `Content-Type: text/html`, then signs a blob-scoped read-only
/// https SAS with an Entra **user delegation key** and returns its absolute URL. Returns `Error`
/// (never throws) when the backend is unconfigured, when the host has no Entra identity, or on any
/// storage failure. No returned message or log line contains the full SAS.
///
/// SECURITY (accepted risk — focused-review F17): `html` is author-controlled canvas content, and it
/// is published as ACTIVE, non-sandboxed HTML/JS with only `Content-Type` — no CSP, no
/// `X-Content-Type-Options`, no sanitization — so whatever JS it contains runs top-level in the
/// recipient's browser for the life of the link. Not sanitized on purpose: that would break the
/// feature's interactivity goal (Decision #6), and a per-blob CSP would need a CDN/proxy. Documented,
/// accepted trade-off — see `docs/spec/canvas-sharing.md` §"Security Posture".
let publish (filename: string) (html: string) : Async<Result<string, string>> =
    asyncResult {
        let config = readCanvasShareConfig ()
        let! accountName = config.AccountName |> Result.requireSome notConfiguredMessage
        // The try/with stays around the Azure SDK calls (a genuine interop boundary); the
        // Option→Error gate above is flattened into the asyncResult track.
        try
            let serviceClient = BlobServiceClient(blobEndpoint accountName, credential.Value)
            // Backdate the start to absorb clock skew between this host and the storage service, and
            // derive the expiry from it so the window stays strictly inside Azure's 7-day limit on a
            // user delegation key — at the maximum configured expiry, `expiresOn` is a few minutes
            // short of `now + 7d` rather than exactly on the boundary, which the service rejects.
            let startsOn = DateTimeOffset.UtcNow.AddMinutes(-5.0)
            let expiresOn = startsOn.AddDays(float config.DefaultExpiryDays)
            let! delegationKey =
                // The explicit CancellationToken selects the (startsOn, expiresOn, ct) overload; with
                // two arguments F# binds the same-arity (options, ct) overload instead and fails.
                serviceClient.GetUserDelegationKeyAsync(
                    Nullable startsOn, expiresOn, Threading.CancellationToken.None)
                |> Async.AwaitTask
            let containerClient = serviceClient.GetBlobContainerClient(config.Container)
            // Create the PRIVATE container on demand (idempotent) so a fresh account/subscription
            // works on first publish — the SDK never auto-creates it, and a missing container
            // otherwise fails the upload with 404 ContainerNotFound. PublicAccessType.None keeps
            // anonymous access off at the container level (Decision #4).
            let! _ = containerClient.CreateIfNotExistsAsync(PublicAccessType.None) |> Async.AwaitTask
            let blob = blobName (generatePrefix ()) filename
            let blobClient = containerClient.GetBlobClient(blob)
            // charset is declared so non-ASCII doc content isn't mojibaked when the blob is
            // opened standalone (the export injects no <meta charset>).
            let headers = BlobHttpHeaders(ContentType = "text/html; charset=utf-8")
            use stream = new MemoryStream(Encoding.UTF8.GetBytes html)
            let! _ =
                blobClient.UploadAsync(stream, BlobUploadOptions(HttpHeaders = headers))
                |> Async.AwaitTask
            let sasParameters =
                (buildSasBuilder config.Container blob expiresOn)
                    .ToSasQueryParameters(delegationKey.Value, accountName)
            return $"{blobClient.Uri}?{sasParameters}"
        with
        | :? AuthenticationFailedException as ex ->
            // The identity itself is unusable (expired/absent az login). Log the type only — an
            // Entra failure message can carry tenant and account detail.
            Log.log "CanvasShare" $"Publish failed to authenticate: {ex.GetType().Name}"
            return! Error signInRequiredMessage
        | :? Azure.RequestFailedException as ex ->
            // Log/return the status + error code only (e.g. 404 ContainerNotFound, 403
            // AuthorizationPermissionMismatch when the role assignment is missing) — a safe,
            // actionable token; the full message can echo request details.
            Log.log "CanvasShare" $"Publish to container '{config.Container}' failed: HTTP {ex.Status} {ex.ErrorCode}"
            return! Error $"Failed to publish shared doc: {ex.ErrorCode} (HTTP {ex.Status})."
        | ex ->
            Log.log "CanvasShare" $"Publish to container '{config.Container}' failed: {ex.GetType().Name}"
            return! Error $"Failed to publish shared doc ({ex.GetType().Name})."
    }
