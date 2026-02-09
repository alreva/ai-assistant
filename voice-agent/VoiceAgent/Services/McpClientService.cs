// VoiceAgent/Services/McpClientService.cs
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI.Chat;

namespace VoiceAgent.Services;

public class McpClientService : IMcpClientService, IAsyncDisposable
{
    private readonly McpClientConfig _mcpConfig;
    private readonly AzureOpenAIConfig _aiConfig;
    private readonly ILogger<McpClientService> _logger;
    private McpClient? _mcpClient;
    private ChatClient? _chatClient;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public McpClientService(
        McpClientConfig mcpConfig,
        AzureOpenAIConfig aiConfig,
        ILogger<McpClientService> logger)
    {
        _mcpConfig = mcpConfig;
        _aiConfig = aiConfig;
        _logger = logger;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            // Initialize Azure OpenAI
            var azureClient = new AzureOpenAIClient(
                new Uri(_aiConfig.Endpoint),
                new AzureKeyCredential(_aiConfig.ApiKey));
            _chatClient = azureClient.GetChatClient(_aiConfig.DeploymentName);

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

    public async Task<string> ExecuteQueryAsync(string query)
    {
        await EnsureInitializedAsync();

        try
        {
            // Use LLM to understand what kind of query this is
            var toolCall = await DetermineToolCallAsync(query, isQuery: true);

            if (toolCall == null)
            {
                return "I couldn't understand that query. Try asking about your time entries or projects.";
            }

            _logger.LogInformation("Calling MCP tool: {Tool} with args: {Args}",
                toolCall.ToolName, JsonSerializer.Serialize(toolCall.Arguments));

            var result = await _mcpClient!.CallToolAsync(
                toolCall.ToolName,
                toolCall.Arguments);

            // Extract text from result
            var text = string.Join("\n", result.Content
                .OfType<TextContentBlock>()
                .Select(c => c.Text));

            // Convert to TTS-friendly format
            return ConvertToTtsFriendly(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing query: {Query}", query);
            return "Sorry, something went wrong while processing your request.";
        }
    }

    public async Task<string> ExecuteUpdateAsync(string action, string parameters)
    {
        await EnsureInitializedAsync();

        try
        {
            // Use LLM to understand the update command
            var toolCall = await DetermineToolCallAsync(parameters, isQuery: false);

            if (toolCall == null)
            {
                return "I couldn't understand that command. Try saying something like 'log 8 hours on PROJECT'.";
            }

            _logger.LogInformation("Calling MCP tool: {Tool} with args: {Args}",
                toolCall.ToolName, JsonSerializer.Serialize(toolCall.Arguments));

            var result = await _mcpClient!.CallToolAsync(
                toolCall.ToolName,
                toolCall.Arguments);

            // Extract text from result
            var text = string.Join("\n", result.Content
                .OfType<TextContentBlock>()
                .Select(c => c.Text));

            return ConvertToTtsFriendly(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing update: {Parameters}", parameters);
            return "Sorry, something went wrong while processing your request.";
        }
    }

    public async Task<string> GetActionSummaryAsync(string text)
    {
        await EnsureInitializedAsync();

        try
        {
            var prompt = $@"You are a voice assistant summarizing a time reporting action.
The user said: ""{text}""

Summarize what action will be taken in a single, clear sentence suitable for speech.
Start with ""I'll"" and be specific about what will happen.
For example: ""I'll log 8 hours on the INTERNAL project for today.""

Just respond with the summary, nothing else.";

            var response = await _chatClient!.CompleteChatAsync(
                [new UserChatMessage(prompt)]);

            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting action summary for: {Text}", text);
            return $"I'll process: {text}.";
        }
    }

    private async Task<ToolCallInfo?> DetermineToolCallAsync(string userText, bool isQuery)
    {
        var tools = await _mcpClient!.ListToolsAsync();
        var toolDescriptions = string.Join("\n", tools.Select(t =>
            $"- {t.Name}: {t.Description ?? "No description"}"));

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var prompt = $@"You are parsing a voice command for a time reporting system.
Today's date is {today}.

User said: ""{userText}""

Available MCP tools:
{toolDescriptions}

IMPORTANT - Tool argument schemas:
- log_time: {{""projectCode"": ""PROJECT"", ""task"": ""Development"", ""standardHours"": 8, ""startDate"": ""YYYY-MM-DD"", ""completionDate"": ""YYYY-MM-DD""}}
- query_time_entries: {{""projectCode"": ""optional"", ""startDate"": ""optional"", ""endDate"": ""optional""}}
- delete_time_entry: {{""entryId"": ""uuid""}}
- submit_time_entry: {{""entryId"": ""uuid""}}
- get_available_projects: {{}}

{(isQuery ? "This is a QUERY command - the user wants to retrieve information." : "This is an UPDATE command - the user wants to make a change.")}

Respond with ONLY a JSON object:
{{
  ""tool"": ""tool_name"",
  ""arguments"": {{ ... }}
}}

Rules:
- For dates use YYYY-MM-DD format
- For ""today"" use {today}
- For log_time: startDate and completionDate should be the same for single-day entries
- For standardHours default to 8 if not specified
- For task default to ""Development"" if not specified

If you cannot determine a valid tool call, respond with: {{""tool"": null}}";

        var response = await _chatClient!.CompleteChatAsync(
            [new UserChatMessage(prompt)]);

        var responseText = response.Value.Content[0].Text;
        _logger.LogDebug("LLM response for tool determination: {Response}", responseText);

        try
        {
            // Extract JSON from response (handle markdown code blocks)
            var jsonMatch = Regex.Match(responseText, @"\{[\s\S]*\}");
            if (!jsonMatch.Success)
            {
                return null;
            }

            var json = JsonDocument.Parse(jsonMatch.Value);
            var toolName = json.RootElement.GetProperty("tool").GetString();

            if (string.IsNullOrEmpty(toolName))
            {
                return null;
            }

            var arguments = new Dictionary<string, object?>();
            if (json.RootElement.TryGetProperty("arguments", out var argsElement))
            {
                foreach (var prop in argsElement.EnumerateObject())
                {
                    arguments[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText()
                    };
                }
            }

            return new ToolCallInfo(toolName, arguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM response: {Response}", responseText);
            return null;
        }
    }

    private string ConvertToTtsFriendly(string text)
    {
        // Remove common emojis used in the MCP responses
        text = text.Replace("✅", "")
                   .Replace("❌", "")
                   .Replace("📋", "")
                   .Replace("⚠️", "")
                   .Replace("•", "-");

        // Remove markdown formatting
        text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1"); // Bold
        text = Regex.Replace(text, @"\*([^*]+)\*", "$1"); // Italic
        text = Regex.Replace(text, @"^#+\s*", "", RegexOptions.Multiline); // Headers
        text = Regex.Replace(text, @"^\s*-\s*", "", RegexOptions.Multiline); // Bullets

        // Shorten UUIDs to last 4 chars for TTS
        text = Regex.Replace(text, @"ID:\s*[a-f0-9-]{32,36}", m =>
        {
            var id = m.Value.Replace("ID:", "").Trim();
            return $"ID ending in {id[^4..]}";
        });

        // Clean up extra whitespace
        text = Regex.Replace(text, @"\n\s*\n", "\n");
        text = text.Trim();

        return text;
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient != null)
        {
            await _mcpClient.DisposeAsync();
        }
        _initLock.Dispose();
    }

    private record ToolCallInfo(string ToolName, Dictionary<string, object?> Arguments);
}
