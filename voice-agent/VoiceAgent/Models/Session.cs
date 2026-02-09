namespace VoiceAgent.Models;

public class Session
{
    public string SessionId { get; }
    public DateTime LastActivityTime { get; set; }
    public DateTime? ConfirmationRequestedAt { get; set; }
    public string? PendingAction { get; private set; }
    public string? PendingActionDescription { get; private set; }

    // For prepared updates
    public string? PendingToolName { get; private set; }
    public Dictionary<string, object?>? PendingToolArguments { get; private set; }
    public string? PendingUserQuery { get; private set; }

    public Session(string sessionId)
    {
        SessionId = sessionId;
        LastActivityTime = DateTime.UtcNow;
    }

    public bool IsExpired(TimeSpan timeout)
    {
        return DateTime.UtcNow - LastActivityTime > timeout;
    }

    public bool IsConfirmationExpired(TimeSpan timeout)
    {
        if (ConfirmationRequestedAt == null) return false;
        return DateTime.UtcNow - ConfirmationRequestedAt.Value > timeout;
    }

    public void SetPendingConfirmation(string action, string description)
    {
        PendingAction = action;
        PendingActionDescription = description;
        ConfirmationRequestedAt = DateTime.UtcNow;
    }

    public void SetPendingUpdate(string toolName, Dictionary<string, object?> arguments, string userQuery)
    {
        PendingAction = "update";
        PendingToolName = toolName;
        PendingToolArguments = arguments;
        PendingUserQuery = userQuery;
        ConfirmationRequestedAt = DateTime.UtcNow;
    }

    public void ClearPendingConfirmation()
    {
        PendingAction = null;
        PendingActionDescription = null;
        PendingToolName = null;
        PendingToolArguments = null;
        PendingUserQuery = null;
        ConfirmationRequestedAt = null;
    }

    public bool HasPendingConfirmation => PendingAction != null;
    public bool HasPendingUpdate => PendingToolName != null;

    public void TouchActivity()
    {
        LastActivityTime = DateTime.UtcNow;
    }
}
