# Whisper Streaming PoC

Real-time microphone transcription over WebSocket.

## Dependencies

### Server
- websockets
- numpy
- mlx-whisper OR faster-whisper

### Client
- sounddevice
- numpy
- websockets
- py-webrtcvad OR silero-vad

## Configuration

```bash
WHISPER_BACKEND=mlx        # or "faster"
WHISPER_MODEL=small
VAD_BACKEND=webrtc         # or "silero"
```

## Endpoints

- `/ws/transcribe/prompt` - Prompt conditioning strategy
- `/ws/transcribe/context` - Context audio + trim strategy
- `/ws/transcribe/hybrid` - Combined strategy

## Quick Start

### Server

```bash
# Create and activate venv
python3 -m venv venv
source venv/bin/activate

# For Apple Silicon (mlx-whisper)
pip install -e ".[server-mlx,dev]"
WHISPER_BACKEND=mlx WHISPER_MODEL=small python -m server.main

# For CPU/CUDA (faster-whisper)
pip install -e ".[server-faster,dev]"
WHISPER_BACKEND=faster WHISPER_MODEL=small python -m server.main
```

### Client

```bash
# In another terminal
source venv/bin/activate
pip install -e ".[client,dev]"

# With WebRTC VAD (lightweight)
VAD_BACKEND=webrtc STRATEGY=prompt python -m client.main

# With Silero VAD (more accurate)
pip install -e ".[client-silero]"
VAD_BACKEND=silero STRATEGY=prompt python -m client.main
```

## Testing Strategies

Compare boundary artifact handling:

```bash
# Test prompt conditioning
STRATEGY=prompt python -m client.main

# Test context audio overlap
STRATEGY=context python -m client.main

# Test hybrid approach
STRATEGY=hybrid python -m client.main
```

## Running Tests

```bash
source venv/bin/activate
pytest tests/ -v
```
