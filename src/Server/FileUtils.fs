module Server.FileUtils

/// Collapse a message to a single line and cap it at `maxLen`, appending an ellipsis when truncated.
/// Used for the card's last-user / last-assistant preview lines.
let truncateMessage (maxLen: int) (text: string) =
    let singleLine = text.Replace("\r", "").Replace("\n", " ").Trim()
    if singleLine.Length <= maxLen then singleLine
    else singleLine[.. maxLen - 1].TrimEnd() + "..."
