namespace CanvasShareViewer

open System
open System.Threading
open System.Threading.Tasks

type internal ShareLookupResult =
    | Available of BlobDocument
    | NotFound

module internal ShareLookup =

    [<Literal>]
    let InvalidPathProbeBlobName = "_invalid-path-probe"

    let resolve
        (reader: BlobReader)
        (clock: unit -> DateTimeOffset)
        prefix
        filename
        (cancellationToken: CancellationToken)
        : Task<ShareLookupResult> =
        task {
            let path = SharePath.tryCreate prefix filename

            let exactBlobName =
                path
                |> Option.map SharePath.blobName
                |> Option.defaultValue InvalidPathProbeBlobName

            let! stored =
                reader.ReadExact exactBlobName cancellationToken

            let metadata =
                stored
                |> Option.map _.Metadata
                |> Option.defaultValue Map.empty

            let live = ShareExpiry.isLive (clock ()) metadata

            return
                match path, stored, live with
                | Some _, Some document, true ->
                    Available document
                | _ ->
                    NotFound
        }
