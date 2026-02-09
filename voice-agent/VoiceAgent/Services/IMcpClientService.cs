// VoiceAgent/Services/IMcpClientService.cs
namespace VoiceAgent.Services;

public record PreparedUpdate(bool IsValid, string Summary, string? ToolName, Dictionary<string, object?>? Arguments);

public interface IMcpClientService
{
    Task<string> ExecuteQueryAsync(string query);
    Task<string> ExecuteUpdateAsync(string action, string parameters);
    Task<string> ExecutePreparedUpdateAsync(string toolName, Dictionary<string, object?> arguments, string userQuery);
    Task<string> GetActionSummaryAsync(string text);
    Task<PreparedUpdate> PrepareUpdateAsync(string text);
}
