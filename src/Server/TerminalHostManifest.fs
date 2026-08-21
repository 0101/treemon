module Server.TerminalHostManifest

open System
open System.IO
open System.Text
open System.Text.Json
open Server.TerminalHostEndpoint
open Server.TerminalHostProcess

type internal DiscoveryManifest =
    { Pid: int
      ProcessStartTimeUtcTicks: int64
      Endpoint: string
      BearerToken: string
      HostVersion: string
      ControlApiVersion: int
      StagedExecutableVersion: string option }

let private pathComparison =
    if OperatingSystem.IsWindows() then
        StringComparison.OrdinalIgnoreCase
    else
        StringComparison.Ordinal

let internal samePath left right =
    String.Equals(left, right, pathComparison)

let internal pathKey (path: string) =
    if OperatingSystem.IsWindows() then
        path.ToUpperInvariant()
    else
        path

let internal hostIdentityMatches left right =
    left.Pid = right.Pid
    && left.ProcessStartTimeUtcTicks = right.ProcessStartTimeUtcTicks

let internal validBoundedText maximum (value: string) =
    not (String.IsNullOrWhiteSpace value)
    && value.Length <= maximum
    && value
       |> Seq.forall (fun character ->
           not (Char.IsControl character))

let internal validVersion allowBuildMetadata (value: string) =
    validBoundedText 128 value
    && value
       |> Seq.forall (fun character ->
           Char.IsAsciiLetterOrDigit character
           || character = '.'
           || character = '-'
           || character = '_'
           || (allowBuildMetadata && character = '+'))

let private validBearerToken (value: string) =
    value.Length >= 32
    && value.Length <= 128
    && value
       |> Seq.forall (fun character ->
           Char.IsAsciiLetterOrDigit character
           || character = '-'
           || character = '_')

let internal validSessionId (value: string) =
    value.Length = 32
    && value |> Seq.forall Uri.IsHexDigit

let internal exactProperties required optional (element: JsonElement) =
    if element.ValueKind <> JsonValueKind.Object then
        false
    else
        let names =
            element.EnumerateObject()
            |> Seq.map _.Name
            |> Seq.toList

        let distinct = names |> Set.ofList
        let allowed = Set.union required optional

        names.Length = distinct.Count
        && Set.isSubset required distinct
        && Set.isSubset distinct allowed

let private optionalString name (element: JsonElement) =
    match
        element.EnumerateObject()
        |> Seq.tryFind (fun property -> property.Name = name)
    with
    | None -> Ok None
    | Some property when property.Value.ValueKind = JsonValueKind.String ->
        match property.Value.GetString() |> Option.ofObj with
        | Some value -> Ok(Some value)
        | None -> Error $"{name} must be a JSON string"
    | Some _ -> Error $"{name} must be a JSON string"

let private validControlEndpoint (value: string) =
    try
        let endpoint = Uri(value, UriKind.Absolute)

        isLoopbackHttpUri endpoint
        && endpoint.AbsolutePath = "/"
    with _ ->
        false

let private parseManifest (text: string) =
    try
        use document =
            JsonDocument.Parse(
                text,
                JsonDocumentOptions(
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4
                )
            )

        let root = document.RootElement

        let required =
            set
                [ "pid"; "processStartTimeUtcTicks"; "endpoint"
                  "bearerToken"; "hostVersion"; "controlApiVersion" ]

        let optional = set [ "stagedExecutableVersion" ]

        if not (exactProperties required optional root) then
            Error "TerminalHost discovery manifest has an invalid shape"
        else
            let pid = root.GetProperty("pid").GetInt32()
            let processStartTimeUtcTicks =
                root.GetProperty("processStartTimeUtcTicks").GetInt64()
            let endpoint = root.GetProperty("endpoint").GetString() |> Option.ofObj
            let bearerToken = root.GetProperty("bearerToken").GetString() |> Option.ofObj
            let hostVersion = root.GetProperty("hostVersion").GetString() |> Option.ofObj
            let apiVersion = root.GetProperty("controlApiVersion").GetInt32()

            match optionalString "stagedExecutableVersion" root with
            | Error _ ->
                Error "TerminalHost discovery manifest has an invalid staged executable version"
            | Ok stagedVersion ->
                if pid <= 0 || processStartTimeUtcTicks <= 0L then
                    Error "TerminalHost discovery manifest has an invalid process identity"
                elif endpoint |> Option.exists validControlEndpoint |> not then
                    Error "TerminalHost discovery manifest has an invalid control endpoint"
                elif bearerToken |> Option.exists validBearerToken |> not then
                    Error "TerminalHost discovery manifest has an invalid bearer token"
                elif
                    hostVersion
                    |> Option.exists (validVersion true)
                    |> not
                then
                    Error "TerminalHost discovery manifest has an invalid host version"
                elif
                    stagedVersion
                    |> Option.exists (validVersion false >> not)
                then
                    Error "TerminalHost discovery manifest has an invalid staged executable version"
                else
                    Ok
                        { Pid = pid
                          ProcessStartTimeUtcTicks = processStartTimeUtcTicks
                          Endpoint = endpoint |> Option.get
                          BearerToken = bearerToken |> Option.get
                          HostVersion = hostVersion |> Option.get
                          ControlApiVersion = apiVersion
                          StagedExecutableVersion = stagedVersion }
    with
    | :? JsonException
    | :? InvalidOperationException
    | :? FormatException
    | :? OverflowException ->
        Error "TerminalHost discovery manifest is malformed"

let private manifestPath config =
    Path.Combine(config.HostStateDirectory, "host.json")

let internal readManifest config =
    let path = manifestPath config

    try
        let info = FileInfo path

        if not info.Exists then
            Ok None
        elif
            info.Length <= 0L
            || info.Length > 65_536L
            || (info.Attributes &&& FileAttributes.ReparsePoint) <> enum 0
        then
            Error "TerminalHost discovery manifest is invalid"
        else
            File.ReadAllText(path, Encoding.UTF8)
            |> parseManifest
            |> Result.map Some
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException ->
        Ok None
    | error ->
        Error $"Could not read the TerminalHost discovery manifest: {error.Message}"

let internal processIdentityMatches config (manifest: DiscoveryManifest) =
    config.ProcessIdentityMatches manifest.Pid manifest.ProcessStartTimeUtcTicks

let internal resolveProcessExecutable config (manifest: DiscoveryManifest) =
    config.ResolveProcessExecutable manifest.Pid manifest.ProcessStartTimeUtcTicks
