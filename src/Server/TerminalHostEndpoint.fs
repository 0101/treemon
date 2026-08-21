module Server.TerminalHostEndpoint

open System

let internal isLoopbackHttpUri (endpoint: Uri) =
    endpoint.Scheme = Uri.UriSchemeHttp
    && endpoint.Host = "127.0.0.1"
    && endpoint.Port > 0
    && endpoint.Port <= 65_535
    && String.IsNullOrEmpty endpoint.Query
    && String.IsNullOrEmpty endpoint.Fragment
    && String.IsNullOrEmpty endpoint.UserInfo
