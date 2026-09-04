module ModalOverlay

open Feliz

let private withClass baseClass extraClass =
    if System.String.IsNullOrWhiteSpace extraClass then
        baseClass
    else
        $"{baseClass} {extraClass}"

let modalOverlayWithClasses overlayClass dialogClass (onDismiss: (unit -> unit) option) (children: ReactElement list) =
    Html.div [
        prop.className (withClass "modal-overlay" overlayClass)
        match onDismiss with
        | Some dismiss -> prop.onClick (fun _ -> dismiss ())
        | None -> ()
        prop.children [
            Html.div [
                prop.className (withClass "modal-dialog" dialogClass)
                if onDismiss.IsSome then prop.onClick (fun e -> e.stopPropagation())
                prop.children children
            ]
        ]
    ]

let modalOverlay onDismiss children =
    modalOverlayWithClasses "" "" onDismiss children
