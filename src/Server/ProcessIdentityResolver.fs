module Server.ProcessIdentityResolverRuntime

open System
open System.Diagnostics
open Server.SessionActivity

let defaultResolver =
    ProcessIdentityResolver.create (fun processId ->
        try
            use childProcess = Process.GetProcessById processId

            if childProcess.HasExited then
                Ok None
            else
                childProcess.StartTime.ToUniversalTime().Ticks
                |> ProcessIdentity.create processId
                |> Result.map Some
        with
        | :? ArgumentException
        | :? InvalidOperationException -> Ok None
        | error ->
            Error $"Could not resolve process identity: {error.Message}")
