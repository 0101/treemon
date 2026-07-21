module Server.CanvasSelectionScript

open System.IO
open System.Reflection

let source =
    let resourceName = "CanvasSelectionContext.js"
    let assembly = Assembly.GetExecutingAssembly()
    use stream =
        assembly.GetManifestResourceStream(resourceName)
        |> Option.ofObj
        |> Option.defaultWith (fun () ->
            let available = assembly.GetManifestResourceNames() |> String.concat ", "
            failwith $"Embedded resource '{resourceName}' not found. Available resources: [{available}]")
    use reader = new StreamReader(stream)
    reader.ReadToEnd()

let script = "<script>" + source + "</script>"
