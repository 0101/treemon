module Server.TerminalSessionCleanup

open System
open Shared
open Server.SessionActivity
open Server.SessionActivityService
open Server.TerminalSessionActivity

let internal terminalSessionCleanupWithDiagnostics
    (diagnostics: LifecycleDiagnostics.Sink)
    (service: SessionActivityService)
    : WorktreeCleanup.PrepareSessionClose =
    fun originPaths ->
        let query terminalSessionIds =
            queryOwnedSessions
                (fun ids -> service.QuerySelectedTerminalActivity ids)
                DateTimeOffset.UtcNow
                terminalSessionIds
            |> Result.map _.OpenSessions

        let captured =
            originPaths
            |> Map.keys
            |> Set.ofSeq
            |> query
            |> Result.mapError (fun error ->
                Log.log
                    "Lifecycle"
                    $"Could not capture exact sessions for {originPaths.Count} terminal(s): {error}"

                error)

        let beforeHostClose (activeTerminalIds: Set<TerminalSessionId>) =
            async {
                match captured with
                | Error _ -> return ()
                | Ok sessions ->
                    let targets =
                        sessions
                        |> List.filter (fun session ->
                            activeTerminalIds.Contains session.TerminalSessionId)
                        |> List.choose (fun session ->
                            originPaths
                            |> Map.tryFind session.TerminalSessionId
                            |> Option.map (fun worktreePath ->
                                ({ WorktreePath =
                                    WorktreePath.value worktreePath
                                   ProcessIdentity =
                                    session.ProcessIdentity }
                                 : SessionBridge.ShutdownTarget)))

                    let! _ =
                        SessionBridge.shutdownExactBatchUsing
                            diagnostics
                            service.ClosedProcessSnapshot
                            targets

                    return ()
            }

        let afterHostClose (closedTerminalIds: Set<TerminalSessionId>) =
            if Set.isEmpty closedTerminalIds then
                Ok()
            else
                let observedAfter = query closedTerminalIds
                let closedAt = DateTimeOffset.UtcNow

                let sessions, queryErrors =
                    match captured, observedAfter with
                    | Ok capturedSessions, Ok observedSessions ->
                        capturedSessions @ observedSessions, []
                    | Error _, Ok observedSessions ->
                        observedSessions, []
                    | Ok capturedSessions, Error error ->
                        capturedSessions,
                        [ $"Could not reconcile exact terminal sessions: {error}" ]
                    | Error captureError, Error reconcileError ->
                        [],
                        [ $"Could not capture exact terminal sessions: {captureError}"
                          $"Could not reconcile exact terminal sessions: {reconcileError}" ]

                let acknowledgements =
                    sessions
                    |> List.filter (fun session ->
                        closedTerminalIds.Contains session.TerminalSessionId)
                    |> List.distinctBy _.ProcessIdentity
                    |> List.map (fun session ->
                        session, service.CloseProcess(session.ProcessIdentity, closedAt))

                acknowledgements
                |> List.iter (fun (session, acknowledgement) ->
                    let outcome =
                        match acknowledgement with
                        | ClosureAcknowledge.Closed ->
                            LifecycleDiagnostics.ExactClosureOutcome.Recorded
                        | ClosureAcknowledge.Missing ->
                            LifecycleDiagnostics.ExactClosureOutcome.Missing
                        | ClosureAcknowledge.Failed _ ->
                            LifecycleDiagnostics.ExactClosureOutcome.Failed

                    diagnostics (
                        LifecycleDiagnostics.Diagnostic.ExactClosure
                            { ProcessIdentity = session.ProcessIdentity
                              SessionId = session.CopilotSessionId
                              TerminalSessionId = session.TerminalSessionId
                              Outcome = outcome }
                    ))

                let closureErrors =
                    acknowledgements
                    |> List.choose (fun (_, acknowledgement) ->
                        match acknowledgement with
                        | ClosureAcknowledge.Closed -> None
                        | ClosureAcknowledge.Missing ->
                            Some "an exact session closure target was not found"
                        | ClosureAcknowledge.Failed error -> Some error)

                match queryErrors @ closureErrors with
                | [] -> Ok()
                | errors -> Error(String.concat "; " errors)

        { BeforeHostClose = beforeHostClose
          AfterHostClose = afterHostClose }

let internal terminalSessionCleanup service =
    terminalSessionCleanupWithDiagnostics
        LifecycleDiagnostics.write
        service
