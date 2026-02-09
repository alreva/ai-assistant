// VoiceAgent/Services/IAgentService.cs
using VoiceAgent.Models;

namespace VoiceAgent.Services;

public interface IAgentService
{
    Task<AgentResponse> ProcessMessageAsync(string sessionId, string text, string? character = null);
}
