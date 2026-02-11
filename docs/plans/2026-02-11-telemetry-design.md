# Telemetry & Observability Design

## Goal

Add end-to-end observability across all 4 services (client, STT, agent, TTS) so that every voice interaction can be traced through the full pipeline, with visibility into internal decisions, LLM I/O, and performance characteristics.

## Architecture

All services instrumented with **OpenTelemetry**, exporting to a single **Azure Application Insights** resource.

- Python services (client, STT): `azure-monitor-opentelemetry` + `opentelemetry-api`
- .NET services (agent, TTS): `Azure.Monitor.OpenTelemetry.Exporter` + `OpenTelemetry` NuGet packages
- No new infrastructure containers, sidecars, or databases
- Estimated cost: **free** (well under 5 GB/month ingestion, which is the free tier)

## Trace Context Propagation

Services communicate over WebSocket with JSON messages. Trace context is propagated via a flat `traceparent` field added to existing message payloads:

```json
{
  "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "type": "transcribe",
  "audio": "<base64>",
  "sample_rate": 16000
}
```

No breaking changes — services that don't understand `traceparent` ignore it. Rollout is incremental.

**Exception:** TTS responses are binary audio chunks. Trace context is only on the initial JSON request. Making TTS responses JSON-based is a potential future improvement.

## Session Concept

A **session** groups multiple voice interactions into one conversation. The existing `session_id` (UUID, generated in `AgentClient.__init__`) is reused and extended:

- **Current scope:** client → agent only
- **New scope:** client → all services (STT, agent, TTS)
- Added as a custom attribute on every span and log entry
- In App Insights: filter/group by `session_id` to see an entire conversation

**Current lifecycle:** session starts when the client process starts, ends when it stops.

**Future lifecycle:** wake word detection starts a session, user dismissal ("thank you") ends it, new wake word starts a fresh session — without restarting the client. This only requires changing when `session_id` gets regenerated.

## Trace Flow Per Voice Interaction

1. **Client** detects speech via VAD, generates root span `voice-interaction`, creates `traceparent`
2. Client sends audio to STT with `traceparent` + `session_id`
3. **STT** creates child span `stt-transcribe`, returns transcription with same `traceparent`
4. Client sends transcription to agent with same `traceparent` + `session_id`
5. **Agent** creates child span `agent-process`, calls LLM, runs MCP tools, returns response with `traceparent`
6. Client sends text to TTS with same `traceparent` + `session_id`
7. **TTS** creates child span `tts-synthesize`, streams back binary audio
8. Client closes root span when playback completes

Result in Transaction Search:

```
voice-interaction (client)          ███████████████████████ 3200ms
  └─ stt-transcribe (stt)          ██████ 650ms
  └─ agent-process (agent)         █████████████ 1800ms
     └─ llm-call (agent)           ██████████ 1400ms
     └─ mcp-tool (agent)           ██ 200ms
  └─ tts-synthesize (tts)          █████ 500ms
```

## Per-Service Telemetry

### Client (Python)

- **Span:** `voice-interaction` (end-to-end duration)
- **Events:** VAD start/end, connection state changes (`connected`/`disconnected`/`offline`), transcription received, agent response received, TTS playback start/done, mic mute/unmute, noise detections
- **Metrics:** round-trip latency, audio energy levels, WebSocket reconnection count
- **Logs:** all existing `print()` statements converted to `logging` module, auto-captured by OpenTelemetry and attached to active trace
- **Attributes:** VAD backend (webrtc), min energy threshold, silence/max speech config, agent character, cooldown duration

### STT Server (Python)

- **Span:** `stt-transcribe`
- **Attributes:** audio duration, model used, detected language, processing time
- **Events:** hallucination filter triggered, empty result
- **Metrics:** transcription latency, confidence scores

### Agent (.NET)

- **Span:** `agent-process`
- **Child span:** `llm-call` with attributes: token usage (input/output), model name, full prompt sent, full response received
- **Child span:** `mcp-tool-call` (one per tool invocation) with attributes: tool name, arguments, result, duration
- **Events:** confirmation flow triggered, session state changes
- **Attributes:** character personality active, session ID

### TTS (.NET)

- **Span:** `tts-synthesize`
- **Attributes:** text length, voice used, chunk count, audio duration
- **Events:** synthesis started, first chunk sent, completion
- **Metrics:** synthesis latency, first-chunk latency

## Dashboard / Visualization

Using out-of-the-box App Insights features only (no custom dashboards initially):

- **Application Map:** auto-generated topology showing all 4 services, connection latency, and failure rates
- **Transaction Search:** search/filter individual interactions, click to see full waterfall across all services

Custom dashboards (KQL-based) can be added later for frequently queried metrics like LLM token usage trends or end-to-end latency percentiles.

## Implementation Approach

### Azure Setup

- Create one Application Insights resource
- Add `APPLICATIONINSIGHTS_CONNECTION_STRING` to `.env` for all services

### Python Services (client, STT)

- Add `azure-monitor-opentelemetry` and `opentelemetry-api` to requirements
- One-time setup call at startup to configure the exporter
- Small shared utility for extracting/injecting `traceparent` from WebSocket JSON messages
- Replace `print()` calls with `logging` module (OpenTelemetry captures these automatically)
- Wrap key operations in `tracer.start_as_current_span()`

### .NET Services (agent, TTS)

- Add `Azure.Monitor.OpenTelemetry.Exporter` and `OpenTelemetry` NuGet packages
- Configure in `Program.cs` via the standard OpenTelemetry builder
- Extract `traceparent` from incoming WebSocket JSON messages
- Use `ActivitySource` / `Activity` (.NET native OpenTelemetry API) for spans
- LLM input/output logged as span attributes on `llm-call` activity
- MCP tool calls each get their own child activity

### Rollout Order

1. **Agent** (highest value — LLM visibility)
2. **STT** (transcription performance)
3. **Client** (end-to-end trace root, session management)
4. **TTS** (last hop, least complex)

Each service can be instrumented independently. Traces get richer as more services participate.

## Explicitly Out of Scope

- No custom dashboards — use Application Map + Transaction Search
- No sampling — 100% of traces at this volume
- No separate metrics pipeline (Prometheus-style) — traces + logs are sufficient
- No baggage/tracestate propagation — just `traceparent`
- No TTS response protocol change (binary → JSON) — future improvement
- No alerting rules — get data flowing first, add alerts once baselines are known
- No structured logging overhaul — swap `print()` to `logging` in Python; .NET already uses `ILogger`
