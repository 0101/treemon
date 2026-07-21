module Server.CanvasDocKinds

open System.Text.Json
open Shared

let private systemViews =
    let resourceName = "CanvasDocKinds.json"
    EmbeddedResource.readText resourceName
    |> JsonSerializer.Deserialize<string array>
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith $"Embedded resource '{resourceName}' contained null")
    |> Array.map _.ToLowerInvariant()
    |> Set.ofArray

let classify (filename: string) =
    if systemViews |> Set.contains (filename.ToLowerInvariant()) then SystemView else AgentDoc
