// VoiceAgent/Services/AgentService.cs
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using VoiceAgent.Models;

namespace VoiceAgent.Services;

public class AgentService : IAgentService
{
    private static readonly ActivitySource ActivitySource = new("VoiceAgent");
    private const int MaxToolCallsPerRequest = 5;
    private static readonly HashSet<string> DestructiveTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "log_time",
        "delete_time_entry",
        "submit_time_entry",
        "update_time_entry",
        "move_task_to_project",
        "approve_time_entry",
        "decline_time_entry"
    };

    private readonly IMcpClientService _mcpClient;
    private readonly SessionManager _sessionManager;
    private readonly ConfirmationDetector _confirmationDetector;
    private readonly CharacterConfig _character;
    private readonly ILogger<AgentService> _logger;
    private readonly ChatClient _chatClient;

    public AgentService(
        IMcpClientService mcpClient,
        SessionManager sessionManager,
        ConfirmationDetector confirmationDetector,
        CharacterConfig character,
        AzureOpenAIConfig aiConfig,
        ILogger<AgentService> logger)
    {
        _mcpClient = mcpClient;
        _sessionManager = sessionManager;
        _confirmationDetector = confirmationDetector;
        _character = character;
        _logger = logger;

        var azureClient = new Azure.AI.OpenAI.AzureOpenAIClient(
            new Uri(aiConfig.Endpoint),
            new Azure.AzureKeyCredential(aiConfig.ApiKey));
        _chatClient = azureClient.GetChatClient(aiConfig.DeploymentName);

        logger.LogInformation("Agent initialized with character: {Character}", _character.Name);
    }

    public async Task<AgentResponse> ProcessMessageAsync(string sessionId, string text, string? character = null)
    {
        _logger.LogInformation(">>> User request [session={SessionId}]: {Text}", sessionId, text);

        var session = _sessionManager.GetOrCreateSession(sessionId);

        // Set or update character for this session
        if (!string.IsNullOrEmpty(character))
        {
            session.Character = Characters.GetByName(character);
            _logger.LogInformation("Session character set to: {Character}", session.Character.Name);
        }
        else if (session.Character == null)
        {
            session.Character = _character; // Use default from config
        }

        // Check for expired confirmation
        if (session.HasPendingConfirmation && _sessionManager.IsConfirmationExpired(session))
        {
            session.ClearPendingBatchExecution();
        }

        // Handle pending confirmation
        if (session.HasPendingConfirmation)
        {
            var pending = session.PendingBatchExecution!;
            var confirmationType = await _confirmationDetector.DetectAsync(text, pending.ConfirmationPrompt);

            if (confirmationType == ConfirmationType.Confirmed)
            {
                return await ExecutePendingToolAsync(session, text);
            }

            if (confirmationType == ConfirmationType.Cancelled)
            {
                session.ClearPendingBatchExecution();
                session.AddUserMessage(text);
                var cancelText = "Cancelled. No changes made.";
                session.AddAssistantMessage(cancelText);
                return new AgentResponse { Text = cancelText, Ssml = GenerateSsml(cancelText, session), AwaitingConfirmation = false };
            }

            // Modification requested - clear pending and let agent handle with context
            session.ClearPendingBatchExecution();
        }

        // Add user message to history
        session.AddUserMessage(text);

        // Run the conversational agent loop
        return await RunAgentLoopAsync(session);
    }

    private async Task<AgentResponse> ExecutePendingToolAsync(Session session, string userText)
    {
        var pending = session.PendingBatchExecution!;
        session.ClearPendingBatchExecution();

        // Add user confirmation to history
        session.AddUserMessage(userText);

        // Add all tool calls as one assistant message
        session.AddToolCalls(pending.ToolCalls);

        // Execute each tool and add results
        foreach (var tc in pending.ToolCalls)
        {
            string result;
            using (var toolActivity = ActivitySource.StartActivity("mcp-tool-call"))
            {
                toolActivity?.SetTag("tool.name", tc.ToolName);
                toolActivity?.SetTag("tool.arguments", PrettyJson(JsonSerializer.Serialize(tc.Arguments)));
                result = await _mcpClient.ExecuteToolAsync(tc.ToolName, tc.Arguments);
                toolActivity?.SetTag("tool.result", PrettyJson(result));
            }
            session.AddToolResult(tc.ToolCallId, tc.ToolName, result);
        }

        return await RunAgentLoopAsync(session);
    }

    private async Task<AgentResponse> RunAgentLoopAsync(Session session)
    {
        var tools = await _mcpClient.GetToolsAsChatToolsAsync();
        var toolCallCount = 0;

        while (toolCallCount < MaxToolCallsPerRequest)
        {
            var messages = BuildChatMessages(session);
            var options = new ChatCompletionOptions();
            foreach (var tool in tools)
            {
                options.Tools.Add(tool);
            }

            ChatCompletion choice;
            using (var llmActivity = ActivitySource.StartActivity("llm-call"))
            {
                var payloadMessages = SerializeMessages(messages);
                llmActivity?.SetTag("llm.message_count", messages.Count);
                llmActivity?.SetTag("llm.tool_count", tools.Count);
                llmActivity?.SetTag("llm.request_payload", payloadMessages);

                var response = await _chatClient.CompleteChatAsync(messages, options);
                choice = response.Value;

                llmActivity?.SetTag("llm.input_tokens", choice.Usage?.InputTokenCount);
                llmActivity?.SetTag("llm.output_tokens", choice.Usage?.OutputTokenCount);
                llmActivity?.SetTag("llm.finish_reason", choice.FinishReason.ToString());
                llmActivity?.SetTag("llm.response_payload", SerializeResponse(choice));
            }

            // Check if LLM wants to call tools
            if (choice.ToolCalls.Count > 0)
            {
                var allCalls = choice.ToolCalls.Select(tc => new ToolCallInfo(
                    tc.Id, tc.FunctionName, ParseToolArguments(tc.FunctionArguments.ToString()))).ToList();

                var destructiveCalls = allCalls.Where(tc => DestructiveTools.Contains(tc.ToolName)).ToList();

                if (destructiveCalls.Count > 0)
                {
                    // Any destructive tool in batch → hold ALL for batch confirmation
                    var (confirmText, confirmSsml) = await GenerateBatchConfirmationAsync(session, allCalls);
                    session.SetPendingBatchExecution(allCalls, confirmText);
                    session.AddAssistantMessage(confirmText);

                    _logger.LogInformation("<<< Agent response (awaiting batch confirmation for {Count} tools): {Text}", allCalls.Count, confirmText);

                    Activity.Current?.AddEvent(new ActivityEvent("confirmation-requested",
                        tags: new ActivityTagsCollection
                        {
                            { "tool.count", allCalls.Count },
                            { "tool.names", string.Join(", ", allCalls.Select(tc => tc.ToolName)) },
                            { "confirmation.prompt", confirmText }
                        }));

                    return new AgentResponse { Text = confirmText, Ssml = confirmSsml, AwaitingConfirmation = true };
                }

                // All non-destructive — execute all immediately
                toolCallCount += allCalls.Count;
                session.AddToolCalls(allCalls);
                foreach (var tc in allCalls)
                {
                    using var toolActivity = ActivitySource.StartActivity("mcp-tool-call");
                    toolActivity?.SetTag("tool.name", tc.ToolName);
                    toolActivity?.SetTag("tool.arguments", PrettyJson(JsonSerializer.Serialize(tc.Arguments)));

                    var result = await _mcpClient.ExecuteToolAsync(tc.ToolName, tc.Arguments);

                    toolActivity?.SetTag("tool.result", PrettyJson(result));
                    session.AddToolResult(tc.ToolCallId, tc.ToolName, result);
                }
                continue;
            }

            // LLM returned a text response
            var rawResponse = choice.Content.Count > 0 ? choice.Content[0].Text : "I'm not sure how to help with that.";
            var (plainText, ssml) = ParseSsmlResponse(rawResponse, session);
            session.AddAssistantMessage(plainText);

            _logger.LogInformation("<<< Agent response: {Text}", plainText);
            return new AgentResponse { Text = plainText, Ssml = ssml, AwaitingConfirmation = false };
        }

        // Safety limit reached
        var fallbackMessage = "I've processed several operations. Is there anything else you'd like me to do?";
        session.AddAssistantMessage(fallbackMessage);
        _logger.LogWarning("Max tool calls reached for session {SessionId}", session.SessionId);
        return new AgentResponse { Text = fallbackMessage, Ssml = GenerateSsml(fallbackMessage, session), AwaitingConfirmation = false };
    }

    private List<ChatMessage> BuildChatMessages(Session session)
    {
        var messages = new List<ChatMessage>();
        var character = session.Character ?? _character;

        // System prompt
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var lang = character.VoiceName.Length >= 5 ? character.VoiceName[..5] : "en-US";
        var systemPrompt = $@"Today's date is {today}. You are a voice assistant for time reporting.

CHARACTER:
{character.Personality}

SPEECH STYLE:
{character.SpeechStyle}

RULES:
- Keep responses SHORT (1-2 sentences max)
- NEVER use markdown: no asterisks, no bullet points, no bold, no lists
- NEVER use emojis or special Unicode characters — this is voice-only output
- Say dates as ""November 28th"" not ""2024-11-28""
- Never read UUIDs or technical IDs aloud
- Stay in character but still be helpful

TIME LOGGING:
- Need: project code (required), hours (required)
- Task defaults to ""Development"", date defaults to today
- Ask naturally for missing info

TOOL CALLING:
- You can call up to {MaxToolCallsPerRequest} tools in a single response
- If a task needs more operations than that, do the first batch and tell the user you'll continue with the next batch after
- Prefer calling multiple tools at once over one-by-one when the operations are independent

IMPORTANT:
- Do NOT ask ""should I proceed?"" - just call the tool when ready
- The system handles confirmation for destructive tools automatically

RESPONSE FORMAT:
When you respond with text (not tool calls), return a JSON object with two fields:
{{""text"": ""your plain text response"", ""ssml"": ""<speak>...</speak>""}}

For the ssml field, use this Azure TTS template as the outer structure and enrich the inner content with SSML as you see fit:
<speak version=""1.0"" xmlns=""http://www.w3.org/2001/10/synthesis"" xmlns:mstts=""https://www.w3.org/2001/mstts"" xml:lang=""{lang}"">
  <voice name=""{character.VoiceName}"">
    <mstts:silence type=""Sentenceboundary"" value=""{character.SentencePause}""/>
    <mstts:silence type=""Comma-exact"" value=""{character.CommaPause}""/>
    <mstts:express-as style=""{character.DefaultStyle}"" styledegree=""{character.StyleDegree}"">
      <prosody rate=""{character.Rate}"" pitch=""{character.Pitch}"">
        YOUR CONTENT HERE
      </prosody>
    </mstts:express-as>
  </voice>
</speak>";

        messages.Add(new SystemChatMessage(systemPrompt));

        // Build conversation history
        foreach (var msg in session.ConversationHistory)
        {
            switch (msg.Role)
            {
                case ConversationRole.User:
                    messages.Add(new UserChatMessage(msg.Content ?? ""));
                    break;

                case ConversationRole.Assistant:
                    if (msg.ToolCalls is { Count: > 0 })
                    {
                        // Batch tool calls — single assistant message with all calls
                        var chatToolCalls = msg.ToolCalls.Select(tc =>
                            ChatToolCall.CreateFunctionToolCall(
                                tc.ToolCallId, tc.ToolName,
                                BinaryData.FromString(JsonSerializer.Serialize(tc.Arguments)))).ToList();
                        messages.Add(new AssistantChatMessage(chatToolCalls));
                    }
                    else if (msg.ToolCallId != null && msg.ToolName != null && msg.ToolArguments != null)
                    {
                        // Legacy single tool call
                        var toolCall = ChatToolCall.CreateFunctionToolCall(
                            msg.ToolCallId,
                            msg.ToolName,
                            BinaryData.FromString(JsonSerializer.Serialize(msg.ToolArguments)));
                        messages.Add(new AssistantChatMessage([toolCall]));
                    }
                    else if (msg.Content != null)
                    {
                        messages.Add(new AssistantChatMessage(msg.Content));
                    }
                    break;

                case ConversationRole.Tool:
                    if (msg.ToolCallId != null)
                    {
                        messages.Add(new ToolChatMessage(msg.ToolCallId, msg.Content ?? ""));
                    }
                    break;
            }
        }

        return messages;
    }

    private static Dictionary<string, object?> ParseToolArguments(string json)
    {
        var arguments = new Dictionary<string, object?>();
        try
        {
            var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                arguments[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var intVal) ? intVal : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText()
                };
            }
        }
        catch (Exception)
        {
            // Return empty if parsing fails
        }
        return arguments;
    }

    private static string GenerateConfirmationPrompt(string toolName, Dictionary<string, object?> arguments)
    {
        return toolName.ToLowerInvariant() switch
        {
            "log_time" => GenerateLogTimeConfirmation(arguments),
            "delete_time_entry" => "I'll delete this time entry. Say yes to confirm or no to cancel.",
            "submit_time_entry" => "I'll submit this time entry for approval. Say yes to confirm or no to cancel.",
            "update_time_entry" => GenerateUpdateConfirmation(arguments),
            "move_task_to_project" => GenerateMoveConfirmation(arguments),
            "approve_time_entry" => "I'll approve this time entry. Say yes to confirm or no to cancel.",
            "decline_time_entry" => "I'll decline this time entry. Say yes to confirm or no to cancel.",
            _ => $"I'll execute {toolName}. Say yes to confirm or no to cancel."
        };
    }

    private static string GenerateLogTimeConfirmation(Dictionary<string, object?> args)
    {
        var project = args.GetValueOrDefault("projectCode")?.ToString() ?? "unknown project";
        var hours = args.GetValueOrDefault("standardHours")?.ToString() ?? "8";
        var task = args.GetValueOrDefault("task")?.ToString() ?? "Development";
        var date = args.GetValueOrDefault("startDate")?.ToString();

        var dateStr = "today";
        if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var parsedDate))
        {
            if (parsedDate.Date == DateTime.Today)
                dateStr = "today";
            else if (parsedDate.Date == DateTime.Today.AddDays(-1))
                dateStr = "yesterday";
            else
                dateStr = parsedDate.ToString("MMMM d");
        }

        return $"I'll log {hours} hours on {project} for {task} on {dateStr}. Say yes to confirm.";
    }

    private static string GenerateUpdateConfirmation(Dictionary<string, object?> args)
    {
        var updates = new List<string>();
        if (args.ContainsKey("standardHours")) updates.Add($"hours to {args["standardHours"]}");
        if (args.ContainsKey("projectCode")) updates.Add($"project to {args["projectCode"]}");
        if (args.ContainsKey("task")) updates.Add($"task to {args["task"]}");

        var updateStr = updates.Count > 0 ? string.Join(" and ", updates) : "this entry";
        return $"I'll update {updateStr}. Say yes to confirm or no to cancel.";
    }

    private static string GenerateMoveConfirmation(Dictionary<string, object?> args)
    {
        var newProject = args.GetValueOrDefault("newProjectCode")?.ToString() ?? "the new project";
        return $"I'll move this entry to {newProject}. Say yes to confirm or no to cancel.";
    }

    private static string SerializeMessages(List<ChatMessage> messages)
    {
        var serialized = new List<object>();
        foreach (var msg in messages)
        {
            switch (msg)
            {
                case SystemChatMessage sys:
                    serialized.Add(new { role = "system", content = sys.Content[0].Text });
                    break;
                case UserChatMessage usr:
                    serialized.Add(new { role = "user", content = usr.Content[0].Text });
                    break;
                case AssistantChatMessage asst:
                    if (asst.ToolCalls.Count > 0)
                    {
                        var toolCalls = asst.ToolCalls.Select(tc => new
                        {
                            id = tc.Id,
                            function = tc.FunctionName,
                            arguments = tc.FunctionArguments.ToString()
                        });
                        serialized.Add(new { role = "assistant", tool_calls = toolCalls });
                    }
                    else
                    {
                        serialized.Add(new { role = "assistant", content = asst.Content.Count > 0 ? asst.Content[0].Text : "" });
                    }
                    break;
                case ToolChatMessage tool:
                    serialized.Add(new { role = "tool", tool_call_id = tool.ToolCallId, content = tool.Content[0].Text });
                    break;
            }
        }
        return JsonSerializer.Serialize(serialized, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string SerializeResponse(ChatCompletion choice)
    {
        if (choice.ToolCalls.Count > 0)
        {
            var toolCalls = choice.ToolCalls.Select(tc => new
            {
                id = tc.Id,
                function = tc.FunctionName,
                arguments = tc.FunctionArguments.ToString()
            });
            return JsonSerializer.Serialize(new { tool_calls = toolCalls }, new JsonSerializerOptions { WriteIndented = true });
        }

        var text = choice.Content.Count > 0 ? choice.Content[0].Text : null;
        return JsonSerializer.Serialize(new { text }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string PrettyJson(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    private (string plainText, string ssml) ParseSsmlResponse(string rawResponse, Session session)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            var fallback = "I'm not sure how to help with that.";
            return (fallback, GenerateSsml(fallback, session));
        }

        // Strip markdown code block wrapping if present
        var trimmed = rawResponse.Trim();
        if (trimmed.StartsWith("```"))
        {
            trimmed = Regex.Replace(trimmed, @"^```(?:json)?\s*\n?", "");
            trimmed = Regex.Replace(trimmed, @"\n?```\s*$", "");
            trimmed = trimmed.Trim();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<SsmlResponse>(trimmed);
            if (parsed?.Text != null)
            {
                var plainText = parsed.Text;
                var ssml = parsed.Ssml;

                // Validate SSML if present
                if (!string.IsNullOrWhiteSpace(ssml) && ssml.TrimStart().StartsWith("<speak"))
                {
                    try
                    {
                        var xmlDoc = new XmlDocument();
                        xmlDoc.LoadXml(ssml);
                        return (plainText, ssml);
                    }
                    catch (XmlException ex)
                    {
                        _logger.LogWarning("LLM returned invalid SSML XML, falling back to GenerateSsml: {Error}", ex.Message);
                    }
                }

                // JSON parsed but SSML missing or invalid — use GenerateSsml fallback
                return (plainText, GenerateSsml(plainText, session));
            }
        }
        catch (JsonException)
        {
            // Not JSON — treat as plain text
        }

        // Raw plain text fallback
        return (rawResponse, GenerateSsml(rawResponse, session));
    }

    private async Task<(string plainText, string ssml)> GenerateBatchConfirmationAsync(
        Session session, List<ToolCallInfo> toolCalls)
    {
        try
        {
            var messages = BuildChatMessages(session);

            // Describe all operations
            var descriptions = toolCalls.Select(tc =>
                $"- {tc.ToolName}({JsonSerializer.Serialize(tc.Arguments)})").ToList();
            var allOps = string.Join("\n", descriptions);

            messages.Add(new SystemChatMessage(
                $"You are about to execute {toolCalls.Count} operation(s):\n{allOps}\n\n" +
                "Describe to the user what you're about to do with specific details. " +
                "Ask for confirmation. Stay in character. Keep it concise. Vary how you ask."));

            var response = await _chatClient.CompleteChatAsync(messages);
            var raw = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : null;

            if (!string.IsNullOrWhiteSpace(raw))
                return ParseSsmlResponse(raw, session);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate LLM batch confirmation, using static fallback");
        }

        var fallback = GenerateBatchConfirmationPrompt(toolCalls);
        return (fallback, GenerateSsml(fallback, session));
    }

    private static string GenerateBatchConfirmationPrompt(List<ToolCallInfo> toolCalls)
    {
        if (toolCalls.Count == 1)
            return GenerateConfirmationPrompt(toolCalls[0].ToolName, toolCalls[0].Arguments);

        // Group by tool name for summary
        var groups = toolCalls.GroupBy(tc => tc.ToolName).Select(g => $"{g.Count()} {g.Key}");
        return $"I'll execute {toolCalls.Count} operations ({string.Join(", ", groups)}). Say yes to confirm or no to cancel.";
    }

    private string GenerateSsml(string text, Session session)
    {
        var character = session.Character ?? _character;

        // Escape XML special characters
        var escapedText = System.Security.SecurityElement.Escape(text) ?? string.Empty;
        var voice = character.VoiceName;
        var style = character.DefaultStyle;
        var degree = character.StyleDegree;
        var rate = character.Rate;
        var pitch = character.Pitch;
        var sentencePause = character.SentencePause;
        var commaPause = character.CommaPause;
        var lang = voice.Length >= 5 ? voice[..5] : "en-US";

        return $"""
            <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xmlns:mstts="https://www.w3.org/2001/mstts" xml:lang="{lang}">
              <voice name="{voice}">
                <mstts:silence type="Sentenceboundary" value="{sentencePause}"/>
                <mstts:silence type="Comma-exact" value="{commaPause}"/>
                <mstts:express-as style="{style}" styledegree="{degree}">
                  <prosody rate="{rate}" pitch="{pitch}">
                    {escapedText}
                  </prosody>
                </mstts:express-as>
              </voice>
            </speak>
            """;
    }
}
