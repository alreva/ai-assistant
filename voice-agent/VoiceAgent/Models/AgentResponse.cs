using System.Text.Json.Serialization;

namespace VoiceAgent.Models;

public class AgentResponse
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "response";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("awaiting_confirmation")]
    public bool AwaitingConfirmation { get; set; }
}
