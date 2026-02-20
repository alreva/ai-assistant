using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SpeechService.Models;

namespace SpeechService.Services;

public class ElevenLabsTtsService : ITtsService
{
    private static readonly HttpClient Http = new();
    private readonly ElevenLabsConfig _config;
    private readonly ILogger<ElevenLabsTtsService> _logger;

    public ElevenLabsTtsService(ElevenLabsConfig config, ILogger<ElevenLabsTtsService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SynthesizeToStreamAsync(TtsRequest request, Func<byte[], Task> onAudioChunk, CancellationToken ct)
    {
        var voiceId = request.Voice;
        var outputFormat = MapOutputFormat(request.OutputFormat);
        var url = $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}/stream?output_format={outputFormat}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Add("xi-api-key", _config.ApiKey);
        httpRequest.Content = JsonContent.Create(new ElevenLabsRequestBody
        {
            Text = request.Text,
            ModelId = _config.ModelId
        });

        _logger.LogInformation("Calling ElevenLabs TTS API (streaming, voice={Voice}, format={Format})", voiceId, outputFormat);

        using var response = await Http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[8192];
        var totalBytes = 0;
        var chunkCount = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            totalBytes += bytesRead;
            chunkCount++;
            var chunk = buffer.AsMemory(0, bytesRead).ToArray();
            await onAudioChunk(chunk);
        }

        _logger.LogInformation("Streamed {Bytes} bytes in {Chunks} chunks", totalBytes, chunkCount);
    }

    private static string MapOutputFormat(string azureFormat) => azureFormat switch
    {
        "raw-24khz-16bit-mono-pcm" => "pcm_24000",
        "raw-16khz-16bit-mono-pcm" => "pcm_16000",
        "raw-48khz-16bit-mono-pcm" => "pcm_48000",
        _ when azureFormat.Contains("mp3") => "mp3_44100_128",
        _ => "mp3_44100_128"
    };

    private class ElevenLabsRequestBody
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("model_id")]
        public string ModelId { get; set; } = "eleven_multilingual_v2";
    }
}
