// VoiceAgent/Models/SsmlResponse.cs
using System.Text.Json.Serialization;

namespace VoiceAgent.Models;

public class SsmlResponse
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("ssml")]
    public string? Ssml { get; set; }
}
