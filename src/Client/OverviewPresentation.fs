module OverviewPresentation

open Shared
open OverviewData

[<RequireQualifiedAccess>]
type OverviewSelection =
    | Agents of AgentGroupKind
    | Tasks of TaskBucketKind

let nextHistoryWindow =
    function
    | None -> Some HistoryWindow.Hours12
    | Some HistoryWindow.Hours12 -> Some HistoryWindow.Hours24
    | Some HistoryWindow.Hours24 -> Some HistoryWindow.Hours72
    | Some HistoryWindow.Hours72 -> None

let historyWindowLabel =
    function
    | HistoryWindow.Hours12 -> "12h"
    | HistoryWindow.Hours24 -> "24h"
    | HistoryWindow.Hours72 -> "72h"

let taskLabel =
    function
    | TaskBucketKind.Planned -> "Planned"
    | TaskBucketKind.Underway -> "Underway"
    | TaskBucketKind.Blocked -> "Blocked"
    | TaskBucketKind.Done -> "Done"
    | TaskBucketKind.ToLand -> "To land"
    | TaskBucketKind.Unattended -> "Unattended"

let taskClass =
    function
    | TaskBucketKind.Planned -> "task-planned"
    | TaskBucketKind.Underway -> "task-underway"
    | TaskBucketKind.Blocked -> "task-blocked"
    | TaskBucketKind.Done -> "task-done"
    | TaskBucketKind.ToLand -> "task-toland"
    | TaskBucketKind.Unattended -> "task-unattended"

let activityLabel =
    function
    | CurrentActivity.Investigating -> "Investigating"
    | CurrentActivity.Planning -> "Planning"
    | CurrentActivity.Executing -> "Executing"
    | CurrentActivity.Reviewing -> "Reviewing"
    | CurrentActivity.PR -> "PR"
    | CurrentActivity.Working -> "Working"

let activityClass =
    function
    | CurrentActivity.Investigating -> "activity-investigating"
    | CurrentActivity.Planning -> "activity-planning"
    | CurrentActivity.Executing -> "activity-executing"
    | CurrentActivity.Reviewing -> "activity-reviewing"
    | CurrentActivity.PR -> "activity-pr"
    | CurrentActivity.Working -> "activity-working"

let agentLabel =
    function
    | AgentGroupKind.Activity activity -> activityLabel activity
    | AgentGroupKind.Waiting -> "Waiting"
    | AgentGroupKind.Idle -> "Idle"

let agentClass =
    function
    | AgentGroupKind.Activity activity -> activityClass activity
    | AgentGroupKind.Waiting -> "activity-waiting"
    | AgentGroupKind.Idle -> "activity-idle"

let selectionPresent selection (overview: Overview) =
    match selection with
    | OverviewSelection.Agents kind -> overview.Agents |> List.exists (fun group -> group.Kind = kind)
    | OverviewSelection.Tasks kind -> overview.Tasks |> List.exists (fun bucket -> bucket.Kind = kind)
