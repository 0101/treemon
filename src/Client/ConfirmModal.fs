module ConfirmModal

open Shared
open Feliz

type ConfirmModal =
    | NoConfirm
    | ConfirmDelete of branch: string * path: WorktreePath * hasSession: bool
    | ConfirmArchive of branch: string * path: WorktreePath

type Msg =
    | DeleteWorktree of path: WorktreePath
    | DeleteAndCloseSession of path: WorktreePath
    | ArchiveWorktree of path: WorktreePath
    | ArchiveAndCloseSession of path: WorktreePath
    | DismissConfirm

type Action =
    | NoAction
    | Delete of path: WorktreePath
    | DeleteAfterKillSession of path: WorktreePath
    | Archive of path: WorktreePath
    | ArchiveAfterKillSession of path: WorktreePath

let update (msg: Msg) : ConfirmModal * Action =
    match msg with
    | DeleteWorktree path -> NoConfirm, Delete path
    | DeleteAndCloseSession path -> NoConfirm, DeleteAfterKillSession path
    | ArchiveWorktree path -> NoConfirm, Archive path
    | ArchiveAndCloseSession path -> NoConfirm, ArchiveAfterKillSession path
    | DismissConfirm -> NoConfirm, NoAction

let view (dispatch: Msg -> unit) (confirm: ConfirmModal) =
    match confirm with
    | NoConfirm -> Html.none
    | ConfirmDelete (branch, path, hasSession) ->
        ModalOverlay.modalOverlay (Some (fun () -> dispatch DismissConfirm)) [
            Html.div [ prop.className "modal-header"; prop.text "Remove worktree" ]
            Html.div [
                prop.className "modal-body"
                prop.children [
                    Html.p [
                        Html.text "Remove worktree "
                        Html.code [ prop.text branch ]
                        Html.text "? This will delete the worktree folder and local branch."
                    ]
                ]
            ]
            Html.div [
                prop.className "modal-footer"
                prop.children [
                    Html.button [
                        prop.className "modal-btn cancel"
                        prop.onClick (fun _ -> dispatch DismissConfirm)
                        prop.text "Cancel"
                    ]
                    Html.button [
                        prop.className "modal-btn danger"
                        if not hasSession then prop.autoFocus true
                        prop.onClick (fun _ -> dispatch (DeleteWorktree path))
                        prop.text "Delete"
                    ]
                    if hasSession then
                        Html.button [
                            prop.className "modal-btn danger"
                            prop.autoFocus true
                            prop.onClick (fun _ -> dispatch (DeleteAndCloseSession path))
                            prop.text "Delete and close terminal"
                        ]
                ]
            ]
        ]
    | ConfirmArchive (branch, path) ->
        ModalOverlay.modalOverlay (Some (fun () -> dispatch DismissConfirm)) [
            Html.div [ prop.className "modal-header"; prop.text "Archive worktree" ]
            Html.div [
                prop.className "modal-body"
                prop.children [
                    Html.span [ prop.text "This worktree has an active terminal session." ]
                ]
            ]
            Html.div [
                prop.className "modal-footer"
                prop.children [
                    Html.button [
                        prop.className "modal-btn cancel"
                        prop.onClick (fun _ -> dispatch DismissConfirm)
                        prop.text "Cancel"
                    ]
                    Html.button [
                        prop.className "modal-btn submit"
                        prop.onClick (fun _ -> dispatch (ArchiveWorktree path))
                        prop.text "Archive"
                    ]
                    Html.button [
                        prop.className "modal-btn danger"
                        prop.autoFocus true
                        prop.onClick (fun _ -> dispatch (ArchiveAndCloseSession path))
                        prop.text "Archive and close terminal"
                    ]
                ]
            ]
        ]
