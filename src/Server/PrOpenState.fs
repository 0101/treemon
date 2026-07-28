/// Whether a branch has an open pull request *right now*, asked at the moment Treemon decides
/// whether a mechanically synced branch must be pushed. Deliberately separate from the dashboard's
/// cached PR map, which refreshes on its own cadence and cannot tell a closed-unmerged pull request
/// from an open one. See `docs/spec/worktree-monitor.md` (Branch Sync).
module Server.PrOpenState

open System
open System.Text.Json

/// Three-valued on purpose: `NoOpenPr` finishes a sync locally while `UnknownPrState` hands the
/// worktree to an agent, so a provider or parse failure must never collapse into a confirmed absence.
type OpenPrState =
    | OpenPr
    | NoOpenPr
    | UnknownPrState

/// The lookup runs inside background sync work that no HTTP response is waiting on, so it carries
/// its own bound instead of the interactive deadline `ProcessRunner.runArgumentList` applies.
let private queryTimeoutMs = 30_000

/// A branch-filtered query answers with at most a handful of pull requests; the bound only stops a
/// misbehaving provider from streaming megabytes through the server process.
let private queryCaptureLimitBytes = 64 * 1024

let private failureReason =
    function
    | ProcessRunner.StartFailed _ -> "could not start"
    | ProcessRunner.TimedOut -> "timed out"
    | ProcessRunner.CaptureLimitExceeded _ -> "exceeded its output limit"

/// Runs one provider query through an argument list, so a branch name can never be parsed as part of
/// a command string, and collapses every process-level failure - including a non-zero exit from an
/// authentication error - to `UnknownPrState`.
let internal runQuery (context: string) (fileName: string) (arguments: string list) =
    async {
        let! result =
            ProcessRunner.runArgumentListWithTimeout
                queryTimeoutMs
                queryCaptureLimitBytes
                queryCaptureLimitBytes
                context
                fileName
                arguments
                None

        match result with
        | Ok output when output.ExitCode = 0 -> return Ok(Text.Encoding.UTF8.GetString(output.Stdout))
        | Ok output ->
            Log.log context $"Open PR lookup exited with {output.ExitCode}"
            return Error UnknownPrState
        | Error failure ->
            Log.log context $"Open PR lookup {failureReason failure}"
            return Error UnknownPrState
    }

/// Classifies the response to a query that already asked the provider for one branch.
/// `sourceBranchOf` reads that provider's own source-branch field - or, where the provider filters on
/// more than the branch, the whole filtered head projected into one comparable value that `branch`
/// then names. Entries naming any other source mean the filter did not apply, so the answer stays
/// unknown rather than being read as an absence.
/// Only the shape of a payload is logged, never the payload itself.
let internal classifyResponse
    (context: string)
    (sourceBranchOf: JsonElement -> string option)
    (branch: string)
    (json: string)
    =
    try
        use doc = JsonDocument.Parse(json)

        if doc.RootElement.ValueKind <> JsonValueKind.Array then
            Log.log context "Open PR lookup returned a non-array payload"
            UnknownPrState
        else
            let branches =
                doc.RootElement.EnumerateArray()
                |> Seq.map sourceBranchOf
                |> Seq.toList

            match branches with
            | [] -> NoOpenPr
            | _ when branches |> List.forall ((=) (Some branch)) -> OpenPr
            | _ ->
                Log.log context "Open PR lookup returned entries outside the requested branch"
                UnknownPrState
    with ex ->
        Log.log context $"Open PR lookup returned an unreadable payload ({ex.GetType().Name})"
        UnknownPrState
