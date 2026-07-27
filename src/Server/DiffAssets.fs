module Server.DiffAssets

open Microsoft.AspNetCore.Http

type Asset =
    { ContentType: string
      Content: string }

let [<Literal>] Version = "3.4.52"

let private assetRoot = $"/assets/diff2html/{Version}"

let cssPath = $"{assetRoot}/diff2html.min.css"
let rendererPath = $"{assetRoot}/diff2html.min.js"
let highlighterPath = $"{assetRoot}/diff2html-ui-slim.min.js"

let private css =
    lazy (EmbeddedResource.readText "Diff2HtmlCss")

let private renderer =
    lazy (EmbeddedResource.readText "Diff2HtmlRenderer")

let private highlighter =
    lazy (EmbeddedResource.readText "Diff2HtmlHighlighter")

let tryFind path =
    match path with
    | value when value = cssPath ->
        Some
            { ContentType = "text/css; charset=utf-8"
              Content = css.Value }
    | value when value = rendererPath ->
        Some
            { ContentType = "text/javascript; charset=utf-8"
              Content = renderer.Value }
    | value when value = highlighterPath ->
        Some
            { ContentType = "text/javascript; charset=utf-8"
              Content = highlighter.Value }
    | _ ->
        None

let writeResponse (asset: Asset) (ctx: HttpContext) = task {
    ctx.Response.ContentType <- asset.ContentType
    ctx.Response.Headers["Cache-Control"] <- "public, max-age=31536000, immutable"
    do! ctx.Response.WriteAsync(asset.Content)
}
