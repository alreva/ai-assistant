// VoiceAgent.Tests/Services/SessionManagerTests.cs
using FluentAssertions;
using VoiceAgent.Models;
using VoiceAgent.Services;

namespace VoiceAgent.Tests.Services;

public class SessionManagerTests
{
    [Fact]
    public void GetOrCreateSession_NewSession_CreatesSession()
    {
        var manager = new SessionManager(
            sessionTimeout: TimeSpan.FromHours(4),
            confirmationTimeout: TimeSpan.FromMinutes(2));

        var session = manager.GetOrCreateSession("new-session");

        session.Should().NotBeNull();
        session.SessionId.Should().Be("new-session");
    }

    [Fact]
    public void GetOrCreateSession_ExistingSession_ReturnsSameSession()
    {
        var manager = new SessionManager(
            sessionTimeout: TimeSpan.FromHours(4),
            confirmationTimeout: TimeSpan.FromMinutes(2));

        var session1 = manager.GetOrCreateSession("session-1");
        var session2 = manager.GetOrCreateSession("session-1");

        session1.Should().BeSameAs(session2);
    }

    [Fact]
    public void GetOrCreateSession_ExpiredSession_CreatesNewSession()
    {
        var manager = new SessionManager(
            sessionTimeout: TimeSpan.FromMilliseconds(1),
            confirmationTimeout: TimeSpan.FromMinutes(2));

        var session1 = manager.GetOrCreateSession("session-1");
        Thread.Sleep(10);
        var session2 = manager.GetOrCreateSession("session-1");

        session1.Should().NotBeSameAs(session2);
    }

    [Fact]
    public void EndSession_RemovesSession()
    {
        var manager = new SessionManager(
            sessionTimeout: TimeSpan.FromHours(4),
            confirmationTimeout: TimeSpan.FromMinutes(2));

        var session1 = manager.GetOrCreateSession("session-1");
        manager.EndSession("session-1");
        var session2 = manager.GetOrCreateSession("session-1");

        session1.Should().NotBeSameAs(session2);
    }
}
