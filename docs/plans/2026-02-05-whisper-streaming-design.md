# Whisper Streaming PoC Design

## Goals

- Real-time microphone transcription with lowest latency
- WebSocket-based HTTP service
- Test 3 boundary artifact reduction strategies
- Swappable backends (mlx_whisper, faster-whisper)
- Swappable VAD (Silero, WebRTC)

## Architecture

```
┌─────────────────┐     WebSocket      ┌─────────────────┐
│  Python Client  │ ←───────────────→  │   HTTP Server   │
│                 │   Raw PCM + meta   │                 │
│  - Mic capture  │   Full Whisper     │  - Backend:     │
│  - VAD          │     output         │    mlx_whisper  │
│  - Chunking     │                    │    faster-whisper
└─────────────────┘                    │  - Strategy     │
                                       │    selection    │
                                       └─────────────────┘
```

## Boundary Artifact Strategies

Three strategies, each with its own endpoint:

1. **Prompt conditioning** — Pass previous transcript as Whisper's `initial_prompt`
2. **Context audio + trim** — Send overlap audio, return only new text using timestamps
3. **Hybrid** — Both context audio and prompt conditioning

## Folder Structure

```
whisper-streaming/
├── server/
│   ├── main.py              # WebSocket server
│   ├── transcriber.py       # Strategy implementations
│   └── backends/
│       ├── base.py          # Abstract backend interface
│       ├── mlx_backend.py   # mlx_whisper
│       └── faster_backend.py # faster-whisper
├── client/
│   ├── main.py              # Mic capture + WebSocket client
│   └── vad/
│       ├── base.py          # VADBackend ABC
│       ├── silero_vad.py    # Silero implementation
│       └── webrtc_vad.py    # py-webrtcvad implementation
├── pyproject.toml           # Source of truth for deps
└── README.md                # Human-readable docs + deps list
```

## WebSocket Protocol

### Endpoints

**`/ws/transcribe/prompt`** — Prompt conditioning strategy

```json
{
  "audio": "<base64 PCM 16-bit 16kHz mono>",
  "sample_rate": 16000,
  "previous_transcript": "optional..."
}
```

**`/ws/transcribe/context`** — Context audio + trim strategy

```json
{
  "audio": "<base64 PCM>",
  "context_audio": "<base64 PCM overlap>",
  "sample_rate": 16000
}
```

**`/ws/transcribe/hybrid`** — Both strategies combined

```json
{
  "audio": "<base64 PCM>",
  "context_audio": "<base64 PCM overlap>",
  "sample_rate": 16000,
  "previous_transcript": "optional..."
}
```

### Response (all endpoints)

```json
{
  "text": "transcript",
  "segments": [...],
  "language": "en",
  "processing_time_ms": 142
}
```

### Error Response

```json
{
  "error": "description",
  "code": "INVALID_AUDIO"
}
```

## Client-side VAD & Chunking

### VAD Options

| Backend | Pros | Cons |
|---------|------|------|
| **py-webrtcvad** | Very fast, tiny, C-based | Less accurate |
| **Silero VAD** | More accurate, popular | Heavier (~50MB model) |

### Chunking Parameters

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `min_chunk_ms` | 500 | Don't send tiny fragments |
| `max_chunk_ms` | 5000 | Cap chunk size for latency |
| `silence_threshold_ms` | 300 | Silence duration to trigger send |
| `context_overlap_ms` | 1000 | Audio overlap for context strategy |

### Client Flow

```
┌─────────┐    ┌─────┐    ┌────────┐    ┌──────────┐
│   Mic   │───→│ VAD │───→│ Buffer │───→│ WebSocket│
└─────────┘    └─────┘    └────────┘    └──────────┘
                  │            │
                  │  speech    │  silence detected
                  │  detected  │  OR max_chunk reached
                  └───start────┴───────send─────────→
```

## Backend Abstraction

### Interface

```python
class WhisperBackend(ABC):
    @abstractmethod
    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        initial_prompt: str | None = None
    ) -> TranscriptResult:
        pass

    @abstractmethod
    def load_model(self, model_name: str) -> None:
        pass
```

### Normalized Output

```python
@dataclass
class TranscriptResult:
    text: str
    segments: list[Segment]
    language: str
    processing_time_ms: float
```

### Configuration

```bash
WHISPER_BACKEND=mlx        # or "faster"
WHISPER_MODEL=small        # tiny, base, small, medium, large-v3
VAD_BACKEND=webrtc         # or "silero"
```

## Dependencies

### Server

- `websockets` — WebSocket server
- `numpy` — Audio array handling

### Server Backends (choose one)

- `mlx-whisper` — For Apple Silicon
- `faster-whisper` — For CPU/CUDA (RPi, etc.)

### Client

- `sounddevice` — Mic capture
- `numpy` — Audio handling
- `websockets` — Client connection

### Client VAD (choose one or both)

- `py-webrtcvad` — WebRTC VAD (lightweight)
- `silero-vad` — Silero VAD (more accurate)

### Dev

- `pytest` — Testing
- `ruff` — Linting
