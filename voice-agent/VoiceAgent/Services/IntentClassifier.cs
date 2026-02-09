// VoiceAgent/Services/ConfirmationDetector.cs
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace VoiceAgent.Services;

public enum ConfirmationType
{
    Confirmed,
    Cancelled,
    Modification
}

public class ConfirmationDetector
{
    private readonly ChatClient _chatClient;
    private readonly ILogger<ConfirmationDetector> _logger;

    public ConfirmationDetector(AzureOpenAIConfig aiConfig, ILogger<ConfirmationDetector> logger)
    {
        var azureClient = new AzureOpenAIClient(
            new Uri(aiConfig.Endpoint),
            new AzureKeyCredential(aiConfig.ApiKey));
        _chatClient = azureClient.GetChatClient(aiConfig.DeploymentName);
        _logger = logger;
    }

    public async Task<ConfirmationType> DetectAsync(string userResponse, string pendingAction)
    {
        var prompt = $@"The user was asked to confirm this action: ""{pendingAction}""

The user responded: ""{userResponse}""

Classify the user's response as exactly one of:
- CONFIRM: User agrees to proceed (yes, ok, do it, confirm, etc.)
- CANCEL: User wants to abort entirely (no, cancel, never mind, stop, etc.)
- MODIFY: User wants to change something (different date, different hours, different project, etc.)

Respond with only one word: CONFIRM, CANCEL, or MODIFY";

        try
        {
            var response = await _chatClient.CompleteChatAsync([new UserChatMessage(prompt)]);
            var result = response.Value.Content[0].Text.Trim().ToUpperInvariant();

            _logger.LogInformation("ConfirmationDetector: pending=\"{Pending}\" user=\"{User}\" -> {Result}",
                pendingAction, userResponse, result);

            return result switch
            {
                "CONFIRM" => ConfirmationType.Confirmed,
                "CANCEL" => ConfirmationType.Cancelled,
                "MODIFY" => ConfirmationType.Modification,
                _ => ConfirmationType.Modification // Default to modification if unclear
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConfirmationDetector failed, defaulting to Modification");
            return ConfirmationType.Modification;
        }
    }
}
