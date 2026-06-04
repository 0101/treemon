module Server.CanvasScanner

open System
open System.IO
open System.Security.Cryptography
open Shared

let private canvasDir path = Path.Combine(path, ".agents", "canvas")

let private hashFile (filePath: string) =
    use stream = File.OpenRead(filePath)
    use sha = SHA256.Create()
    sha.ComputeHash(stream)
    |> Array.map _.ToString("x2")
    |> String.Concat

let scan (worktreePath: string) : CanvasDoc list =
    let dir = canvasDir worktreePath
    if Directory.Exists(dir) then
        let owners = CanvasDocOwnership.getAll worktreePath
        Directory.GetFiles(dir, "*.html")
        |> Array.sort
        |> Array.map (fun filePath ->
            let filename = Path.GetFileName(filePath)
            { Filename = filename
              ContentHash = hashFile filePath
              LastModified = DateTimeOffset(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero)
              OwnerSessionId = owners |> Map.tryFind filename })
        |> Array.toList
    else
        []

let tryCreateWatcher (post: CanvasDoc list -> unit) (worktreePath: string) : FileSystemWatcher option =
    let dir = canvasDir worktreePath
    let branch = Path.GetFileName(worktreePath)
    if Directory.Exists(dir) then
        let watcher = new FileSystemWatcher(dir, "*.html")
        let handleEvent (eventType: string) (e: FileSystemEventArgs) =
            Log.log "CanvasWatcher" $"{eventType}: {e.Name} in {branch}"
            try scan worktreePath |> post
            with _ -> ()
        watcher.Changed.Add(handleEvent "Changed")
        watcher.Created.Add(handleEvent "Created")
        watcher.Deleted.Add(handleEvent "Deleted")
        watcher.Renamed.Add(handleEvent "Renamed")
        watcher.EnableRaisingEvents <- true
        Some watcher
    else
        None
