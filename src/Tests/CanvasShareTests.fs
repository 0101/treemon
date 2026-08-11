module Tests.CanvasShareTests

open System
open System.IO
open System.Text.RegularExpressions
open NUnit.Framework
open Azure.Storage.Blobs.Models
open Azure.Storage.Sas
open Server
open Server.CanvasShare
open Server.GlobalConfig
open Tests.TestUtils

// This suite covers only the PURE and deterministic parts of the publish backend (spec
// docs/spec/canvas-sharing.md): blob naming, user-delegation signing, service-client reuse and the
// config reader — plus the unconfigured gate, which fails before any credential or network use.
// The Azure round-trip cannot be emulated (Azurite does not implement GetUserDelegationKey), so it is
// verified against the real account instead — see the spec's "Verification" section.

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

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type UserDelegationSigningTests() =

    let startsOn = DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let expiresOn = startsOn.AddDays(7.0)
    let delegationKey =
        BlobsModelFactory.UserDelegationKey(
            "object-id",
            "tenant-id",
            startsOn,
            expiresOn,
            "b",
            "2025-11-05",
            Convert.ToBase64String(Array.create 32 42uy))

    [<Test>]
    member _.``buildSignedBlobUrl applies the delegation identity and blob-scoped grant``() =
        let blobUri = Uri("https://tmcanvasabc.blob.core.windows.net/canvas-shared/prefix/doc.html")
        let signedUrl =
            buildSignedBlobUrl
                blobUri
                "tmcanvasabc"
                delegationKey
                (buildSasBuilder "canvas-shared" "prefix/doc.html" expiresOn)
        let signedUri = Uri signedUrl

        Assert.Multiple(fun () ->
            Assert.That(signedUri.GetLeftPart(UriPartial.Path), Is.EqualTo(blobUri.AbsoluteUri))
            Assert.That(signedUri.Query, Does.Contain("skoid=object-id"))
            Assert.That(signedUri.Query, Does.Contain("sktid=tenant-id"))
            Assert.That(signedUri.Query, Does.Contain("sks=b"))
            Assert.That(signedUri.Query, Does.Contain("sp=r"))
            Assert.That(signedUri.Query, Does.Contain("spr=https"))
            Assert.That(signedUri.Query, Does.Contain("sr=b")))


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
    member _.``readCanvasShareConfig reads accountName, container and defaultExpiryDays``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir """{ "canvasShare": { "accountName": "tmcanvasabc", "container": "shared-docs", "defaultExpiryDays": 3 } }"""
            let config = readCanvasShareConfig ()
            Assert.That(config.AccountName, Is.EqualTo(Some "tmcanvasabc"))
            Assert.That(config.Container, Is.EqualTo("shared-docs"))
            Assert.That(config.DefaultExpiryDays, Is.EqualTo(3)))

    [<Test>]
    member _.``readCanvasShareConfig has no accountName by default — that is what unconfigured means``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir """{ "canvasShare": { "container": "shared-docs" } }"""
            Assert.That(readCanvasShareConfig().AccountName, Is.EqualTo(None)))

    [<Test>]
    member _.``readCanvasShareConfig treats a blank accountName as absent``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir """{ "canvasShare": { "accountName": "   " } }"""
            Assert.That(readCanvasShareConfig().AccountName, Is.EqualTo(None),
                        "a whitespace-only account name must not be published to"))

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
    member _.``readCanvasShareConfig ignores an expiry beyond the user-delegation-key limit``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            // A user delegation key lives at most 7 days; Azure refuses a longer window outright when
            // the key is minted, so an over-long config value must fall back rather than fail at publish.
            seed dir """{ "canvasShare": { "defaultExpiryDays": 30 } }"""
            Assert.That(readCanvasShareConfig().DefaultExpiryDays,
                        Is.EqualTo(defaultCanvasShareConfig.DefaultExpiryDays)))

    [<Test>]
    member _.``readCanvasShareConfig accepts the maximum bounded expiry``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir """{ "canvasShare": { "defaultExpiryDays": 7 } }"""
            Assert.That(readCanvasShareConfig().DefaultExpiryDays, Is.EqualTo(maxCanvasShareExpiryDays)))

    [<Test>]
    member _.``the expiry ceiling is Azure's 7-day user-delegation-key limit``() =
        // Pinned deliberately: raising this constant would mint links Azure refuses to sign.
        Assert.That(maxCanvasShareExpiryDays, Is.EqualTo(7))


// ── unconfigured / credential gate ─────────────────────────────────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
// readCanvasShareConfig reads config.json under TREEMON_CONFIG_DIR (a process-global env var), so
// keep this fixture non-parallel alongside the other config-touching fixtures.
[<NonParallelizable>]
type PublishConfigGateTests() =

    [<Test>]
    member _.``serviceClient reuses one Azure authentication pipeline per account``() =
        let first = serviceClient "tmcanvasabc"
        Assert.That(serviceClient "tmcanvasabc", Is.SameAs(first))
        Assert.That(serviceClient "tmcanvasxyz", Is.Not.SameAs(first))

    [<Test>]
    member _.``publish returns the not-configured error when no account name is set``() =
        // Without an account name there is nothing to publish to, so publish must fail closed
        // BEFORE acquiring a credential or touching the network.
        withTempConfigDir "canvas-share-publish" (fun _ ->
            match runAsync (publish "doc.html" "<html></html>") with
            | Error msg ->
                Assert.That(msg, Is.EqualTo(notConfiguredMessage))
                Assert.That(msg, Does.Contain("canvasShare.accountName"),
                            "the error must tell the operator which config key to set")
            | Ok url -> Assert.Fail($"expected Error when unconfigured, got Ok {url}"))

    [<Test>]
    member _.``the not-configured message names no application-managed storage credential``() =
        // Treemon stores no account key or connection string, so the message must not send an
        // operator hunting for one.
        Assert.That(notConfiguredMessage, Does.Not.Contain("AZURE_STORAGE_CONNECTION_STRING"))
        Assert.That(notConfiguredMessage.ToLowerInvariant(), Does.Not.Contain("key"))

    [<Test>]
    member _.``the sign-in message points at az login``() =
        // An expired host identity is the one routine failure of the credential model, so the
        // message must name the fix rather than leak an SDK exception type.
        Assert.That(signInRequiredMessage, Does.Contain("az login"))
