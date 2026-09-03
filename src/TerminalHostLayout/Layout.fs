namespace Treemon.TerminalHosting

open System
open System.IO
open System.Text.RegularExpressions

type TerminalHostLayout =
    { StateDirectory: string
      StagingDirectory: string
      ManifestPath: string
      HostExecutableName: string
      TtydExecutableName: string
      VersionDirectoryPattern: string
      RequiredBundleFileNames: string list }

[<RequireQualifiedAccess>]
module TerminalHostLayout =
    let [<Literal>] StateDirectoryEnvironmentVariable = "TREEMON_TERMINAL_HOST_STATE_DIR"
    let [<Literal>] ManifestFileName = "host.json"
    let [<Literal>] StagingDirectoryName = "staged"
    let [<Literal>] TtydExecutableName = "ttyd.exe"
    let [<Literal>] TtydLicenseFileName = "ttyd-LICENSE.txt"
    let [<Literal>] VersionDirectoryPattern = @"\A[A-Za-z0-9._-]{1,128}\z"

    let HostExecutableName = if OperatingSystem.IsWindows() then "TerminalHost.exe" else "TerminalHost"

    let RequiredBundleFileNames =
        [ HostExecutableName
          "TerminalHost.dll"
          "TerminalHost.deps.json"
          "TerminalHost.runtimeconfig.json"
          "TerminalHostLayout.dll"
          "FSharp.Core.dll"
          TtydExecutableName
          TtydLicenseFileName ]

    let private versionDirectoryRegex =
        Regex(VersionDirectoryPattern, RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking, TimeSpan.FromSeconds 1.0)

    let defaultStateDirectory () =
        let localApplicationData =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)

        let root =
            if String.IsNullOrWhiteSpace localApplicationData then
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".treemon"
                )
            else
                Path.Combine(localApplicationData, "Treemon")

        Path.Combine(root, "TerminalHost")

    let forStateDirectory stateDirectory =
        let state = Path.GetFullPath stateDirectory

        { StateDirectory = state
          StagingDirectory = Path.Combine(state, StagingDirectoryName)
          ManifestPath = Path.Combine(state, ManifestFileName)
          HostExecutableName = HostExecutableName
          TtydExecutableName = TtydExecutableName
          VersionDirectoryPattern = VersionDirectoryPattern
          RequiredBundleFileNames = RequiredBundleFileNames }

    let current () =
        Environment.GetEnvironmentVariable(StateDirectoryEnvironmentVariable)
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith defaultStateDirectory
        |> forStateDirectory

    let isValidVersionDirectoryName value =
        not (String.IsNullOrWhiteSpace value)
        && versionDirectoryRegex.IsMatch value

    let versionDirectory layout version =
        Path.Combine(layout.StagingDirectory, version)

    let validateStagedVersion layout version =
        try
            if not (isValidVersionDirectoryName version) then
                Error "The staged TerminalHost version is not a direct version directory"
            else
                let directory = DirectoryInfo(versionDirectory layout version)

                // The version grammar excludes separators and rooted paths; a changed final segment
                // catches "."/".." and platform path normalization without a second parent walk.
                if directory.Name <> version then
                    Error "The staged TerminalHost version is not a direct version directory"
                elif
                    not directory.Exists
                    || (directory.Attributes &&& FileAttributes.ReparsePoint) <> enum 0
                then
                    Error "The staged TerminalHost version directory is missing or unsafe"
                else
                    let invalidMember =
                        layout.RequiredBundleFileNames
                        |> List.map (fun name ->
                            FileInfo(Path.Combine(directory.FullName, name)))
                        |> List.tryFind (fun info ->
                            not info.Exists
                            || (info.Attributes &&& FileAttributes.ReparsePoint) <> enum 0)

                    match invalidMember with
                    | Some info ->
                        Error
                            $"The staged TerminalHost bundle member is missing or unsafe at '{info.FullName}'"
                    | None ->
                        Ok(Path.Combine(directory.FullName, layout.HostExecutableName))
        with error ->
            Error $"Could not validate the staged TerminalHost executable: {error.Message}"

    let adjacentTtydExecutablePath (hostExecutablePath: string) =
        hostExecutablePath
        |> Path.GetDirectoryName
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.map (fun directory -> Path.Combine(directory, TtydExecutableName))
