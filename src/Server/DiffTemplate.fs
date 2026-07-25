module Server.DiffTemplate

/// The generated worktree diff viewer HTML.
///
/// DiffTemplate.html is embedded into the server assembly and is also read directly by
/// browser tests, so provisioning and tested behavior share one source of truth.
let html = EmbeddedResource.readText "DiffTemplate.html"
