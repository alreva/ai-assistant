// VoiceAgent/Services/AgentService.cs
using Microsoft.Extensions.Logging;
using VoiceAgent.Models;

namespace VoiceAgent.Services;

public class AgentService : IAgentService
{
    private readonly IMcpClientService _mcpClient;
    private readonly SessionManager _sessionManager;
    private readonly IntentClassifier _intentClassifier;
    private readonly ILogger<AgentService> _logger;

    public AgentService(
        IMcpClientService mcpClient,
        SessionManager sessionManager,
        IntentClassifier intentClassifier,
        ILogger<AgentService> logger)
    {
        _mcpClient = mcpClient;
        _sessionManager = sessionManager;
        _intentClassifier = intentClassifier;
        _logger = logger;
    }

    public async Task<AgentResponse> ProcessMessageAsync(string sessionId, string text)
    {
        _logger.LogInformation(">>> User request [session={SessionId}]: {Text}", sessionId, text);

        var session = _sessionManager.GetOrCreateSession(sessionId);
        var intent = await _intentClassifier.ClassifyIntentAsync(text);

        _logger.LogInformation("Intent classified: {Intent}", intent);

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
                if (session.HasPendingUpdate)
                {
                    // Execute the pre-validated tool call
                    var result = await _mcpClient.ExecutePreparedUpdateAsync(
                        session.PendingToolName!,
                        session.PendingToolArguments!,
                        session.PendingUserQuery!);
                    session.ClearPendingConfirmation();
                    return new AgentResponse { Text = result, AwaitingConfirmation = false };
                }
                else
                {
                    // Fallback to old behavior
                    var result = await _mcpClient.ExecuteUpdateAsync(
                        session.PendingAction!,
                        session.PendingActionDescription!);
                    session.ClearPendingConfirmation();
                    return new AgentResponse { Text = result, AwaitingConfirmation = false };
                }
            }

            if (intent == IntentType.Cancellation)
            {
                session.ClearPendingConfirmation();
                return new AgentResponse { Text = "Cancelled. No changes made.", AwaitingConfirmation = false };
            }

            // New command while confirmation pending - cancel old and process new
            session.ClearPendingConfirmation();
        }

        var response = intent switch
        {
            IntentType.Query => await HandleQueryAsync(text),
            IntentType.Update => await HandleUpdateAsync(session, text),
            IntentType.EndSession => HandleEndSession(sessionId),
            _ => new AgentResponse { Text = "I'm not sure what you mean. Could you rephrase that?" }
        };

        _logger.LogInformation("<<< Agent response: {Text}", response.Text);
        return response;
    }

    private async Task<AgentResponse> HandleQueryAsync(string text)
    {
        var result = await _mcpClient.ExecuteQueryAsync(text);
        return new AgentResponse { Text = result, AwaitingConfirmation = false };
    }

    private async Task<AgentResponse> HandleUpdateAsync(Session session, string text)
    {
        // Validate and prepare the update BEFORE asking for confirmation
        var prepared = await _mcpClient.PrepareUpdateAsync(text);

        if (!prepared.IsValid)
        {
            _logger.LogWarning("Could not prepare update for: {Text}", text);
            return new AgentResponse
            {
                Text = "I couldn't understand that command. Try saying something like 'log 8 hours on INTERNAL'.",
                AwaitingConfirmation = false
            };
        }

        // Store the validated tool call for confirmation
        session.SetPendingUpdate(prepared.ToolName!, prepared.Arguments!, text);

        return new AgentResponse
        {
            Text = $"{prepared.Summary} Say yes to confirm or no to cancel.",
            AwaitingConfirmation = true
        };
    }

    private AgentResponse HandleEndSession(string sessionId)
    {
        _sessionManager.EndSession(sessionId);
        return new AgentResponse { Text = "Goodbye. Session ended.", AwaitingConfirmation = false };
    }
}
