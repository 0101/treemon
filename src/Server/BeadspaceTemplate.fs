module Server.BeadspaceTemplate

/// The Beadspace dashboard HTML.
///
/// Single source of truth: the `BeadspaceTemplate.html` file, embedded into this
/// assembly as a resource (see the `<EmbeddedResource>` entry in Server.fsproj) and
/// read once here at module initialization. The E2E tests (`BeadspaceCanvasTests`)
/// read that same file from disk, so the runtime template and the tested template are
/// one definition and cannot drift.
let html = EmbeddedResource.readText "BeadspaceTemplate.html"
