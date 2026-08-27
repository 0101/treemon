namespace CanvasShareViewer

open System
open System.Threading
open System.Threading.Tasks

type internal ShareLookupResult<'stored> =
    | Available of 'stored
    | NotFound

module internal ShareLookup =

    let private resolve
        read
        metadata
        release
        (clock: unit -> DateTimeOffset)
        prefix
        filename
        (cancellationToken: CancellationToken)
        : Task<ShareLookupResult<'stored>> =
        task {
            match SharePath.tryCreate prefix filename with
            | None ->
                return NotFound
            | Some path ->
                let! stored =
                    read
                        (SharePath.blobName path)
                        cancellationToken

                match stored with
                | Some value
                    when ShareExpiry.isLive
                        (clock ())
                        (metadata value)
                        ->
                    return Available value
                | Some value ->
                    release value
                    return NotFound
                | None ->
                    return NotFound
        }

    let resolveProperties (reader: BlobReader) =
        resolve reader.ReadPropertiesExact id ignore

    let resolveDocument (reader: BlobReader) =
        resolve
            reader.ReadExact
            _.Metadata
            (fun document -> document.Content.Dispose())
