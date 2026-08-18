module Tests.CanvasShareTests

open System
open System.IO
open System.Text.RegularExpressions
open NUnit.Framework
open Server
open Server.CanvasShare
open Server.GlobalConfig
open Shared
open Tests.TestUtils

// Pure publisher contracts plus the fail-before-network configuration gate. The real Azure
// round-trip is covered by the deployment verification described in docs/spec/canvas-sharing.md.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ShareFilenameContractTests() =

    let validPrefix = "0123456789AbCdEfGhIjKl"

    [<TestCase("status.html")>]
    [<TestCase("Status.HTML")>]
    [<TestCase("release..notes.html")>]
    member _.``publisher and viewer accept the same valid filename``(filename: string) =
        Assert.Multiple(fun () ->
            Assert.That(
                validateFilename filename |> Result.isOk,
                Is.True,
                "publisher")
            Assert.That(
                CanvasShareViewer.SharePath.tryCreate
                    validPrefix
                    filename
                |> Option.isSome,
                Is.True,
                "viewer")
            Assert.That(
                blobName validPrefix filename,
                Is.EqualTo($"{validPrefix}/{filename}"),
                "exact Blob name"))

    [<TestCase("")>]
    [<TestCase("notes.txt")>]
    [<TestCase("folder/notes.html")>]
    [<TestCase(@"folder\notes.html")>]
    [<TestCase("../notes.html")>]
    member _.``publisher and viewer reject the same invalid filename``(filename: string) =
        let publisherError =
            match validateFilename filename with
            | Error error -> error
            | Ok() ->
                Assert.Fail($"Publisher accepted invalid filename '{filename}'.")
                ""

        Assert.Multiple(fun () ->
            Assert.That(
                publisherError,
                Is.EqualTo(InvalidFilenameMessage),
                "publisher")
            Assert.That(
                CanvasShareViewer.SharePath.tryCreate
                    validPrefix
                    filename
                |> Option.isNone,
                Is.True,
                "viewer"))

    [<TestCase("notes.txt")>]
    [<TestCase("folder/notes.html")>]
    [<TestCase(@"folder\notes.html")>]
    [<TestCase("../notes.html")>]
    member _.``share API rejects invalid filename before file or upload work``(filename: string) =
        withTempDir "canvas-share-filename" (fun worktreePath ->
            let request =
                { WorktreePath = WorktreePath worktreePath
                  Filename = filename }

            match WorktreeApi.shareCanvasDocImpl request |> runAsync with
            | Error error ->
                Assert.That(
                    error,
                    Is.EqualTo(InvalidFilenameMessage))
            | Ok result ->
                Assert.Fail(
                    $"Expected invalid filename Error before publishing, got Ok {result}"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BlobNamingTests() =

    [<Test>]
    member _.``blobName joins the prefix and filename with a slash``() =
        Assert.That(blobName "PREFIX123" "build-status.html", Is.EqualTo("PREFIX123/build-status.html"))

    [<Test>]
    member _.``blobName keeps the real filename so the recipient sees a meaningful title``() =
        Assert.That(blobName "abc" "weekly-sync.html", Does.EndWith("/weekly-sync.html"))

    [<Test>]
    member _.``blobName preserves filename casing and consecutive dots``() =
        Assert.That(
            blobName "P" "Release..Notes.HTML",
            Is.EqualTo("P/Release..Notes.HTML"))

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


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type UploadContractTests() =

    let expiresOn =
        DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(2.0))

    [<Test>]
    member _.``upload writes only expiresOn metadata in UTC round-trip form``() =
        let options = buildUploadOptions expiresOn

        Assert.Multiple(fun () ->
            Assert.That(options.Metadata.Count, Is.EqualTo(1))
            Assert.That(options.Metadata.ContainsKey(ExpiryMetadataKey), Is.True)
            Assert.That(ExpiryMetadataKey, Is.EqualTo("expiresOn"))
            Assert.That(
                options.Metadata[ExpiryMetadataKey],
                Is.EqualTo("2030-01-02T01:04:05.0000000+00:00")))

    [<Test>]
    member _.``upload declares UTF-8 HTML``() =
        Assert.That(
            (buildUploadOptions expiresOn).HttpHeaders.ContentType,
            Is.EqualTo("text/html; charset=utf-8"))

    [<Test>]
    member _.``publisher expiry metadata satisfies the viewer wire contract``() =
        let metadata =
            (buildUploadOptions expiresOn).Metadata
            |> Seq.map (fun pair -> pair.Key, pair.Value)
            |> Map.ofSeq
        let justBeforeExpiry =
            expiresOn.ToUniversalTime().AddTicks(-1L)

        Assert.Multiple(fun () ->
            Assert.That(
                ExpiryMetadataKey,
                Is.EqualTo(CanvasShareViewer.ShareExpiry.MetadataKey))
            Assert.That(
                CanvasShareViewer.ShareExpiry.isLive
                    justBeforeExpiry
                    metadata,
                Is.True))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ViewerUrlTests() =

    let prefix = "0123456789AbCdEfGhIjKl"

    [<TestCase("status.html")>]
    [<TestCase("Status.HTML")>]
    [<TestCase("release..notes.html")>]
    member _.``publisher naming satisfies the viewer wire contract``(filename: string) =
        Assert.Multiple(fun () ->
            Assert.That(
                PrefixLength,
                Is.EqualTo(CanvasShareViewer.SharePath.PrefixLength))
            Assert.That(
                CanvasShareViewer.SharePath.tryCreate
                    (generatePrefix ())
                    filename
                |> Option.isSome,
                Is.True))

    [<Test>]
    member _.``viewer URL uses the canonical deployed origin and clean c path``() =
        let url =
            buildViewerUrl
                (Uri("https://treemon.azurewebsites.net"))
                prefix
                "status.html"

        Assert.That(
            url,
            Is.EqualTo(
                $"https://treemon.azurewebsites.net/c/{prefix}/status.html"))

    [<Test>]
    member _.``viewer URL uses the configured HTTPS host``() =
        let url =
            buildViewerUrl
                (Uri("https://isolated-viewer.test:7443"))
                prefix
                "status.html"

        Assert.That(
            url,
            Is.EqualTo(
                $"https://isolated-viewer.test:7443/c/{prefix}/status.html"))

    [<Test>]
    member _.``viewer URL percent-encodes the filename as one path segment``() =
        let url =
            buildViewerUrl
                (Uri("https://viewer.test"))
                prefix
                "Q3 report #1.html"

        Assert.That(
            url,
            Is.EqualTo(
                "https://viewer.test/c/"
                + prefix
                + "/Q3%20report%20%231.html"))

    [<TestCase("Status.HTML")>]
    [<TestCase("release..notes.html")>]
    member _.``viewer URL preserves the exact compatible filename``(filename: string) =
        let url =
            buildViewerUrl
                (Uri("https://viewer.test"))
                prefix
                filename

        Assert.That(
            url,
            Is.EqualTo(
                $"https://viewer.test/c/{prefix}/{filename}"))

    [<Test>]
    member _.``viewer URL has no query fragment or Blob credential``() =
        let url =
            buildViewerUrl
                (Uri("https://viewer.test"))
                prefix
                "status.html"
        let uri = Uri url

        Assert.Multiple(fun () ->
            Assert.That(uri.Query, Is.Empty)
            Assert.That(uri.Fragment, Is.Empty)
            Assert.That(url, Does.Not.Contain("?"))
            Assert.That(url, Does.Not.Contain("sig="))
            Assert.That(url, Does.Not.Contain(".blob.core.windows.net")))

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
    member _.``readCanvasShareConfig reads every non-secret publisher setting``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir
                """{ "canvasShare": { "accountName": "tmcanvasabc", "container": "shared-docs", "defaultExpiryDays": 3, "viewerBaseUrl": "https://treemon.azurewebsites.net" } }"""
            let config = readCanvasShareConfig ()
            Assert.That(config.AccountName, Is.EqualTo(Some "tmcanvasabc"))
            Assert.That(config.Container, Is.EqualTo("shared-docs"))
            Assert.That(config.DefaultExpiryDays, Is.EqualTo(3))
            Assert.That(
                config.ViewerBaseUrl,
                Is.EqualTo(Some(Uri("https://treemon.azurewebsites.net")))))

    [<Test>]
    member _.``account and viewer URL have no defaults``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir """{ "canvasShare": { "container": "shared-docs" } }"""
            let config = readCanvasShareConfig ()
            Assert.That(config.AccountName, Is.EqualTo(None))
            Assert.That(config.ViewerBaseUrl, Is.EqualTo(None)))

    [<Test>]
    member _.``readCanvasShareConfig treats a blank accountName as absent``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir """{ "canvasShare": { "accountName": "   " } }"""
            Assert.That(readCanvasShareConfig().AccountName, Is.EqualTo(None),
                        "a whitespace-only account name must not be published to"))

    [<Test>]
    member _.``readCanvasShareConfig accepts a configurable HTTPS viewer URL``() =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir
                """{ "canvasShare": { "viewerBaseUrl": " https://isolated-viewer.test:7443/base/ " } }"""
            Assert.That(
                readCanvasShareConfig().ViewerBaseUrl,
                Is.EqualTo(Some(Uri("https://isolated-viewer.test:7443/base/")))))

    [<TestCase("")>]
    [<TestCase("   ")>]
    [<TestCase("http://viewer.test")>]
    [<TestCase("https:viewer.test")>]
    [<TestCase("viewer.test")>]
    [<TestCase("not a URL")>]
    member _.``blank malformed or non-HTTPS viewer URL is unconfigured``(value: string) =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir
                $"""{{ "canvasShare": {{ "viewerBaseUrl": "{value}" }} }}"""
            Assert.That(readCanvasShareConfig().ViewerBaseUrl, Is.EqualTo(None)))

    [<TestCase("https://viewer.test?credential=no")>]
    [<TestCase("https://viewer.test/#fragment")>]
    [<TestCase("https://credential@viewer.test")>]
    member _.``viewer base URL rejects credential query and fragment components``(value: string) =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir
                $"""{{ "canvasShare": {{ "viewerBaseUrl": "{value}" }} }}"""
            Assert.That(readCanvasShareConfig().ViewerBaseUrl, Is.EqualTo(None)))

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

    [<TestCase(1)>]
    [<TestCase(7)>]
    [<TestCase(30)>]
    member _.``readCanvasShareConfig accepts a bounded product lifetime``(days: int) =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir
                $"""{{ "canvasShare": {{ "defaultExpiryDays": {days} }} }}"""
            Assert.That(readCanvasShareConfig().DefaultExpiryDays, Is.EqualTo(days)))

    [<TestCase(0)>]
    [<TestCase(31)>]
    member _.``readCanvasShareConfig rejects a lifetime outside one through thirty``(days: int) =
        withTempConfigDir "canvas-share-config" (fun dir ->
            seed dir
                $"""{{ "canvasShare": {{ "defaultExpiryDays": {days} }} }}"""
            Assert.That(readCanvasShareConfig().DefaultExpiryDays,
                        Is.EqualTo(defaultCanvasShareConfig.DefaultExpiryDays)))

    [<Test>]
    member _.``the durable product expiry ceiling is thirty days``() =
        Assert.That(maxCanvasShareExpiryDays, Is.EqualTo(30))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
// readCanvasShareConfig reads config.json under TREEMON_CONFIG_DIR (a process-global env var), so
// keep this fixture non-parallel alongside the other config-touching fixtures.
[<NonParallelizable>]
type PublishConfigGateTests() =

    let seed (dir: string) (json: string) =
        File.WriteAllText(Path.Combine(dir, "config.json"), json)

    [<TestCase("notes.txt")>]
    [<TestCase("folder/notes.html")>]
    [<TestCase(@"folder\notes.html")>]
    [<TestCase("../notes.html")>]
    member _.``publish rejects an invalid filename before configuration or Azure``(filename: string) =
        withTempConfigDir "canvas-share-publish" (fun _ ->
            match runAsync (publish filename "<html></html>") with
            | Error msg ->
                Assert.That(msg, Is.EqualTo(InvalidFilenameMessage))
            | Ok url ->
                Assert.Fail(
                    $"expected invalid filename Error before publishing, got Ok {url}"))

    [<Test>]
    member _.``serviceClient reuses one Azure authentication pipeline per account``() =
        let first = serviceClient "tmcanvasabc"
        Assert.That(serviceClient "tmcanvasabc", Is.SameAs(first))
        Assert.That(serviceClient "tmcanvasxyz", Is.Not.SameAs(first))

    [<Test>]
    member _.``publish fails before network when account name is missing``() =
        withTempConfigDir "canvas-share-publish" (fun dir ->
            seed dir
                """{ "canvasShare": { "viewerBaseUrl": "https://isolated-viewer.test" } }"""
            match runAsync (publish "doc.html" "<html></html>") with
            | Error msg ->
                Assert.That(msg, Is.EqualTo(notConfiguredMessage))
                Assert.That(msg, Does.Contain("canvasShare.accountName"),
                            "the error must tell the operator which config key to set")
            | Ok url -> Assert.Fail($"expected Error when unconfigured, got Ok {url}"))

    [<Test>]
    member _.``publish fails before network when viewer URL is missing``() =
        withTempConfigDir "canvas-share-publish" (fun dir ->
            seed dir
                """{ "canvasShare": { "accountName": "network-must-not-be-contacted" } }"""
            match runAsync (publish "doc.html" "<html></html>") with
            | Error msg ->
                Assert.That(msg, Is.EqualTo(notConfiguredMessage))
                Assert.That(msg, Does.Contain("canvasShare.viewerBaseUrl"),
                            "the error must tell the operator which config key to set")
            | Ok url -> Assert.Fail($"expected Error when unconfigured, got Ok {url}"))

    [<Test>]
    member _.``the not-configured message names no application-managed storage credential``() =
        Assert.That(notConfiguredMessage, Does.Not.Contain("AZURE_STORAGE_CONNECTION_STRING"))
        Assert.That(notConfiguredMessage.ToLowerInvariant(), Does.Not.Contain("key"))

    [<Test>]
    member _.``the sign-in message points at az login``() =
        // An expired host identity is the one routine failure of the credential model, so the
        // message must name the fix rather than leak an SDK exception type.
        Assert.That(signInRequiredMessage, Does.Contain("az login"))
