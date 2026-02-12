// VoiceAgent/Services/AgentService.cs
using System.Diagnostics;
using System.Text.Json;
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
            session.ClearPendingToolExecution();
        }

        // Handle pending confirmation
        if (session.HasPendingConfirmation)
        {
            var pending = session.PendingToolExecution!;
            var confirmationType = await _confirmationDetector.DetectAsync(text, pending.ConfirmationPrompt);

            if (confirmationType == ConfirmationType.Confirmed)
            {
                return await ExecutePendingToolAsync(session, text);
            }

            if (confirmationType == ConfirmationType.Cancelled)
            {
                session.ClearPendingToolExecution();
                session.AddUserMessage(text);
                var cancelText = "Cancelled. No changes made.";
                session.AddAssistantMessage(cancelText);
                return new AgentResponse { Text = cancelText, Ssml = GenerateSsml(cancelText, session), AwaitingConfirmation = false };
            }

            // Modification requested - clear pending and let agent handle with context
            session.ClearPendingToolExecution();
        }

        // Add user message to history
        session.AddUserMessage(text);

        // Run the conversational agent loop
        return await RunAgentLoopAsync(session);
    }

    private async Task<AgentResponse> ExecutePendingToolAsync(Session session, string userText)
    {
        var pending = session.PendingToolExecution!;
        session.ClearPendingToolExecution();

        // Add user confirmation to history
        session.AddUserMessage(userText);

        // Execute the tool
        string result;
        using (var toolActivity = ActivitySource.StartActivity("mcp-tool-call"))
        {
            toolActivity?.SetTag("tool.name", pending.ToolName);
            toolActivity?.SetTag("tool.arguments", PrettyJson(JsonSerializer.Serialize(pending.Arguments)));

            result = await _mcpClient.ExecuteToolAsync(pending.ToolName, pending.Arguments);

            toolActivity?.SetTag("tool.result", PrettyJson(result));
        }

        // Add tool call and result to history
        session.AddToolCall(pending.ToolCallId, pending.ToolName, pending.Arguments);
        session.AddToolResult(pending.ToolCallId, pending.ToolName, result);

        // Format for TTS
        var ttsResponse = await _mcpClient.FormatForTtsAsync(result, pending.ConfirmationPrompt, pending.ToolName);
        session.AddAssistantMessage(ttsResponse);

        _logger.LogInformation("<<< Agent response: {Text}", ttsResponse);
        return new AgentResponse { Text = ttsResponse, Ssml = GenerateSsml(ttsResponse, session), AwaitingConfirmation = false };
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

            // Check if LLM wants to call a tool
            if (choice.ToolCalls.Count > 0)
            {
                var toolCall = choice.ToolCalls[0]; // Process one tool at a time
                var toolName = toolCall.FunctionName;
                var argsJson = toolCall.FunctionArguments.ToString();
                var arguments = ParseToolArguments(argsJson);

                // Check if destructive tool - require confirmation
                if (DestructiveTools.Contains(toolName))
                {
                    var confirmationPrompt = GenerateConfirmationPrompt(toolName, arguments);
                    session.SetPendingToolExecution(toolCall.Id, toolName, arguments, confirmationPrompt);
                    session.AddAssistantMessage(confirmationPrompt);

                    _logger.LogInformation("<<< Agent response (awaiting confirmation): {Text}", confirmationPrompt);

                    Activity.Current?.AddEvent(new ActivityEvent("confirmation-requested",
                        tags: new ActivityTagsCollection
                        {
                            { "tool.name", toolName },
                            { "confirmation.prompt", confirmationPrompt }
                        }));

                    return new AgentResponse { Text = confirmationPrompt, Ssml = GenerateSsml(confirmationPrompt, session), AwaitingConfirmation = true };
                }

                // Non-destructive tool - execute immediately
                toolCallCount++;
                string result;
                using (var toolActivity = ActivitySource.StartActivity("mcp-tool-call"))
                {
                    toolActivity?.SetTag("tool.name", toolName);
                    toolActivity?.SetTag("tool.arguments", PrettyJson(argsJson));

                    result = await _mcpClient.ExecuteToolAsync(toolName, arguments);

                    toolActivity?.SetTag("tool.result", PrettyJson(result));
                }

                session.AddToolCall(toolCall.Id, toolName, arguments);
                session.AddToolResult(toolCall.Id, toolName, result);

                // Continue the loop to let LLM process the result
                continue;
            }

            // LLM returned a text response
            var responseText = choice.Content.Count > 0 ? choice.Content[0].Text : "I'm not sure how to help with that.";
            session.AddAssistantMessage(responseText);

            _logger.LogInformation("<<< Agent response: {Text}", responseText);
            return new AgentResponse { Text = responseText, Ssml = GenerateSsml(responseText, session), AwaitingConfirmation = false };
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
        var systemPrompt = $@"Today's date is {today}. You are a voice assistant for time reporting.

CHARACTER:
{character.Personality}

SPEECH STYLE:
{character.SpeechStyle}

RULES:
- Keep responses SHORT (1-2 sentences max)
- NEVER use markdown: no asterisks, no bullet points, no bold, no lists
- Say dates as ""November 28th"" not ""2024-11-28""
- Never read UUIDs or technical IDs aloud
- Stay in character but still be helpful

TIME LOGGING:
- Need: project code (required), hours (required)
- Task defaults to ""Development"", date defaults to today
- Ask naturally for missing info

IMPORTANT:
- Do NOT ask ""should I proceed?"" - the system handles confirmation automatically
- Just call the tool when ready - system will prompt for confirmation";

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
                    if (msg.ToolCallId != null && msg.ToolName != null && msg.ToolArguments != null)
                    {
                        // Assistant message with tool call
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
                        var tc = asst.ToolCalls[0];
                        serialized.Add(new
                        {
                            role = "assistant",
                            tool_call = new { id = tc.Id, function = tc.FunctionName, arguments = tc.FunctionArguments.ToString() }
                        });
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
