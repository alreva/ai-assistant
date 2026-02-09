# Voice Agent for Time Reporting - Design

## Goal

Voice-controlled time reporting assistant that receives transcriptions from whisper-streaming, uses Azure OpenAI to understand intent, and executes time-reporting actions via MCP tools.

## Architecture

```
┌──────────────┐    WebSocket     ┌──────────────────┐    HTTPS    ┌─────────────┐
│   Whisper    │ ───────────────→ │   .NET Agent     │ ──────────→ │ Azure OpenAI│
│   Client     │ ←─────────────── │   Service        │ ←────────── │   (GPT-4o)  │
│   (Python)   │   text response  │   (on RPi)       │   reasoning │             │
└──────────────┘                  └────────┬─────────┘             └─────────────┘
                                           │
                                           │ MCP (stdio)
                                           ↓
                                  ┌──────────────────┐    GraphQL   ┌─────────────┐
                                  │  Time Reporting  │ ───────────→ │  PostgreSQL │
                                  │  MCP Server      │              │  Database   │
                                  └──────────────────┘              └─────────────┘
```

## Tech Stack

- Runtime: .NET 10 on Raspberry Pi
- Framework: Microsoft Agent Framework
- LLM: Azure OpenAI (GPT-4o)
- Communication: WebSocket (whisper client to agent)
- Backend: Existing Time Reporting MCP server and GraphQL API

## Agent Flow

### Query Commands (execute immediately)

Examples: "Show my time entries for this week", "What projects are available?", "How many hours did I log yesterday?"

Flow: User speaks, agent understands, agent executes MCP tool, agent responds with results.

### Update Commands (require confirmation)

Examples: "Log 8 hours on INTERNAL", "Move yesterday's entry to CLIENT-A", "Delete the entry from Monday", "Submit all entries for approval"

Flow:
1. User speaks command
2. Agent calls Azure OpenAI to understand intent
3. Agent detects UPDATE command
4. Agent responds with summary and asks for confirmation
5. User says "yes" or "no"
6. Agent executes or cancels

Example dialogue:
- User: "Log 8 hours on INTERNAL for today"
- Agent: "I'll log 8 hours on INTERNAL project for February 9th. Say yes to confirm or no to cancel."
- User: "Yes"
- Agent: "Done. Logged 8 hours on INTERNAL for February 9th."

## WebSocket Protocol

### Client to Agent (transcription)

```json
{
  "type": "transcription",
  "text": "Log 8 hours on internal project",
  "session_id": "abc-123"
}
```

### Agent to Client (response)

```json
{
  "type": "response",
  "text": "I'll log 8 hours on INTERNAL project for February 9th. Say yes to confirm or no to cancel.",
  "awaiting_confirmation": true
}
```

## Session Management

### Session Lifecycle

- Created: First message with new session_id
- Resumed: Reconnect with existing session_id (survives WebSocket disconnects)
- Ended by user: "goodbye", "done", "that's all"
- Ended by timeout: No activity for 4 hours

### Inactivity Tracking

Lazy cleanup approach:
- Agent stores last_activity_time per session
- Every incoming message updates this timestamp
- On new message: check if now minus last_activity_time exceeds 4 hours
- If expired, clear session state and start fresh
- If valid, resume session

### Confirmation Timeout

Lazy check approach (2-minute timeout):
- Agent stores confirmation_requested_at timestamp when asking for confirmation
- On next message, check if pending confirmation is older than 2 minutes
- If expired, cancel the pending action and inform user
- Response: "Your previous request was cancelled. [process new command]"

## Output Format

TTS-friendly text for future Azure Speech integration:
- Natural, spoken language
- No tables, bullets, or markdown formatting
- Concise and clear responses

Examples:
- "You have 3 time entries this week. Monday: 8 hours on INTERNAL. Tuesday: 6 hours on CLIENT-A. Wednesday: 4 hours on CLIENT-B."
- "I'll log 8 hours on INTERNAL project for today. Please confirm."

## Error Handling

Graceful failures with TTS-friendly messages:

| Error | Response |
|-------|----------|
| Azure OpenAI unavailable | "Sorry, I can't process that right now. The AI service is not responding." |
| Time reporting API down | "Sorry, the time reporting system is not available. Please try again later." |
| MCP tool fails | "Something went wrong while logging your time. Please try again." |
| Invalid project/task | "I couldn't find a project called X. Say show projects to see available options." |
| Ambiguous command | "I'm not sure what you mean. Could you rephrase that?" |

All errors logged to file for debugging, but user only hears friendly messages.

## Project Structure

```
ai-assistant/
├── whisper-streaming/          # Existing Python client + server
└── voice-agent/                # New .NET agent service
    ├── VoiceAgent/
    │   ├── Program.cs
    │   ├── Services/
    │   │   ├── AgentService.cs
    │   │   ├── SessionManager.cs
    │   │   └── McpClientService.cs
    │   ├── Handlers/
    │   │   └── TranscriptionHandler.cs
    │   └── Models/
    │       ├── TranscriptionMessage.cs
    │       └── AgentResponse.cs
    ├── VoiceAgent.Tests/
    └── voice-agent.sln
```

## Configuration

Environment variables:

```bash
# Azure OpenAI
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com
AZURE_OPENAI_DEPLOYMENT=gpt-4o
AZURE_OPENAI_API_KEY=your-key

# Agent settings
AGENT_PORT=8766
SESSION_TIMEOUT_HOURS=4
CONFIRMATION_TIMEOUT_MINUTES=2

# Time Reporting MCP
MCP_COMMAND=dotnet
MCP_ARGS=run --project /path/to/TimeReportingMcpSdk
GRAPHQL_API_URL=http://your-api:5001/graphql
```

## System Prompt

```
You are a voice-controlled time reporting assistant.
Users speak commands to log, query, and manage time entries.

For QUERY commands (show, list, how many), execute immediately.

For UPDATE commands (log, move, delete, submit, update),
summarize what you understood and ask for confirmation.
Wait for "yes" before executing.

Respond in natural, spoken language. No tables, bullets,
or formatting. Keep responses concise and clear.
```

## Future Enhancements

- Text-to-Speech output via Azure Speech
- Persistent memory across sessions (user preferences)
- Proactive confirmation timeout messages
