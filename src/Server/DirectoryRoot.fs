module Server.DirectoryRoot

open System
open System.IO
open System.Text.Json
open Shared

/// A monitored root that is not a git repository. An agent whose work is not commits — tickets,
/// cloud configuration, anything driven through someone else's API — has no branch, no diff and no
/// PR, so today it gets no card at all and its live status has nowhere to land.
///
/// Rather than inventing a second kind of card, such a folder answers the same questions git answers
/// by describing itself in a state file it maintains. Treemon learns nothing about Linear or AWS: it
/// reads a label, a summary and a timestamp, exactly as it reads a branch, a commit subject and a
/// commit time, and the card renders unchanged.
///
/// Everything in the file is written by an agent and rendered on a dashboard, so it is untrusted
/// input: text is capped here, at the boundary, rather than anywhere further in.
let StateFileName = DirectoryStateFile.FileName

let [<Literal>] private MaxLabelChars = 120
let [<Literal>] private MaxSummaryChars = 500

type DirectoryState =
    { /// Stands where a branch would: whatever names what the agent is currently on.
      Label: string
      /// Stands where the last commit subject would: what it most recently did.
      Summary: string
      /// Stands where the last commit time would.
      UpdatedAt: DateTimeOffset
      /// Stands where a dirty worktree would: work in progress rather than settled.
      Busy: bool }

let private capped maximum (value: string) =
    let trimmed = value.Trim()

    if trimmed.Length <= maximum then
        trimmed
    else
        trimmed.Substring(0, maximum)

let internal stateFilePath (root: string) = Path.Combine(root, StateFileName)

/// A linked worktree carries `.git` as a file and a main one as a directory; either way its presence
/// means git is authoritative here.
let internal isGitWorktree (path: string) =
    let marker = Path.Combine(path, ".git")
    Directory.Exists marker || File.Exists marker

/// The state a folder declares, or None when it declares none. A file that cannot be read or parsed
/// is None as well: a half-written state file is indistinguishable from an absent one for this
/// purpose, and a refresh that throws would take the whole repository's discovery with it.
///
/// A state file only ever speaks for a directory that is not a git worktree. Inside a repository git
/// is authoritative, and a file dropped - or committed - into one must not quietly replace the
/// branch, dirty flag and PR its card is built from, for this checkout or anyone else's.
let tryReadState (root: string) : DirectoryState option =
    let path = stateFilePath root

    if isGitWorktree root || not (File.Exists path) then
        None
    else
        try
            use document = JsonDocument.Parse(File.ReadAllText path)
            let root = document.RootElement

            let stringOf name =
                match root.TryGetProperty(name: string) with
                | true, element when element.ValueKind = JsonValueKind.String ->
                    element.GetString() |> Option.ofObj
                | _ -> None

            let label = stringOf DirectoryStateFile.Label |> Option.defaultValue "" |> capped MaxLabelChars

            // A label is the one thing a card cannot be drawn without, so a file that supplies none
            // declares nothing usable.
            if String.IsNullOrWhiteSpace label then
                None
            else
                let updatedAt =
                    stringOf DirectoryStateFile.UpdatedAt
                    |> Option.bind (fun value ->
                        match DateTimeOffset.TryParse value with
                        | true, parsed -> Some parsed
                        | _ -> None)
                    |> Option.defaultValue DateTimeOffset.MinValue

                let busy =
                    match root.TryGetProperty DirectoryStateFile.Busy with
                    | true, element when element.ValueKind = JsonValueKind.True -> true
                    | _ -> false

                Some
                    { Label = label
                      Summary = stringOf DirectoryStateFile.Summary |> Option.defaultValue "" |> capped MaxSummaryChars
                      UpdatedAt = updatedAt
                      Busy = busy }
        with
        | :? JsonException
        | :? IOException
        | :? UnauthorizedAccessException -> None

/// The single worktree a declared directory stands for. It is its own root, the way a repository's
/// main worktree is.
/// The path is normalized here for the same reason `git worktree list` output is: it becomes a known
/// path, and a report naming this directory is matched against it after normalization. An unnormalized
/// one would never match on Windows, where the two spellings differ in separator and case.
let worktreeInfo (root: string) (state: DirectoryState) : GitWorktree.WorktreeInfo =
    { Path = PathUtils.normalizePath root
      Head = ""
      Branch = Some state.Label }

/// The declared state expressed as the record a card already reads, so nothing downstream — the API,
/// the client, the overview band — needs to know this worktree is not a repository. The genuinely
/// git-shaped fields say "nothing to report" rather than a made-up value: no upstream, nothing behind
/// a base, no comparison content.
let gitData (root: string) (state: DirectoryState) : GitWorktree.GitData =
    { Path = PathUtils.normalizePath root
      Branch = state.Label
      HeadCommit = ""
      LastCommitMessage = state.Summary
      LastCommitTime = state.UpdatedAt
      Upstream = GitWorktree.NoUpstream
      MainBehindCount = 0
      BaseRevision = None
      IsDirty = state.Busy
      Comparison = if state.Busy then GitWorktree.HasContent else GitWorktree.Clean
      // Commits and line counts have no analogue here, and reporting zeroes would read as a
      // measured "nothing done" rather than "not measured".
      WorkMetrics = None }
