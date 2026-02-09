using System.Text.Json.Serialization;

namespace SpeechService.Models;

public class TtsRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("voice")]
    public string Voice { get; set; } = "en-US-JennyNeural";

    [JsonPropertyName("rate")]
    public string Rate { get; set; } = "0%";

    [JsonPropertyName("pitch")]
    public string Pitch { get; set; } = "0%";

    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("output_format")]
    public string OutputFormat { get; set; } = "audio-16khz-32kbitrate-mono-mp3";

    [JsonPropertyName("ssml")]
    public string? Ssml { get; set; }
}
