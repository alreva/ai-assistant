using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpeechService.Models;
using SpeechService.Services;

namespace SpeechService.Handlers;

public class WebSocketHandler
{
    private readonly ITtsService _ttsService;
    private readonly ILogger<WebSocketHandler> _logger;

    public WebSocketHandler(ITtsService ttsService, ILogger<WebSocketHandler> logger)
    {
        _ttsService = ttsService;
        _logger = logger;
    }

    public async Task HandleConnectionAsync(WebSocket webSocket, CancellationToken ct)
    {
        var buffer = new byte[8192];

        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", ct);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleTextMessageAsync(webSocket, message, ct);
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning("WebSocket error: {Error}", ex.Message);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("WebSocket connection canceled");
        }
    }

    private async Task HandleTextMessageAsync(WebSocket webSocket, string message, CancellationToken ct)
    {
        _logger.LogInformation("Received TTS request: {Message}", message[..Math.Min(100, message.Length)]);

        TtsRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<TtsRequest>(message);
            if (request is null || string.IsNullOrEmpty(request.Text))
            {
                _logger.LogWarning("Invalid request: text is required");
                await SendErrorAsync(webSocket, "Invalid request: text is required", ct);
                return;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Invalid JSON: {Error}", ex.Message);
            await SendErrorAsync(webSocket, $"Invalid JSON: {ex.Message}", ct);
            return;
        }

        try
        {
            _logger.LogInformation("Synthesizing: {Text}", request.Text[..Math.Min(50, request.Text.Length)]);
            var chunkCount = 0;
            await _ttsService.SynthesizeToStreamAsync(
                request,
                async chunk =>
                {
                    if (webSocket.State == WebSocketState.Open)
                    {
                        chunkCount++;
                        // Send each chunk as a complete message (endOfMessage=true)
                        await webSocket.SendAsync(chunk, WebSocketMessageType.Binary, true, ct);
                    }
                },
                ct);

            // Send final empty chunk to signal completion
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.SendAsync(Array.Empty<byte>(), WebSocketMessageType.Binary, true, ct);
            }

            _logger.LogInformation("Completed synthesis: {Chunks} chunks sent", chunkCount);

            // Close the connection after sending audio
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Synthesis failed");
            await SendErrorAsync(webSocket, $"Synthesis failed: {ex.Message}", ct);
        }
    }

    private static async Task SendErrorAsync(WebSocket webSocket, string error, CancellationToken ct)
    {
        if (webSocket.State != WebSocketState.Open) return;

        var errorJson = JsonSerializer.Serialize(new { error });
        var errorBytes = Encoding.UTF8.GetBytes(errorJson);
        await webSocket.SendAsync(errorBytes, WebSocketMessageType.Text, true, ct);
    }
}
