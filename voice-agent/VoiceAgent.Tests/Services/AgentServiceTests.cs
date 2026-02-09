// VoiceAgent.Tests/Services/AgentServiceTests.cs
using FluentAssertions;
using Moq;
using VoiceAgent.Models;
using VoiceAgent.Services;

namespace VoiceAgent.Tests.Services;

public class AgentServiceTests
{
    [Fact]
    public async Task ProcessMessage_QueryIntent_ExecutesImmediately()
    {
        var mockMcpClient = new Mock<IMcpClientService>();
        mockMcpClient
            .Setup(m => m.ExecuteQueryAsync(It.IsAny<string>()))
            .ReturnsAsync("You have 3 entries this week.");

        var service = new AgentService(
            mockMcpClient.Object,
            new SessionManager(TimeSpan.FromHours(4), TimeSpan.FromMinutes(2)),
            new IntentClassifier());

        var response = await service.ProcessMessageAsync("session-1", "show my entries");

        response.Text.Should().Contain("entries");
        response.AwaitingConfirmation.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessMessage_UpdateIntent_AsksForConfirmation()
    {
        var mockMcpClient = new Mock<IMcpClientService>();

        var service = new AgentService(
            mockMcpClient.Object,
            new SessionManager(TimeSpan.FromHours(4), TimeSpan.FromMinutes(2)),
            new IntentClassifier());

        var response = await service.ProcessMessageAsync("session-1", "log 8 hours on INTERNAL");

        response.AwaitingConfirmation.Should().BeTrue();
        response.Text.Should().Contain("confirm");
    }

    [Fact]
    public async Task ProcessMessage_ConfirmationAfterUpdate_ExecutesAction()
    {
        var mockMcpClient = new Mock<IMcpClientService>();
        mockMcpClient
            .Setup(m => m.ExecuteUpdateAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("Logged 8 hours on INTERNAL.");

        var sessionManager = new SessionManager(TimeSpan.FromHours(4), TimeSpan.FromMinutes(2));
        var service = new AgentService(
            mockMcpClient.Object,
            sessionManager,
            new IntentClassifier());

        // First, request an update
        await service.ProcessMessageAsync("session-1", "log 8 hours on INTERNAL");

        // Then confirm
        var response = await service.ProcessMessageAsync("session-1", "yes");

        response.Text.Should().Contain("Logged");
        response.AwaitingConfirmation.Should().BeFalse();
        mockMcpClient.Verify(m => m.ExecuteUpdateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_CancellationAfterUpdate_CancelsAction()
    {
        var mockMcpClient = new Mock<IMcpClientService>();

        var service = new AgentService(
            mockMcpClient.Object,
            new SessionManager(TimeSpan.FromHours(4), TimeSpan.FromMinutes(2)),
            new IntentClassifier());

        // First, request an update
        await service.ProcessMessageAsync("session-1", "log 8 hours on INTERNAL");

        // Then cancel
        var response = await service.ProcessMessageAsync("session-1", "no");

        response.Text.Should().Contain("Cancelled");
        response.AwaitingConfirmation.Should().BeFalse();
        mockMcpClient.Verify(m => m.ExecuteUpdateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessMessage_EndSession_EndsSession()
    {
        var mockMcpClient = new Mock<IMcpClientService>();

        var service = new AgentService(
            mockMcpClient.Object,
            new SessionManager(TimeSpan.FromHours(4), TimeSpan.FromMinutes(2)),
            new IntentClassifier());

        var response = await service.ProcessMessageAsync("session-1", "goodbye");

        response.Text.Should().Contain("Goodbye");
    }
}
