using System.Diagnostics;
using System.Text.Json;
using VoiceAgent.Models;
using VoiceAgent.Services;

namespace VoiceAgent.Handlers;

public class WebSocketHandler
{
    private static readonly ActivitySource ActivitySource = new("VoiceAgent");
    private readonly IAgentService _agentService;

    public WebSocketHandler(IAgentService agentService)
    {
        _agentService = agentService;
    }

    public async Task<string> HandleMessageAsync(string messageJson)
    {
        try
        {
            var message = JsonSerializer.Deserialize<TranscriptionMessage>(messageJson);

            if (message == null)
                return JsonSerializer.Serialize(new { error = "Invalid message format" });
            if (string.IsNullOrEmpty(message.SessionId))
                return JsonSerializer.Serialize(new { error = "Missing session_id" });
            if (string.IsNullOrEmpty(message.Text))
                return JsonSerializer.Serialize(new { error = "Missing text" });

            // Extract parent trace context if present
            ActivityContext parentContext = default;
            if (!string.IsNullOrEmpty(message.Traceparent))
            {
                ActivityContext.TryParse(message.Traceparent, null, out parentContext);
            }

            using var activity = ActivitySource.StartActivity(
                "agent-process",
                ActivityKind.Server,
                parentContext);

            activity?.SetTag("session.id", message.SessionId);
            activity?.SetTag("message.text", message.Text);
            if (message.Character != null)
                activity?.SetTag("agent.character", message.Character);

            var response = await _agentService.ProcessMessageAsync(
                message.SessionId, message.Text, message.Character);

            // Include traceparent in response so client can continue the trace
            if (activity != null)
            {
                response.Traceparent = $"00-{activity.TraceId}-{activity.SpanId}-01";
            }

            return JsonSerializer.Serialize(response);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { error = "Invalid JSON" });
        }
        catch (Exception ex)
        {
            Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return JsonSerializer.Serialize(new { error = $"Internal error: {ex.Message}" });
        }
    }
}
