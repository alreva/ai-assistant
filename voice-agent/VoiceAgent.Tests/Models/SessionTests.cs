using FluentAssertions;
using VoiceAgent.Models;

namespace VoiceAgent.Tests.Models;

public class SessionTests
{
    [Fact]
    public void Session_NewSession_HasNoExpiredTimeout()
    {
        var session = new Session("test-session");

        session.IsExpired(TimeSpan.FromHours(4)).Should().BeFalse();
    }

    [Fact]
    public void Session_OldSession_IsExpired()
    {
        var session = new Session("test-session");
        session.LastActivityTime = DateTime.UtcNow.AddHours(-5);

        session.IsExpired(TimeSpan.FromHours(4)).Should().BeTrue();
    }

    [Fact]
    public void Session_PendingConfirmation_ExpiresAfterTimeout()
    {
        var session = new Session("test-session");
        session.SetPendingConfirmation("log_time", "Log 8 hours on INTERNAL");
        session.ConfirmationRequestedAt = DateTime.UtcNow.AddMinutes(-3);

        session.IsConfirmationExpired(TimeSpan.FromMinutes(2)).Should().BeTrue();
    }

    [Fact]
    public void Session_TouchActivity_UpdatesLastActivityTime()
    {
        var session = new Session("test-session");
        var before = session.LastActivityTime;

        Thread.Sleep(10);
        session.TouchActivity();

        session.LastActivityTime.Should().BeAfter(before);
    }
}
