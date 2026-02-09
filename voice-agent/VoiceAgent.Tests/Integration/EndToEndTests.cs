// VoiceAgent.Tests/Integration/EndToEndTests.cs
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using VoiceAgent.Models;

namespace VoiceAgent.Tests.Integration;

public class EndToEndTests
{
    // Note: These tests require the server to be running
    // Run: dotnet run --project VoiceAgent in a separate terminal

    [Fact(Skip = "Requires running server")]
    public async Task WebSocket_SendTranscription_ReceivesResponse()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("ws://localhost:8766"), CancellationToken.None);

        var message = new TranscriptionMessage
        {
            Type = "transcription",
            Text = "show my entries",
            SessionId = "test-session"
        };

        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);

        var response = JsonSerializer.Deserialize<AgentResponse>(responseJson);
        response.Should().NotBeNull();
        response!.Type.Should().Be("response");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
    }
}
