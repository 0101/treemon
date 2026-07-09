module Server.JsonHelpers

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

let tryInt name el = tryProp name el |> Option.map _.GetInt32()
let tryInt64 name el = tryProp name el |> Option.map _.GetInt64()
let tryBool name el = tryProp name el |> Option.map _.GetBoolean()
