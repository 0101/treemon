/// Repository-declared categorization of changed files: the validated model behind `diffCategories`
/// in `.treemon.json`. Matching and classification build on this model.
module Server.DiffCategories

open System
open System.Text.Json
open FsToolkit.ErrorHandling

/// A validated category: a `Leaf` selects files with patterns, a `Branch` nests further categories.
/// Record payloads rather than tuple cases, so compiled patterns can be added to a leaf without
/// rewriting every pattern match against the tree.
type CategoryNode =
    | Leaf of CategoryLeaf
    | Branch of CategoryBranch

and CategoryLeaf = { Name: string; Patterns: string list }

and CategoryBranch = { Name: string; Children: CategoryNode list }

/// The three states a repository's categorization resolves to. A malformed or out-of-bounds
/// configuration becomes `Invalid` with a reason, never a silent `Missing`.
type Configuration =
    | Missing
    | Invalid of reason: string
    | Configured of CategoryNode list

let nodeName node =
    match node with
    | Leaf leaf -> leaf.Name
    | Branch branch -> branch.Name

// `internal` rather than `private`: the viewer template repeats the depth bound in its configure
// prompt, and a test pins that prose to this constant.
let internal maxDepth = 4
let private maxNodes = 200
let private maxNameLength = 100
let private maxPatternsPerLeaf = 50
let private maxPatternLength = 200

/// The trailing group label the viewer reserves for unmatched files. `internal` for the same reason
/// as `maxDepth`: the template names it too, and a test holds the two together.
let internal reservedTopLevelName = "Other"

// Reasons reach the browser, so they are fixed sentences: no filesystem path, no raw configuration
// text, no exception message can travel out of validation.
let private unreadableReason = ".treemon.json could not be parsed"
let private topLevelReason = "diffCategories must be a non-empty array of categories"
let private childrenReason = "children must be a non-empty array of categories"
let private categoryShapeReason = "each category must be an object"
let private nameReason = "each category needs a name"
let private exclusiveReason = "each category needs either patterns or children, but not both"
let private patternsReason = "patterns must be a non-empty array of strings"
let private duplicateNamesReason = "categories sharing a parent need distinct names"
let private reservedNameReason = "the top-level category name 'Other' is reserved for unmatched files"
let private depthReason = $"categories may nest at most {maxDepth} levels deep"
let private nodeCountReason = $"a repository may declare at most {maxNodes} categories"
let private patternCountReason = $"a category may list at most {maxPatternsPerLeaf} patterns"
let private patternLengthReason = $"a pattern may be at most {maxPatternLength} characters long"
let private nameLengthReason = $"a name may be at most {maxNameLength} characters long"

/// Ordinal case-insensitive equality, the comparison sibling uniqueness and the reserved top-level
/// name are defined in terms of.
let private equalsIgnoreCase (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

let private hasDuplicateNames names =
    names
    |> List.exists (fun name -> names |> List.filter (equalsIgnoreCase name) |> List.length > 1)

let rec private countNodes node =
    match node with
    | Leaf _ -> 1
    | Branch branch -> 1 + (branch.Children |> List.sumBy countNodes)

let private tryProperty (propertyName: string) (element: JsonElement) =
    match element.TryGetProperty(propertyName) with
    | true, value -> Some value
    | _ -> None

/// The elements of a JSON array, or none at all for any other value kind — the two are equally
/// invalid wherever the schema demands a non-empty array.
let private arrayItems (element: JsonElement) =
    if element.ValueKind = JsonValueKind.Array then element.EnumerateArray() |> List.ofSeq else []

/// The name bound applies to the trimmed value, which is what is stored, displayed, and repeated in
/// every file's `categoryPath`.
let private validateName (element: JsonElement) =
    match tryProperty "name" element with
    | Some name when name.ValueKind = JsonValueKind.String ->
        match name.GetString().Trim() with
        | "" -> Error nameReason
        | trimmed when trimmed.Length > maxNameLength -> Error nameLengthReason
        | trimmed -> Ok trimmed
    | _ -> Error nameReason

let private validatePatterns (element: JsonElement) =
    let items = arrayItems element

    if items.IsEmpty then Error patternsReason
    elif items.Length > maxPatternsPerLeaf then Error patternCountReason
    elif items |> List.exists (fun item -> item.ValueKind <> JsonValueKind.String) then Error patternsReason
    else
        let patterns = items |> List.map _.GetString()

        if patterns |> List.exists (fun pattern -> pattern.Length > maxPatternLength) then
            Error patternLengthReason
        else
            Ok patterns

let rec private validateNode depth (element: JsonElement) =
    if depth > maxDepth then Error depthReason
    elif element.ValueKind <> JsonValueKind.Object then Error categoryShapeReason
    else
        result {
            let! name = validateName element

            match tryProperty "patterns" element, tryProperty "children" element with
            | Some patterns, None ->
                let! validated = validatePatterns patterns
                return Leaf { Name = name; Patterns = validated }
            | None, Some children ->
                let! validated = validateNodeArray childrenReason (depth + 1) children
                return Branch { Name = name; Children = validated }
            | _ -> return! Error exclusiveReason
        }

/// The node bound is enforced per sibling array as well as against the whole tree: more siblings than
/// the whole repository may declare is already invalid, and rejecting it here keeps the quadratic
/// duplicate-name scan below off an oversized array.
and private validateNodeArray emptyReason depth (element: JsonElement) =
    let items = arrayItems element

    if items.IsEmpty then Error emptyReason
    elif items.Length > maxNodes then Error nodeCountReason
    else
        result {
            let! nodes = items |> List.traverseResultM (validateNode depth)

            return!
                if nodes |> List.map nodeName |> hasDuplicateNames then Error duplicateNamesReason
                else Ok nodes
        }

/// Root categories are depth 1, so the whole-repository bounds are checked once against the built
/// tree rather than tracked through the recursion.
let private validate element =
    let validated =
        result {
            let! roots = validateNodeArray topLevelReason 1 element

            do!
                if roots |> List.exists (nodeName >> equalsIgnoreCase reservedTopLevelName) then
                    Error reservedNameReason
                else
                    Ok()

            return! if (roots |> List.sumBy countNodes) > maxNodes then Error nodeCountReason else Ok roots
        }

    match validated with
    | Ok roots -> Configured roots
    | Error reason -> Invalid reason

let read (repoRoot: string) : Configuration =
    match TreemonConfig.readDiffCategories repoRoot with
    | TreemonConfig.Absent -> Missing
    | TreemonConfig.Unreadable -> Invalid unreadableReason
    | TreemonConfig.Present element -> validate element

/// One segment of a compiled pattern. `**` stands for whole segments; a wildcard-free segment is one
/// ordinal comparison; any other segment is matched character by character, so `*` and `?` can never
/// cross a `/`.
type private GlobSegment =
    | AnySegments
    | Literal of string
    | SegmentPattern of char[]

/// A compiled pattern: its segments plus, for a pattern carrying no `**`, the exact number of path
/// segments it can match, so a path of any other depth is rejected before a character is examined.
type private CompiledGlob =
    { Segments: GlobSegment[]
      FixedLength: int option }

/// A leaf lifted out of the tree with its patterns already compiled, its display path from the root,
/// and its position in depth-first configuration order — the order that decides both which
/// overlapping leaf wins and how matched files are grouped.
type private CompiledLeaf =
    { Order: int
      CategoryPath: string list
      Globs: CompiledGlob list }

/// The pattern positions reachable from `position` without consuming an item, because a star is
/// allowed to stand for nothing at all.
let rec private reachableThroughStars isStar (pattern: 'a[]) position =
    if position < pattern.Length && isStar pattern[position] then
        position :: reachableThroughStars isStar pattern (position + 1)
    else
        [ position ]

/// Matches a star pattern against `items` by simulating it as an automaton whose state is the set of
/// reachable pattern positions. Repository-authored patterns therefore cost pattern length times item
/// count even when crowded with stars, instead of backtracking exponentially.
let private matchesWildcard isStar matchesItem (pattern: 'a[]) (items: 'b[]) =
    let advance positions item =
        positions
        |> Seq.collect (fun position ->
            if position >= pattern.Length then []
            elif isStar pattern[position] then [ position ]
            elif matchesItem pattern[position] item then [ position + 1 ]
            else [])
        |> Seq.collect (reachableThroughStars isStar pattern)
        |> Set.ofSeq

    items
    |> Array.fold advance (reachableThroughStars isStar pattern 0 |> Set.ofList)
    |> Set.contains pattern.Length

/// Ordinal, case-sensitive matching of one path segment: `?` stands for one character and `*` for any
/// run of characters, both confined to this segment.
let private matchesSegment (pattern: char[]) (segment: string) =
    matchesWildcard
        (fun patternChar -> patternChar = '*')
        (fun patternChar character -> patternChar = '?' || patternChar = character)
        pattern
        (segment.ToCharArray())

let private matchesGlob (glob: CompiledGlob) (segments: string[]) =
    let matchesOneSegment globSegment segment =
        match globSegment with
        | AnySegments -> true
        | Literal literal -> String.Equals(literal, segment, StringComparison.Ordinal)
        | SegmentPattern pattern -> matchesSegment pattern segment

    // Without `**` every glob segment consumes exactly one path segment, so the automaton has nothing
    // to search: either the depths agree and each segment matches, or the path cannot match at all.
    match glob.FixedLength with
    | Some length when length <> segments.Length -> false
    | Some _ -> Array.forall2 matchesOneSegment glob.Segments segments
    | None -> matchesWildcard (fun globSegment -> globSegment = AnySegments) matchesOneSegment glob.Segments segments

/// Drops every star that directly follows a star, in either alphabet. A run of stars means exactly
/// what a single star means, and collapsing it at compile time is what keeps `reachableThroughStars`
/// down to two positions, so matching stays linear in pattern length however a repository writes it.
let private collapseStarRuns isStar (items: 'a[]) =
    items
    |> Array.indexed
    |> Array.filter (fun (index, item) -> index = 0 || not (isStar item && isStar items[index - 1]))
    |> Array.map snd

/// Splits a pattern into segments once per configuration read, so classifying a file never re-parses
/// it. Everything outside `**`, `*`, and `?` is literal text, including regular-expression syntax.
let private compileGlob (pattern: string) =
    let compileSegment (segment: string) =
        if segment = "**" then AnySegments
        elif segment.IndexOfAny([| '*'; '?' |]) < 0 then Literal segment
        else SegmentPattern(segment.ToCharArray() |> collapseStarRuns (fun character -> character = '*'))

    let segments =
        pattern.Split('/')
        |> Array.map compileSegment
        |> collapseStarRuns (fun segment -> segment = AnySegments)

    { Segments = segments
      FixedLength = if segments |> Array.contains AnySegments then None else Some segments.Length }

let rec private compileNode ancestors node =
    match node with
    | Leaf leaf -> [ ancestors @ [ leaf.Name ], leaf.Patterns |> List.map compileGlob ]
    | Branch branch -> branch.Children |> List.collect (compileNode (ancestors @ [ branch.Name ]))

let private compileLeaves nodes =
    nodes
    |> List.collect (compileNode [])
    |> List.mapi (fun order (categoryPath, globs) ->
        { Order = order
          CategoryPath = categoryPath
          Globs = globs })

/// Patterns are repository-relative and always matched against the whole path, so a Windows-style
/// separator is normalized away before the path is split into segments.
let private segmentsOf (path: string) = path.Replace('\\', '/').Split('/')

let private tryMatch leaves (path: string) =
    let segments = segmentsOf path
    leaves |> List.tryFind (fun leaf -> leaf.Globs |> List.exists (fun glob -> matchesGlob glob segments))

/// Classifies every changed file and pairs it with the category path the viewer renders — the trimmed
/// node names from the root to the matching leaf, empty when nothing matched and for every file of a
/// `Missing` or `Invalid` configuration. `pathsOf` yields a file's current repository-relative path
/// and, for a rename, the path it moved from; the new path is matched first so a moved file lands in
/// its destination category, and the old path is consulted only when the new path matches nothing.
///
/// Files come back grouped: groups follow leaf configuration order, unmatched files come last so the
/// viewer's trailing `Other` group is contiguous, and files keep their original relative order inside
/// a group. Patterns compile once per call rather than once per file.
let classifyAndOrder (configuration: Configuration) (pathsOf: 'a -> string * string option) (items: 'a list) =
    match configuration with
    | Missing
    | Invalid _ -> items |> List.map (fun item -> item, [])
    | Configured nodes ->
        let leaves = compileLeaves nodes
        let unmatchedOrder = leaves.Length

        items
        |> List.map (fun item ->
            let path, oldPath = pathsOf item

            let matched =
                tryMatch leaves path
                |> Option.orElseWith (fun () -> oldPath |> Option.bind (tryMatch leaves))

            let order = matched |> Option.map _.Order |> Option.defaultValue unmatchedOrder
            order, (item, matched |> Option.map _.CategoryPath |> Option.defaultValue []))
        // Sorting a list is stable, which is what keeps files in their original order inside a group.
        |> List.sortBy fst
        |> List.map snd
