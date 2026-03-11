module ModalOverlay

open Feliz

let modalOverlay (onDismiss: (unit -> unit) option) (children: ReactElement list) =
    Html.div [
        prop.className "modal-overlay"
        match onDismiss with
        | Some dismiss -> prop.onClick (fun _ -> dismiss ())
        | None -> ()
        prop.children [
            Html.div [
                prop.className "modal-dialog"
                if onDismiss.IsSome then prop.onClick (fun e -> e.stopPropagation())
                prop.children children
            ]
        ]
    ]
