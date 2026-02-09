namespace VoiceAgent.Models;

public enum ConversationRole
{
    User,
    Assistant,
    Tool
}

public record ConversationMessage(
    ConversationRole Role,
    string? Content,
    string? ToolCallId = null,
    string? ToolName = null,
    Dictionary<string, object?>? ToolArguments = null,
    string? ToolResult = null);

public record PendingToolExecution(
    string ToolCallId,
    string ToolName,
    Dictionary<string, object?> Arguments,
    string ConfirmationPrompt);

public class Session
{
    private const int MaxHistoryMessages = 50;

    public string SessionId { get; }
    public DateTime LastActivityTime { get; set; }
    public DateTime? ConfirmationRequestedAt { get; set; }
    public List<ConversationMessage> ConversationHistory { get; } = new();
    public PendingToolExecution? PendingToolExecution { get; private set; }
    public CharacterConfig? Character { get; set; }

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

    public void AddUserMessage(string content)
    {
        ConversationHistory.Add(new ConversationMessage(ConversationRole.User, content));
        TrimHistory();
        TouchActivity();
    }

    public void AddAssistantMessage(string content)
    {
        ConversationHistory.Add(new ConversationMessage(ConversationRole.Assistant, content));
        TrimHistory();
        TouchActivity();
    }

    public void AddToolCall(string toolCallId, string toolName, Dictionary<string, object?> arguments)
    {
        ConversationHistory.Add(new ConversationMessage(
            ConversationRole.Assistant,
            Content: null,
            ToolCallId: toolCallId,
            ToolName: toolName,
            ToolArguments: arguments));
        TrimHistory();
        TouchActivity();
    }

    public void AddToolResult(string toolCallId, string toolName, string result)
    {
        ConversationHistory.Add(new ConversationMessage(
            ConversationRole.Tool,
            Content: result,
            ToolCallId: toolCallId,
            ToolName: toolName));
        TrimHistory();
        TouchActivity();
    }

    public void SetPendingToolExecution(string toolCallId, string toolName, Dictionary<string, object?> arguments, string confirmationPrompt)
    {
        PendingToolExecution = new PendingToolExecution(toolCallId, toolName, arguments, confirmationPrompt);
        ConfirmationRequestedAt = DateTime.UtcNow;
    }

    public void ClearPendingToolExecution()
    {
        PendingToolExecution = null;
        ConfirmationRequestedAt = null;
    }

    public bool HasPendingConfirmation => PendingToolExecution != null;

    public void TouchActivity()
    {
        LastActivityTime = DateTime.UtcNow;
    }

    private void TrimHistory()
    {
        while (ConversationHistory.Count > MaxHistoryMessages)
        {
            ConversationHistory.RemoveAt(0);
        }
    }
}
