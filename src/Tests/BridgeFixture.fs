module Tests.BridgeFixture

open System.Net
open Tests.TestUtils

let withBridgesAt ports action =
    let rec bind remaining opened =
        match remaining with
        | [] -> action (List.rev opened)
        | port :: rest ->
            if port = 5000 || port = 5002 then
                invalidArg (nameof ports) "Bridge fixtures require non-production ports"

            use listener = new HttpListener()
            let url = $"http://127.0.0.1:{port}/"
            listener.Prefixes.Add url
            listener.Start()
            bind rest ((listener, url) :: opened)

    bind ports []

let withBridges count action =
    withBridgesAt (getFreeTcpPorts count) action
