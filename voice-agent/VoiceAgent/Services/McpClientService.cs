// VoiceAgent/Services/McpClientService.cs
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI.Chat;

namespace VoiceAgent.Services;

public class McpClientService : IMcpClientService, IAsyncDisposable
{
    private readonly McpClientConfig _mcpConfig;
    private readonly ILogger<McpClientService> _logger;
    private McpClient? _mcpClient;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public McpClientService(
        McpClientConfig mcpConfig,
        ILogger<McpClientService> logger)
    {
        _mcpConfig = mcpConfig;
        _logger = logger;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            // Initialize MCP client
            var args = _mcpConfig.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "TimeReportingMcp",
                Command = _mcpConfig.Command,
                Arguments = args,
                EnvironmentVariables = _mcpConfig.Environment.ToDictionary(x => x.Key, x => (string?)x.Value)
            });

            _mcpClient = await McpClient.CreateAsync(transport);
            _logger.LogInformation("MCP client connected successfully");

            // Log available tools
            var tools = await _mcpClient.ListToolsAsync();
            _logger.LogInformation("Available MCP tools: {Tools}",
                string.Join(", ", tools.Select(t => t.Name)));

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<IList<ChatTool>> GetToolsAsChatToolsAsync()
    {
        await EnsureInitializedAsync();

        var mcpTools = await _mcpClient!.ListToolsAsync();
        var chatTools = new List<ChatTool>();

        foreach (var tool in mcpTools)
        {
            var schemaJson = tool.JsonSchema.GetRawText();
            var chatTool = ChatTool.CreateFunctionTool(
                tool.Name,
                tool.Description ?? $"Tool: {tool.Name}",
                BinaryData.FromString(schemaJson));
            chatTools.Add(chatTool);
        }

        return chatTools;
    }

    public async Task<string> ExecuteToolAsync(string toolName, Dictionary<string, object?> arguments)
    {
        await EnsureInitializedAsync();

        _logger.LogInformation("Executing MCP tool: {Tool} with args: {Args}",
            toolName, JsonSerializer.Serialize(arguments));

        try
        {
            var result = await _mcpClient!.CallToolAsync(toolName, arguments);

            var text = string.Join("\n", result.Content
                .OfType<TextContentBlock>()
                .Select(c => c.Text));

            _logger.LogInformation("MCP tool result: {Result}", text);
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing tool: {Tool}", toolName);
            return $"Error executing {toolName}: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient != null)
        {
            await _mcpClient.DisposeAsync();
        }
        _initLock.Dispose();
    }
}
