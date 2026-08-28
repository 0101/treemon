module Server.CanvasFilename

open System
open System.Text.Json
open System.Text.RegularExpressions

let private resourceName = "CanvasFilenameContract.json"

let internal pattern =
    use document = JsonDocument.Parse(EmbeddedResource.readText resourceName)

    match document.RootElement.TryGetProperty("pattern") with
    | true, value when value.ValueKind = JsonValueKind.String ->
        value.GetString()
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () ->
            failwith $"Embedded resource '{resourceName}' contained an empty pattern")
    | _ ->
        failwith $"Embedded resource '{resourceName}' did not contain a string pattern"

let private filenameRegex = Regex(pattern, RegexOptions.CultureInvariant)

let isValid (filename: string) =
    if String.IsNullOrEmpty filename then
        false
    else
        let matched = filenameRegex.Match filename
        matched.Success && matched.Index = 0 && matched.Length = filename.Length
