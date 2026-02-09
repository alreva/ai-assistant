// VoiceAgent.Tests/Handlers/WebSocketHandlerTests.cs
using System.Text.Json;
using FluentAssertions;
using Moq;
using VoiceAgent.Handlers;
using VoiceAgent.Models;
using VoiceAgent.Services;

namespace VoiceAgent.Tests.Handlers;

public class WebSocketHandlerTests
{
    [Fact]
    public async Task HandleMessage_ValidTranscription_ReturnsAgentResponse()
    {
        var mockAgent = new Mock<IAgentService>();
        mockAgent
            .Setup(a => a.ProcessMessageAsync("session-1", "hello"))
            .ReturnsAsync(new AgentResponse { Text = "Hi there!", AwaitingConfirmation = false });

        var handler = new WebSocketHandler(mockAgent.Object);

        var inputJson = """{"type":"transcription","text":"hello","session_id":"session-1"}""";
        var result = await handler.HandleMessageAsync(inputJson);

        var response = JsonSerializer.Deserialize<AgentResponse>(result);
        response.Should().NotBeNull();
        response!.Text.Should().Be("Hi there!");
    }

    [Fact]
    public async Task HandleMessage_InvalidJson_ReturnsError()
    {
        var mockAgent = new Mock<IAgentService>();
        var handler = new WebSocketHandler(mockAgent.Object);

        var result = await handler.HandleMessageAsync("not valid json");

        result.Should().Contain("error");
    }

    [Fact]
    public async Task HandleMessage_MissingSessionId_ReturnsError()
    {
        var mockAgent = new Mock<IAgentService>();
        var handler = new WebSocketHandler(mockAgent.Object);

        var inputJson = """{"type":"transcription","text":"hello"}""";
        var result = await handler.HandleMessageAsync(inputJson);

        result.Should().Contain("session_id");
    }
}
