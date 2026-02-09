// VoiceAgent/Services/IMcpClientService.cs
namespace VoiceAgent.Services;

public interface IMcpClientService
{
    Task<string> ExecuteQueryAsync(string query);
    Task<string> ExecuteUpdateAsync(string action, string parameters);
    Task<string> GetActionSummaryAsync(string text);
}
