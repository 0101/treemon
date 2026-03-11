module ConfirmModal

open Shared
open Feliz

type ConfirmModal =
    | NoConfirm
    | ConfirmDelete of branch: string * path: WorktreePath * hasSession: bool
    | ConfirmArchive of branch: string * path: WorktreePath

type Msg =
    | DeleteWorktree of branch: string
    | DeleteAndCloseSession of path: WorktreePath * branch: string
    | ArchiveWorktree of branch: string
    | ArchiveAndCloseSession of path: WorktreePath * branch: string
    | DismissConfirm

type Action =
    | NoAction
    | Delete of branch: string
    | DeleteAfterKillSession of path: WorktreePath
    | Archive of branch: string
    | ArchiveAfterKillSession of path: WorktreePath

let update (msg: Msg) : ConfirmModal * Action =
    match msg with
    | DeleteWorktree branch -> NoConfirm, Delete branch
    | DeleteAndCloseSession (path, _) -> NoConfirm, DeleteAfterKillSession path
    | ArchiveWorktree branch -> NoConfirm, Archive branch
    | ArchiveAndCloseSession (path, _) -> NoConfirm, ArchiveAfterKillSession path
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
                    Html.span [ prop.text $"Remove worktree " ]
                    Html.code [ prop.text branch ]
                    Html.span [ prop.text "? This will delete the worktree folder and local branch." ]
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
                        prop.onClick (fun _ -> dispatch (DeleteWorktree branch))
                        prop.text "Delete"
                    ]
                    if hasSession then
                        Html.button [
                            prop.className "modal-btn danger"
                            prop.autoFocus true
                            prop.onClick (fun _ -> dispatch (DeleteAndCloseSession (path, branch)))
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
                        prop.onClick (fun _ -> dispatch (ArchiveWorktree branch))
                        prop.text "Archive"
                    ]
                    Html.button [
                        prop.className "modal-btn danger"
                        prop.autoFocus true
                        prop.onClick (fun _ -> dispatch (ArchiveAndCloseSession (path, branch)))
                        prop.text "Archive and close terminal"
                    ]
                ]
            ]
        ]
