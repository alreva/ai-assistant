using FluentAssertions;
using VoiceAgent.Models;

namespace VoiceAgent.Tests.Models;

public class SessionTests
{
    [Fact]
    public void Session_NewSession_HasNoExpiredTimeout()
    {
        var session = new Session("test-session");

        session.IsExpired(TimeSpan.FromHours(4)).Should().BeFalse();
    }

    [Fact]
    public void Session_OldSession_IsExpired()
    {
        var session = new Session("test-session");
        session.LastActivityTime = DateTime.UtcNow.AddHours(-5);

        session.IsExpired(TimeSpan.FromHours(4)).Should().BeTrue();
    }

    [Fact]
    public void Session_PendingToolExecution_ExpiresAfterTimeout()
    {
        var session = new Session("test-session");
        session.SetPendingToolExecution(
            "call-1",
            "log_time",
            new Dictionary<string, object?> { ["projectCode"] = "INTERNAL" },
            "I'll log time. Say yes to confirm.");
        session.ConfirmationRequestedAt = DateTime.UtcNow.AddMinutes(-3);

        session.IsConfirmationExpired(TimeSpan.FromMinutes(2)).Should().BeTrue();
    }

    [Fact]
    public void Session_TouchActivity_UpdatesLastActivityTime()
    {
        var session = new Session("test-session");
        var before = session.LastActivityTime;

        Thread.Sleep(10);
        session.TouchActivity();

        session.LastActivityTime.Should().BeAfter(before);
    }

    [Fact]
    public void Session_AddUserMessage_AddsToHistory()
    {
        var session = new Session("test-session");

        session.AddUserMessage("Hello");

        session.ConversationHistory.Should().HaveCount(1);
        session.ConversationHistory[0].Role.Should().Be(ConversationRole.User);
        session.ConversationHistory[0].Content.Should().Be("Hello");
    }

    [Fact]
    public void Session_AddAssistantMessage_AddsToHistory()
    {
        var session = new Session("test-session");

        session.AddAssistantMessage("Hi there!");

        session.ConversationHistory.Should().HaveCount(1);
        session.ConversationHistory[0].Role.Should().Be(ConversationRole.Assistant);
        session.ConversationHistory[0].Content.Should().Be("Hi there!");
    }

    [Fact]
    public void Session_AddToolCall_AddsToHistory()
    {
        var session = new Session("test-session");
        var args = new Dictionary<string, object?> { ["projectCode"] = "INTERNAL" };

        session.AddToolCall("call-1", "log_time", args);

        session.ConversationHistory.Should().HaveCount(1);
        session.ConversationHistory[0].Role.Should().Be(ConversationRole.Assistant);
        session.ConversationHistory[0].ToolCallId.Should().Be("call-1");
        session.ConversationHistory[0].ToolName.Should().Be("log_time");
        session.ConversationHistory[0].ToolArguments.Should().BeEquivalentTo(args);
    }

    [Fact]
    public void Session_AddToolResult_AddsToHistory()
    {
        var session = new Session("test-session");

        session.AddToolResult("call-1", "log_time", "Success");

        session.ConversationHistory.Should().HaveCount(1);
        session.ConversationHistory[0].Role.Should().Be(ConversationRole.Tool);
        session.ConversationHistory[0].ToolCallId.Should().Be("call-1");
        session.ConversationHistory[0].Content.Should().Be("Success");
    }

    [Fact]
    public void Session_HistoryTrimsWhenExceedsMax()
    {
        var session = new Session("test-session");

        // Add 55 messages (max is 50)
        for (int i = 0; i < 55; i++)
        {
            session.AddUserMessage($"Message {i}");
        }

        session.ConversationHistory.Should().HaveCount(50);
        session.ConversationHistory[0].Content.Should().Be("Message 5");
        session.ConversationHistory[49].Content.Should().Be("Message 54");
    }

    [Fact]
    public void Session_SetPendingToolExecution_SetsPendingState()
    {
        var session = new Session("test-session");
        var args = new Dictionary<string, object?> { ["hours"] = 8 };

        session.SetPendingToolExecution("call-1", "log_time", args, "Confirm?");

        session.HasPendingConfirmation.Should().BeTrue();
        session.PendingToolExecution.Should().NotBeNull();
        session.PendingToolExecution!.ToolName.Should().Be("log_time");
        session.PendingToolExecution.Arguments.Should().BeEquivalentTo(args);
        session.ConfirmationRequestedAt.Should().NotBeNull();
    }

    [Fact]
    public void Session_ClearPendingToolExecution_ClearsPendingState()
    {
        var session = new Session("test-session");
        session.SetPendingToolExecution("call-1", "log_time", new Dictionary<string, object?>(), "Confirm?");

        session.ClearPendingToolExecution();

        session.HasPendingConfirmation.Should().BeFalse();
        session.PendingToolExecution.Should().BeNull();
        session.ConfirmationRequestedAt.Should().BeNull();
    }
}
