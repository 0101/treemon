namespace CanvasShareViewer

open System
open Microsoft.Extensions.Configuration

type internal ViewerConfiguration =
    { StorageAccountName: string
      ShareContainer: string }

module internal ViewerConfiguration =

    [<Literal>]
    let SectionName = "CanvasShareViewer"

    [<Literal>]
    let StorageAccountNameKey = "StorageAccountName"

    [<Literal>]
    let ShareContainerKey = "ShareContainer"

    let private requiredValue (section: IConfigurationSection) key =
        section[key]
        |> Option.ofObj
        |> Option.map _.Trim()
        |> Option.filter (not << String.IsNullOrWhiteSpace)

    let read (configuration: IConfiguration) =
        let section = configuration.GetSection(SectionName)

        match
            requiredValue section StorageAccountNameKey,
            requiredValue section ShareContainerKey
        with
        | Some accountName, Some container ->
            Ok
                { StorageAccountName = accountName
                  ShareContainer = container }
        | None, _ ->
            Error
                $"Missing required configuration '{SectionName}:{StorageAccountNameKey}'."
        | _, None ->
            Error
                $"Missing required configuration '{SectionName}:{ShareContainerKey}'."
