# Telemetry & Observability Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add OpenTelemetry-based distributed tracing across all 4 services (agent, STT, client, TTS), exporting to Azure Application Insights, with trace context propagation over WebSocket messages and session-level grouping.

**Architecture:** Each service gets OpenTelemetry SDK + Azure Monitor exporter. Trace context flows via a `traceparent` field in existing WebSocket JSON messages. A `session_id` attribute groups multiple voice interactions into one conversation. No new infrastructure — just SDK dependencies and one App Insights resource.

**Tech Stack:** OpenTelemetry (.NET `ActivitySource`/`Activity`, Python `opentelemetry-api`), `Azure.Monitor.OpenTelemetry.Exporter` (.NET), `azure-monitor-opentelemetry` (Python)

**Design doc:** `docs/plans/2026-02-11-telemetry-design.md`

---

## Task 1: Azure Setup & Environment Configuration

**Files:**
- Modify: `.env.sample`
- Modify: `podman-compose.yml`
- Modify: `podman-compose.rpi.yml`
- Modify: `podman-compose.rpi-cpu.yml`

**Step 1: Create Application Insights resource (manual)**

In Azure Portal:
1. Create resource → Application Insights
2. Name: `ai-assistant-telemetry`
3. Region: same as your Azure OpenAI (e.g., East US)
4. Resource Mode: Workspace-based (create new Log Analytics workspace or use existing)
5. Copy the **Connection String** from the Overview page

**Step 2: Add connection string to .env.sample**

Add to `.env.sample` after the Azure Speech section:

```bash
# =============================================================================
# Telemetry (Azure Application Insights)
# =============================================================================
APPLICATIONINSIGHTS_CONNECTION_STRING=
```

Add the actual value to your `.env` file (not committed).

**Step 3: Pass connection string to all services in compose files**

In `podman-compose.yml`, add `APPLICATIONINSIGHTS_CONNECTION_STRING` to each service's `env_file` already covers `.env`, so it will be picked up automatically. However, we need to explicitly pass it in the `environment` section for services that also set environment vars, to ensure it's forwarded.

Add to each service in `podman-compose.yml`:

```yaml
  stt-server:
    environment:
      - APPLICATIONINSIGHTS_CONNECTION_STRING=${APPLICATIONINSIGHTS_CONNECTION_STRING:-}
      # ... existing vars ...

  agent:
    environment:
      - APPLICATIONINSIGHTS_CONNECTION_STRING=${APPLICATIONINSIGHTS_CONNECTION_STRING:-}
      # ... existing vars ...

  tts:
    environment:
      - APPLICATIONINSIGHTS_CONNECTION_STRING=${APPLICATIONINSIGHTS_CONNECTION_STRING:-}
      # ... existing vars ...
```

In `podman-compose.rpi.yml` and `podman-compose.rpi-cpu.yml`, add to client service:

```yaml
  client:
    environment:
      - APPLICATIONINSIGHTS_CONNECTION_STRING=${APPLICATIONINSIGHTS_CONNECTION_STRING:-}
      # ... existing vars ...
```

**Step 4: Commit**

```bash
git add .env.sample podman-compose.yml podman-compose.rpi.yml podman-compose.rpi-cpu.yml
git commit -m "feat(telemetry): add Application Insights connection string to env and compose"
```

---

## Task 2: Agent — Add OpenTelemetry Packages

**Files:**
- Modify: `voice-agent/VoiceAgent/VoiceAgent.csproj`

**Step 1: Add NuGet packages**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/voice-agent/VoiceAgent
dotnet add package OpenTelemetry --version 1.12.0
dotnet add package OpenTelemetry.Extensions.Hosting --version 1.12.0
dotnet add package Azure.Monitor.OpenTelemetry.Exporter --version 1.4.0
```

Note: Check latest stable versions. The key packages are:
- `OpenTelemetry` — core API
- `OpenTelemetry.Extensions.Hosting` — integrates with `IHostBuilder`
- `Azure.Monitor.OpenTelemetry.Exporter` — sends data to App Insights

**Step 2: Verify build**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/voice-agent
dotnet build VoiceAgent/VoiceAgent.csproj
```

Expected: BUILD SUCCEEDED

**Step 3: Verify tests still pass**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/voice-agent
dotnet test
```

Expected: All 29 tests pass

**Step 4: Commit**

```bash
git add voice-agent/VoiceAgent/VoiceAgent.csproj
git commit -m "feat(telemetry): add OpenTelemetry NuGet packages to agent"
```

---

## Task 3: Agent — Configure OpenTelemetry in Program.cs

**Files:**
- Modify: `voice-agent/VoiceAgent/Program.cs`

**Step 1: Add OpenTelemetry configuration**

Add these usings at the top of `Program.cs`:

```csharp
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Azure.Monitor.OpenTelemetry.Exporter;
```

After the existing DI registrations (around line 48), add OpenTelemetry setup:

```csharp
// OpenTelemetry
var connectionString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("voice-agent"))
        .WithTracing(t =>
        {
            t.AddSource("VoiceAgent");
            t.AddAzureMonitorTraceExporter(o => o.ConnectionString = connectionString);
        });
}
```

**Step 2: Verify build**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/voice-agent
dotnet build VoiceAgent/VoiceAgent.csproj
```

**Step 3: Verify tests still pass**

```bash
dotnet test
```

**Step 4: Commit**

```bash
git add voice-agent/VoiceAgent/Program.cs
git commit -m "feat(telemetry): configure OpenTelemetry with Azure Monitor in agent"
```

---

## Task 4: Agent — Add Traceparent to Message Models

**Files:**
- Modify: `voice-agent/VoiceAgent/Models/TranscriptionMessage.cs`
- Modify: `voice-agent/VoiceAgent/Models/AgentResponse.cs`
- Modify: `voice-agent/VoiceAgent.Tests/Models/MessageSerializationTests.cs`

**Step 1: Write tests for traceparent field**

Add to `MessageSerializationTests.cs`:

```csharp
[Fact]
public void TranscriptionMessage_DeserializesTraceparent()
{
    var json = """
        {
            "type": "transcription",
            "text": "Log 8 hours on INTERNAL",
            "session_id": "abc-123",
            "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
        }
        """;

    var message = JsonSerializer.Deserialize<TranscriptionMessage>(json);

    message.Should().NotBeNull();
    message!.Traceparent.Should().Be("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
}

[Fact]
public void TranscriptionMessage_TraceparentIsOptional()
{
    var json = """
        {
            "type": "transcription",
            "text": "hello",
            "session_id": "abc-123"
        }
        """;

    var message = JsonSerializer.Deserialize<TranscriptionMessage>(json);

    message.Should().NotBeNull();
    message!.Traceparent.Should().BeNull();
}

[Fact]
public void AgentResponse_IncludesTraceparent()
{
    var response = new AgentResponse
    {
        Text = "Done!",
        Traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
    };

    var json = JsonSerializer.Serialize(response);

    json.Should().Contain("\"traceparent\"");
}
```

**Step 2: Run tests to verify they fail**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/voice-agent
dotnet test --filter "Traceparent"
```

Expected: FAIL (property doesn't exist yet)

**Step 3: Add traceparent to TranscriptionMessage**

Add to `TranscriptionMessage.cs`:

```csharp
[JsonPropertyName("traceparent")]
public string? Traceparent { get; set; }
```

**Step 4: Add traceparent to AgentResponse**

Add to `AgentResponse.cs`:

```csharp
[JsonPropertyName("traceparent")]
public string? Traceparent { get; set; }
```

**Step 5: Run tests to verify they pass**

```bash
dotnet test
```

Expected: All tests pass

**Step 6: Commit**

```bash
git add voice-agent/VoiceAgent/Models/TranscriptionMessage.cs voice-agent/VoiceAgent/Models/AgentResponse.cs voice-agent/VoiceAgent.Tests/Models/MessageSerializationTests.cs
git commit -m "feat(telemetry): add traceparent field to agent message models"
```

---

## Task 5: Agent — Instrument WebSocketHandler with Trace Context

**Files:**
- Modify: `voice-agent/VoiceAgent/Handlers/WebSocketHandler.cs`
- Modify: `voice-agent/VoiceAgent/Services/IAgentService.cs`
- Modify: `voice-agent/VoiceAgent/Services/AgentService.cs`

**Step 1: Create ActivitySource and instrument WebSocketHandler**

The WebSocketHandler needs to:
1. Extract `traceparent` from incoming messages
2. Start an Activity (span) linked to that parent
3. Pass the traceparent through to the agent service
4. Include traceparent in the response

Replace `WebSocketHandler.cs` with:

```csharp
using System.Diagnostics;
using System.Text.Json;
using VoiceAgent.Models;
using VoiceAgent.Services;

namespace VoiceAgent.Handlers;

public class WebSocketHandler
{
    private static readonly ActivitySource ActivitySource = new("VoiceAgent");
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
                return JsonSerializer.Serialize(new { error = "Invalid message format" });
            if (string.IsNullOrEmpty(message.SessionId))
                return JsonSerializer.Serialize(new { error = "Missing session_id" });
            if (string.IsNullOrEmpty(message.Text))
                return JsonSerializer.Serialize(new { error = "Missing text" });

            // Extract parent trace context if present
            ActivityContext parentContext = default;
            if (!string.IsNullOrEmpty(message.Traceparent))
            {
                ActivityContext.TryParse(message.Traceparent, null, out parentContext);
            }

            using var activity = ActivitySource.StartActivity(
                "agent-process",
                ActivityKind.Server,
                parentContext);

            activity?.SetTag("session.id", message.SessionId);
            activity?.SetTag("message.text", message.Text);
            if (message.Character != null)
                activity?.SetTag("agent.character", message.Character);

            var response = await _agentService.ProcessMessageAsync(
                message.SessionId, message.Text, message.Character);

            // Include traceparent in response so client can continue the trace
            if (activity != null)
            {
                response.Traceparent = $"00-{activity.TraceId}-{activity.SpanId}-01";
            }

            return JsonSerializer.Serialize(response);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { error = "Invalid JSON" });
        }
        catch (Exception ex)
        {
            Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return JsonSerializer.Serialize(new { error = $"Internal error: {ex.Message}" });
        }
    }
}
```

**Step 2: Verify build and tests pass**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/voice-agent
dotnet build VoiceAgent/VoiceAgent.csproj && dotnet test
```

Expected: All tests pass (existing tests don't send traceparent, so the field is just null — no behavior change)

**Step 3: Commit**

```bash
git add voice-agent/VoiceAgent/Handlers/WebSocketHandler.cs
git commit -m "feat(telemetry): instrument agent WebSocketHandler with trace context"
```

---

## Task 6: Agent — Instrument AgentService (LLM Calls & MCP Tools)

**Files:**
- Modify: `voice-agent/VoiceAgent/Services/AgentService.cs`
- Modify: `voice-agent/VoiceAgent/Services/McpClientService.cs`

This is the highest-value instrumentation — visibility into LLM decisions and tool calls.

**Step 1: Instrument AgentService.RunAgentLoopAsync**

Add `ActivitySource` to `AgentService.cs` and wrap key operations:

Add at class level (after the existing fields):

```csharp
private static readonly ActivitySource ActivitySource = new("VoiceAgent");
```

In `RunAgentLoopAsync`, wrap the LLM call (around line 150 in original) with a span:

```csharp
// Inside the while loop, around the LLM call:
ChatCompletion choice;
using (var llmActivity = ActivitySource.StartActivity("llm-call"))
{
    llmActivity?.SetTag("llm.model", _chatClient.GetType().Name);
    llmActivity?.SetTag("llm.message_count", messages.Count);
    llmActivity?.SetTag("llm.tool_count", tools.Count);

    var response = await _chatClient.CompleteChatAsync(messages, options);
    choice = response.Value;

    llmActivity?.SetTag("llm.input_tokens", choice.Usage?.InputTokenCount);
    llmActivity?.SetTag("llm.output_tokens", choice.Usage?.OutputTokenCount);
    llmActivity?.SetTag("llm.finish_reason", choice.FinishReason.ToString());

    if (choice.Content.Count > 0)
        llmActivity?.SetTag("llm.response_text", choice.Content[0].Text);
}
```

Wrap MCP tool execution with a span:

```csharp
// Around the non-destructive tool execution:
string result;
using (var toolActivity = ActivitySource.StartActivity("mcp-tool-call"))
{
    toolActivity?.SetTag("tool.name", toolName);
    toolActivity?.SetTag("tool.arguments", argsJson);

    result = await _mcpClient.ExecuteToolAsync(toolName, arguments);

    toolActivity?.SetTag("tool.result", result.Length > 1000 ? result[..1000] : result);
}
```

Also add span tags for the confirmation flow:

```csharp
// When a destructive tool requires confirmation:
Activity.Current?.AddEvent(new ActivityEvent("confirmation-requested",
    tags: new ActivityTagsCollection
    {
        { "tool.name", toolName },
        { "confirmation.prompt", confirmationPrompt }
    }));
```

**Step 2: Instrument McpClientService.ExecuteToolAsync**

In `McpClientService.cs`, the existing logging is already good. The spans from AgentService will cover tool calls. No changes needed here unless you want per-tool spans at the MCP level too (skip for now — the agent-level spans capture tool name, args, and result).

**Step 3: Verify build and tests pass**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/voice-agent
dotnet build VoiceAgent/VoiceAgent.csproj && dotnet test
```

**Step 4: Commit**

```bash
git add voice-agent/VoiceAgent/Services/AgentService.cs
git commit -m "feat(telemetry): instrument agent LLM calls and MCP tool executions"
```

---

## Task 7: STT Server — Add OpenTelemetry Packages

**Files:**
- Modify: `whisper-streaming/pyproject.toml`

**Step 1: Add OpenTelemetry dependencies**

Add a new optional dependency group in `pyproject.toml`:

```toml
[project.optional-dependencies]
# ... existing groups ...
telemetry = [
    "opentelemetry-api>=1.29.0",
    "opentelemetry-sdk>=1.29.0",
    "azure-monitor-opentelemetry>=1.6.4",
]
```

**Step 2: Install in dev environment**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/whisper-streaming
pip install -e ".[telemetry]"
```

**Step 3: Verify existing tests still pass**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/whisper-streaming
python -m pytest tests/test_backend_factory.py tests/test_backends_base.py -v
```

**Step 4: Update STT Dockerfiles**

In `docker/stt-server.Dockerfile`, add to the pip install line:

```dockerfile
RUN pip install --no-cache-dir \
    websockets>=12.0 \
    numpy>=1.24.0 \
    faster-whisper>=1.0.0 \
    opentelemetry-api>=1.29.0 \
    opentelemetry-sdk>=1.29.0 \
    azure-monitor-opentelemetry>=1.6.4
```

In `docker/stt-server-hailo.Dockerfile`, add to the pip install line:

```dockerfile
RUN pip install --no-cache-dir \
    websockets>=12.0 \
    numpy>=1.24.0 \
    opentelemetry-api>=1.29.0 \
    opentelemetry-sdk>=1.29.0 \
    azure-monitor-opentelemetry>=1.6.4
```

**Step 5: Commit**

```bash
git add whisper-streaming/pyproject.toml docker/stt-server.Dockerfile docker/stt-server-hailo.Dockerfile
git commit -m "feat(telemetry): add OpenTelemetry dependencies for STT server"
```

---

## Task 8: STT Server — Instrument with Tracing

**Files:**
- Modify: `whisper-streaming/server/main.py`

**Step 1: Add telemetry setup to server startup**

At the top of `main.py`, after existing imports, add:

```python
import os
from opentelemetry import trace

# Conditional telemetry setup
_connection_string = os.environ.get("APPLICATIONINSIGHTS_CONNECTION_STRING")
if _connection_string:
    from azure.monitor.opentelemetry import configure_azure_monitor
    configure_azure_monitor(connection_string=_connection_string)

tracer = trace.get_tracer("stt-server")
```

**Step 2: Extract traceparent and create spans in the handler**

In the `handler` function (around line 129), modify the message processing:

```python
async def handler(websocket):
    client_addr = websocket.remote_address
    logger.info(f"Client connected: {client_addr}")
    session = TranscriptionSession(backend, sample_rate=16000)
    loop = asyncio.get_event_loop()

    async for raw_message in websocket:
        try:
            message = json.loads(raw_message)
        except json.JSONDecodeError as e:
            logger.error(f"Invalid JSON: {e}")
            continue

        msg_type = message.get("type")
        traceparent_str = message.get("traceparent")
        session_id = message.get("session_id")

        # Extract parent context from traceparent
        parent_context = None
        if traceparent_str:
            from opentelemetry.trace.propagation import TraceContextTextMapPropagator
            propagator = TraceContextTextMapPropagator()
            parent_context = propagator.extract({"traceparent": traceparent_str})

        if msg_type == "transcribe":
            with tracer.start_as_current_span(
                "stt-transcribe",
                context=parent_context,
                kind=trace.SpanKind.SERVER,
            ) as span:
                if session_id:
                    span.set_attribute("session.id", session_id)

                audio_b64 = message.get("audio", "")
                sample_rate = message.get("sample_rate", 16000)
                audio_bytes = base64.b64decode(audio_b64)
                audio = np.frombuffer(audio_bytes, dtype=np.float32)

                duration_ms = len(audio) / sample_rate * 1000
                span.set_attribute("audio.duration_ms", duration_ms)
                span.set_attribute("audio.sample_rate", sample_rate)

                logger.info(f"Transcribing {duration_ms:.0f}ms audio")
                result = await loop.run_in_executor(None, session.transcribe, audio)

                if result["type"] == "noise":
                    span.set_attribute("result.type", "noise")
                    span.add_event("hallucination-filtered")
                    logger.info("Detected noise/hallucination")
                else:
                    span.set_attribute("result.type", "result")
                    span.set_attribute("result.text", result["text"][:200])
                    span.set_attribute("result.language", result.get("language", ""))
                    span.set_attribute("result.processing_time_ms", result.get("processing_time_ms", 0))
                    logger.info(f"Result ({result['processing_time_ms']:.0f}ms): {result['text'][:80]}...")

                # Include traceparent in response for trace continuity
                current_span = trace.get_current_span()
                ctx = current_span.get_span_context()
                if ctx.is_valid:
                    result["traceparent"] = f"00-{format(ctx.trace_id, '032x')}-{format(ctx.span_id, '016x')}-01"

                try:
                    await websocket.send(json.dumps(result))
                except ConnectionClosed:
                    break
        else:
            logger.warning(f"Unknown message type: {msg_type}")

    logger.info(f"Client disconnected: {client_addr}")
```

**Step 3: Verify the server starts (manual test)**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/whisper-streaming
# Without App Insights (should still work)
WHISPER_BACKEND=faster python -c "from server.main import create_app; print('import OK')"
```

**Step 4: Commit**

```bash
git add whisper-streaming/server/main.py
git commit -m "feat(telemetry): instrument STT server with OpenTelemetry tracing"
```

---

## Task 9: Client — Add OpenTelemetry Packages

**Files:**
- Modify: `whisper-streaming/pyproject.toml`
- Modify: `docker/client.Dockerfile`

**Step 1: Add to pyproject.toml client dependencies**

The telemetry group from Task 7 already covers client too (same pyproject.toml). If the client uses a separate install, add to client optional deps:

```toml
[project.optional-dependencies]
client = [
    "sounddevice>=0.4.6",
    "webrtcvad>=2.0.10",
]
# telemetry group already added in Task 7
```

**Step 2: Update client Dockerfile**

In `docker/client.Dockerfile`, add to pip install:

```dockerfile
RUN pip install --no-cache-dir \
    websockets>=12.0 \
    numpy>=1.24.0 \
    sounddevice>=0.4.6 \
    webrtcvad>=2.0.10 \
    opentelemetry-api>=1.29.0 \
    opentelemetry-sdk>=1.29.0 \
    azure-monitor-opentelemetry>=1.6.4
```

**Step 3: Commit**

```bash
git add whisper-streaming/pyproject.toml docker/client.Dockerfile
git commit -m "feat(telemetry): add OpenTelemetry dependencies for client"
```

---

## Task 10: Client — Replace print() with logging

**Files:**
- Modify: `whisper-streaming/client/main.py`
- Modify: `whisper-streaming/client/tts.py`
- Modify: `whisper-streaming/client/audio.py`

This is a prerequisite for telemetry — OpenTelemetry auto-captures `logging` module output but not `print()`.

**Step 1: Add logging setup to client/main.py**

At the top of `main.py`, after existing imports, add:

```python
import logging

logging.basicConfig(
    level=os.environ.get("LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S"
)
logger = logging.getLogger("client")
```

**Step 2: Replace all print() calls with logger calls**

In `main.py`, replace each `print()` with the appropriate log level:

- Connection/lifecycle messages → `logger.info()`
- Error messages → `logger.error()`
- Noise/debug → `logger.debug()`
- Transcription results, agent responses → `logger.info()`

Examples:
```python
# Before:
print(f"[agent] Connected to {self.agent_url}")
# After:
logger.info("[agent] Connected to %s", self.agent_url)

# Before:
print(f"[agent] Not available: {e}")
# After:
logger.warning("[agent] Not available: %s", e)
```

Apply to all ~40 print statements in main.py. Keep the same message format (brackets, timestamps) so logs look the same.

**Step 3: Replace print() in tts.py**

Add at the top:

```python
import logging
logger = logging.getLogger("client.tts")
```

Replace all `print()` calls. Remove the `_ts()` helper — the logging formatter adds timestamps.

**Step 4: Replace print() in audio.py**

Add at the top:

```python
import logging
logger = logging.getLogger("client.audio")
```

Replace the single print statement.

**Step 5: Verify the client still works (import test)**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/whisper-streaming
python -c "from client.main import main; print('import OK')"
```

**Step 6: Commit**

```bash
git add whisper-streaming/client/main.py whisper-streaming/client/tts.py whisper-streaming/client/audio.py
git commit -m "refactor(client): replace print() with logging module for telemetry capture"
```

---

## Task 11: Client — Instrument with Tracing and Session Propagation

**Files:**
- Modify: `whisper-streaming/client/main.py`
- Modify: `whisper-streaming/client/tts.py`

This is the most complex task — the client is the root of every trace.

**Step 1: Add telemetry setup to client main()**

In the `main()` function (around line 672), add before creating clients:

```python
# Telemetry setup
connection_string = os.environ.get("APPLICATIONINSIGHTS_CONNECTION_STRING")
if connection_string:
    from azure.monitor.opentelemetry import configure_azure_monitor
    configure_azure_monitor(connection_string=connection_string)

from opentelemetry import trace
tracer = trace.get_tracer("client")
```

**Step 2: Generate traceparent helper**

Add a utility function at module level:

```python
def _make_traceparent(span):
    """Build W3C traceparent string from current span."""
    ctx = span.get_span_context()
    if ctx and ctx.is_valid:
        return f"00-{format(ctx.trace_id, '032x')}-{format(ctx.span_id, '016x')}-01"
    return None
```

**Step 3: Instrument BatchClient — voice-interaction root span**

In `BatchClient.run()`, wrap the speech-detected-to-response-complete flow in a span. Around the section where speech is finalized and sent (approximately lines 330-380):

```python
# When speech is finalized and ready to send:
with tracer.start_as_current_span("voice-interaction") as interaction_span:
    interaction_span.set_attribute("session.id", self.agent_client.session_id if self.agent_client else "none")
    interaction_span.set_attribute("audio.duration_ms", duration_ms)
    traceparent = _make_traceparent(interaction_span)

    # Send to STT with traceparent
    # Modify _build_transcribe_message to accept traceparent
    message = self._build_transcribe_message(audio, traceparent=traceparent)
    # ... send and receive ...

    # Send to agent with traceparent
    # (traceparent already included in agent message)

    # TTS playback (traceparent on TTS request)
```

**Step 4: Pass session_id and traceparent to STT messages**

Modify `_build_transcribe_message` in BatchClient:

```python
def _build_transcribe_message(self, audio: np.ndarray, traceparent: str | None = None) -> str:
    audio_b64 = base64.b64encode(audio.astype(np.float32).tobytes()).decode()
    msg = {
        "type": "transcribe",
        "audio": audio_b64,
        "sample_rate": self.sample_rate,
    }
    if self.agent_client:
        msg["session_id"] = self.agent_client.session_id
    if traceparent:
        msg["traceparent"] = traceparent
    return json.dumps(msg)
```

**Step 5: Pass traceparent to agent messages**

In `AgentClient.send_transcription()`, add traceparent:

```python
async def send_transcription(self, text: str, traceparent: str | None = None) -> dict | None:
    # ... existing code ...
    msg_data = {
        "type": "transcription",
        "text": text,
        "session_id": self.session_id
    }
    if self.character:
        msg_data["character"] = self.character
    if traceparent:
        msg_data["traceparent"] = traceparent
    # ... rest unchanged ...
```

**Step 6: Pass session_id and traceparent to TTS**

In `TtsClient.speak()`, add parameters:

```python
async def speak(self, text: str, ssml: str | None = None,
                traceparent: str | None = None,
                session_id: str | None = None) -> float:
    # ... existing code ...
    request_data = {
        "text": text,
        "voice": self.voice,
        "output_format": "raw-24khz-16bit-mono-pcm"
    }
    if ssml:
        request_data["ssml"] = ssml
    if traceparent:
        request_data["traceparent"] = traceparent
    if session_id:
        request_data["session_id"] = session_id
    # ... rest unchanged ...
```

**Step 7: Apply same changes to StreamingClient**

Mirror the BatchClient changes in StreamingClient (same patterns, same modifications to `_build_transcribe_message`).

**Step 8: Verify import**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/whisper-streaming
python -c "from client.main import main; print('import OK')"
```

**Step 9: Commit**

```bash
git add whisper-streaming/client/main.py whisper-streaming/client/tts.py
git commit -m "feat(telemetry): instrument client with root spans and trace propagation"
```

---

## Task 12: TTS — Add OpenTelemetry Packages

**Files:**
- Modify: `SpeechService/SpeechService/SpeechService.csproj`

**Step 1: Add NuGet packages**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/SpeechService/SpeechService
dotnet add package OpenTelemetry --version 1.12.0
dotnet add package OpenTelemetry.Extensions.Hosting --version 1.12.0
dotnet add package Azure.Monitor.OpenTelemetry.Exporter --version 1.4.0
```

**Step 2: Verify build**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/SpeechService
dotnet build SpeechService/SpeechService.csproj
```

**Step 3: Commit**

```bash
git add SpeechService/SpeechService/SpeechService.csproj
git commit -m "feat(telemetry): add OpenTelemetry NuGet packages to TTS"
```

---

## Task 13: TTS — Configure and Instrument

**Files:**
- Modify: `SpeechService/SpeechService/Program.cs`
- Modify: `SpeechService/SpeechService/Models/TtsRequest.cs`
- Modify: `SpeechService/SpeechService/Handlers/WebSocketHandler.cs`

**Step 1: Add traceparent and session_id to TtsRequest**

In `TtsRequest.cs`, add:

```csharp
[JsonPropertyName("traceparent")]
public string? Traceparent { get; set; }

[JsonPropertyName("session_id")]
public string? SessionId { get; set; }
```

**Step 2: Configure OpenTelemetry in Program.cs**

Add usings and configuration similar to the agent (Task 3):

```csharp
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Azure.Monitor.OpenTelemetry.Exporter;
```

After DI registrations:

```csharp
var connectionString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("speech-service"))
        .WithTracing(t =>
        {
            t.AddSource("SpeechService");
            t.AddAzureMonitorTraceExporter(o => o.ConnectionString = connectionString);
        });
}
```

**Step 3: Instrument WebSocketHandler**

In `WebSocketHandler.cs`, add tracing to `HandleTextMessageAsync`:

```csharp
private static readonly ActivitySource ActivitySource = new("SpeechService");
```

In `HandleTextMessageAsync`, after deserializing the request:

```csharp
// Extract parent context
ActivityContext parentContext = default;
if (!string.IsNullOrEmpty(request.Traceparent))
{
    ActivityContext.TryParse(request.Traceparent, null, out parentContext);
}

using var activity = ActivitySource.StartActivity(
    "tts-synthesize",
    ActivityKind.Server,
    parentContext);

activity?.SetTag("session.id", request.SessionId);
activity?.SetTag("tts.text_length", request.Text.Length);
activity?.SetTag("tts.voice", request.Voice);

// ... existing synthesis code ...

activity?.SetTag("tts.chunk_count", chunkCount);
```

**Step 4: Verify build**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/SpeechService
dotnet build SpeechService/SpeechService.csproj
```

**Step 5: Commit**

```bash
git add SpeechService/SpeechService/Program.cs SpeechService/SpeechService/Models/TtsRequest.cs SpeechService/SpeechService/Handlers/WebSocketHandler.cs
git commit -m "feat(telemetry): instrument TTS with OpenTelemetry tracing"
```

---

## Task 14: Integration Verification

**Step 1: Build all containers**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant
podman compose build
```

**Step 2: Start services with App Insights connection string in .env**

```bash
./scripts/run-mac.sh up
```

**Step 3: Run the client and make a voice interaction**

Speak a test command like "show my time entries."

**Step 4: Check Azure Portal**

1. Open Application Insights → Application Map — verify all 4 services appear
2. Open Transaction Search → filter by last 5 minutes → verify distributed trace shows waterfall
3. Click a trace → verify you see: `voice-interaction` → `stt-transcribe` → `agent-process` (with `llm-call` child) → `tts-synthesize`
4. Verify `session_id` attribute is present on all spans

**Step 5: Run all unit tests**

```bash
cd /Users/oleksandrreva/Documents/git/ai-assistant/voice-agent && dotnet test
cd /Users/oleksandrreva/Documents/git/ai-assistant/whisper-streaming && python -m pytest tests/ -v
```

**Step 6: Final commit if any fixups needed**

```bash
git add -A && git commit -m "fix(telemetry): integration fixups"
```

---

## Summary

| Task | Service | What |
|------|---------|------|
| 1 | All | Azure setup + env configuration |
| 2 | Agent | Add NuGet packages |
| 3 | Agent | Configure OpenTelemetry in Program.cs |
| 4 | Agent | Add traceparent to message models (TDD) |
| 5 | Agent | Instrument WebSocketHandler with trace context |
| 6 | Agent | Instrument LLM calls and MCP tools |
| 7 | STT | Add Python OpenTelemetry packages |
| 8 | STT | Instrument with tracing |
| 9 | Client | Add Python OpenTelemetry packages |
| 10 | Client | Replace print() with logging |
| 11 | Client | Instrument with root spans and propagation |
| 12 | TTS | Add NuGet packages |
| 13 | TTS | Configure and instrument |
| 14 | All | Integration verification |
