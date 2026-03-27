module Tests.SessionPersistenceTests

open System
open System.IO
open System.Text.Json
open NUnit.Framework
open Server.SessionManager
open Shared.PathUtils

let private emptySessionMap : Map<string, nativeint> = Map.empty

let private withTempDir (action: string -> unit) =
    let tempDir = Path.Combine(Path.GetTempPath(), $"treemon-test-{Guid.NewGuid()}")
    Directory.CreateDirectory(tempDir) |> ignore
    let originalDir = Environment.CurrentDirectory
    Environment.CurrentDirectory <- tempDir

    try
        action tempDir
    finally
        Environment.CurrentDirectory <- originalDir

        try
            Directory.Delete(tempDir, recursive = true)
        with _ ->
            ()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PersistSessionsTests() =

    [<Test>]
    member _.``persistSessions creates data directory if missing``() =
        withTempDir (fun tempDir ->
            let dataDir = Path.Combine(tempDir, "data")
            Assert.That(Directory.Exists(dataDir), Is.False, "data/ should not exist before persist")

            persistSessions Map.empty |> Async.RunSynchronously

            Assert.That(Directory.Exists(dataDir), Is.True, "data/ should be created by persistSessions"))

    [<Test>]
    member _.``persistSessions writes valid JSON with sessions key``() =
        withTempDir (fun _ ->
            let sessions =
                [ @"Q:\code\repo-main", nativeint 12345
                  @"Q:\code\repo-feat", nativeint 67890 ]
                |> Map.ofList

            persistSessions sessions |> Async.RunSynchronously

            let filePath = Path.Combine("data", "sessions.json")
            Assert.That(File.Exists(filePath), Is.True, "sessions.json should exist after persist")

            let json = File.ReadAllText(filePath)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Object), "Root should be an object")

            let hasSessions, sessionsElement = root.TryGetProperty("sessions")
            Assert.That(hasSessions, Is.True, "Root should have 'sessions' property")
            Assert.That(sessionsElement.ValueKind, Is.EqualTo(JsonValueKind.Object), "sessions should be an object"))

    [<Test>]
    member _.``persistSessions writes correct keys and integer values``() =
        withTempDir (fun _ ->
            let sessions =
                [ @"Q:\code\repo-main", nativeint 12345
                  @"Q:\code\repo-feat", nativeint 67890 ]
                |> Map.ofList

            persistSessions sessions |> Async.RunSynchronously

            let json = File.ReadAllText(Path.Combine("data", "sessions.json"))
            use doc = JsonDocument.Parse(json)
            let sessionsElement = doc.RootElement.GetProperty("sessions")

            let entries =
                sessionsElement.EnumerateObject()
                |> Seq.map (fun p -> p.Name, p.Value.GetInt64())
                |> Map.ofSeq

            Assert.That(entries.Count, Is.EqualTo(2))
            Assert.That(entries.ContainsKey(@"Q:\code\repo-main"), Is.True)
            Assert.That(entries.ContainsKey(@"Q:\code\repo-feat"), Is.True)
            Assert.That(entries[@"Q:\code\repo-main"], Is.EqualTo(12345L))
            Assert.That(entries[@"Q:\code\repo-feat"], Is.EqualTo(67890L)))

    [<Test>]
    member _.``persistSessions writes empty sessions object for empty map``() =
        withTempDir (fun _ ->
            persistSessions Map.empty |> Async.RunSynchronously

            let json = File.ReadAllText(Path.Combine("data", "sessions.json"))
            use doc = JsonDocument.Parse(json)
            let sessionsElement = doc.RootElement.GetProperty("sessions")

            let count = sessionsElement.EnumerateObject() |> Seq.length
            Assert.That(count, Is.EqualTo(0)))

    [<Test>]
    member _.``persistSessions overwrites previous file``() =
        withTempDir (fun _ ->
            let first = [ @"Q:\code\old", nativeint 111 ] |> Map.ofList
            persistSessions first |> Async.RunSynchronously

            let second = [ @"Q:\code\new", nativeint 222 ] |> Map.ofList
            persistSessions second |> Async.RunSynchronously

            let json = File.ReadAllText(Path.Combine("data", "sessions.json"))
            use doc = JsonDocument.Parse(json)
            let sessionsElement = doc.RootElement.GetProperty("sessions")

            let entries =
                sessionsElement.EnumerateObject()
                |> Seq.map _.Name
                |> Seq.toList

            Assert.That(entries, Is.EqualTo([ @"Q:\code\new" ]),
                "File should contain only the second write's data"))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LoadSessionsTests() =

    [<Test>]
    member _.``loadSessions returns empty map when file does not exist``() =
        withTempDir (fun _ ->
            let result = loadSessions ()
            Assert.That(result, Is.EqualTo(emptySessionMap)))

    [<Test>]
    member _.``loadSessions returns empty map for corrupt JSON``() =
        withTempDir (fun _ ->
            Directory.CreateDirectory("data") |> ignore
            File.WriteAllText(Path.Combine("data", "sessions.json"), "not valid json {{{{")

            let result = loadSessions ()
            Assert.That(result, Is.EqualTo(emptySessionMap)))

    [<Test>]
    member _.``loadSessions returns empty map for JSON missing sessions key``() =
        withTempDir (fun _ ->
            Directory.CreateDirectory("data") |> ignore
            File.WriteAllText(Path.Combine("data", "sessions.json"), """{"other": 42}""")

            let result = loadSessions ()
            Assert.That(result, Is.EqualTo(emptySessionMap)))

    [<Test>]
    member _.``loadSessions returns empty map for empty JSON object``() =
        withTempDir (fun _ ->
            Directory.CreateDirectory("data") |> ignore
            File.WriteAllText(Path.Combine("data", "sessions.json"), "{}")

            let result = loadSessions ()
            Assert.That(result, Is.EqualTo(emptySessionMap)))

    [<Test>]
    member _.``loadSessions returns empty map for empty file``() =
        withTempDir (fun _ ->
            Directory.CreateDirectory("data") |> ignore
            File.WriteAllText(Path.Combine("data", "sessions.json"), "")

            let result = loadSessions ()
            Assert.That(result, Is.EqualTo(emptySessionMap)))

    [<Test>]
    member _.``loadSessions filters out invalid HWNDs from persisted data``() =
        withTempDir (fun _ ->
            let sessions =
                [ @"Q:\code\repo-main", nativeint 99999999
                  @"Q:\code\repo-feat", nativeint 88888888 ]
                |> Map.ofList

            persistSessions sessions |> Async.RunSynchronously

            let result = loadSessions ()
            Assert.That(result, Is.EqualTo(emptySessionMap),
                "Fake HWNDs should be filtered out by IsWindow validation"))


[<TestFixture>]
[<Category("Local")>]
type RoundTripTests() =

    [<Test>]
    member _.``persistSessions then loadSessions with real HWND preserves valid entry``() =
        withTempDir (fun _ ->
            let realWindows = Server.Win32.listTopLevelWindows ()

            if realWindows.IsEmpty then
                Assert.Ignore("No windows available for round-trip test with real HWND")

            let realHwnd = realWindows |> List.head
            let sessions = [ @"Q:\code\test-worktree", realHwnd ] |> Map.ofList

            persistSessions sessions |> Async.RunSynchronously
            let loaded = loadSessions ()

            Assert.That(loaded.Count, Is.EqualTo(1), "One valid HWND should survive round-trip")
            let normalizedKey = normalizePath @"Q:\code\test-worktree"
            Assert.That(loaded.ContainsKey(normalizedKey), Is.True)
            Assert.That(loaded[normalizedKey], Is.EqualTo(realHwnd)))

    [<Test>]
    member _.``round-trip preserves multiple valid entries``() =
        withTempDir (fun _ ->
            let realWindows = Server.Win32.listTopLevelWindows ()

            if realWindows.Length < 2 then
                Assert.Ignore("Need at least 2 windows for multi-entry round-trip test")

            let hwnd1 = realWindows[0]
            let hwnd2 = realWindows[1]

            let sessions =
                [ @"Q:\code\repo-a", hwnd1
                  @"Q:\code\repo-b", hwnd2 ]
                |> Map.ofList

            persistSessions sessions |> Async.RunSynchronously
            let loaded = loadSessions ()

            Assert.That(loaded.Count, Is.EqualTo(2))
            let keyA = normalizePath @"Q:\code\repo-a"
            let keyB = normalizePath @"Q:\code\repo-b"
            Assert.That(loaded[keyA], Is.EqualTo(hwnd1))
            Assert.That(loaded[keyB], Is.EqualTo(hwnd2)))

    [<Test>]
    member _.``round-trip filters invalid entries but keeps valid ones``() =
        withTempDir (fun _ ->
            let realWindows = Server.Win32.listTopLevelWindows ()

            if realWindows.IsEmpty then
                Assert.Ignore("No windows available for mixed round-trip test")

            let realHwnd = realWindows |> List.head
            let fakeHwnd = nativeint 99999999

            let sessions =
                [ @"Q:\code\valid", realHwnd
                  @"Q:\code\invalid", fakeHwnd ]
                |> Map.ofList

            persistSessions sessions |> Async.RunSynchronously
            let loaded = loadSessions ()

            Assert.That(loaded.Count, Is.EqualTo(1), "Only the valid HWND should survive")
            let normalizedValid = normalizePath @"Q:\code\valid"
            let normalizedInvalid = normalizePath @"Q:\code\invalid"
            Assert.That(loaded.ContainsKey(normalizedValid), Is.True)
            Assert.That(loaded.ContainsKey(normalizedInvalid), Is.False))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type JsonFormatTests() =

    [<Test>]
    member _.``JSON matches spec format - object with sessions key containing string-to-integer mapping``() =
        withTempDir (fun _ ->
            let sessions =
                [ @"Q:\code\AITestAgent-wt-abc", nativeint 12345678
                  @"Q:\code\AITestAgent-wt-def", nativeint 87654321 ]
                |> Map.ofList

            persistSessions sessions |> Async.RunSynchronously

            let json = File.ReadAllText(Path.Combine("data", "sessions.json"))
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            let rootProperties =
                root.EnumerateObject()
                |> Seq.map _.Name
                |> Seq.toList

            Assert.That(rootProperties, Is.EqualTo([ "sessions" ]),
                "Root object should have exactly one property 'sessions'")

            let sessionsElement = root.GetProperty("sessions")

            sessionsElement.EnumerateObject()
            |> Seq.iter (fun prop ->
                Assert.That(prop.Name, Is.TypeOf<string>(), "Keys should be strings")
                Assert.That(prop.Value.ValueKind, Is.EqualTo(JsonValueKind.Number),
                    $"Value for key '{prop.Name}' should be a number")))

    [<Test>]
    member _.``JSON values are integers not floats``() =
        withTempDir (fun _ ->
            let sessions = [ @"Q:\code\test", nativeint 42 ] |> Map.ofList
            persistSessions sessions |> Async.RunSynchronously

            let json = File.ReadAllText(Path.Combine("data", "sessions.json"))
            Assert.That(json.Contains("42"), Is.True, "HWND should appear as integer 42")
            Assert.That(json.Contains("42."), Is.False, "HWND should not appear as float"))
