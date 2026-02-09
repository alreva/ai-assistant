# Voice Agent Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a .NET voice agent that receives transcriptions via WebSocket, uses Azure OpenAI to understand intent, and executes time-reporting actions via MCP tools.

**Architecture:** WebSocket server receives transcriptions from whisper-streaming client. Microsoft Agent Framework orchestrates LLM calls to Azure OpenAI. MCP client connects to existing time-reporting MCP server for tool execution.

**Tech Stack:** .NET 10, Microsoft.Agents.AI.OpenAI, ModelContextProtocol, System.Net.WebSockets, xUnit

---

## Task 1: Create Solution and Project Structure

**Files:**
- Create: `voice-agent/VoiceAgent/VoiceAgent.csproj`
- Create: `voice-agent/VoiceAgent.Tests/VoiceAgent.Tests.csproj`
- Create: `voice-agent/voice-agent.sln`

**Step 1: Create solution directory**

```bash
mkdir -p voice-agent
cd voice-agent
```

**Step 2: Create the solution and projects**

```bash
dotnet new sln -n voice-agent
dotnet new console -n VoiceAgent -f net10.0
dotnet new xunit -n VoiceAgent.Tests -f net10.0
dotnet sln add VoiceAgent
dotnet sln add VoiceAgent.Tests
dotnet add VoiceAgent.Tests reference VoiceAgent
```

**Step 3: Add required packages to VoiceAgent.csproj**

```bash
cd VoiceAgent
dotnet add package Azure.AI.OpenAI --prerelease
dotnet add package Azure.Identity
dotnet add package Microsoft.Agents.AI.OpenAI --prerelease
dotnet add package ModelContextProtocol --prerelease
dotnet add package Microsoft.Extensions.Hosting
cd ..
```

**Step 4: Add test packages to VoiceAgent.Tests.csproj**

```bash
cd VoiceAgent.Tests
dotnet add package Moq
dotnet add package FluentAssertions
cd ..
```

**Step 5: Verify build**

Run: `dotnet build`
Expected: Build succeeded with 0 errors

**Step 6: Commit**

```bash
git add voice-agent/
git commit -m "chore: create voice-agent solution with projects and dependencies"
```

---

## Task 2: Define Message Models

**Files:**
- Create: `voice-agent/VoiceAgent/Models/TranscriptionMessage.cs`
- Create: `voice-agent/VoiceAgent/Models/AgentResponse.cs`
- Test: `voice-agent/VoiceAgent.Tests/Models/MessageSerializationTests.cs`

**Step 1: Write the failing test**

```csharp
// VoiceAgent.Tests/Models/MessageSerializationTests.cs
using System.Text.Json;
using FluentAssertions;
using VoiceAgent.Models;

namespace VoiceAgent.Tests.Models;

public class MessageSerializationTests
{
    [Fact]
    public void TranscriptionMessage_DeserializesFromJson()
    {
        var json = """
            {
                "type": "transcription",
                "text": "Log 8 hours on INTERNAL",
                "session_id": "abc-123"
            }
            """;

        var message = JsonSerializer.Deserialize<TranscriptionMessage>(json);

        message.Should().NotBeNull();
        message!.Type.Should().Be("transcription");
        message.Text.Should().Be("Log 8 hours on INTERNAL");
        message.SessionId.Should().Be("abc-123");
    }

    [Fact]
    public void AgentResponse_SerializesToJson()
    {
        var response = new AgentResponse
        {
            Type = "response",
            Text = "I'll log 8 hours. Confirm?",
            AwaitingConfirmation = true
        };

        var json = JsonSerializer.Serialize(response);

        json.Should().Contain("\"type\":\"response\"");
        json.Should().Contain("\"text\":\"I'll log 8 hours. Confirm?\"");
        json.Should().Contain("\"awaiting_confirmation\":true");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `cd voice-agent && dotnet test --filter "MessageSerializationTests"`
Expected: FAIL - types not found

**Step 3: Write minimal implementation**

```csharp
// VoiceAgent/Models/TranscriptionMessage.cs
using System.Text.Json.Serialization;

namespace VoiceAgent.Models;

public class TranscriptionMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;
}
```

```csharp
// VoiceAgent/Models/AgentResponse.cs
using System.Text.Json.Serialization;

namespace VoiceAgent.Models;

public class AgentResponse
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "response";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("awaiting_confirmation")]
    public bool AwaitingConfirmation { get; set; }
}
```

**Step 4: Run test to verify it passes**

Run: `cd voice-agent && dotnet test --filter "MessageSerializationTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add voice-agent/
git commit -m "feat: add TranscriptionMessage and AgentResponse models"
```

---

## Task 3: Implement Session Model

**Files:**
- Create: `voice-agent/VoiceAgent/Models/Session.cs`
- Test: `voice-agent/VoiceAgent.Tests/Models/SessionTests.cs`

**Step 1: Write the failing test**

```csharp
// VoiceAgent.Tests/Models/SessionTests.cs
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
```

**Step 2: Run test to verify it fails**

Run: `cd voice-agent && dotnet test --filter "SessionTests"`
Expected: FAIL - Session type not found

**Step 3: Write minimal implementation**

```csharp
// VoiceAgent/Models/Session.cs
namespace VoiceAgent.Models;

public class Session
{
    public string SessionId { get; }
    public DateTime LastActivityTime { get; set; }
    public DateTime? ConfirmationRequestedAt { get; set; }
    public string? PendingAction { get; private set; }
    public string? PendingActionDescription { get; private set; }

    public Session(string sessionId)
    {
        SessionId = sessionId;
        LastActivityTime = DateTime.UtcNow;
    }

    public bool IsExpired(TimeSpan timeout)
    {
        return DateTime.UtcNow - LastActivityTime > timeout;
    }

    public bool IsConfirmationExpired(TimeSpan timeout)
    {
        if (ConfirmationRequestedAt == null) return false;
        return DateTime.UtcNow - ConfirmationRequestedAt.Value > timeout;
    }

    public void SetPendingConfirmation(string action, string description)
    {
        PendingAction = action;
        PendingActionDescription = description;
        ConfirmationRequestedAt = DateTime.UtcNow;
    }

    public void ClearPendingConfirmation()
    {
        PendingAction = null;
        PendingActionDescription = null;
        ConfirmationRequestedAt = null;
    }

    public bool HasPendingConfirmation => PendingAction != null;

    public void TouchActivity()
    {
        LastActivityTime = DateTime.UtcNow;
    }
}
```

**Step 4: Run test to verify it passes**

Run: `cd voice-agent && dotnet test --filter "SessionTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add voice-agent/
git commit -m "feat: add Session model with expiration logic"
```

---

## Task 4: Implement SessionManager

**Files:**
- Create: `voice-agent/VoiceAgent/Services/SessionManager.cs`
- Test: `voice-agent/VoiceAgent.Tests/Services/SessionManagerTests.cs`

**Step 1: Write the failing test**

```csharp
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
```

**Step 2: Run test to verify it fails**

Run: `cd voice-agent && dotnet test --filter "SessionManagerTests"`
Expected: FAIL - SessionManager not found

**Step 3: Write minimal implementation**

```csharp
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
```

**Step 4: Run test to verify it passes**

Run: `cd voice-agent && dotnet test --filter "SessionManagerTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add voice-agent/
git commit -m "feat: add SessionManager with timeout handling"
```

---

## Task 5: Implement Intent Classifier

**Files:**
- Create: `voice-agent/VoiceAgent/Services/IntentClassifier.cs`
- Test: `voice-agent/VoiceAgent.Tests/Services/IntentClassifierTests.cs`

**Step 1: Write the failing test**

```csharp
// VoiceAgent.Tests/Services/IntentClassifierTests.cs
using FluentAssertions;
using VoiceAgent.Services;

namespace VoiceAgent.Tests.Services;

public class IntentClassifierTests
{
    [Theory]
    [InlineData("show my entries", IntentType.Query)]
    [InlineData("list all projects", IntentType.Query)]
    [InlineData("what time did I log", IntentType.Query)]
    [InlineData("how many hours", IntentType.Query)]
    public void ClassifyIntent_QueryPhrases_ReturnsQuery(string text, IntentType expected)
    {
        var classifier = new IntentClassifier();

        var result = classifier.ClassifyIntent(text);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("log 8 hours", IntentType.Update)]
    [InlineData("delete the entry", IntentType.Update)]
    [InlineData("move yesterday's time", IntentType.Update)]
    [InlineData("submit for approval", IntentType.Update)]
    [InlineData("update the hours", IntentType.Update)]
    public void ClassifyIntent_UpdatePhrases_ReturnsUpdate(string text, IntentType expected)
    {
        var classifier = new IntentClassifier();

        var result = classifier.ClassifyIntent(text);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("yes", IntentType.Confirmation)]
    [InlineData("confirm", IntentType.Confirmation)]
    [InlineData("do it", IntentType.Confirmation)]
    [InlineData("go ahead", IntentType.Confirmation)]
    public void ClassifyIntent_ConfirmationPhrases_ReturnsConfirmation(string text, IntentType expected)
    {
        var classifier = new IntentClassifier();

        var result = classifier.ClassifyIntent(text);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("no", IntentType.Cancellation)]
    [InlineData("cancel", IntentType.Cancellation)]
    [InlineData("never mind", IntentType.Cancellation)]
    [InlineData("stop", IntentType.Cancellation)]
    public void ClassifyIntent_CancellationPhrases_ReturnsCancellation(string text, IntentType expected)
    {
        var classifier = new IntentClassifier();

        var result = classifier.ClassifyIntent(text);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("goodbye", IntentType.EndSession)]
    [InlineData("done", IntentType.EndSession)]
    [InlineData("that's all", IntentType.EndSession)]
    public void ClassifyIntent_EndSessionPhrases_ReturnsEndSession(string text, IntentType expected)
    {
        var classifier = new IntentClassifier();

        var result = classifier.ClassifyIntent(text);

        result.Should().Be(expected);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `cd voice-agent && dotnet test --filter "IntentClassifierTests"`
Expected: FAIL - IntentClassifier not found

**Step 3: Write minimal implementation**

```csharp
// VoiceAgent/Services/IntentClassifier.cs
namespace VoiceAgent.Services;

public enum IntentType
{
    Query,
    Update,
    Confirmation,
    Cancellation,
    EndSession,
    Unknown
}

public class IntentClassifier
{
    private static readonly string[] QueryKeywords = ["show", "list", "what", "how many", "get", "display"];
    private static readonly string[] UpdateKeywords = ["log", "delete", "move", "submit", "update", "add", "create", "remove"];
    private static readonly string[] ConfirmationKeywords = ["yes", "confirm", "do it", "go ahead", "proceed", "okay", "ok"];
    private static readonly string[] CancellationKeywords = ["no", "cancel", "never mind", "stop", "abort", "don't"];
    private static readonly string[] EndSessionKeywords = ["goodbye", "bye", "done", "that's all", "exit", "quit"];

    public IntentType ClassifyIntent(string text)
    {
        var lowerText = text.ToLowerInvariant();

        // Check end session first (short phrases)
        if (EndSessionKeywords.Any(k => lowerText.Contains(k)))
            return IntentType.EndSession;

        // Check confirmation/cancellation (usually short responses)
        if (ConfirmationKeywords.Any(k => lowerText.Contains(k)))
            return IntentType.Confirmation;

        if (CancellationKeywords.Any(k => lowerText.Contains(k)))
            return IntentType.Cancellation;

        // Check query vs update
        if (UpdateKeywords.Any(k => lowerText.Contains(k)))
            return IntentType.Update;

        if (QueryKeywords.Any(k => lowerText.Contains(k)))
            return IntentType.Query;

        return IntentType.Unknown;
    }
}
```

**Step 4: Run test to verify it passes**

Run: `cd voice-agent && dotnet test --filter "IntentClassifierTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add voice-agent/
git commit -m "feat: add IntentClassifier for query/update detection"
```

---

## Task 6: Implement AgentService Interface

**Files:**
- Create: `voice-agent/VoiceAgent/Services/IAgentService.cs`
- Create: `voice-agent/VoiceAgent/Services/AgentService.cs`
- Test: `voice-agent/VoiceAgent.Tests/Services/AgentServiceTests.cs`

**Step 1: Write the failing test**

```csharp
// VoiceAgent.Tests/Services/AgentServiceTests.cs
using FluentAssertions;
using Moq;
using VoiceAgent.Models;
using VoiceAgent.Services;

namespace VoiceAgent.Tests.Services;

public class AgentServiceTests
{
    [Fact]
    public async Task ProcessMessage_QueryIntent_ExecutesImmediately()
    {
        var mockMcpClient = new Mock<IMcpClientService>();
        mockMcpClient
            .Setup(m => m.ExecuteQueryAsync(It.IsAny<string>()))
            .ReturnsAsync("You have 3 entries this week.");

        var service = new AgentService(
            mockMcpClient.Object,
            new SessionManager(TimeSpan.FromHours(4), TimeSpan.FromMinutes(2)),
            new IntentClassifier());

        var response = await service.ProcessMessageAsync("session-1", "show my entries");

        response.Text.Should().Contain("entries");
        response.AwaitingConfirmation.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessMessage_UpdateIntent_AsksForConfirmation()
    {
        var mockMcpClient = new Mock<IMcpClientService>();

        var service = new AgentService(
            mockMcpClient.Object,
            new SessionManager(TimeSpan.FromHours(4), TimeSpan.FromMinutes(2)),
            new IntentClassifier());

        var response = await service.ProcessMessageAsync("session-1", "log 8 hours on INTERNAL");

        response.AwaitingConfirmation.Should().BeTrue();
        response.Text.Should().Contain("confirm");
    }

    [Fact]
    public async Task ProcessMessage_ConfirmationAfterUpdate_ExecutesAction()
    {
        var mockMcpClient = new Mock<IMcpClientService>();
        mockMcpClient
            .Setup(m => m.ExecuteUpdateAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("Logged 8 hours on INTERNAL.");

        var sessionManager = new SessionManager(TimeSpan.FromHours(4), TimeSpan.FromMinutes(2));
        var service = new AgentService(
            mockMcpClient.Object,
            sessionManager,
            new IntentClassifier());

        // First, request an update
        await service.ProcessMessageAsync("session-1", "log 8 hours on INTERNAL");

        // Then confirm
        var response = await service.ProcessMessageAsync("session-1", "yes");

        response.Text.Should().Contain("Logged");
        response.AwaitingConfirmation.Should().BeFalse();
        mockMcpClient.Verify(m => m.ExecuteUpdateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_CancellationAfterUpdate_CancelsAction()
    {
        var mockMcpClient = new Mock<IMcpClientService>();

        var service = new AgentService(
            mockMcpClient.Object,
            new SessionManager(TimeSpan.FromHours(4), TimeSpan.FromMinutes(2)),
            new IntentClassifier());

        // First, request an update
        await service.ProcessMessageAsync("session-1", "log 8 hours on INTERNAL");

        // Then cancel
        var response = await service.ProcessMessageAsync("session-1", "no");

        response.Text.Should().Contain("Cancelled");
        response.AwaitingConfirmation.Should().BeFalse();
        mockMcpClient.Verify(m => m.ExecuteUpdateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessMessage_EndSession_EndsSession()
    {
        var mockMcpClient = new Mock<IMcpClientService>();

        var service = new AgentService(
            mockMcpClient.Object,
            new SessionManager(TimeSpan.FromHours(4), TimeSpan.FromMinutes(2)),
            new IntentClassifier());

        var response = await service.ProcessMessageAsync("session-1", "goodbye");

        response.Text.Should().Contain("Goodbye");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `cd voice-agent && dotnet test --filter "AgentServiceTests"`
Expected: FAIL - types not found

**Step 3: Write minimal implementation**

```csharp
// VoiceAgent/Services/IAgentService.cs
using VoiceAgent.Models;

namespace VoiceAgent.Services;

public interface IAgentService
{
    Task<AgentResponse> ProcessMessageAsync(string sessionId, string text);
}
```

```csharp
// VoiceAgent/Services/IMcpClientService.cs
namespace VoiceAgent.Services;

public interface IMcpClientService
{
    Task<string> ExecuteQueryAsync(string query);
    Task<string> ExecuteUpdateAsync(string action, string parameters);
    Task<string> GetActionSummaryAsync(string text);
}
```

```csharp
// VoiceAgent/Services/AgentService.cs
using VoiceAgent.Models;

namespace VoiceAgent.Services;

public class AgentService : IAgentService
{
    private readonly IMcpClientService _mcpClient;
    private readonly SessionManager _sessionManager;
    private readonly IntentClassifier _intentClassifier;

    public AgentService(
        IMcpClientService mcpClient,
        SessionManager sessionManager,
        IntentClassifier intentClassifier)
    {
        _mcpClient = mcpClient;
        _sessionManager = sessionManager;
        _intentClassifier = intentClassifier;
    }

    public async Task<AgentResponse> ProcessMessageAsync(string sessionId, string text)
    {
        var session = _sessionManager.GetOrCreateSession(sessionId);
        var intent = _intentClassifier.ClassifyIntent(text);

        // Check for expired confirmation
        if (session.HasPendingConfirmation && _sessionManager.IsConfirmationExpired(session))
        {
            session.ClearPendingConfirmation();
            // Continue processing the new message
        }

        // Handle confirmation/cancellation if pending
        if (session.HasPendingConfirmation)
        {
            if (intent == IntentType.Confirmation)
            {
                var result = await _mcpClient.ExecuteUpdateAsync(
                    session.PendingAction!,
                    session.PendingActionDescription!);
                session.ClearPendingConfirmation();
                return new AgentResponse { Text = result, AwaitingConfirmation = false };
            }

            if (intent == IntentType.Cancellation)
            {
                session.ClearPendingConfirmation();
                return new AgentResponse { Text = "Cancelled. No changes made.", AwaitingConfirmation = false };
            }

            // New command while confirmation pending - cancel old and process new
            session.ClearPendingConfirmation();
        }

        return intent switch
        {
            IntentType.Query => await HandleQueryAsync(text),
            IntentType.Update => await HandleUpdateAsync(session, text),
            IntentType.EndSession => HandleEndSession(sessionId),
            _ => new AgentResponse { Text = "I'm not sure what you mean. Could you rephrase that?" }
        };
    }

    private async Task<AgentResponse> HandleQueryAsync(string text)
    {
        var result = await _mcpClient.ExecuteQueryAsync(text);
        return new AgentResponse { Text = result, AwaitingConfirmation = false };
    }

    private async Task<AgentResponse> HandleUpdateAsync(Session session, string text)
    {
        var summary = await _mcpClient.GetActionSummaryAsync(text);
        session.SetPendingConfirmation("update", text);
        return new AgentResponse
        {
            Text = $"{summary} Say yes to confirm or no to cancel.",
            AwaitingConfirmation = true
        };
    }

    private AgentResponse HandleEndSession(string sessionId)
    {
        _sessionManager.EndSession(sessionId);
        return new AgentResponse { Text = "Goodbye. Session ended.", AwaitingConfirmation = false };
    }
}
```

**Step 4: Run test to verify it passes**

Run: `cd voice-agent && dotnet test --filter "AgentServiceTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add voice-agent/
git commit -m "feat: add AgentService with intent handling and confirmation flow"
```

---

## Task 7: Implement WebSocket Handler

**Files:**
- Create: `voice-agent/VoiceAgent/Handlers/WebSocketHandler.cs`
- Test: `voice-agent/VoiceAgent.Tests/Handlers/WebSocketHandlerTests.cs`

**Step 1: Write the failing test**

```csharp
// VoiceAgent.Tests/Handlers/WebSocketHandlerTests.cs
using System.Text.Json;
using FluentAssertions;
using Moq;
using VoiceAgent.Handlers;
using VoiceAgent.Models;
using VoiceAgent.Services;

namespace VoiceAgent.Tests.Handlers;

public class WebSocketHandlerTests
{
    [Fact]
    public async Task HandleMessage_ValidTranscription_ReturnsAgentResponse()
    {
        var mockAgent = new Mock<IAgentService>();
        mockAgent
            .Setup(a => a.ProcessMessageAsync("session-1", "hello"))
            .ReturnsAsync(new AgentResponse { Text = "Hi there!", AwaitingConfirmation = false });

        var handler = new WebSocketHandler(mockAgent.Object);

        var inputJson = """{"type":"transcription","text":"hello","session_id":"session-1"}""";
        var result = await handler.HandleMessageAsync(inputJson);

        var response = JsonSerializer.Deserialize<AgentResponse>(result);
        response.Should().NotBeNull();
        response!.Text.Should().Be("Hi there!");
    }

    [Fact]
    public async Task HandleMessage_InvalidJson_ReturnsError()
    {
        var mockAgent = new Mock<IAgentService>();
        var handler = new WebSocketHandler(mockAgent.Object);

        var result = await handler.HandleMessageAsync("not valid json");

        result.Should().Contain("error");
    }

    [Fact]
    public async Task HandleMessage_MissingSessionId_ReturnsError()
    {
        var mockAgent = new Mock<IAgentService>();
        var handler = new WebSocketHandler(mockAgent.Object);

        var inputJson = """{"type":"transcription","text":"hello"}""";
        var result = await handler.HandleMessageAsync(inputJson);

        result.Should().Contain("session_id");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `cd voice-agent && dotnet test --filter "WebSocketHandlerTests"`
Expected: FAIL - WebSocketHandler not found

**Step 3: Write minimal implementation**

```csharp
// VoiceAgent/Handlers/WebSocketHandler.cs
using System.Text.Json;
using VoiceAgent.Models;
using VoiceAgent.Services;

namespace VoiceAgent.Handlers;

public class WebSocketHandler
{
    private readonly IAgentService _agentService;

    public WebSocketHandler(IAgentService agentService)
    {
        _agentService = agentService;
    }

    public async Task<string> HandleMessageAsync(string messageJson)
    {
        try
        {
            var message = JsonSerializer.Deserialize<TranscriptionMessage>(messageJson);

            if (message == null)
            {
                return JsonSerializer.Serialize(new { error = "Invalid message format" });
            }

            if (string.IsNullOrEmpty(message.SessionId))
            {
                return JsonSerializer.Serialize(new { error = "Missing session_id" });
            }

            if (string.IsNullOrEmpty(message.Text))
            {
                return JsonSerializer.Serialize(new { error = "Missing text" });
            }

            var response = await _agentService.ProcessMessageAsync(message.SessionId, message.Text);
            return JsonSerializer.Serialize(response);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { error = "Invalid JSON" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Internal error: {ex.Message}" });
        }
    }
}
```

**Step 4: Run test to verify it passes**

Run: `cd voice-agent && dotnet test --filter "WebSocketHandlerTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add voice-agent/
git commit -m "feat: add WebSocketHandler for message processing"
```

---

## Task 8: Implement MCP Client Service

**Files:**
- Create: `voice-agent/VoiceAgent/Services/McpClientService.cs`
- Test: `voice-agent/VoiceAgent.Tests/Services/McpClientServiceTests.cs`

**Step 1: Write the failing test**

```csharp
// VoiceAgent.Tests/Services/McpClientServiceTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceAgent.Services;

namespace VoiceAgent.Tests.Services;

public class McpClientServiceTests
{
    [Fact]
    public void McpClientService_CanBeConstructed()
    {
        var config = new McpClientConfig
        {
            Command = "dotnet",
            Arguments = "run --project ./TimeReportingMcpSdk"
        };

        var service = new McpClientService(config, NullLogger<McpClientService>.Instance);

        service.Should().NotBeNull();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `cd voice-agent && dotnet test --filter "McpClientServiceTests"`
Expected: FAIL - types not found

**Step 3: Write minimal implementation**

```csharp
// VoiceAgent/Services/McpClientConfig.cs
namespace VoiceAgent.Services;

public class McpClientConfig
{
    public string Command { get; set; } = "dotnet";
    public string Arguments { get; set; } = string.Empty;
    public Dictionary<string, string> Environment { get; set; } = new();
}
```

```csharp
// VoiceAgent/Services/McpClientService.cs
using Microsoft.Extensions.Logging;

namespace VoiceAgent.Services;

public class McpClientService : IMcpClientService
{
    private readonly McpClientConfig _config;
    private readonly ILogger<McpClientService> _logger;

    public McpClientService(McpClientConfig config, ILogger<McpClientService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<string> ExecuteQueryAsync(string query)
    {
        // TODO: Implement actual MCP call
        _logger.LogInformation("Executing query: {Query}", query);
        await Task.CompletedTask;
        return "Query result placeholder";
    }

    public async Task<string> ExecuteUpdateAsync(string action, string parameters)
    {
        // TODO: Implement actual MCP call
        _logger.LogInformation("Executing update: {Action} with {Parameters}", action, parameters);
        await Task.CompletedTask;
        return "Update executed successfully";
    }

    public async Task<string> GetActionSummaryAsync(string text)
    {
        // TODO: Use LLM to summarize the action
        _logger.LogInformation("Getting action summary for: {Text}", text);
        await Task.CompletedTask;
        return $"I'll process: {text}.";
    }
}
```

**Step 4: Run test to verify it passes**

Run: `cd voice-agent && dotnet test --filter "McpClientServiceTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add voice-agent/
git commit -m "feat: add McpClientService skeleton"
```

---

## Task 9: Implement Program.cs with WebSocket Server

**Files:**
- Modify: `voice-agent/VoiceAgent/Program.cs`

**Step 1: Write the WebSocket server setup**

```csharp
// VoiceAgent/Program.cs
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoiceAgent.Handlers;
using VoiceAgent.Services;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
var port = int.Parse(Environment.GetEnvironmentVariable("AGENT_PORT") ?? "8766");
var sessionTimeoutHours = int.Parse(Environment.GetEnvironmentVariable("SESSION_TIMEOUT_HOURS") ?? "4");
var confirmationTimeoutMinutes = int.Parse(Environment.GetEnvironmentVariable("CONFIRMATION_TIMEOUT_MINUTES") ?? "2");

// Register services
builder.Services.AddSingleton(new SessionManager(
    TimeSpan.FromHours(sessionTimeoutHours),
    TimeSpan.FromMinutes(confirmationTimeoutMinutes)));

builder.Services.AddSingleton<IntentClassifier>();

builder.Services.AddSingleton(new McpClientConfig
{
    Command = Environment.GetEnvironmentVariable("MCP_COMMAND") ?? "dotnet",
    Arguments = Environment.GetEnvironmentVariable("MCP_ARGS") ?? "run --project TimeReportingMcpSdk"
});

builder.Services.AddSingleton<IMcpClientService, McpClientService>();
builder.Services.AddSingleton<IAgentService, AgentService>();
builder.Services.AddSingleton<WebSocketHandler>();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var handler = app.Services.GetRequiredService<WebSocketHandler>();

// Start WebSocket server
var listener = new HttpListener();
listener.Prefixes.Add($"http://+:{port}/");
listener.Start();

logger.LogInformation("Voice Agent WebSocket server started on port {Port}", port);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var context = await listener.GetContextAsync();

        if (context.Request.IsWebSocketRequest)
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            _ = HandleWebSocketAsync(wsContext.WebSocket, handler, logger, cts.Token);
        }
        else
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
        }
    }
}
finally
{
    listener.Stop();
}

static async Task HandleWebSocketAsync(
    WebSocket webSocket,
    WebSocketHandler handler,
    ILogger logger,
    CancellationToken cancellationToken)
{
    var buffer = new byte[4096];

    try
    {
        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", cancellationToken);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                logger.LogDebug("Received: {Message}", message);

                var response = await handler.HandleMessageAsync(message);
                var responseBytes = Encoding.UTF8.GetBytes(response);

                await webSocket.SendAsync(responseBytes, WebSocketMessageType.Text, true, cancellationToken);
                logger.LogDebug("Sent: {Response}", response);
            }
        }
    }
    catch (WebSocketException ex)
    {
        logger.LogWarning("WebSocket error: {Error}", ex.Message);
    }
}
```

**Step 2: Verify build**

Run: `cd voice-agent && dotnet build`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add voice-agent/
git commit -m "feat: add WebSocket server in Program.cs"
```

---

## Task 10: Integration Test - End to End

**Files:**
- Create: `voice-agent/VoiceAgent.Tests/Integration/EndToEndTests.cs`

**Step 1: Write the integration test**

```csharp
// VoiceAgent.Tests/Integration/EndToEndTests.cs
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using VoiceAgent.Models;

namespace VoiceAgent.Tests.Integration;

public class EndToEndTests
{
    // Note: These tests require the server to be running
    // Run: dotnet run --project VoiceAgent in a separate terminal

    [Fact(Skip = "Requires running server")]
    public async Task WebSocket_SendTranscription_ReceivesResponse()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("ws://localhost:8766"), CancellationToken.None);

        var message = new TranscriptionMessage
        {
            Type = "transcription",
            Text = "show my entries",
            SessionId = "test-session"
        };

        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);

        var response = JsonSerializer.Deserialize<AgentResponse>(responseJson);
        response.Should().NotBeNull();
        response!.Type.Should().Be("response");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
    }
}
```

**Step 2: Run all unit tests**

Run: `cd voice-agent && dotnet test`
Expected: All tests pass

**Step 3: Commit**

```bash
git add voice-agent/
git commit -m "test: add integration test placeholder"
```

---

## Task 11: Update Whisper Client to Connect to Agent

**Files:**
- Modify: `whisper-streaming/client/main.py`
- Modify: `whisper-streaming/rpi-client.sh`

**Step 1: Add AGENT_URL environment variable support**

Update `whisper-streaming/rpi-client.sh`:

```bash
# Add after existing environment variables
export AGENT_URL="${AGENT_URL:-}"  # Optional: ws://localhost:8766
```

**Step 2: Add agent forwarding to streaming client**

This task creates a bridge: whisper client → agent → user display

The whisper client will:
1. Send transcriptions to agent WebSocket (if AGENT_URL is set)
2. Display agent responses instead of raw transcriptions
3. Fall back to normal display if agent unavailable

**Step 3: Commit**

```bash
git add whisper-streaming/
git commit -m "feat: add agent integration to whisper client"
```

---

## Summary

**Total Tasks:** 11
**Estimated Time:** 4-6 hours

**Key Components Built:**
1. Message models (TranscriptionMessage, AgentResponse)
2. Session management with timeout handling
3. Intent classifier for query/update detection
4. Agent service with confirmation flow
5. WebSocket handler
6. MCP client service (skeleton)
7. WebSocket server

**Next Steps After Plan:**
1. Implement actual MCP client using Microsoft Agent Framework
2. Integrate Azure OpenAI for natural language understanding
3. Add TTS output formatting
4. Deploy to RPi and test with whisper-streaming

**References:**
- [Microsoft Agent Framework Quick Start](https://learn.microsoft.com/en-us/agent-framework/tutorials/quick-start)
- [Using MCP Tools](https://learn.microsoft.com/en-us/agent-framework/user-guide/model-context-protocol/using-mcp-tools)
- [GitHub: microsoft/agent-framework](https://github.com/microsoft/agent-framework)
