module CanvasSessionPrompt

/// First message for the replacement session started from an AgentDoc's `Start session` button.
let forAgentDoc (worktreePath: string) (filename: string) =
    let docPath = $"{worktreePath}/.agents/canvas/{filename}"

    $"Take over canvas doc: {docPath}\n\n"
    + "This is the first message in a new session. The session that owns the doc is not connected, "
    + "so Treemon started you to take over.\n\n"
    + "Do this before any other work:\n"
    + "1. Use the canvas skill. It explains how the document works and how to handle messages from it.\n"
    + $"2. Call the canvas_take_ownership tool with filename \"{filename}\", so future interactions reach you.\n"
    + "3. Read the file to understand the document and its current state.\n\n"
    + "After claiming it, a pending interaction from the user may arrive. Handle it using the canvas "
    + "skill and update the doc; do not answer only in the terminal. Treemon already renders the file, "
    + "so do not start a server or open a separate preview."
