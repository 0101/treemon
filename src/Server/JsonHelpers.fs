module Server.JsonHelpers

open System.Text.Json

let tryProp (name: string) (el: JsonElement) =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> Some v
    | _ -> None

let tryString name el = tryProp name el |> Option.map _.GetString()
let tryInt name el = tryProp name el |> Option.map _.GetInt32()
let tryInt64 name el = tryProp name el |> Option.map _.GetInt64()
let tryBool name el = tryProp name el |> Option.map _.GetBoolean()
