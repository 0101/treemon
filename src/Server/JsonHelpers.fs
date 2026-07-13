module Server.JsonHelpers

open System
open System.Text.Json

let tryProp (name: string) (el: JsonElement) =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> Some v
    | _ -> None

let tryString name el = tryProp name el |> Option.map _.GetString()

/// Like `tryString` but returns `None` when the property exists as a non-string JSON value
/// (number/bool/object) instead of throwing, by guarding on `ValueKind = String`. Use this at
/// malformed-tolerant parse boundaries where one bad row must not abort the whole collection.
let tryStringValue name el =
    tryProp name el
    |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
    |> Option.map _.GetString()

/// Reads a `"timestamp"` string property and parses it as a `DateTimeOffset`, returning `None`
/// when the property is absent, non-string, or unparseable (guards on `ValueKind = String` via
/// `tryStringValue`). Shared by the Copilot and Claude detectors so their timestamp handling
/// can't diverge.
let tryTimestamp (root: JsonElement) : DateTimeOffset option =
    tryStringValue "timestamp" root
    |> Option.bind (fun s ->
        match DateTimeOffset.TryParse(s) with
        | true, v -> Some v
        | _ -> None)

let tryInt name el = tryProp name el |> Option.map _.GetInt32()
let tryInt64 name el = tryProp name el |> Option.map _.GetInt64()
let tryBool name el = tryProp name el |> Option.map _.GetBoolean()
