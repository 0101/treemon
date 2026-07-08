module CreateWorktreeModal

open Shared
open Navigation
open Elmish
open Feliz

type CreateWorktreeForm =
    { RepoId: RepoId
      Branches: string list
      Name: string
      BaseBranch: string
      Prompt: string }

type ModalState =
    | Closed
    | LoadingBranches of RepoId
    | Open of CreateWorktreeForm
    | Creating of RepoId
    | CreateError of repoId: RepoId * message: string
    | CreateWarning of repoId: RepoId * messages: string list

type Msg =
    | OpenCreateWorktree of RepoId
    | BranchesLoaded of Result<string list, exn>
    | SetNewWorktreeName of string
    | SetBaseBranch of string
    | SetPrompt of string
    | SubmitCreateWorktree
    | CreateWorktreeCompleted of Result<string list, string>
    | CloseCreateModal

let repoId =
    function
    | Closed -> None
    | LoadingBranches repoId -> Some repoId
    | Open form -> Some form.RepoId
    | Creating repoId -> Some repoId
    | CreateError (repoId, _) -> Some repoId
    | CreateWarning (repoId, _) -> Some repoId

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
    match msg, modal with
    | OpenCreateWorktree rid, _ ->
        { Modal = LoadingBranches rid; RestoredFocus = None; RefreshWorktrees = false },
        Cmd.OfAsync.either api.Value.getBranches (RepoId.value rid) (Ok >> BranchesLoaded) (Error >> BranchesLoaded)

    | BranchesLoaded (Ok branches), LoadingBranches rid ->
        let baseBranch = branches |> List.tryHead |> Option.defaultValue ""
        just (Open { RepoId = rid; Branches = branches; Name = ""; BaseBranch = baseBranch; Prompt = "" })
    | BranchesLoaded (Ok _), _ -> just modal

    | BranchesLoaded (Error _), LoadingBranches rid -> just (CreateError (rid, "Failed to load branches"))
    | BranchesLoaded (Error _), _ -> just modal

    | SetNewWorktreeName name, Open form -> just (Open { form with Name = name })
    | SetNewWorktreeName _, _ -> just modal

    | SetBaseBranch branch, Open form -> just (Open { form with BaseBranch = branch })
    | SetBaseBranch _, _ -> just modal

    | SetPrompt prompt, Open form -> just (Open { form with Prompt = prompt })
    | SetPrompt _, _ -> just modal

    | SubmitCreateWorktree, Open form when form.Name.Trim().Length > 0 ->
        let request: CreateWorktreeRequest =
            { RepoId = RepoId.value form.RepoId
              BranchName = BranchName.create (form.Name.Trim())
              BaseBranch = BranchName.create form.BaseBranch
              Prompt = (let t = form.Prompt.Trim() in if t = "" then None else Some t) }
        { Modal = Creating form.RepoId; RestoredFocus = None; RefreshWorktrees = false },
        Cmd.OfAsync.perform api.Value.createWorktree request CreateWorktreeCompleted
    | SubmitCreateWorktree, _ -> just modal

    | CreateWorktreeCompleted (Ok warnings), Creating rid when not (List.isEmpty warnings) ->
        { Modal = CreateWarning (rid, warnings); RestoredFocus = None; RefreshWorktrees = true }, Cmd.none
    | CreateWorktreeCompleted (Ok _), _ ->
        let restored = repoId modal |> Option.map RepoHeader
        { Modal = Closed; RestoredFocus = restored; RefreshWorktrees = true }, Cmd.none

    | CreateWorktreeCompleted (Error errorMsg), Creating rid -> just (CreateError (rid, errorMsg))
    | CreateWorktreeCompleted (Error _), _ -> just modal

    | CloseCreateModal, _ ->
        let restored = repoId modal |> Option.map RepoHeader
        { Modal = Closed; RestoredFocus = restored; RefreshWorktrees = false }, Cmd.none

let private modalOverlay (dispatch: Msg -> unit) (dismissible: bool) (children: ReactElement list) =
    let onDismiss = if dismissible then Some (fun () -> dispatch CloseCreateModal) else None
    ModalOverlay.modalOverlay onDismiss children

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
                        prop.onKeyDown (fun e ->
                            if e.key = "Escape" then dispatch CloseCreateModal)
                        prop.children (
                            form.Branches
                            |> List.map (fun b ->
                                Html.option [ prop.value b; prop.text b ]))
                    ]
                    Html.textarea [
                        prop.className "modal-textarea"
                        prop.rows 3
                        prop.placeholder "Optional prompt — a non-empty value launches an investigate session in the new worktree"
                        prop.value form.Prompt
                        prop.onChange (fun (v: string) -> dispatch (SetPrompt v))
                        prop.onKeyDown (fun e ->
                            // Enter inserts a newline (default); it must not submit. Escape closes.
                            if e.key = "Escape" then dispatch CloseCreateModal)
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

    | CreateWarning (_, messages) ->
        modalOverlay dispatch true [
            Html.div [
                prop.className "modal-header warning"
                prop.text "Worktree created — with warnings"
            ]
            Html.div [
                prop.className "modal-body"
                prop.children (
                    messages
                    |> List.map (fun message ->
                        Html.div [ prop.className "modal-warning-message"; prop.text message ]))
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
