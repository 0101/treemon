/// Publishes an already-exported, standalone canvas doc to a pre-provisioned private Azure Blob
/// container and returns its clean authenticated-viewer URL. Deliberately independent of BOTH the
/// Shared API contract and CanvasExport: `publish` takes an already-exported HTML string plus the
/// doc's filename, so `WorktreeApi.shareCanvasDocImpl` owns export and `CanvasShareResult` assembly.
///
/// Treemon stores no storage credential. A cached `AzureCliCredential` pipeline uses the operator's
/// delegated Azure identity for uploads, while recipients authenticate to the separate viewer and
/// never receive a Blob credential. Each blob carries its own expiry metadata and lands at an
/// unguessable `<random-prefix>/<filename>` name. The prefix narrows exact lookup but is not an
/// authorization grant: the viewer's Entra gate and synchronous expiry check enforce access.
///
/// Shared active HTML is never served directly from Blob Storage. The viewer renders it in the
/// sandbox/CSP boundary described in `docs/spec/canvas-sharing.md`; deleting the backing blob still
/// revokes one document immediately.
module Server.CanvasShare

open System
open System.Collections.Concurrent
open System.Globalization
open System.IO
open System.Text
open Azure.Identity
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open FsToolkit.ErrorHandling
open Server.GlobalConfig

/// Base62 alphabet (digits + upper + lower) for the unguessable blob prefix — URL-safe, and with no
/// `/` so it can't muddle the `<prefix>/<filename>` split.
let private base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"

/// Length of the opaque random prefix. 22 base62 chars ≈ 131 bits of entropy; it is a durable,
/// unguessable lookup identifier while Entra authentication remains the authorization gate.
[<Literal>]
let internal PrefixLength = 22

/// Publisher/viewer wire-contract key for the view-time-enforced expiry.
[<Literal>]
let internal ExpiryMetadataKey = "expiresOn"

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
/// uniqueness + unguessability; the real filename gives the recipient a meaningful page/tab title.
/// Pure given the prefix, so the naming shape is unit-testable.
let internal blobName (prefix: string) (filename: string) : string =
    $"{prefix}/{leafName filename}"

/// Builds the upload contract shared with the viewer: UTF-8 HTML plus an exact `expiresOn`
/// metadata value in UTC round-trip form.
let internal buildUploadOptions (expiresOn: DateTimeOffset) =
    let headers = BlobHttpHeaders(ContentType = "text/html; charset=utf-8")
    let metadata =
        dict [
            ExpiryMetadataKey,
            expiresOn.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
        ]
    BlobUploadOptions(HttpHeaders = headers, Metadata = metadata)

/// Constructs a recipient URL without consulting the Blob URI. The filename is encoded as one path
/// segment, and validated config guarantees the base has no query or fragment to carry through.
let internal buildViewerUrl (viewerBaseUrl: Uri) (prefix: string) (filename: string) =
    let encodedFilename = filename |> leafName |> Uri.EscapeDataString
    $"{viewerBaseUrl.AbsoluteUri.TrimEnd('/')}/c/{prefix}/{encodedFilename}"

let private credential = lazy (AzureCliCredential())

// The bearer-token cache belongs to the BlobServiceClient pipeline, so clients must survive across
// publishes. Lazy values prevent duplicate construction when concurrent first shares race.
let private serviceClients = ConcurrentDictionary<string, Lazy<BlobServiceClient>>()

let internal serviceClient (accountName: string) =
    let client =
        serviceClients.GetOrAdd(
            accountName,
            fun name ->
                lazy (BlobServiceClient(
                    Uri($"https://{name}.blob.core.windows.net"),
                    credential.Value)))
    client.Value

/// The client-facing "not configured" message names both required non-secret endpoints rather than
/// suggesting an application-managed key or connection string.
let internal notConfiguredMessage =
    "Canvas sharing is not configured — set canvasShare.accountName and an HTTPS canvasShare.viewerBaseUrl in ~/.treemon/config.json."

/// The message shown when the host has no usable Entra identity (e.g. `az login` has expired). This
/// is the one routine operational failure of the credential model, so it names the fix rather than
/// surfacing an SDK exception type.
let internal signInRequiredMessage =
    "Canvas sharing could not authenticate to Azure — run `az login` on this host and try again."

/// Publish an already-exported standalone HTML doc and return its clean viewer URL.
///
/// Uploads `html` to the configured pre-provisioned private container at
/// `<random-prefix>/<filename>`, with UTF-8 HTML headers and the exact expiry metadata the viewer
/// enforces. Returns `Error` (never throws) when either endpoint is unconfigured, the host has no
/// usable delegated identity, or storage fails. Returned errors and logs contain no recipient URL.
let publish (filename: string) (html: string) : Async<Result<string, string>> =
    asyncResult {
        let config = readCanvasShareConfig ()
        let! accountName = config.AccountName |> Result.requireSome notConfiguredMessage
        let! viewerBaseUrl = config.ViewerBaseUrl |> Result.requireSome notConfiguredMessage
        // The try/with stays around the Azure SDK calls (a genuine interop boundary); the
        // configuration gates above are flattened into the asyncResult track and run before any
        // credential acquisition or network call.
        try
            let serviceClient = serviceClient accountName
            let expiresOn = DateTimeOffset.UtcNow.AddDays(float config.DefaultExpiryDays)
            let containerClient = serviceClient.GetBlobContainerClient(config.Container)
            let prefix = generatePrefix ()
            let blob = blobName prefix filename
            let blobClient = containerClient.GetBlobClient(blob)
            use stream = new MemoryStream(Encoding.UTF8.GetBytes html)
            let! _ =
                blobClient.UploadAsync(stream, buildUploadOptions expiresOn)
                |> Async.AwaitTask
            return buildViewerUrl viewerBaseUrl prefix filename
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
