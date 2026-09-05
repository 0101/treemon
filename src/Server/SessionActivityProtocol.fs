module Server.SessionActivityProtocol

open System
open System.Globalization
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

let private parseProvider =
    function
    | "copilot_cli" -> Ok CopilotCli
    | other -> Error $"unknown provider '{other}'"

let private parseTerminalSessionId value =
    match value |> Option.filter (String.IsNullOrWhiteSpace >> not) with
    | None -> Ok None
    | Some raw ->
        TerminalSessionId.create raw |> Result.map Some

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
    | false, _ -> Error $"malformed timestamp '{value}'"

let internal maxTextLength = 8192

let internal capText (value: string) =
    if isNull value || value.Length <= maxTextLength then
        value
    else
        value.Substring(0, maxTextLength)

let internal maxToolCallIdLength = 512

let private parseToolCallId kind (toolCallId: string) =
    if String.IsNullOrWhiteSpace toolCallId then
        Error $"{kind} requires toolCallId"
    elif toolCallId.Length > maxToolCallIdLength then
        Error
            $"{kind} toolCallId exceeds {maxToolCallIdLength} characters"
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
    | None -> Error "missing message"
    | Some dto when String.IsNullOrWhiteSpace dto.text ->
        Error "missing message text"
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
            Error "skill_invoked requires skillName"
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
            Error "usage_info requires tokenLimit > 0"
        else
            Ok(UsageInfo(max 0 currentTokens, tokenLimit))
    | other -> Error $"unknown kind '{other}'"

let internal kindText =
    function
    | SessionPresent -> "session_present"
    | SessionClosed -> "session_closed"
    | TurnStarted -> "turn_started"
    | UserPrompt _ -> "user_prompt"
    | AssistantMessage _ -> "assistant_message"
    | SkillInvoked _ -> "skill_invoked"
    | IntentReported _ -> "intent_reported"
    | TitleReported _ -> "title_reported"
    | TitleBootstrap _ -> "title_bootstrap"
    | AwaitingUserInput _ -> "awaiting_user_input"
    | UserInputCompleted _ -> "user_input_completed"
    | BackgroundAgentStarted _ -> "background_agent_started"
    | BackgroundAgentFinished _ -> "background_agent_finished"
    | TurnEnded -> "turn_ended"
    | WentIdle -> "went_idle"
    | Heartbeat -> "heartbeat"
    | UsageInfo _ -> "usage_info"

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

let parseReport
    (now: DateTimeOffset)
    (request: SessionActivityRequest)
    =
    match Option.ofObj request with
    | None -> Error "missing body"
    | Some request ->
        let message = Option.ofObj request.message

        if request.parentProcessId <= 0 then
            Error "missing or invalid parentProcessId"
        elif String.IsNullOrWhiteSpace request.worktreePath then
            Error "missing worktreePath"
        elif String.IsNullOrWhiteSpace request.eventId then
            Error "missing eventId"
        elif String.IsNullOrWhiteSpace request.occurredAt then
            Error "missing occurredAt"
        elif String.IsNullOrWhiteSpace request.kind then
            Error "missing kind"
        else
            SessionId.create request.sessionId
            |> Result.bind (fun sessionId ->
                parseProvider request.provider
                |> Result.bind (fun provider ->
                    parseTerminalSessionId (Option.ofObj request.terminalSessionId)
                    |> Result.bind (fun terminalSessionId ->
                        tryParseTimestamp request.occurredAt
                        |> Result.bind (fun rawOccurredAt ->
                            let occurredAt =
                                clampFutureTimestamp now rawOccurredAt

                            parseEvent
                                occurredAt
                                request.kind
                                message
                                request.skillName
                                request.toolCallId
                                request.currentTokens
                                request.tokenLimit
                            |> Result.map (fun event ->
                                { ParentProcessId = request.parentProcessId
                                  SessionId = sessionId
                                  TerminalSessionId = terminalSessionId
                                  WorktreePath =
                                    WorktreePath(
                                        PathUtils.normalizePath request.worktreePath
                                    )
                                  Provider = provider
                                  EventId = EventId request.eventId
                                  OccurredAt = occurredAt
                                  Event = withMessageTimestamp occurredAt event })))))
