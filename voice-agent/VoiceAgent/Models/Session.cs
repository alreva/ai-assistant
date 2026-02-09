namespace VoiceAgent.Models;

public class Session
{
    public string SessionId { get; }
    public DateTime LastActivityTime { get; set; }
    public DateTime? ConfirmationRequestedAt { get; set; }
    public string? PendingAction { get; private set; }
    public string? PendingActionDescription { get; private set; }

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

    public void ClearPendingConfirmation()
    {
        PendingAction = null;
        PendingActionDescription = null;
        ConfirmationRequestedAt = null;
    }

    public bool HasPendingConfirmation => PendingAction != null;

    public void TouchActivity()
    {
        LastActivityTime = DateTime.UtcNow;
    }
}
