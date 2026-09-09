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
let [<Literal>] private MaxRepoChars = 200

type DirectoryState =
    { /// Stands where a branch would: whatever names what the agent is currently on.
      Label: string
      /// Stands where the last commit subject would: what it most recently did.
      Summary: string
      /// Stands where the last commit time would.
      UpdatedAt: DateTimeOffset
      /// Stands where a dirty worktree would: work in progress rather than settled.
      Busy: bool
      /// The repository the agent says it is currently working in, relative to this folder, when it
      /// works in one at all.
      Repo: string option }

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

            // Capping is right for text a card renders; a path is not text. Truncating one yields a
            // different path, which could name a different repository, so an over-long value
            // declares nothing instead.
            let repo =
                stringOf DirectoryStateFile.Repo
                |> Option.map _.Trim()
                |> Option.filter (fun value -> value <> "" && value.Length <= MaxRepoChars)

            // The file answers two questions and a folder may answer either. A label is what a card
            // is drawn from; a repo is which card a session belongs to. A folder that sits above its
            // repositories wants only the second, and requiring a label there would make it name a
            // card that is never drawn.
            if String.IsNullOrWhiteSpace label && Option.isNone repo then
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
                      Busy = busy
                      Repo = repo }
        with
        | :? JsonException
        | :? IOException
        | :? UnauthorizedAccessException -> None

/// The repository a declared directory says it is currently working in, resolved against the folder.
///
/// This is how an agent that sits above its repositories says which one its session belongs to.
/// Otherwise that is answered by elimination - a folder holding exactly one monitored repository
/// resolves to it - and elimination stops working the moment a second one is monitored beside it,
/// silently, because a report for an unmonitored path is accepted and dropped.
///
/// Untrusted like everything else in the file, and it decides which card a session lights up, so:
/// relative only, resolved inside the folder, and a worktree git actually recognises. Anything else
/// declares nothing, which is the same as declaring none.
///
/// Including a value the path APIs refuse outright - an embedded NUL makes GetFullPath throw. This
/// runs on every report from a folder no worktree encloses, so a value that threw would not fail one
/// request but every request that agent makes, for as long as the file said so.
let declaredRepo (root: string) (state: DirectoryState) : string option =
    state.Repo
    |> Option.bind (fun declared ->
        try
            if Path.IsPathRooted declared then
                None
            else
                let resolved = Path.GetFullPath(Path.Combine(root, declared))
                let normalizedRoot = PathUtils.normalizePath root
                let prefix = normalizedRoot + string Path.DirectorySeparatorChar

                if not ((PathUtils.normalizePath resolved).StartsWith(prefix, StringComparison.Ordinal)) then
                    None
                elif not (isGitWorktree resolved) then
                    None
                else
                    Some resolved
        with
        | :? ArgumentException
        | :? NotSupportedException
        | :? PathTooLongException
        | :? IOException
        | :? UnauthorizedAccessException -> None)

/// Whether this folder is asking for a card of its own, rather than only redirecting its session to
/// a repository's. A card is drawn from the label, so a folder that supplies none is not one.
let describesCard (state: DirectoryState) =
    not (String.IsNullOrWhiteSpace state.Label)

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
