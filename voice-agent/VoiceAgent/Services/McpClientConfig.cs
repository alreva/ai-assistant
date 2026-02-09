// VoiceAgent/Services/McpClientConfig.cs
namespace VoiceAgent.Services;

public class McpClientConfig
{
    public string Command { get; set; } = "dotnet";
    public string Arguments { get; set; } = string.Empty;
    public Dictionary<string, string> Environment { get; set; } = new();
}

public class AzureOpenAIConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string DeploymentName { get; set; } = "gpt-4o";
}
