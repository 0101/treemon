module Server.ClaudeStatus

open System
open System.Collections.Concurrent
open System.IO
open Shared

let private claudeProjectsDir =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "projects"
    )

let encodeWorktreePath (worktreePath: string) =
    worktreePath.Replace(":", "-").Replace("\\", "-").Replace("/", "-")

let private findLatestJsonl (projectDir: string) =
    try
        if Directory.Exists(projectDir) then
            Directory.GetFiles(projectDir, "*.jsonl")
            |> Array.map (fun f -> FileInfo(f))
            |> Array.sortByDescending (fun fi -> fi.LastWriteTimeUtc)
            |> Array.tryHead
        else
            None
    with ex ->
        Log.log "Claude" (sprintf "Failed to list directory %s: %s" projectDir ex.Message)
        None

let private statusFromAge (age: TimeSpan) =
    match age with
    | a when a < TimeSpan.FromMinutes(2.0) -> Active
    | a when a < TimeSpan.FromMinutes(30.0) -> Recent
    | _ -> Idle

let getClaudeStatus (worktreePath: string) =
    let encoded = encodeWorktreePath worktreePath
    let projectDir = Path.Combine(claudeProjectsDir, encoded)

    match findLatestJsonl projectDir with
    | Some fi ->
        try
            let age = DateTimeOffset.UtcNow - DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
            statusFromAge age
        with ex ->
            Log.log "Claude" (sprintf "Failed to read mtime for %s: %s" fi.FullName ex.Message)
            Unknown
    | None -> Unknown

module Cache =
    type CacheEntry<'T> =
        { Value: 'T
          CachedAt: DateTimeOffset }

    let private cache = ConcurrentDictionary<string, CacheEntry<ClaudeCodeStatus>>()
    let private ttl = TimeSpan.FromSeconds(15.0)

    let getCachedClaudeStatus (worktreePath: string) =
        let now = DateTimeOffset.UtcNow

        match cache.TryGetValue(worktreePath) with
        | true, entry when now - entry.CachedAt < ttl -> entry.Value
        | _ ->
            let status = getClaudeStatus worktreePath
            cache.[worktreePath] <- { Value = status; CachedAt = now }
            status

    let getOldestCachedAt () =
        cache.Values
        |> Seq.map (fun entry -> entry.CachedAt)
        |> Seq.sortBy id
        |> Seq.tryHead
