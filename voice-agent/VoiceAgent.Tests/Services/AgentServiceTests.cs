// VoiceAgent.Tests/Services/AgentServiceTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAI.Chat;
using VoiceAgent.Models;
using VoiceAgent.Services;

namespace VoiceAgent.Tests.Services;

public class AgentServiceTests
{
    [Fact]
    public void AgentService_CanBeConstructed()
    {
        var mockMcpClient = new Mock<IMcpClientService>();
        var sessionManager = new SessionManager(TimeSpan.FromHours(4), TimeSpan.FromMinutes(2));
        var mockConfirmationDetector = new Mock<ConfirmationDetector>(
            new AzureOpenAIConfig { Endpoint = "https://test.openai.azure.com", ApiKey = "test", DeploymentName = "gpt-4o" },
            NullLogger<ConfirmationDetector>.Instance);
        var aiConfig = new AzureOpenAIConfig
        {
            Endpoint = "https://example.openai.azure.com",
            ApiKey = "test-key",
            DeploymentName = "gpt-4o"
        };

        var service = new AgentService(
            mockMcpClient.Object,
            sessionManager,
            mockConfirmationDetector.Object,
            aiConfig,
            NullLogger<AgentService>.Instance);

        service.Should().NotBeNull();
    }
}
