namespace CanvasShareViewer

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Azure
open Azure.Core
open Azure.Storage.Blobs

type internal BlobDocument =
    { Content: ReadOnlyMemory<byte>
      Metadata: Map<string, string> }

type internal BlobReader =
    { ReadExact:
        string ->
        CancellationToken ->
        Task<BlobDocument option> }

module internal BlobStorage =

    let private metadataMap
        (values: IDictionary<string, string>)
        =
        values
        |> Seq.map (fun pair -> pair.Key, pair.Value)
        |> Map.ofSeq

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

        { ReadExact =
            fun blobName cancellationToken ->
                task {
                    try
                        let! response =
                            containerClient
                                .GetBlobClient(blobName)
                                .DownloadContentAsync(cancellationToken)

                        let download = response.Value

                        return
                            Some
                                { Content = download.Content.ToMemory()
                                  Metadata =
                                    download.Details.Metadata
                                    |> metadataMap }
                    with
                    | :? RequestFailedException as ex when
                        ex.Status = 404
                        ->
                        return None
                } }
