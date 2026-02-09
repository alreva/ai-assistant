// VoiceAgent/Services/AgentService.cs
using VoiceAgent.Models;

namespace VoiceAgent.Services;

public class AgentService : IAgentService
{
    private readonly IMcpClientService _mcpClient;
    private readonly SessionManager _sessionManager;
    private readonly IntentClassifier _intentClassifier;

    public AgentService(
        IMcpClientService mcpClient,
        SessionManager sessionManager,
        IntentClassifier intentClassifier)
    {
        _mcpClient = mcpClient;
        _sessionManager = sessionManager;
        _intentClassifier = intentClassifier;
    }

    public async Task<AgentResponse> ProcessMessageAsync(string sessionId, string text)
    {
        var session = _sessionManager.GetOrCreateSession(sessionId);
        var intent = _intentClassifier.ClassifyIntent(text);

        // Check for expired confirmation
        if (session.HasPendingConfirmation && _sessionManager.IsConfirmationExpired(session))
        {
            session.ClearPendingConfirmation();
            // Continue processing the new message
        }

        // Handle confirmation/cancellation if pending
        if (session.HasPendingConfirmation)
        {
            if (intent == IntentType.Confirmation)
            {
                var result = await _mcpClient.ExecuteUpdateAsync(
                    session.PendingAction!,
                    session.PendingActionDescription!);
                session.ClearPendingConfirmation();
                return new AgentResponse { Text = result, AwaitingConfirmation = false };
            }

            if (intent == IntentType.Cancellation)
            {
                session.ClearPendingConfirmation();
                return new AgentResponse { Text = "Cancelled. No changes made.", AwaitingConfirmation = false };
            }

            // New command while confirmation pending - cancel old and process new
            session.ClearPendingConfirmation();
        }

        return intent switch
        {
            IntentType.Query => await HandleQueryAsync(text),
            IntentType.Update => await HandleUpdateAsync(session, text),
            IntentType.EndSession => HandleEndSession(sessionId),
            _ => new AgentResponse { Text = "I'm not sure what you mean. Could you rephrase that?" }
        };
    }

    private async Task<AgentResponse> HandleQueryAsync(string text)
    {
        var result = await _mcpClient.ExecuteQueryAsync(text);
        return new AgentResponse { Text = result, AwaitingConfirmation = false };
    }

    private async Task<AgentResponse> HandleUpdateAsync(Session session, string text)
    {
        var summary = await _mcpClient.GetActionSummaryAsync(text);
        session.SetPendingConfirmation("update", text);
        return new AgentResponse
        {
            Text = $"{summary} Say yes to confirm or no to cancel.",
            AwaitingConfirmation = true
        };
    }

    private AgentResponse HandleEndSession(string sessionId)
    {
        _sessionManager.EndSession(sessionId);
        return new AgentResponse { Text = "Goodbye. Session ended.", AwaitingConfirmation = false };
    }
}
