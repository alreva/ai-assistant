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

            _logger.LogInformation("MCP tool result: {Result}", text);

            // Convert to TTS-friendly format
            var ttsResult = await FormatForTtsAsync(text, query, toolCall.ToolName);
            _logger.LogInformation("TTS formatted: {Result}", ttsResult);

            return ttsResult;
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

            _logger.LogInformation("MCP tool result: {Result}", text);

            var ttsResult = await FormatForTtsAsync(text, parameters, toolCall.ToolName);
            _logger.LogInformation("TTS formatted: {Result}", ttsResult);

            return ttsResult;
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

    public async Task<PreparedUpdate> PrepareUpdateAsync(string text)
    {
        await EnsureInitializedAsync();

        try
        {
            // Single LLM call to interpret, validate, and summarize
            var tools = await _mcpClient!.ListToolsAsync();
            var toolDescriptions = string.Join("\n", tools.Select(t =>
                $"- {t.Name}: {t.Description ?? "No description"}"));

            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var prompt = $@"You are parsing a voice command for a time reporting system.
The speech recognition may have errors, so interpret the user's intent.
Today's date is {today}.

User said: ""{text}""

Available MCP tools:
{toolDescriptions}

Tool argument schemas:
- log_time: {{""projectCode"": ""PROJECT"", ""task"": ""Development"", ""standardHours"": 8, ""overtimeHours"": 0, ""startDate"": ""YYYY-MM-DD"", ""completionDate"": ""YYYY-MM-DD""}}
- delete_time_entry: {{""id"": ""uuid""}}
- submit_time_entry: {{""id"": ""uuid""}}
- update_time_entry: {{""id"": ""uuid"", ...fields to update}}
- move_task_to_project: {{""entryId"": ""uuid"", ""newProjectCode"": ""CODE"", ""newTask"": ""Task""}}

Respond with a JSON object containing:
1. ""tool"": the tool name to call (or null if you can't understand)
2. ""arguments"": the tool arguments
3. ""summary"": a human-readable summary starting with ""I'll"" (e.g., ""I'll log 8 hours on INTERNAL for today"")

Example response:
{{
  ""tool"": ""log_time"",
  ""arguments"": {{""projectCode"": ""INTERNAL"", ""task"": ""Development"", ""standardHours"": 8, ""startDate"": ""{today}"", ""completionDate"": ""{today}""}},
  ""summary"": ""I'll log 8 hours on the INTERNAL project for today.""
}}

Rules:
- Interpret speech recognition errors (e.g., ""head to hours"" might mean ""add 2 hours"")
- For dates use YYYY-MM-DD format
- For ""today"" use {today}
- For log_time: startDate and completionDate should be the same for single-day entries
- For standardHours default to 8 if not specified
- For task default to ""Development"" if not specified
- If you cannot determine a valid action, respond with: {{""tool"": null, ""summary"": ""I couldn't understand that command.""}}";

            _logger.LogInformation("PrepareUpdate: input=\"{Text}\"", text);

            var response = await _chatClient!.CompleteChatAsync([new UserChatMessage(prompt)]);
            var responseText = response.Value.Content[0].Text;

            _logger.LogInformation("PrepareUpdate: LLM response={Response}", responseText);

            // Parse the response
            var jsonMatch = Regex.Match(responseText, @"\{[\s\S]*\}");
            if (!jsonMatch.Success)
            {
                _logger.LogWarning("PrepareUpdate: No JSON found in response");
                return new PreparedUpdate(false, "I couldn't understand that command.", null, null);
            }

            var json = JsonDocument.Parse(jsonMatch.Value);
            var toolName = json.RootElement.TryGetProperty("tool", out var toolProp) ? toolProp.GetString() : null;
            var summary = json.RootElement.TryGetProperty("summary", out var summaryProp) ? summaryProp.GetString() : "I'll process your request.";

            if (string.IsNullOrEmpty(toolName))
            {
                _logger.LogWarning("PrepareUpdate: Tool name is null/empty");
                return new PreparedUpdate(false, summary ?? "I couldn't understand that command.", null, null);
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

            _logger.LogInformation("PrepareUpdate: Validated tool={Tool} args={Args} summary={Summary}",
                toolName, JsonSerializer.Serialize(arguments), summary);

            return new PreparedUpdate(true, summary!, toolName, arguments);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error preparing update for: {Text}", text);
            return new PreparedUpdate(false, "Sorry, something went wrong.", null, null);
        }
    }

    public async Task<string> ExecutePreparedUpdateAsync(string toolName, Dictionary<string, object?> arguments, string userQuery)
    {
        await EnsureInitializedAsync();

        try
        {
            _logger.LogInformation("ExecutePreparedUpdate: Calling MCP tool: {Tool} with args: {Args}",
                toolName, JsonSerializer.Serialize(arguments));

            var result = await _mcpClient!.CallToolAsync(toolName, arguments);

            var text = string.Join("\n", result.Content
                .OfType<TextContentBlock>()
                .Select(c => c.Text));

            _logger.LogInformation("ExecutePreparedUpdate: MCP tool result: {Result}", text);

            var ttsResult = await FormatForTtsAsync(text, userQuery, toolName);
            _logger.LogInformation("ExecutePreparedUpdate: TTS formatted: {Result}", ttsResult);

            return ttsResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing prepared update: {Tool}", toolName);
            return "Sorry, something went wrong while processing your request.";
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
- who_am_i: {{}} (shows current user identity and permissions)

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

        _logger.LogInformation("DetermineToolCall: input=\"{UserText}\" isQuery={IsQuery}", userText, isQuery);

        var response = await _chatClient!.CompleteChatAsync(
            [new UserChatMessage(prompt)]);

        var responseText = response.Value.Content[0].Text;
        _logger.LogInformation("DetermineToolCall: LLM response={Response}", responseText);

        try
        {
            // Extract JSON from response (handle markdown code blocks)
            var jsonMatch = Regex.Match(responseText, @"\{[\s\S]*\}");
            if (!jsonMatch.Success)
            {
                _logger.LogWarning("DetermineToolCall: No JSON found in response");
                return null;
            }

            var json = JsonDocument.Parse(jsonMatch.Value);
            var toolName = json.RootElement.GetProperty("tool").GetString();

            if (string.IsNullOrEmpty(toolName))
            {
                _logger.LogWarning("DetermineToolCall: Tool name is null/empty");
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

            _logger.LogInformation("DetermineToolCall: output tool={Tool} args={Args}", toolName, JsonSerializer.Serialize(arguments));
            return new ToolCallInfo(toolName, arguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM response: {Response}", responseText);
            return null;
        }
    }

    private async Task<string> FormatForTtsAsync(string rawResult, string userQuery, string toolName)
    {
        var prompt = $@"Convert this data into a natural spoken response for text-to-speech.

User asked: ""{userQuery}""
Tool called: {toolName}
Result data: {rawResult}

Requirements:
- Respond ONLY about the data returned - do not make up information
- Speak naturally, like talking to a friend
- Say dates as ""November 28th"" not ""11/28/2025""
- Say ""8 hours"" not ""8.00h""
- Never read UUIDs or IDs
- Summarize lists, don't read every item
- Keep it brief
- No bullet points or formatting

Response:";

        try
        {
            var response = await _chatClient!.CompleteChatAsync(
                [new UserChatMessage(prompt)]);
            return response.Value.Content[0].Text.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS formatting failed");
            // Basic fallback - strip formatting
            var text = Regex.Replace(rawResult, @"[a-f0-9-]{32,36}", "");
            text = Regex.Replace(text, @"[\*#•✅❌📋⚠️]", "");
            return text.Trim();
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

    private record ToolCallInfo(string ToolName, Dictionary<string, object?> Arguments);
}
