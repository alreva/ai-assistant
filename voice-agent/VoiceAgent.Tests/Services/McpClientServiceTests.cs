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

        var service = new McpClientService(mcpConfig, NullLogger<McpClientService>.Instance);

        service.Should().NotBeNull();
    }
}
