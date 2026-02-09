// VoiceAgent/Services/IMcpClientService.cs
using OpenAI.Chat;

namespace VoiceAgent.Services;

public interface IMcpClientService
{
    Task<IList<ChatTool>> GetToolsAsChatToolsAsync();
    Task<string> ExecuteToolAsync(string toolName, Dictionary<string, object?> arguments);
    Task<string> FormatForTtsAsync(string rawResult, string userQuery, string toolName);
}
