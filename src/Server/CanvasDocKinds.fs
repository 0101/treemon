module Server.CanvasDocKinds

open System.IO
open System.Reflection
open System.Text.Json
open Shared

let private systemViews =
    let resourceName = "CanvasDocKinds.json"
    let assembly = Assembly.GetExecutingAssembly()
    use stream =
        assembly.GetManifestResourceStream(resourceName)
        |> Option.ofObj
        |> Option.defaultWith (fun () ->
            let available = assembly.GetManifestResourceNames() |> String.concat ", "
            failwith $"Embedded resource '{resourceName}' not found. Available resources: [{available}]")
    use reader = new StreamReader(stream)
    JsonSerializer.Deserialize<string array>(reader.ReadToEnd())
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith $"Embedded resource '{resourceName}' contained null")
    |> Array.map _.ToLowerInvariant()
    |> Set.ofArray

let classify (filename: string) =
    if systemViews |> Set.contains (filename.ToLowerInvariant()) then SystemView else AgentDoc
