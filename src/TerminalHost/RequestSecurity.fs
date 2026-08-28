namespace TerminalHost

open System
open System.Net
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http

type RequestMetadata =
    { RemoteAddress: IPAddress option
      LocalAddress: IPAddress option
      LocalPort: int
      HostHeaders: string list
      OriginHeaders: string list
      AuthorizationHeaders: string list
      ContentLength: int64 option }

[<RequireQualifiedAccess>]
type RequestRejection =
    | Forbidden
    | Unauthorized
    | TooLarge

[<RequireQualifiedAccess>]
module RequestSecurity =
    let statusCode = function
        | RequestRejection.Forbidden -> StatusCodes.Status403Forbidden
        | RequestRejection.Unauthorized -> StatusCodes.Status401Unauthorized
        | RequestRejection.TooLarge -> StatusCodes.Status413PayloadTooLarge

    let private fixedTimeEquals (expected: string) (actual: string) =
        let expectedBytes = Encoding.UTF8.GetBytes expected
        let actualBytes = Encoding.UTF8.GetBytes actual

        expectedBytes.Length = actualBytes.Length
        && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes)

    let private validAuthorization (bearerToken: string) (values: string list) =
        match values with
        | [ value ] when value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ->
            let supplied = value.Substring("Bearer ".Length)
            fixedTimeEquals bearerToken supplied
        | _ -> false

    let private matchesOne (expected: string) (values: string list) =
        match values with
        | [ value ] -> String.Equals(value, expected, StringComparison.OrdinalIgnoreCase)
        | _ -> false

    let metadata authorizationHeaders (context: HttpContext) =
        { RemoteAddress = context.Connection.RemoteIpAddress |> Option.ofObj
          LocalAddress = context.Connection.LocalIpAddress |> Option.ofObj
          LocalPort = context.Connection.LocalPort
          HostHeaders = context.Request.Headers.Host |> Seq.toList
          OriginHeaders = context.Request.Headers.Origin |> Seq.toList
          AuthorizationHeaders = authorizationHeaders
          ContentLength = context.Request.ContentLength |> Option.ofNullable }

    let validate (allowedOrigins: string list) (bearerToken: string) (metadata: RequestMetadata) =
        let controlOrigin = $"http://127.0.0.1:{metadata.LocalPort}"

        let validOrigin =
            match metadata.OriginHeaders with
            | [] -> true
            | [ origin ] ->
                controlOrigin :: allowedOrigins
                |> List.exists (fun allowed ->
                    String.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase))
            | _ -> false

        match metadata.RemoteAddress, metadata.LocalAddress with
        | Some remoteAddress, Some localAddress
            when IPAddress.IsLoopback remoteAddress && IPAddress.IsLoopback localAddress ->
            let expectedHost = $"127.0.0.1:{metadata.LocalPort}"

            if not (matchesOne expectedHost metadata.HostHeaders) then
                Error RequestRejection.Forbidden
            elif not validOrigin then
                Error RequestRejection.Forbidden
            elif
                metadata.ContentLength
                |> Option.exists (fun length -> length > Protocol.MaximumRequestBodyBytes)
            then
                Error RequestRejection.TooLarge
            elif not (validAuthorization bearerToken metadata.AuthorizationHeaders) then
                Error RequestRejection.Unauthorized
            else
                Ok()
        | _ ->
            Error RequestRejection.Forbidden
