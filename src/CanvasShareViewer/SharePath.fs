namespace CanvasShareViewer

open System

type internal SharePath =
    private
        { Prefix: string
          Filename: string }

module internal SharePath =

    [<Literal>]
    let PrefixLength = 22

    let private isBase62 character =
        (character >= '0' && character <= '9')
        || (character >= 'A' && character <= 'Z')
        || (character >= 'a' && character <= 'z')

    let tryCreate (prefix: string) (filename: string) =
        let validPrefix =
            prefix.Length = PrefixLength
            && prefix |> Seq.forall isBase62

        let validFilename =
            not (String.IsNullOrEmpty(filename))
            && filename.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            && not (filename.Contains('/'))
            && not (filename.Contains('\\'))

        if validPrefix && validFilename then
            Some
                { Prefix = prefix
                  Filename = filename }
        else
            None

    let blobName path =
        $"{path.Prefix}/{path.Filename}"
