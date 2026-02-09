// VoiceAgent.Tests/Services/McpClientServiceTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceAgent.Services;

namespace VoiceAgent.Tests.Services;

public class McpClientServiceTests
{
    [Fact]
    public void McpClientService_CanBeConstructed()
    {
        var mcpConfig = new McpClientConfig
        {
            Command = "dotnet",
            Arguments = "run --project ./TimeReportingMcpSdk"
        };

        var aiConfig = new AzureOpenAIConfig
        {
            Endpoint = "https://example.openai.azure.com",
            ApiKey = "test-key",
            DeploymentName = "gpt-4o"
        };

        var service = new McpClientService(mcpConfig, aiConfig, NullLogger<McpClientService>.Instance);

        service.Should().NotBeNull();
    }
}
