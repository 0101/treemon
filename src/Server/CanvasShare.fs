/// Publishes an already-exported, standalone canvas doc to Azure Blob Storage and mints a per-doc,
/// read-only SAS URL a recipient can open in a plain browser (no login). Deliberately independent of
/// BOTH the Shared API contract and CanvasExport: `publish` takes an already-exported HTML string
/// plus the doc's filename, so the caller (`WorktreeApi.shareCanvasDocImpl`) owns the export step and
/// the assembly of the `CanvasShareResult`. That keeps this module a thin, replaceable storage
/// adapter with only two dependencies: `Azure.Storage.Blobs` and `GlobalConfig`.
///
/// Secrecy model (docs/spec/canvas-sharing.md, Decisions #2/#4/#5): the container is PRIVATE
/// (anonymous access disabled at the account), the blob lands under an unguessable
/// `<random-prefix>/<filename>` name, and the returned link is a blob-scoped, read-only, https-only
/// SAS (`sr=b`, `sp=r`, `spr=https`) with a bounded expiry. Because the SAS is blob-scoped, a leaked
/// link exposes exactly one doc; per-doc revoke is a blob delete.
///
/// The Azure account key is read ONLY from the `AZURE_STORAGE_CONNECTION_STRING` env var and is never
/// logged; a malformed connection string is reported as "not configured" rather than echoed, and a
/// storage failure surfaces only its status/error-code, never the account key or the full SAS.
module Server.CanvasShare

open System
open System.IO
open System.Text
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Sas
open FsToolkit.ErrorHandling
open Server.GlobalConfig

// ── pure: blob naming ────────────────────────────────────────────────────────

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

// ── pure: SAS grant ──────────────────────────────────────────────────────────

/// Builds the per-doc SAS grant: blob-scoped (`sr=b`, `Resource = "b"`), read-only (`sp=r`),
/// https-only (`spr=https`), expiring at `expiresOn`. Pure — it holds NO key and touches no network
/// (the account key is applied later by `BlobClient.GenerateSasUri`), so the exact least-privilege
/// grant is unit-testable in isolation. Least privilege (Decision #2): a recipient of doc A's link
/// cannot read doc B because the signature is bound to A's blob.
let internal buildSasBuilder (containerName: string) (blob: string) (expiresOn: DateTimeOffset) : BlobSasBuilder =
    BlobSasBuilder(
        BlobSasPermissions.Read, expiresOn,
        BlobContainerName = containerName,
        BlobName = blob,
        Resource = "b",
        Protocol = SasProtocol.Https)

// ── impure: publish ──────────────────────────────────────────────────────────

/// The Azure credential secret. Read ONLY from the environment (never the JSON config), so the key
/// stays out of any file; blank/unset ⇒ `None` (⇒ "not configured"). Never logged.
let internal connectionString () : string option =
    Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
    |> Option.ofObj
    |> Option.filter (fun s -> not (String.IsNullOrWhiteSpace s))

/// The client-facing "not configured" message. Names the env var to set; contains no secret.
let internal notConfiguredMessage =
    "Canvas sharing is not configured — set AZURE_STORAGE_CONNECTION_STRING to an Azure Storage account connection string."

/// Construct the container client, swallowing a malformed-connection-string exception so the raw
/// string (which contains the account key) is NEVER surfaced or logged. A parse failure is reported
/// to the caller as the ordinary "not configured" outcome.
let private tryContainerClient (connStr: string) (container: string) : BlobContainerClient option =
    try Some(BlobContainerClient(connStr, container))
    with _ -> None

/// Publish an already-exported standalone HTML doc; return a per-doc read-only SAS URL string.
///
/// Uploads `html` to the PRIVATE container (created on first use if absent) at
/// `<random-prefix>/<filename>` with
/// `Content-Type: text/html`, then mints a blob-scoped read-only https SAS expiring in the config's
/// `DefaultExpiryDays` days and returns its absolute URL. Returns `Error` (never throws) when the
/// backend is unconfigured, when the connection string cannot sign a SAS (a non-account-key
/// credential — Decision #3), or on any storage failure. No returned message or log line contains
/// the account key or the full SAS.
let publish (filename: string) (html: string) : Async<Result<string, string>> =
    asyncResult {
        // A missing connection string ⇒ "not configured"; a malformed one is swallowed by
        // tryContainerClient into the same outcome, so the raw string (account key) is never surfaced.
        let! connStr = connectionString () |> Result.requireSome notConfiguredMessage
        let config = readCanvasShareConfig ()
        let! containerClient =
            tryContainerClient connStr config.Container
            |> Result.requireSome notConfiguredMessage
        // The try/with stays around the Azure SDK calls (a genuine interop boundary); the two
        // Option→Error gates above are flattened into the asyncResult track.
        try
            let blob = blobName (generatePrefix ()) filename
            let blobClient = containerClient.GetBlobClient(blob)
            // A SAS can only be signed from a shared-key (account-key) credential; a connection
            // string carrying only a SAS token can't. Fail clearly instead of throwing, and
            // before uploading so we don't leave an unreachable orphan blob.
            if not blobClient.CanGenerateSasUri then
                return! Error "Canvas sharing needs an account-key connection string so a read-only link can be signed."
            else
                // Create the PRIVATE container on demand (idempotent) so a fresh account/subscription
                // works on first publish — the SDK never auto-creates it, and a missing container
                // otherwise fails the upload with 404 ContainerNotFound. PublicAccessType.None keeps
                // anonymous access off at the container level (Decision #4). Placed AFTER the
                // CanGenerateSasUri gate so a SAS-only credential is still refused offline before any
                // I/O, and inside the existing try so a create failure (e.g. a key lacking create
                // permission) surfaces via the same RequestFailedException handler — no new error path.
                let! _ = containerClient.CreateIfNotExistsAsync(PublicAccessType.None) |> Async.AwaitTask
                // charset is declared so non-ASCII doc content isn't mojibaked when the blob is
                // opened standalone (the export injects no <meta charset>).
                let headers = BlobHttpHeaders(ContentType = "text/html; charset=utf-8")
                use stream = new MemoryStream(Encoding.UTF8.GetBytes html)
                let! _ =
                    blobClient.UploadAsync(stream, BlobUploadOptions(HttpHeaders = headers))
                    |> Async.AwaitTask
                // DefaultExpiryDays is clamped to [1, maxCanvasShareExpiryDays] by readCanvasShareConfig,
                // so AddDays can't overflow DateTimeOffset (year 9999) and orphan the blob just uploaded.
                let expiresOn = DateTimeOffset.UtcNow.AddDays(float config.DefaultExpiryDays)
                let sasUri = blobClient.GenerateSasUri(buildSasBuilder config.Container blob expiresOn)
                return sasUri.ToString()
        with
        | :? Azure.RequestFailedException as ex ->
            // Log/return the status + error code only (e.g. 404 ContainerNotFound) — a safe,
            // actionable token that carries no secret; the full message can echo request
            // details.
            Log.log "CanvasShare" $"Publish to container '{config.Container}' failed: HTTP {ex.Status} {ex.ErrorCode}"
            return! Error $"Failed to publish shared doc: {ex.ErrorCode} (HTTP {ex.Status})."
        | ex ->
            Log.log "CanvasShare" $"Publish to container '{config.Container}' failed: {ex.GetType().Name}"
            return! Error $"Failed to publish shared doc ({ex.GetType().Name})."
    }
