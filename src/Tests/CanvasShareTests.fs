module Tests.CanvasShareTests

open System
open System.IO
open System.Text.RegularExpressions
open NUnit.Framework
open Azure.Storage.Sas
open Server
open Server.CanvasShare
open Server.GlobalConfig
open Tests.TestUtils

// This suite covers only the PURE parts of the publish backend (spec docs/spec/canvas-sharing.md):
// blob naming, the SAS grant parameters, and the config reader — plus the unconfigured gate, which
// is deterministic and network-free. The Azure round-trip (upload → SAS fetch → 409/403/404) is an
// Azurite integration test owned by a separate verification task, not a unit test.

// ── blob naming (pure) ────────────────────────────────────────────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BlobNamingTests() =

    [<Test>]
    member _.``blobName joins the prefix and filename with a slash``() =
        Assert.That(blobName "PREFIX123" "build-status.html", Is.EqualTo("PREFIX123/build-status.html"))

    [<Test>]
    member _.``blobName keeps the real filename so the recipient sees a meaningful title``() =
        // Decision #5: the real filename is preserved (not hashed) after the unguessable prefix.
        Assert.That(blobName "abc" "weekly-sync.html", Does.EndWith("/weekly-sync.html"))

    [<Test>]
    member _.``blobName uses only the leaf so a nested path cannot create nested blobs``() =
        Assert.That(blobName "P" "sub/dir/x.html", Is.EqualTo("P/x.html"))

    [<Test>]
    member _.``leafName strips a forward-slash directory``() =
        Assert.That(leafName "a/b/c.html", Is.EqualTo("c.html"))

    [<Test>]
    member _.``leafName strips a backslash directory``() =
        Assert.That(leafName @"a\b\c.html", Is.EqualTo("c.html"))

    [<Test>]
    member _.``leafName leaves a bare filename untouched``() =
        Assert.That(leafName "build-status.html", Is.EqualTo("build-status.html"))

    [<Test>]
    member _.``generatePrefix is PrefixLength base62 characters``() =
        let prefix = generatePrefix ()
        Assert.That(prefix.Length, Is.EqualTo(PrefixLength),
                    "the prefix must be the fixed high-entropy length")
        Assert.That(Regex.IsMatch(prefix, "^[0-9A-Za-z]+$"), Is.True,
                    "the prefix must be base62 (digits + letters), URL-safe with no separators")

    [<Test>]
    member _.``generatePrefix is unguessable — successive prefixes differ``() =
        // With ~131 bits of entropy a collision is astronomically unlikely; a repeat here means the
        // RNG is not being sampled (e.g. a constant seed).
        let prefixes = List.init 100 (fun _ -> generatePrefix ())
        Assert.That(List.distinct prefixes |> List.length, Is.EqualTo(100),
                    "every minted prefix must be distinct")


// ── SAS grant parameters (pure) ───────────────────────────────────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SasBuilderTests() =

    let expiresOn = DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let build () = buildSasBuilder "canvas-shared" "prefix/doc.html" expiresOn

    [<Test>]
    member _.``buildSasBuilder scopes the grant to a single blob (sr=b)``() =
        // Blob-scoped is the crux of least privilege (Decision #2): doc A's link can't read doc B.
        Assert.That(build().Resource, Is.EqualTo("b"))

    [<Test>]
    member _.``buildSasBuilder grants read-only permission (sp=r)``() =
        Assert.That(build().Permissions, Is.EqualTo("r"),
                    "a shared link must be read-only — no write/delete/list")

    [<Test>]
    member _.``buildSasBuilder restricts the link to https (spr=https)``() =
        Assert.That(build().Protocol, Is.EqualTo(SasProtocol.Https))

    [<Test>]
    member _.``buildSasBuilder carries the requested expiry``() =
        Assert.That(build().ExpiresOn, Is.EqualTo(expiresOn))

    [<Test>]
    member _.``buildSasBuilder binds the container and blob name``() =
        let b = buildSasBuilder "my-container" "abc/report.html" expiresOn
        Assert.That(b.BlobContainerName, Is.EqualTo("my-container"))
        Assert.That(b.BlobName, Is.EqualTo("abc/report.html"))


// ── config reader (touches TREEMON_CONFIG_DIR: non-parallel) ───────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
// readCanvasShareConfig reads config.json under TREEMON_CONFIG_DIR, which withTempConfigDir points
// at a throwaway dir via a process-global env var — so keep this fixture non-parallel.
[<NonParallelizable>]
type CanvasShareConfigTests() =

    let seed (dir: string) (json: string) = File.WriteAllText(Path.Combine(dir, "config.json"), json)

    [<Test>]
    member _.``readCanvasShareConfig returns defaults when the file is absent``() =
        withTempConfigDir "canvas-share-config" (fun _ ->
            Assert.That(readCanvasShareConfig (), Is.EqualTo(defaultCanvasShareConfig)))

    [<Test>]
    member _.``readCanvasShareConfig returns defaults when the section is absent``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir """{ "editor": "vim" }"""
            Assert.That(readCanvasShareConfig (), Is.EqualTo(defaultCanvasShareConfig)))

    [<Test>]
    member _.``readCanvasShareConfig reads container and defaultExpiryDays``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir """{ "canvasShare": { "container": "shared-docs", "defaultExpiryDays": 30 } }"""
            let config = readCanvasShareConfig ()
            Assert.That(config.Container, Is.EqualTo("shared-docs"))
            Assert.That(config.DefaultExpiryDays, Is.EqualTo(30)))

    [<Test>]
    member _.``readCanvasShareConfig defaults the expiry when only the container is set``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir """{ "canvasShare": { "container": "shared-docs" } }"""
            let config = readCanvasShareConfig ()
            Assert.That(config.Container, Is.EqualTo("shared-docs"))
            Assert.That(config.DefaultExpiryDays, Is.EqualTo(defaultCanvasShareConfig.DefaultExpiryDays)))

    [<Test>]
    member _.``readCanvasShareConfig defaults the container when it is blank``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir """{ "canvasShare": { "container": "   " } }"""
            Assert.That(readCanvasShareConfig().Container, Is.EqualTo(defaultCanvasShareConfig.Container)))

    [<Test>]
    member _.``readCanvasShareConfig ignores a non-positive expiry (would mint a dead link)``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir """{ "canvasShare": { "defaultExpiryDays": 0 } }"""
            Assert.That(readCanvasShareConfig().DefaultExpiryDays,
                        Is.EqualTo(defaultCanvasShareConfig.DefaultExpiryDays)))

    [<Test>]
    member _.``readCanvasShareConfig ignores an out-of-range expiry (would overflow AddDays at publish)``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            // Int32.MaxValue days overflows DateTimeOffset.AddDays; clamp back to the default instead.
            seed dir """{ "canvasShare": { "defaultExpiryDays": 2147483647 } }"""
            Assert.That(readCanvasShareConfig().DefaultExpiryDays,
                        Is.EqualTo(defaultCanvasShareConfig.DefaultExpiryDays)))

    [<Test>]
    member _.``readCanvasShareConfig accepts the maximum bounded expiry``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir """{ "canvasShare": { "defaultExpiryDays": 3650 } }"""
            Assert.That(readCanvasShareConfig().DefaultExpiryDays, Is.EqualTo(maxCanvasShareExpiryDays)))


// ── unconfigured / secret handling (touches env var: non-parallel) ─────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
// These mutate the process-global AZURE_STORAGE_CONNECTION_STRING env var, so keep them non-parallel
// and always restore the original value.
[<NonParallelizable>]
type PublishConfigGateTests() =

    let key = "AZURE_STORAGE_CONNECTION_STRING"

    /// Run `action` with the connection-string env var set to `value` (None ⇒ unset), restoring the
    /// original afterwards so a developer's real env is never left mutated.
    let withConnectionString (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(key)
        try
            Environment.SetEnvironmentVariable(key, Option.toObj value)
            action ()
        finally
            Environment.SetEnvironmentVariable(key, original)

    [<Test>]
    member _.``connectionString is None when the env var is unset``() =
        withConnectionString None (fun () ->
            Assert.That(Option.isNone (connectionString ()), Is.True))

    [<Test>]
    member _.``connectionString is None when the env var is blank``() =
        withConnectionString (Some "   ") (fun () ->
            Assert.That(Option.isNone (connectionString ()), Is.True,
                        "a whitespace-only value is not a real credential"))

    [<Test>]
    member _.``connectionString is Some when the env var is set``() =
        withConnectionString (Some "UseDevelopmentStorage=true") (fun () ->
            Assert.That(connectionString (), Is.EqualTo(Some "UseDevelopmentStorage=true")))

    [<Test>]
    member _.``publish returns the not-configured error when there is no connection string``() =
        withConnectionString None (fun () ->
            match runAsync (publish "doc.html" "<html></html>") with
            | Error msg ->
                Assert.That(msg, Is.EqualTo(notConfiguredMessage))
                Assert.That(msg, Does.Contain("AZURE_STORAGE_CONNECTION_STRING"),
                            "the error must tell the operator which env var to set")
            | Ok url -> Assert.Fail($"expected Error when unconfigured, got Ok {url}"))

    [<Test>]
    member _.``publish refuses a SAS-only connection string that cannot sign a SAS``() =
        // Decision #3: minting the (default 90-day) SAS requires an account-key credential. A
        // connection string carrying only a SAS token (no AccountKey) constructs a client but
        // CanGenerateSasUri is false, so publish must refuse it up front — offline, before any I/O.
        let sasOnly =
            "BlobEndpoint=https://devstoreaccount1.blob.core.windows.net;"
            + "SharedAccessSignature=sv=2022-11-02&ss=b&srt=o&sp=r&se=2030-01-01T00:00:00Z&sig=Zm9vYmFy"
        withTempConfigDir "canvas-share-publish" (fun _ ->
            withConnectionString (Some sasOnly) (fun () ->
                match runAsync (publish "doc.html" "<html></html>") with
                | Error msg -> Assert.That(msg, Does.Contain("account-key"),
                                           "a non-account-key credential must be refused with a clear message")
                | Ok url -> Assert.Fail($"expected Error for a SAS-only credential, got Ok {url}")))

    [<Test>]
    member _.``publish reports rather than echoes a malformed connection string``() =
        // A malformed connection string contains (or IS) the account key. Publish must fail with a
        // generic error and must NOT echo the string — proven with a sentinel that must be absent.
        let sentinel = "SENTINELKEYdoNotLeak"
        withTempConfigDir "canvas-share-publish" (fun _ ->
            withConnectionString (Some $"{sentinel}-not-a-real-connection-string") (fun () ->
                match runAsync (publish "doc.html" "<html></html>") with
                | Error msg -> Assert.That(msg, Does.Not.Contain(sentinel),
                                           "a failure must never echo the connection string / account key")
                | Ok url -> Assert.Fail($"expected Error for a malformed connection string, got Ok {url}")))
