using System.Text.Json.Serialization;

namespace VoiceAgent.Models;

public class AgentResponse
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "response";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("ssml")]
    public string? Ssml { get; set; }

    [JsonPropertyName("awaiting_confirmation")]
    public bool AwaitingConfirmation { get; set; }

    [JsonPropertyName("traceparent")]
    public string? Traceparent { get; set; }
}
