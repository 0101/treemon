module Server.BeadspaceTemplate

open System.IO
open System.Reflection

/// The Beadspace dashboard HTML.
///
/// Single source of truth: the `BeadspaceTemplate.html` file, embedded into this
/// assembly as a resource (see the `<EmbeddedResource>` entry in Server.fsproj) and
/// read once here at module initialization. The E2E tests (`BeadspaceCanvasTests`)
/// read that same file from disk, so the runtime template and the tested template are
/// one definition and cannot drift.
let html : string =
    let resourceName = "BeadspaceTemplate.html"
    let assembly = Assembly.GetExecutingAssembly()
    use stream =
        assembly.GetManifestResourceStream(resourceName)
        |> Option.ofObj
        |> Option.defaultWith (fun () ->
            let available = assembly.GetManifestResourceNames() |> String.concat ", "
            failwith
                $"Embedded resource '{resourceName}' not found. Available resources: [{available}]")
    use reader = new StreamReader(stream)
    reader.ReadToEnd()
