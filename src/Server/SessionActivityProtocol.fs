module Server.SessionActivityProtocol

open System
open System.Globalization
open FsToolkit.ErrorHandling
open Server.SessionActivity
open Shared

[<CLIMutable>]
type MessageDto =
    { text: string
      at: string }

[<CLIMutable>]
type SessionActivityRequest =
    { parentProcessId: int
      sessionId: string
      terminalSessionId: string
      worktreePath: string
      provider: string
      eventId: string
      occurredAt: string
      kind: string
      message: MessageDto
      skillName: string
      toolCallId: string
      currentTokens: int
      tokenLimit: int }

[<RequireQualifiedAccess>]
type ProtocolError =
    | MissingBody
    | InvalidParentProcessId
    | MissingWorktreePath
    | MissingEventId
    | MissingOccurredAt
    | MissingKind
    | InvalidSessionId of reason: string
    | InvalidTerminalSessionId of reason: string
    | UnknownProvider of provider: string
    | MalformedTimestamp of value: string
    | MissingMessage
    | MissingMessageText
    | MissingToolCallId of kind: string
    | ToolCallIdTooLong of kind: string * maximum: int
    | SkillNameRequired
    | InvalidTokenLimit
    | UnknownKind of kind: string
    | InvalidWorktreePath

let errorMessage =
    function
    | ProtocolError.MissingBody -> "missing body"
    | ProtocolError.InvalidParentProcessId ->
        "missing or invalid parentProcessId"
    | ProtocolError.MissingWorktreePath -> "missing worktreePath"
    | ProtocolError.MissingEventId -> "missing eventId"
    | ProtocolError.MissingOccurredAt -> "missing occurredAt"
    | ProtocolError.MissingKind -> "missing kind"
    | ProtocolError.InvalidSessionId reason
    | ProtocolError.InvalidTerminalSessionId reason -> reason
    | ProtocolError.UnknownProvider provider ->
        $"unknown provider '{provider}'"
    | ProtocolError.MalformedTimestamp value ->
        $"malformed timestamp '{value}'"
    | ProtocolError.MissingMessage -> "missing message"
    | ProtocolError.MissingMessageText -> "missing message text"
    | ProtocolError.MissingToolCallId kind ->
        $"{kind} requires toolCallId"
    | ProtocolError.ToolCallIdTooLong(kind, maximum) ->
        $"{kind} toolCallId exceeds {maximum} characters"
    | ProtocolError.SkillNameRequired ->
        "skill_invoked requires skillName"
    | ProtocolError.InvalidTokenLimit ->
        "usage_info requires tokenLimit > 0"
    | ProtocolError.UnknownKind kind -> $"unknown kind '{kind}'"
    | ProtocolError.InvalidWorktreePath -> "invalid worktreePath"

let private parseProvider =
    function
    | "copilot_cli" -> Ok CopilotCli
    | other ->
        other
        |> Option.ofObj
        |> Option.defaultValue ""
        |> ProtocolError.UnknownProvider
        |> Error

let private parseTerminalSessionId value =
    match value |> Option.filter (String.IsNullOrWhiteSpace >> not) with
    | None -> Ok None
    | Some raw ->
        TerminalSessionId.create raw
        |> Result.map Some
        |> Result.mapError ProtocolError.InvalidTerminalSessionId

let internal maxSessionIdLength = SessionId.maxLength

let private tryParseTimestamp (value: string) =
    match
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind
        )
    with
    | true, timestamp -> Ok timestamp
    | false, _ ->
        value
        |> Option.ofObj
        |> Option.defaultValue ""
        |> ProtocolError.MalformedTimestamp
        |> Error

let internal maxTextLength = 8192

let internal capText (value: string) =
    if isNull value || value.Length <= maxTextLength then
        value
    else
        value.Substring(0, maxTextLength)

let internal maxToolCallIdLength = 512

let private parseToolCallId kind (toolCallId: string) =
    if String.IsNullOrWhiteSpace toolCallId then
        Error(ProtocolError.MissingToolCallId kind)
    elif toolCallId.Length > maxToolCallIdLength then
        Error(ProtocolError.ToolCallIdTooLong(kind, maxToolCallIdLength))
    else
        Ok toolCallId

let internal futureSkewAllowance = TimeSpan.FromMinutes 5.0

let internal clampFutureTimestamp
    (now: DateTimeOffset)
    (timestamp: DateTimeOffset)
    =
    if timestamp > now + futureSkewAllowance then now else timestamp

let private parseMessage =
    function
    | None -> Error ProtocolError.MissingMessage
    | Some dto when String.IsNullOrWhiteSpace dto.text ->
        Error ProtocolError.MissingMessageText
    | Some dto ->
        tryParseTimestamp dto.at
        |> Result.map (fun timestamp ->
            { Text = capText dto.text
              At = timestamp })

let internal parseEvent
    (occurredAt: DateTimeOffset)
    (kind: string)
    (message: MessageDto option)
    (skillName: string)
    (toolCallId: string)
    (currentTokens: int)
    (tokenLimit: int)
    =
    match kind with
    | "session_present" -> Ok SessionPresent
    | "session_closed" -> Ok SessionClosed
    | "turn_started" -> Ok TurnStarted
    | "turn_ended" -> Ok TurnEnded
    | "went_idle" -> Ok WentIdle
    | "heartbeat" -> Ok Heartbeat
    | "user_prompt" -> parseMessage message |> Result.map UserPrompt
    | "assistant_message" ->
        parseMessage message |> Result.map AssistantMessage
    | "intent_reported" ->
        parseMessage message |> Result.map IntentReported
    | "title_reported" ->
        parseMessage message |> Result.map TitleReported
    | "title_bootstrap" ->
        parseMessage message |> Result.map TitleBootstrap
    | "user_input_completed" -> Ok(UserInputCompleted occurredAt)
    | "background_agent_started" ->
        parseToolCallId kind toolCallId
        |> Result.map (fun identity ->
            BackgroundAgentStarted(identity, occurredAt))
    | "background_agent_finished" ->
        parseToolCallId kind toolCallId
        |> Result.map (fun identity ->
            BackgroundAgentFinished(identity, occurredAt))
    | "skill_invoked" ->
        if String.IsNullOrWhiteSpace skillName then
            Error ProtocolError.SkillNameRequired
        else
            Ok(SkillInvoked(capText skillName))
    | "awaiting_user_input" ->
        match message with
        | None -> Ok(AwaitingUserInput(None, occurredAt))
        | Some dto when String.IsNullOrWhiteSpace dto.text ->
            Ok(AwaitingUserInput(None, occurredAt))
        | Some dto ->
            tryParseTimestamp dto.at
            |> Result.map (fun timestamp ->
                AwaitingUserInput(
                    Some
                        { Text = capText dto.text
                          At = timestamp },
                    occurredAt
                ))
    | "usage_info" ->
        if tokenLimit <= 0 then
            Error ProtocolError.InvalidTokenLimit
        else
            Ok(UsageInfo(max 0 currentTokens, tokenLimit))
    | other ->
        other
        |> Option.ofObj
        |> Option.defaultValue ""
        |> ProtocolError.UnknownKind
        |> Error

let private withMessageTimestamp timestamp =
    function
    | UserPrompt message ->
        UserPrompt { message with At = timestamp }
    | AssistantMessage message ->
        AssistantMessage { message with At = timestamp }
    | IntentReported message ->
        IntentReported { message with At = timestamp }
    | TitleReported message ->
        TitleReported { message with At = timestamp }
    | TitleBootstrap message ->
        TitleBootstrap { message with At = timestamp }
    | AwaitingUserInput(Some message, _) ->
        AwaitingUserInput(Some { message with At = timestamp }, timestamp)
    | AwaitingUserInput(None, _) ->
        AwaitingUserInput(None, timestamp)
    | UserInputCompleted _ -> UserInputCompleted timestamp
    | event -> event

let private parseWorktreePath path =
    try
        path
        |> PathUtils.normalizePath
        |> WorktreePath
        |> Ok
    with
    | :? ArgumentException
    | :? NotSupportedException
    | :? System.IO.IOException
    | :? System.Security.SecurityException ->
        Error ProtocolError.InvalidWorktreePath

let parseReport
    (now: DateTimeOffset)
    (request: SessionActivityRequest)
    =
    match Option.ofObj request with
    | None -> Error ProtocolError.MissingBody
    | Some request ->
        let message = Option.ofObj request.message

        result {
            if request.parentProcessId <= 0 then
                return! Error ProtocolError.InvalidParentProcessId

            if String.IsNullOrWhiteSpace request.worktreePath then
                return! Error ProtocolError.MissingWorktreePath

            if String.IsNullOrWhiteSpace request.eventId then
                return! Error ProtocolError.MissingEventId

            if String.IsNullOrWhiteSpace request.occurredAt then
                return! Error ProtocolError.MissingOccurredAt

            if String.IsNullOrWhiteSpace request.kind then
                return! Error ProtocolError.MissingKind

            let! sessionId =
                SessionId.create request.sessionId
                |> Result.mapError ProtocolError.InvalidSessionId

            let! provider = parseProvider request.provider
            let! worktreePath = parseWorktreePath request.worktreePath

            let! terminalSessionId =
                request.terminalSessionId
                |> Option.ofObj
                |> parseTerminalSessionId

            let! rawOccurredAt = tryParseTimestamp request.occurredAt
            let occurredAt = clampFutureTimestamp now rawOccurredAt

            let! event =
                parseEvent
                    occurredAt
                    request.kind
                    message
                    request.skillName
                    request.toolCallId
                    request.currentTokens
                    request.tokenLimit

            return
                { ParentProcessId = request.parentProcessId
                  SessionId = sessionId
                  TerminalSessionId = terminalSessionId
                  WorktreePath = worktreePath
                  Provider = provider
                  EventId = EventId request.eventId
                  OccurredAt = occurredAt
                  Event = withMessageTimestamp occurredAt event }
        }
