# True Streaming Transcription Design

## Overview

Refactor from batch mode (buffer → send → clear) to true streaming mode where audio frames are sent continuously and partial results are returned in real-time.

## Protocol

### Client → Server

**Audio frame (sent continuously, every ~100ms):**
```json
{
  "type": "audio_frame",
  "audio": "<base64 PCM 16-bit 16kHz mono>",
  "sample_rate": 16000
}
```

**VAD end signal (speech ended, finalize segment):**
```json
{
  "type": "vad_end"
}
```

### Server → Client

**Partial result (sent every 500ms while audio accumulates):**
```json
{
  "type": "partial",
  "text": "Hello wor...",
  "processing_time_ms": 120
}
```

**Final result (sent after vad_end):**
```json
{
  "type": "final",
  "text": "Hello world",
  "segments": [...],
  "language": "en",
  "processing_time_ms": 450
}
```

## Server-Side State

**Per-connection state:**
- `audio_buffer: list[np.ndarray]` — accumulated audio frames
- `previous_transcript: str` — for prompt/hybrid strategies
- `context_audio: np.ndarray | None` — for context/hybrid strategies
- `last_transcribe_time: float` — to track when to send partials

**Transcription loop:**
1. Receive `audio_frame` → append to buffer
2. Every 500ms (configurable via `PARTIAL_INTERVAL_MS`):
   - If buffer has audio → transcribe → send partial
3. On `vad_end`:
   - Transcribe full buffer → send final
   - Update `context_audio` (tail of buffer)
   - Update `previous_transcript`
   - Clear buffer

**Strategy application:**
- `prompt`: Uses `previous_transcript` as initial_prompt
- `context`: Prepends `context_audio` to buffer before transcribing
- `hybrid`: Both

## Client-Side Changes

**Removed:**
- Buffering logic (server handles it now)
- Energy filter (server can handle silence)
- Building large batched messages

**Kept:**
- VAD detection (to send `vad_end` signal)
- Audio capture (30ms chunks)
- Latency tracking

**New behavior:**
1. Capture audio chunk (30ms)
2. Send as `audio_frame` immediately
3. Track VAD state:
   - If speaking → keep sending
   - If silence detected → send `vad_end`
4. Receive responses:
   - `partial` → update display in place
   - `final` → print final, newline, ready for next

**Display (replace in place):**
```
[partial] Hello wor...
[partial] Hello world, how are...    <- overwrites previous
[final] Hello world, how are you?    <- stays, newline
[partial] I'm doing...               <- next utterance starts
```

## Configuration

| Env var | Default | Description |
|---------|---------|-------------|
| `PARTIAL_INTERVAL_MS` | 500 | How often server sends partial results |
| `STRATEGY` | prompt | Transcription strategy (prompt/context/hybrid) |
| `VAD_BACKEND` | webrtc | VAD backend (webrtc/silero) |

## Files to Change

**Server:**
- `server/main.py` — New streaming session handler, periodic partial task

**Client:**
- `client/main.py` — Remove buffering, send frames immediately, in-place display

**Unchanged:**
- `server/transcriber.py` — strategies still work
- `server/backends/*` — no changes
- `client/vad/*` — still used for VAD
- `client/audio.py` — still captures chunks
