module Server.UserMessageFormatting

open System
open System.Text.Json
open System.Text.RegularExpressions
open Shared

let private canvasPrefix = "[canvas] "
let private systemReminderPrefix = "<system_reminder>"

[<RequireQualifiedAccess>]
type internal UserMessageClassification =
    | SystemReminder
    | Display of glyph: MessageGlyph option * text: string

let private identifierWords (identifier: string) =
    identifier.Replace('-', ' ').Replace('_', ' ')
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun word ->
        if String.Equals(word, "cli", StringComparison.OrdinalIgnoreCase) then "CLI"
        else word.ToLowerInvariant())
    |> String.concat " "

let private humanizeIdentifier identifier =
    let words = identifierWords identifier
    if words = "" || Char.IsUpper words[0] then words
    else string (Char.ToUpperInvariant words[0]) + words.Substring(1)

let private tryStringProperty name (element: JsonElement) =
    if element.ValueKind <> JsonValueKind.Object then
        None
    else
        JsonHelpers.tryStringValue name element
        |> Option.bind Option.ofObj
        |> Option.map _.Trim()
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

let rec private formatJsonElement (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.Object ->
        element.EnumerateObject()
        |> Seq.map (fun property -> $"{property.Name}: {formatJsonElement property.Value}")
        |> String.concat ", "
    | JsonValueKind.Array ->
        element.EnumerateArray()
        |> Seq.map formatJsonElement
        |> String.concat ", "
    | JsonValueKind.String ->
        element.GetString()
        |> Option.ofObj
        |> Option.defaultValue ""
        |> fun value -> if String.IsNullOrWhiteSpace value then "" else value
    | JsonValueKind.Null
    | JsonValueKind.Undefined -> ""
    | _ -> element.GetRawText()

let private readableMalformedPayload (payload: string) =
    payload
        .Replace("{", "")
        .Replace("}", "")
        .Replace("[", "")
        .Replace("]", "")
        .Replace("\"", "")
    |> fun text -> Regex.Replace(text, @"\s*:\s*", ": ")
    |> fun text -> Regex.Replace(text, @"\s*,\s*", ", ")
    |> fun text -> Regex.Replace(text, @"\s+", " ")
    |> _.Trim()
    |> function
        | "" -> "Canvas interaction"
        | text -> text

let private formatCanvasPayload (payload: string) =
    try
        use document = JsonDocument.Parse payload
        let root = document.RootElement
        let fallback () =
            match formatJsonElement root with
            | "" -> "Canvas interaction"
            | text -> text

        match tryStringProperty "text" root with
        | Some text -> text
        | None ->
            match tryStringProperty "action" root with
            | Some "canvas-selection" ->
                tryStringProperty "request" root
                |> Option.defaultWith fallback
            | Some "decision" ->
                match tryStringProperty "topic" root, tryStringProperty "choice" root with
                | Some topic, Some choice -> $"{humanizeIdentifier topic}: {humanizeIdentifier choice}"
                | _ -> fallback ()
            | Some "expand-section" ->
                match tryStringProperty "section" root with
                | Some section -> $"Expand {identifierWords section}"
                | None -> fallback ()
            | _ -> fallback ()
    with :? JsonException ->
        readableMalformedPayload payload

let internal classify (text: string) =
    if text.TrimStart().StartsWith(systemReminderPrefix, StringComparison.Ordinal) then
        UserMessageClassification.SystemReminder
    elif text.StartsWith(canvasPrefix, StringComparison.Ordinal) then
        UserMessageClassification.Display(Some MessageGlyph.Canvas, formatCanvasPayload text[canvasPrefix.Length..])
    else
        UserMessageClassification.Display(None, text)
