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
        var config = new McpClientConfig
        {
            Command = "dotnet",
            Arguments = "run --project ./TimeReportingMcpSdk"
        };

        var service = new McpClientService(config, NullLogger<McpClientService>.Instance);

        service.Should().NotBeNull();
    }
}
