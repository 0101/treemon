module Server.EmbeddedResource

open System.IO
open System.Reflection

let readText resourceName =
    let assembly = Assembly.GetExecutingAssembly()
    use stream =
        assembly.GetManifestResourceStream(resourceName)
        |> Option.ofObj
        |> Option.defaultWith (fun () ->
            let available = assembly.GetManifestResourceNames() |> String.concat ", "
            failwith $"Embedded resource '{resourceName}' not found. Available resources: [{available}]")
    use reader = new StreamReader(stream)
    reader.ReadToEnd()
