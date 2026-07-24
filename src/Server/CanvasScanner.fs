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

let scan (worktreePath: string) =
    async {
        let dir = canvasDir worktreePath
        if Directory.Exists(dir) then
            let! owners = CanvasDocOwnership.getAll worktreePath

            return
                Directory.GetFiles(dir, "*.html")
                |> Array.sort
                |> Array.map (fun filePath ->
                    let filename = Path.GetFileName(filePath)
                    { Filename = filename
                      ContentHash = hashFile filePath
                      LastModified = DateTimeOffset(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero)
                      OwnerSessionId = owners |> Map.tryFind filename
                      Kind = CanvasDocKinds.classify filename })
                |> Array.toList
        else
            return []
    }

let tryCreateWatcher (post: CanvasDoc list -> unit) (worktreePath: string) : FileSystemWatcher option =
    let dir = canvasDir worktreePath
    let branch = Path.GetFileName(worktreePath)
    if Directory.Exists(dir) then
        let watcher = new FileSystemWatcher(dir, "*.html")
        let handleEvent (eventType: string) (e: FileSystemEventArgs) =
            Log.log "CanvasWatcher" $"{eventType}: {e.Name} in {branch}"
            async {
                try
                    let! docs = scan worktreePath
                    post docs
                with _ -> ()
            }
            |> Async.Start
        watcher.Changed.Add(handleEvent "Changed")
        watcher.Created.Add(handleEvent "Created")
        watcher.Deleted.Add(handleEvent "Deleted")
        watcher.Renamed.Add(handleEvent "Renamed")
        watcher.EnableRaisingEvents <- true
        Some watcher
    else
        None
