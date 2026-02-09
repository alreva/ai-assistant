using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using VoiceAgent.Models;

namespace VoiceAgent.Tests.Models;

public class MessageSerializationTests
{
    [Fact]
    public void TranscriptionMessage_DeserializesFromJson()
    {
        var json = """
            {
                "type": "transcription",
                "text": "Log 8 hours on INTERNAL",
                "session_id": "abc-123"
            }
            """;

        var message = JsonSerializer.Deserialize<TranscriptionMessage>(json);

        message.Should().NotBeNull();
        message!.Type.Should().Be("transcription");
        message.Text.Should().Be("Log 8 hours on INTERNAL");
        message.SessionId.Should().Be("abc-123");
    }

    [Fact]
    public void AgentResponse_SerializesToJson()
    {
        var response = new AgentResponse
        {
            Type = "response",
            Text = "I'll log 8 hours. Confirm?",
            AwaitingConfirmation = true
        };

        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var json = JsonSerializer.Serialize(response, options);

        json.Should().Contain("\"type\":\"response\"");
        json.Should().Contain("\"text\":\"I'll log 8 hours. Confirm?\"");
        json.Should().Contain("\"awaiting_confirmation\":true");
    }
}
