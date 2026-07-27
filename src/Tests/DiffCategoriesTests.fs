module Tests.DiffCategoriesTests

open System
open System.IO
open NUnit.Framework
open Server
open Server.DiffCategories
open Tests.TestUtils

let private configFileName = ".treemon.json"

let private withRepo (contents: string option) (action: string -> 'a) : 'a =
    withTempDir "treemon-diff-categories" (fun dir ->
        contents |> Option.iter (fun text -> File.WriteAllText(Path.Combine(dir, configFileName), text))
        action dir)

let private readFrom (json: string) : Configuration =
    withRepo (Some json) DiffCategories.read

/// Renders the validated tree as one line per leaf (`Parent > Child [pattern, pattern]`), so tests
/// assert on structure, names, and patterns without constructing nodes.
let rec private outlineNode node =
    match node with
    | Leaf leaf -> [ $"""{leaf.Name} [{String.Join(", ", leaf.Patterns)}]""" ]
    | Branch branch -> branch.Children |> List.collect outlineNode |> List.map (fun line -> $"{branch.Name} > {line}")

let private outlineOf configuration =
    match configuration with
    | Configured nodes -> nodes |> List.collect outlineNode
    | other -> failwith $"expected Configured but got {other}"

let private reasonOf configuration =
    match configuration with
    | Invalid reason -> reason
    | other -> failwith $"expected Invalid but got {other}"

let private assertInvalid (json: string) (expectedFragment: string) =
    let reason = readFrom json |> reasonOf
    Assert.That(reason, Does.Contain(expectedFragment).IgnoreCase, $"actual reason: {reason}")

let private leafJson (name: string) (patterns: string list) =
    let items = patterns |> List.map (fun pattern -> $"\"{pattern}\"") |> String.concat ", "
    $"""{{ "name": "{name}", "patterns": [{items}] }}"""

let private categoriesJson (nodes: string list) =
    $"""{{ "diffCategories": [{String.concat ", " nodes}] }}"""

/// A single chain of branches `depth` levels deep, ending in a leaf at depth 1.
let rec private nestedChain depth =
    if depth <= 1 then leafJson $"Level{depth}" [ "**" ]
    else $"""{{ "name": "Level{depth}", "children": [{nestedChain (depth - 1)}] }}"""


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ReadDiffCategoriesTests() =

    [<Test>]
    member _.``missing file resolves to Missing``() =
        Assert.That(withRepo None DiffCategories.read, Is.EqualTo(Missing))

    [<Test>]
    member _.``file without diffCategories key resolves to Missing``() =
        Assert.That(readFrom """{ "baseBranch": "dev" }""", Is.EqualTo(Missing))

    [<Test>]
    member _.``nested categories keep configuration order and structure``() =
        let outline =
            readFrom """
            {
              "diffCategories": [
                { "name": "Production code",
                  "children": [
                    { "name": "Client", "patterns": ["src/Client/**"] },
                    { "name": "Server", "patterns": ["src/Server/**"] }
                  ] },
                { "name": "Tests", "patterns": ["src/Tests/**", "**/*Tests.fs"] }
              ]
            }"""
            |> outlineOf

        Assert.That(
            outline,
            Is.EqualTo(
                [ "Production code > Client [src/Client/**]"
                  "Production code > Server [src/Server/**]"
                  "Tests [src/Tests/**, **/*Tests.fs]" ]))

    [<Test>]
    member _.``names are trimmed before they are stored``() =
        let outline = readFrom (categoriesJson [ leafJson "  Docs  " [ "docs/**" ] ]) |> outlineOf
        Assert.That(outline, Is.EqualTo([ "Docs [docs/**]" ]))

    [<Test>]
    member _.``patterns are stored verbatim, not trimmed``() =
        let outline = readFrom (categoriesJson [ leafJson "Docs" [ " docs/** " ] ]) |> outlineOf
        Assert.That(outline, Is.EqualTo([ "Docs [ docs/** ]" ]))

    [<Test>]
    member _.``same name under different parents is allowed``() =
        let outline =
            readFrom """
            {
              "diffCategories": [
                { "name": "Code", "children": [{ "name": "Api", "patterns": ["a/**"] }] },
                { "name": "Tests", "children": [{ "name": "Api", "patterns": ["b/**"] }] }
              ]
            }"""
            |> outlineOf

        Assert.That(outline, Is.EqualTo([ "Code > Api [a/**]"; "Tests > Api [b/**]" ]))

    [<Test>]
    member _.``Other is reserved only at the top level``() =
        let outline =
            readFrom """{ "diffCategories": [{ "name": "Code", "children": [{ "name": "Other", "patterns": ["a/**"] }] }] }"""
            |> outlineOf

        Assert.That(outline, Is.EqualTo([ "Code > Other [a/**]" ]))

    [<Test>]
    member _.``diffCategories coexists with unrelated config fields``() =
        withRepo
            (Some """
            {
              "archivedBranches": ["old"],
              "baseBranch": "dev",
              "diffCategories": [{ "name": "Docs", "patterns": ["docs/**"] }],
              "upstreamRemote": "upstream"
            }""")
            (fun dir ->
                Assert.That(DiffCategories.read dir |> outlineOf, Is.EqualTo([ "Docs [docs/**]" ]))
                Assert.That(TreemonConfig.readBaseBranch dir, Is.EqualTo("dev"))
                Assert.That(TreemonConfig.readUpstreamRemote dir, Is.EqualTo(Some "upstream"))
                Assert.That(TreemonConfig.readArchivedBranches dir, Is.EqualTo([ "old" ])))

    [<Test>]
    member _.``reading does not mutate the configuration file``() =
        let json = categoriesJson [ leafJson "Docs" [ "docs/**" ] ]

        withRepo (Some json) (fun dir ->
            let path = Path.Combine(dir, configFileName)
            let writtenAt = File.GetLastWriteTimeUtc(path)

            DiffCategories.read dir |> ignore

            Assert.That(File.ReadAllText(path), Is.EqualTo(json), "file contents must be untouched")
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(writtenAt), "file must not be rewritten"))

    [<Test>]
    member _.``invalid configuration does not mutate the configuration file``() =
        let json = """{ "diffCategories": [] }"""

        withRepo (Some json) (fun dir ->
            DiffCategories.read dir |> ignore
            Assert.That(File.ReadAllText(Path.Combine(dir, configFileName)), Is.EqualTo(json)))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DiffCategoriesValidationTests() =

    [<Test>]
    member _.``empty top-level array is invalid``() =
        assertInvalid """{ "diffCategories": [] }""" "non-empty array"

    [<Test>]
    member _.``non-array diffCategories is invalid``() =
        assertInvalid """{ "diffCategories": { "name": "Docs" } }""" "non-empty array"

    [<Test>]
    member _.``null diffCategories is invalid rather than missing``() =
        assertInvalid """{ "diffCategories": null }""" "non-empty array"

    [<Test>]
    member _.``non-object category is invalid``() =
        assertInvalid """{ "diffCategories": ["Docs"] }""" "must be an object"

    [<Test>]
    member _.``category without a name is invalid``() =
        assertInvalid """{ "diffCategories": [{ "patterns": ["docs/**"] }] }""" "needs a name"

    [<Test>]
    member _.``non-string name is invalid``() =
        assertInvalid """{ "diffCategories": [{ "name": 42, "patterns": ["docs/**"] }] }""" "needs a name"

    [<Test>]
    member _.``blank name is invalid``() =
        assertInvalid (categoriesJson [ leafJson "   " [ "docs/**" ] ]) "needs a name"

    [<Test>]
    member _.``category with both patterns and children is invalid``() =
        assertInvalid
            """{ "diffCategories": [{ "name": "Docs", "patterns": ["docs/**"], "children": [{ "name": "Api", "patterns": ["a/**"] }] }] }"""
            "not both"

    [<Test>]
    member _.``category with neither patterns nor children is invalid``() =
        assertInvalid """{ "diffCategories": [{ "name": "Docs" }] }""" "not both"

    [<Test>]
    member _.``empty patterns array is invalid``() =
        assertInvalid (categoriesJson [ leafJson "Docs" [] ]) "patterns must be a non-empty array"

    [<Test>]
    member _.``non-array patterns is invalid``() =
        assertInvalid """{ "diffCategories": [{ "name": "Docs", "patterns": "docs/**" }] }""" "patterns must be a non-empty array"

    [<Test>]
    member _.``non-string pattern is invalid``() =
        assertInvalid """{ "diffCategories": [{ "name": "Docs", "patterns": ["docs/**", 7] }] }""" "patterns must be a non-empty array"

    [<Test>]
    member _.``empty children array is invalid``() =
        assertInvalid """{ "diffCategories": [{ "name": "Code", "children": [] }] }""" "children must be a non-empty array"

    [<Test>]
    member _.``non-array children is invalid``() =
        assertInvalid """{ "diffCategories": [{ "name": "Code", "children": { "name": "Api" } }] }""" "children must be a non-empty array"

    [<Test>]
    member _.``duplicate sibling names are invalid``() =
        assertInvalid (categoriesJson [ leafJson "Docs" [ "a/**" ]; leafJson "Docs" [ "b/**" ] ]) "distinct names"

    [<Test>]
    member _.``sibling names differing only by case are invalid``() =
        assertInvalid (categoriesJson [ leafJson "Docs" [ "a/**" ]; leafJson "DOCS" [ "b/**" ] ]) "distinct names"

    [<Test>]
    member _.``sibling names duplicated only after trimming are invalid``() =
        assertInvalid (categoriesJson [ leafJson "Docs" [ "a/**" ]; leafJson "  docs " [ "b/**" ] ]) "distinct names"

    [<Test>]
    member _.``duplicate names among nested siblings are invalid``() =
        assertInvalid
            """{ "diffCategories": [{ "name": "Code", "children": [
                  { "name": "Api", "patterns": ["a/**"] },
                  { "name": "api", "patterns": ["b/**"] }] }] }"""
            "distinct names"

    [<Test>]
    member _.``top-level Other is reserved``() =
        assertInvalid (categoriesJson [ leafJson "Other" [ "a/**" ] ]) "reserved"

    [<Test>]
    member _.``top-level Other is reserved regardless of case and padding``() =
        assertInvalid (categoriesJson [ leafJson " oTHer " [ "a/**" ] ]) "reserved"

    [<Test>]
    member _.``four levels of nesting are allowed``() =
        let outline = readFrom $"""{{ "diffCategories": [{nestedChain 4}] }}""" |> outlineOf
        Assert.That(outline, Is.EqualTo([ "Level4 > Level3 > Level2 > Level1 [**]" ]), "root nodes are depth 1")

    [<Test>]
    member _.``five levels of nesting are invalid``() =
        assertInvalid $"""{{ "diffCategories": [{nestedChain 5}] }}""" "at most 4 levels"

    [<Test>]
    member _.``two hundred categories are allowed``() =
        let nodes = List.init 200 (fun i -> leafJson $"C{i}" [ "a/**" ])
        Assert.That(readFrom (categoriesJson nodes) |> outlineOf |> List.length, Is.EqualTo(200))

    [<Test>]
    member _.``more than two hundred categories are invalid``() =
        let nodes = List.init 201 (fun i -> leafJson $"C{i}" [ "a/**" ])
        assertInvalid (categoriesJson nodes) "at most 200 categories"

    [<Test>]
    member _.``the node bound counts branches and nested children``() =
        let children = List.init 200 (fun i -> leafJson $"C{i}" [ "a/**" ])
        let branch = $"""{{ "name": "Code", "children": [{String.concat ", " children}] }}"""
        assertInvalid (categoriesJson [ branch ]) "at most 200 categories"

    [<Test>]
    member _.``fifty patterns on one leaf are allowed``() =
        let patterns = List.init 50 (fun i -> $"src/{i}/**")
        Assert.That(readFrom (categoriesJson [ leafJson "Docs" patterns ]) |> outlineOf |> List.length, Is.EqualTo(1))

    [<Test>]
    member _.``more than fifty patterns on one leaf are invalid``() =
        let patterns = List.init 51 (fun i -> $"src/{i}/**")
        assertInvalid (categoriesJson [ leafJson "Docs" patterns ]) "at most 50 patterns"

    [<Test>]
    member _.``a pattern of two hundred code units is allowed``() =
        let pattern = String('a', 200)
        Assert.That(readFrom (categoriesJson [ leafJson "Docs" [ pattern ] ]) |> outlineOf, Is.EqualTo([ $"Docs [{pattern}]" ]))

    [<Test>]
    member _.``a longer pattern is invalid``() =
        assertInvalid (categoriesJson [ leafJson "Docs" [ String('a', 201) ] ]) "at most 200 characters"

    [<Test>]
    member _.``malformed JSON is invalid rather than missing``() =
        assertInvalid "{ this is not json" "could not be parsed"

    [<Test>]
    member _.``a JSON root that is not an object is invalid rather than missing``() =
        assertInvalid "[1, 2, 3]" "could not be parsed"

    [<Test>]
    member _.``the malformed-file reason leaks no path, JSON, or exception text``() =
        withRepo (Some """{ "diffCategories": [{"name": "S3cretName", "patterns": [ }""") (fun dir ->
            let reason = DiffCategories.read dir |> reasonOf

            Assert.That(reason, Does.Not.Contain(dir), "no filesystem path")
            Assert.That(reason, Does.Not.Contain("S3cretName"), "no raw configuration text")
            Assert.That(reason, Does.Not.Contain("LineNumber").IgnoreCase, "no exception text")
            Assert.That(reason.Length, Is.LessThan(120), "reasons stay short enough to render inline"))

    [<Test>]
    member _.``a validation reason leaks no repository-authored name``() =
        let reason = readFrom (categoriesJson [ leafJson "S3cretName" [] ]) |> reasonOf
        Assert.That(reason, Does.Not.Contain("S3cretName"))
