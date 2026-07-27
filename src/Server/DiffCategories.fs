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

let private maxDepth = 4
let private maxNodes = 200
let private maxPatternsPerLeaf = 50
let private maxPatternLength = 200

/// The trailing group label the viewer reserves for unmatched files.
let private reservedTopLevelName = "Other"

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

let private validateName (element: JsonElement) =
    match tryProperty "name" element with
    | Some name when name.ValueKind = JsonValueKind.String ->
        match name.GetString().Trim() with
        | "" -> Error nameReason
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

and private validateNodeArray emptyReason depth (element: JsonElement) =
    let items = arrayItems element

    if items.IsEmpty then Error emptyReason
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
