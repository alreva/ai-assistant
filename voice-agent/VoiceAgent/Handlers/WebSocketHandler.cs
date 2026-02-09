// VoiceAgent/Handlers/WebSocketHandler.cs
using System.Text.Json;
using VoiceAgent.Models;
using VoiceAgent.Services;

namespace VoiceAgent.Handlers;

public class WebSocketHandler
{
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
            {
                return JsonSerializer.Serialize(new { error = "Invalid message format" });
            }

            if (string.IsNullOrEmpty(message.SessionId))
            {
                return JsonSerializer.Serialize(new { error = "Missing session_id" });
            }

            if (string.IsNullOrEmpty(message.Text))
            {
                return JsonSerializer.Serialize(new { error = "Missing text" });
            }

            var response = await _agentService.ProcessMessageAsync(message.SessionId, message.Text);
            return JsonSerializer.Serialize(response);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { error = "Invalid JSON" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Internal error: {ex.Message}" });
        }
    }
}
