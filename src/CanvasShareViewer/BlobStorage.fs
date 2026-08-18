namespace CanvasShareViewer

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Azure
open Azure.Core
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models

type internal BlobDocument =
    { Content: ReadOnlyMemory<byte>
      Metadata: Map<string, string> }

type internal BlobReader =
    { ReadPropertiesExact:
        string ->
        CancellationToken ->
        Task<Map<string, string> option>
      ReadExact:
        string ->
        CancellationToken ->
        Task<BlobDocument option> }

module internal BlobStorage =

    let internal isMissingBlobFailure
        (failure: RequestFailedException)
        =
        failure.Status = 404
        && String.Equals(
            failure.ErrorCode,
            BlobErrorCode.BlobNotFound.ToString(),
            StringComparison.Ordinal
        )

    let private metadataMap
        (values: IDictionary<string, string>)
        =
        values
        |> Seq.map (fun pair -> pair.Key, pair.Value)
        |> Map.ofSeq

    let private tryReadExact
        (read: unit -> Task<'value>)
        : Task<'value option> =
        task {
            try
                let! value = read ()
                return Some value
            with
            | :? RequestFailedException as ex when
                isMissingBlobFailure ex
                ->
                return None
        }

    let azure
        (configuration: ViewerConfiguration)
        (credential: TokenCredential)
        =
        let serviceClient =
            BlobServiceClient(
                Uri(
                    $"https://{configuration.StorageAccountName}.blob.core.windows.net"
                ),
                credential
            )

        let containerClient =
            serviceClient.GetBlobContainerClient(configuration.ShareContainer)

        { ReadPropertiesExact =
            fun blobName cancellationToken ->
                tryReadExact
                    (fun () ->
                        task {
                            let! response =
                                containerClient
                                    .GetBlobClient(blobName)
                                    .GetPropertiesAsync(
                                        cancellationToken =
                                            cancellationToken
                                    )

                            return
                                response.Value.Metadata
                                |> metadataMap
                        })
          ReadExact =
            fun blobName cancellationToken ->
                tryReadExact
                    (fun () ->
                        task {
                            let! response =
                                containerClient
                                    .GetBlobClient(blobName)
                                    .DownloadContentAsync(
                                        cancellationToken
                                    )

                            let download = response.Value

                            return
                                { Content =
                                    download.Content.ToMemory()
                                  Metadata =
                                    download.Details.Metadata
                                    |> metadataMap }
                        }) }
