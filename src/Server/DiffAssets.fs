module Server.DiffAssets

open Microsoft.AspNetCore.Http

/// How long a browser may reuse an asset. The vendored diff2html bundle lives behind a version
/// segment and is frozen, so it may be cached forever; the first-party viewer assets are served
/// from a stable URL and must be revalidated so an edited stylesheet or script is picked up.
type CachePolicy =
    | Pinned
    | Revalidated

type Asset =
    { ContentType: string
      Cache: CachePolicy
      Content: string }

let [<Literal>] Version = "3.4.52"

let private pinnedRoot = $"/assets/diff2html/{Version}"

let cssPath = $"{pinnedRoot}/diff2html.min.css"
let rendererPath = $"{pinnedRoot}/diff2html.min.js"
let highlighterPath = $"{pinnedRoot}/diff2html-ui-slim.min.js"

let private viewerRoot = "/assets/diff"

let viewerCssPath = $"{viewerRoot}/viewer.css"
let viewerScriptPath = $"{viewerRoot}/viewer.js"

let private stylesheet = "text/css; charset=utf-8"
let private javascript = "text/javascript; charset=utf-8"

let private asset contentType cache resource =
    lazy
        { ContentType = contentType
          Cache = cache
          Content = EmbeddedResource.readText resource }

let private byPath =
    Map
        [ cssPath, asset stylesheet Pinned "Diff2HtmlCss"
          rendererPath, asset javascript Pinned "Diff2HtmlRenderer"
          highlighterPath, asset javascript Pinned "Diff2HtmlHighlighter"
          viewerCssPath, asset stylesheet Revalidated "DiffViewerCss"
          viewerScriptPath, asset javascript Revalidated "DiffViewerScript" ]

let tryFind path =
    byPath
    |> Map.tryFind path
    |> Option.map _.Value

let private cacheControl cache =
    match cache with
    | Pinned -> "public, max-age=31536000, immutable"
    | Revalidated -> "no-cache"

let writeResponse (asset: Asset) (ctx: HttpContext) = task {
    ctx.Response.ContentType <- asset.ContentType
    ctx.Response.Headers["Cache-Control"] <- cacheControl asset.Cache
    do! ctx.Response.WriteAsync(asset.Content)
}
