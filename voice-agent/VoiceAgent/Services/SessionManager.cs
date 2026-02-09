// VoiceAgent/Services/SessionManager.cs
using System.Collections.Concurrent;
using VoiceAgent.Models;

namespace VoiceAgent.Services;

public class SessionManager
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly TimeSpan _sessionTimeout;
    private readonly TimeSpan _confirmationTimeout;

    public SessionManager(TimeSpan sessionTimeout, TimeSpan confirmationTimeout)
    {
        _sessionTimeout = sessionTimeout;
        _confirmationTimeout = confirmationTimeout;
    }

    public Session GetOrCreateSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            if (existing.IsExpired(_sessionTimeout))
            {
                _sessions.TryRemove(sessionId, out _);
            }
            else
            {
                existing.TouchActivity();
                return existing;
            }
        }

        var session = new Session(sessionId);
        _sessions[sessionId] = session;
        return session;
    }

    public void EndSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    public bool IsConfirmationExpired(Session session)
    {
        return session.IsConfirmationExpired(_confirmationTimeout);
    }
}
