module CreateWorktreeModal

open Shared
open Navigation
open Elmish
open Feliz

type CreateWorktreeForm =
    { RepoId: RepoId
      Branches: string list
      Name: string
      BaseBranch: string }

type ModalState =
    | Closed
    | LoadingBranches of RepoId
    | Open of CreateWorktreeForm
    | Creating of RepoId
    | CreateError of repoId: RepoId * message: string

type Msg =
    | OpenCreateWorktree of RepoId
    | BranchesLoaded of Result<string list, exn>
    | SetNewWorktreeName of string
    | SetBaseBranch of string
    | SubmitCreateWorktree
    | CreateWorktreeCompleted of Result<unit, string>
    | CloseCreateModal

let repoId =
    function
    | Closed -> None
    | LoadingBranches repoId -> Some repoId
    | Open form -> Some form.RepoId
    | Creating repoId -> Some repoId
    | CreateError (repoId, _) -> Some repoId

let isOpen =
    function
    | Closed -> false
    | _ -> true

type UpdateResult =
    { Modal: ModalState
      RestoredFocus: FocusTarget option
      RefreshWorktrees: bool }

let private just modal =
    { Modal = modal; RestoredFocus = None; RefreshWorktrees = false }, Cmd.none

let update (api: Lazy<IWorktreeApi>) (msg: Msg) (modal: ModalState) : UpdateResult * Cmd<Msg> =
    match msg with
    | OpenCreateWorktree rid ->
        { Modal = LoadingBranches rid; RestoredFocus = None; RefreshWorktrees = false },
        Cmd.OfAsync.either api.Value.getBranches (RepoId.value rid) (Ok >> BranchesLoaded) (Error >> BranchesLoaded)

    | BranchesLoaded (Ok branches) ->
        match modal with
        | LoadingBranches rid ->
            let baseBranch = branches |> List.tryHead |> Option.defaultValue ""
            just (Open { RepoId = rid; Branches = branches; Name = ""; BaseBranch = baseBranch })
        | _ -> just modal

    | BranchesLoaded (Error _) ->
        match modal with
        | LoadingBranches rid -> just (CreateError (rid, "Failed to load branches"))
        | _ -> just modal

    | SetNewWorktreeName name ->
        match modal with
        | Open form -> just (Open { form with Name = name })
        | _ -> just modal

    | SetBaseBranch branch ->
        match modal with
        | Open form -> just (Open { form with BaseBranch = branch })
        | _ -> just modal

    | SubmitCreateWorktree ->
        match modal with
        | Open form when form.Name.Trim().Length > 0 ->
            let request: CreateWorktreeRequest =
                { RepoId = RepoId.value form.RepoId
                  BranchName = form.Name.Trim()
                  BaseBranch = form.BaseBranch }
            { Modal = Creating form.RepoId; RestoredFocus = None; RefreshWorktrees = false },
            Cmd.OfAsync.perform api.Value.createWorktree request CreateWorktreeCompleted
        | _ -> just modal

    | CreateWorktreeCompleted (Ok _) ->
        let restored = repoId modal |> Option.map RepoHeader
        { Modal = Closed; RestoredFocus = restored; RefreshWorktrees = true }, Cmd.none

    | CreateWorktreeCompleted (Error errorMsg) ->
        match modal with
        | Creating rid -> just (CreateError (rid, errorMsg))
        | _ -> just modal

    | CloseCreateModal ->
        let restored = repoId modal |> Option.map RepoHeader
        { Modal = Closed; RestoredFocus = restored; RefreshWorktrees = false }, Cmd.none

let private modalOverlay (dispatch: Msg -> unit) (dismissible: bool) (children: ReactElement list) =
    Html.div [
        prop.className "modal-overlay"
        if dismissible then prop.onClick (fun _ -> dispatch CloseCreateModal)
        prop.children [
            Html.div [
                prop.className "modal-dialog"
                if dismissible then prop.onClick (fun e -> e.stopPropagation())
                prop.children children
            ]
        ]
    ]

let view (dispatch: Msg -> unit) (modal: ModalState) =
    match modal with
    | Closed -> Html.none

    | LoadingBranches _ ->
        modalOverlay dispatch true [
            Html.div [
                prop.className "modal-body"
                prop.children [ Html.span [ prop.className "modal-loading"; prop.text "Loading branches..." ] ]
            ]
        ]

    | Open form ->
        modalOverlay dispatch true [
            Html.div [
                prop.className "modal-header"
                prop.text "Create worktree"
            ]
            Html.div [
                prop.className "modal-body"
                prop.children [
                    Html.input [
                        prop.className "modal-input"
                        prop.type'.text
                        prop.placeholder "Branch name"
                        prop.value form.Name
                        prop.autoFocus true
                        prop.onChange (fun (v: string) -> dispatch (SetNewWorktreeName v))
                        prop.onKeyDown (fun e ->
                            if e.key = "Enter" then dispatch SubmitCreateWorktree
                            elif e.key = "Escape" then dispatch CloseCreateModal)
                    ]
                    Html.select [
                        prop.className "modal-select"
                        prop.value form.BaseBranch
                        prop.onChange (fun (v: string) -> dispatch (SetBaseBranch v))
                        prop.children (
                            form.Branches
                            |> List.map (fun b ->
                                Html.option [ prop.value b; prop.text b ]))
                    ]
                ]
            ]
            Html.div [
                prop.className "modal-footer"
                prop.children [
                    Html.button [
                        prop.className "modal-btn cancel"
                        prop.onClick (fun _ -> dispatch CloseCreateModal)
                        prop.text "Cancel"
                    ]
                    Html.button [
                        prop.className "modal-btn submit"
                        prop.disabled (form.Name.Trim().Length = 0)
                        prop.onClick (fun _ -> dispatch SubmitCreateWorktree)
                        prop.text "Create"
                    ]
                ]
            ]
        ]

    | Creating _ ->
        modalOverlay dispatch false [
            Html.div [
                prop.className "modal-body creating"
                prop.children [
                    Html.span [ prop.className "creating-text"; prop.text "Creating worktree" ]
                    Html.span [ prop.className "creating-dots" ]
                ]
            ]
        ]

    | CreateError (_, message) ->
        modalOverlay dispatch true [
            Html.div [
                prop.className "modal-header error"
                prop.text "Error"
            ]
            Html.div [
                prop.className "modal-body"
                prop.children [
                    Html.div [ prop.className "modal-error-message"; prop.text message ]
                ]
            ]
            Html.div [
                prop.className "modal-footer"
                prop.children [
                    Html.button [
                        prop.className "modal-btn cancel"
                        prop.onClick (fun _ -> dispatch CloseCreateModal)
                        prop.text "Close"
                    ]
                ]
            ]
        ]
