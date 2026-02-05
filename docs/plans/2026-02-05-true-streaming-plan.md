# True Streaming Transcription Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Refactor from batch mode to true streaming where audio frames are sent continuously and partial results are returned in real-time.

**Architecture:** Client sends 30ms audio frames immediately (no buffering). Server accumulates frames, sends partial transcriptions every 500ms, and finalizes on VAD end signal. All three strategies (prompt/context/hybrid) still apply.

**Tech Stack:** Python, websockets, asyncio, numpy, mlx-whisper/faster-whisper

**Design doc:** `docs/plans/2026-02-05-true-streaming-design.md`

---

### Task 1: Add StreamingSession Class

**Files:**
- Modify: `whisper-streaming/server/main.py`
- Create: `whisper-streaming/tests/test_streaming_session.py`

**Step 1: Write the failing test**

```python
# tests/test_streaming_session.py
import pytest
import numpy as np
from unittest.mock import MagicMock
from server.main import StreamingSession
from server.backends.base import TranscriptResult, Segment


@pytest.fixture
def mock_strategy():
    strategy = MagicMock()
    strategy.transcribe.return_value = TranscriptResult(
        text="hello world",
        segments=[Segment(start=0.0, end=1.0, text="hello world")],
        language="en",
        processing_time_ms=100.0
    )
    return strategy


def test_streaming_session_init(mock_strategy):
    session = StreamingSession(strategy=mock_strategy, strategy_name="prompt")
    assert session.audio_buffer == []
    assert session.previous_transcript == ""
    assert session.context_audio is None


def test_streaming_session_add_audio(mock_strategy):
    session = StreamingSession(strategy=mock_strategy, strategy_name="prompt")
    audio = np.zeros(480, dtype=np.float32)  # 30ms at 16kHz
    session.add_audio(audio)
    assert len(session.audio_buffer) == 1


def test_streaming_session_get_partial(mock_strategy):
    session = StreamingSession(strategy=mock_strategy, strategy_name="prompt")
    audio = np.zeros(16000, dtype=np.float32)  # 1 second
    session.add_audio(audio)

    result = session.get_partial()

    assert result["type"] == "partial"
    assert result["text"] == "hello world"
    assert "processing_time_ms" in result


def test_streaming_session_get_final(mock_strategy):
    session = StreamingSession(strategy=mock_strategy, strategy_name="prompt")
    audio = np.zeros(16000, dtype=np.float32)
    session.add_audio(audio)

    result = session.get_final()

    assert result["type"] == "final"
    assert result["text"] == "hello world"
    assert "segments" in result
    assert session.audio_buffer == []  # cleared after final
    assert session.previous_transcript == "hello world"
```

**Step 2: Run test to verify it fails**

Run: `whisper-streaming/venv/bin/pytest whisper-streaming/tests/test_streaming_session.py -v`
Expected: FAIL with ImportError (StreamingSession doesn't exist)

**Step 3: Write minimal implementation**

Add to `whisper-streaming/server/main.py` after imports:

```python
class StreamingSession:
    """Manages per-connection streaming state."""

    def __init__(self, strategy, strategy_name: str, sample_rate: int = 16000):
        self.strategy = strategy
        self.strategy_name = strategy_name
        self.sample_rate = sample_rate
        self.audio_buffer: list[np.ndarray] = []
        self.previous_transcript = ""
        self.context_audio: np.ndarray | None = None
        self.context_overlap_ms = 1000

    def add_audio(self, audio: np.ndarray):
        """Add audio frame to buffer."""
        self.audio_buffer.append(audio)

    def get_buffer_duration_ms(self) -> float:
        """Get total duration of buffered audio in milliseconds."""
        if not self.audio_buffer:
            return 0
        total_samples = sum(len(chunk) for chunk in self.audio_buffer)
        return (total_samples / self.sample_rate) * 1000

    def _get_audio_for_transcription(self) -> np.ndarray:
        """Get audio array, prepending context if using context/hybrid strategy."""
        audio = np.concatenate(self.audio_buffer).flatten()

        if self.strategy_name in ("context", "hybrid") and self.context_audio is not None:
            audio = np.concatenate([self.context_audio, audio])

        return audio

    def _get_context_duration(self) -> float:
        """Get context audio duration in seconds."""
        if self.strategy_name in ("context", "hybrid") and self.context_audio is not None:
            return len(self.context_audio) / self.sample_rate
        return 0.0

    def get_partial(self) -> dict:
        """Transcribe current buffer and return partial result."""
        if not self.audio_buffer:
            return {"type": "partial", "text": "", "processing_time_ms": 0}

        audio = self._get_audio_for_transcription()

        kwargs = {}
        if self.strategy_name in ("prompt", "hybrid") and self.previous_transcript:
            kwargs["previous_transcript"] = self.previous_transcript

        result = self.strategy.transcribe(audio, self.sample_rate, **kwargs)

        # For context/hybrid, extract only new text based on timing
        text = result.text
        if self.strategy_name in ("context", "hybrid") and self.context_audio is not None:
            context_duration = self._get_context_duration()
            new_segments = [s for s in result.segments if s.end > context_duration]
            text = " ".join(s.text for s in new_segments)

        return {
            "type": "partial",
            "text": text,
            "processing_time_ms": result.processing_time_ms
        }

    def get_final(self) -> dict:
        """Transcribe, finalize segment, update state, return final result."""
        if not self.audio_buffer:
            return {"type": "final", "text": "", "segments": [], "language": "unknown", "processing_time_ms": 0}

        audio = self._get_audio_for_transcription()

        kwargs = {}
        if self.strategy_name in ("prompt", "hybrid") and self.previous_transcript:
            kwargs["previous_transcript"] = self.previous_transcript

        result = self.strategy.transcribe(audio, self.sample_rate, **kwargs)

        # For context/hybrid, filter segments after context
        segments = result.segments
        text = result.text
        if self.strategy_name in ("context", "hybrid") and self.context_audio is not None:
            context_duration = self._get_context_duration()
            segments = [
                Segment(start=s.start - context_duration, end=s.end - context_duration, text=s.text)
                for s in result.segments if s.end > context_duration
            ]
            text = " ".join(s.text for s in segments)

        # Update state for next segment
        raw_audio = np.concatenate(self.audio_buffer).flatten()
        overlap_samples = int(self.sample_rate * self.context_overlap_ms / 1000)
        if len(raw_audio) > overlap_samples:
            self.context_audio = raw_audio[-overlap_samples:]
        else:
            self.context_audio = raw_audio

        self.previous_transcript = text
        self.audio_buffer = []

        return {
            "type": "final",
            "text": text,
            "segments": [{"start": s.start, "end": s.end, "text": s.text} for s in segments],
            "language": result.language,
            "processing_time_ms": result.processing_time_ms
        }
```

Also add import at top:
```python
from .backends.base import Segment
```

**Step 4: Run test to verify it passes**

Run: `whisper-streaming/venv/bin/pytest whisper-streaming/tests/test_streaming_session.py -v`
Expected: PASS

---

### Task 2: Add Streaming WebSocket Handler

**Files:**
- Modify: `whisper-streaming/server/main.py`
- Create: `whisper-streaming/tests/test_streaming_handler.py`

**Step 1: Write the failing test**

```python
# tests/test_streaming_handler.py
import pytest
import json
import base64
import numpy as np
from unittest.mock import MagicMock, AsyncMock
from server.main import handle_audio_frame, handle_vad_end, StreamingSession
from server.backends.base import TranscriptResult, Segment


@pytest.fixture
def mock_strategy():
    strategy = MagicMock()
    strategy.transcribe.return_value = TranscriptResult(
        text="hello",
        segments=[Segment(start=0.0, end=1.0, text="hello")],
        language="en",
        processing_time_ms=50.0
    )
    return strategy


@pytest.fixture
def session(mock_strategy):
    return StreamingSession(strategy=mock_strategy, strategy_name="prompt")


def test_handle_audio_frame(session):
    audio = np.zeros(480, dtype=np.float32)
    audio_b64 = base64.b64encode(audio.tobytes()).decode()

    message = {
        "type": "audio_frame",
        "audio": audio_b64,
        "sample_rate": 16000
    }

    handle_audio_frame(message, session)

    assert len(session.audio_buffer) == 1


def test_handle_vad_end(session):
    # Add some audio first
    audio = np.zeros(16000, dtype=np.float32)
    session.add_audio(audio)

    result = handle_vad_end(session)

    assert result["type"] == "final"
    assert session.audio_buffer == []
```

**Step 2: Run test to verify it fails**

Run: `whisper-streaming/venv/bin/pytest whisper-streaming/tests/test_streaming_handler.py -v`
Expected: FAIL with ImportError

**Step 3: Write minimal implementation**

Add to `whisper-streaming/server/main.py`:

```python
def handle_audio_frame(message: dict, session: StreamingSession):
    """Handle incoming audio frame, add to session buffer."""
    audio_b64 = message.get("audio", "")
    audio_bytes = base64.b64decode(audio_b64)
    audio = np.frombuffer(audio_bytes, dtype=np.float32)
    session.add_audio(audio)


def handle_vad_end(session: StreamingSession) -> dict:
    """Handle VAD end signal, return final result."""
    return session.get_final()
```

**Step 4: Run test to verify it passes**

Run: `whisper-streaming/venv/bin/pytest whisper-streaming/tests/test_streaming_handler.py -v`
Expected: PASS

---

### Task 3: Refactor WebSocket Handler for Streaming

**Files:**
- Modify: `whisper-streaming/server/main.py`

**Step 1: Update the handler function in create_app()**

Replace the existing `handler` function inside `create_app()` with:

```python
    async def handler(websocket):
        path = websocket.request.path
        parts = path.strip("/").split("/")
        if len(parts) >= 3 and parts[0] == "ws" and parts[1] == "transcribe":
            strategy_name = parts[2]
        else:
            logger.warning(f"Invalid path: {path}")
            await websocket.close(1008, "Invalid path")
            return

        if strategy_name not in strategies:
            logger.warning(f"Unknown strategy: {strategy_name}")
            await websocket.close(1008, f"Unknown strategy: {strategy_name}")
            return

        client_addr = websocket.remote_address
        logger.info(f"Client connected: {client_addr} -> /{strategy_name}")

        strategy = strategies[strategy_name]
        session = StreamingSession(strategy=strategy, strategy_name=strategy_name)

        partial_interval_ms = int(os.environ.get("PARTIAL_INTERVAL_MS", "500"))
        last_partial_time = asyncio.get_event_loop().time()

        try:
            async for raw_message in websocket:
                message = json.loads(raw_message)
                msg_type = message.get("type")

                if msg_type == "audio_frame":
                    handle_audio_frame(message, session)

                    # Check if we should send a partial
                    current_time = asyncio.get_event_loop().time()
                    elapsed_ms = (current_time - last_partial_time) * 1000

                    if elapsed_ms >= partial_interval_ms and session.get_buffer_duration_ms() > 0:
                        logger.debug(f"[{strategy_name}] Sending partial ({session.get_buffer_duration_ms():.0f}ms audio)")
                        partial = session.get_partial()
                        await websocket.send(json.dumps(partial))
                        last_partial_time = current_time

                elif msg_type == "vad_end":
                    if session.get_buffer_duration_ms() > 0:
                        logger.info(f"[{strategy_name}] VAD end, finalizing ({session.get_buffer_duration_ms():.0f}ms audio)")
                        final = handle_vad_end(session)
                        logger.info(f"[{strategy_name}] Final ({final['processing_time_ms']:.0f}ms): {final['text'][:80]}...")
                        await websocket.send(json.dumps(final))
                    last_partial_time = asyncio.get_event_loop().time()

                else:
                    logger.warning(f"Unknown message type: {msg_type}")

        except json.JSONDecodeError as e:
            logger.error(f"Invalid JSON: {e}")
        finally:
            logger.info(f"Client disconnected: {client_addr}")
```

**Step 2: Run existing tests to verify nothing broke**

Run: `whisper-streaming/venv/bin/pytest whisper-streaming/tests/ -v`
Expected: All tests PASS

---

### Task 4: Refactor Client for Streaming

**Files:**
- Modify: `whisper-streaming/client/main.py`
- Create: `whisper-streaming/tests/test_client_streaming.py`

**Step 1: Write the failing test**

```python
# tests/test_client_streaming.py
import pytest
import json
import base64
import numpy as np
from client.main import StreamingClient


def test_build_audio_frame_message():
    # Mock dependencies
    import sys
    from unittest.mock import MagicMock
    sys.modules['sounddevice'] = MagicMock()
    sys.modules['webrtcvad'] = MagicMock()

    from client.main import StreamingClient

    client = StreamingClient.__new__(StreamingClient)
    client.sample_rate = 16000

    audio = np.zeros(480, dtype=np.float32)
    message = client._build_audio_frame(audio)

    data = json.loads(message)
    assert data["type"] == "audio_frame"
    assert data["sample_rate"] == 16000
    assert "audio" in data


def test_build_vad_end_message():
    import sys
    from unittest.mock import MagicMock
    sys.modules['sounddevice'] = MagicMock()
    sys.modules['webrtcvad'] = MagicMock()

    from client.main import StreamingClient

    client = StreamingClient.__new__(StreamingClient)
    message = client._build_vad_end()

    data = json.loads(message)
    assert data["type"] == "vad_end"
```

**Step 2: Run test to verify it fails**

Run: `whisper-streaming/venv/bin/pytest whisper-streaming/tests/test_client_streaming.py -v`
Expected: FAIL with AttributeError

**Step 3: Rewrite client/main.py for streaming**

```python
# client/main.py
import os
import sys
import json
import base64
import asyncio
import time
import numpy as np
import websockets

from .audio import AudioCapture
from .vad import create_vad


class LatencyStats:
    """Track latency statistics."""

    def __init__(self):
        self.round_trips: list[float] = []
        self.server_times: list[float] = []

    def record(self, server_ms: float):
        self.server_times.append(server_ms)

    def summary(self) -> str:
        if not self.server_times:
            return "No data"
        n = len(self.server_times)
        avg_srv = sum(self.server_times) / n
        return f"Transcriptions: {n} | Avg server time: {avg_srv:.0f}ms"


class StreamingClient:
    def __init__(
        self,
        server_url: str,
        strategy: str = "prompt",
        sample_rate: int = 16000,
        silence_threshold_ms: int = 300,
    ):
        self.server_url = f"{server_url}/ws/transcribe/{strategy}"
        self.strategy = strategy
        self.sample_rate = sample_rate
        self.silence_threshold_ms = silence_threshold_ms

        self.vad = create_vad()
        self.audio_capture = AudioCapture(sample_rate=sample_rate, chunk_ms=30)
        self.latency_stats = LatencyStats()

        self._current_partial = ""

    def _build_audio_frame(self, audio: np.ndarray) -> str:
        """Build audio_frame message."""
        audio_b64 = base64.b64encode(audio.astype(np.float32).tobytes()).decode()
        return json.dumps({
            "type": "audio_frame",
            "audio": audio_b64,
            "sample_rate": self.sample_rate
        })

    def _build_vad_end(self) -> str:
        """Build vad_end message."""
        return json.dumps({"type": "vad_end"})

    def _display_partial(self, text: str):
        """Display partial result, overwriting previous."""
        # Clear previous line and write new
        clear = "\r" + " " * (len(self._current_partial) + 20) + "\r"
        sys.stdout.write(clear)
        sys.stdout.write(f"[partial] {text}")
        sys.stdout.flush()
        self._current_partial = text

    def _display_final(self, text: str, processing_ms: float):
        """Display final result on new line."""
        # Clear partial line first
        clear = "\r" + " " * (len(self._current_partial) + 20) + "\r"
        sys.stdout.write(clear)
        print(f"[final {processing_ms:.0f}ms] {text}")
        self._current_partial = ""

    async def _handle_response(self, response: str):
        """Handle server response (partial or final)."""
        data = json.loads(response)
        msg_type = data.get("type")

        if msg_type == "partial":
            text = data.get("text", "").strip()
            if text:
                self._display_partial(text)

        elif msg_type == "final":
            text = data.get("text", "").strip()
            processing_ms = data.get("processing_time_ms", 0)
            self.latency_stats.record(processing_ms)
            if text:
                self._display_final(text, processing_ms)

    async def run(self):
        """Main client loop."""
        print(f"Connecting to {self.server_url}")
        print(f"Strategy: {self.strategy}")
        print(f"VAD: {os.environ.get('VAD_BACKEND', 'webrtc')}")
        print("Press Ctrl+C to stop\n")

        async with websockets.connect(self.server_url, max_size=10 * 1024 * 1024) as ws:
            with self.audio_capture:
                silence_count = 0
                silence_chunks = int(self.silence_threshold_ms / 30)
                is_speaking = False

                async def receive_responses():
                    """Background task to receive and handle responses."""
                    try:
                        async for response in ws:
                            await self._handle_response(response)
                    except websockets.exceptions.ConnectionClosed:
                        pass

                # Start response handler
                response_task = asyncio.create_task(receive_responses())

                try:
                    while True:
                        chunk = self.audio_capture.get_chunk(timeout=0.1)
                        if chunk is None:
                            await asyncio.sleep(0.01)
                            continue

                        # Send audio frame immediately
                        message = self._build_audio_frame(chunk)
                        await ws.send(message)

                        # VAD detection
                        chunk_bytes = (chunk * 32768).astype(np.int16).tobytes()
                        speech_detected = self.vad.is_speech(chunk_bytes, self.sample_rate)

                        if speech_detected:
                            silence_count = 0
                            is_speaking = True
                        elif is_speaking:
                            silence_count += 1
                            if silence_count >= silence_chunks:
                                # Send VAD end signal
                                await ws.send(self._build_vad_end())
                                is_speaking = False
                                silence_count = 0

                finally:
                    response_task.cancel()
                    try:
                        await response_task
                    except asyncio.CancelledError:
                        pass


async def main():
    server = os.environ.get("SERVER_URL", "ws://localhost:8765")
    strategy = os.environ.get("STRATEGY", "prompt")

    client = StreamingClient(server_url=server, strategy=strategy)
    try:
        await client.run()
    except KeyboardInterrupt:
        pass
    finally:
        print(f"\n--- Latency Summary ---")
        print(client.latency_stats.summary())


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
```

**Step 4: Run tests to verify it passes**

Run: `whisper-streaming/venv/bin/pytest whisper-streaming/tests/test_client_streaming.py -v`
Expected: PASS

---

### Task 5: Update Existing Tests

**Files:**
- Modify: `whisper-streaming/tests/test_server.py`

**Step 1: Update test_server.py for new message format**

The old `handle_transcribe` function is replaced. Update tests to test the new streaming handlers:

```python
# tests/test_server.py
import sys
import pytest
import json
import base64
import numpy as np
from unittest.mock import MagicMock

# Mock mlx_whisper before importing
sys.modules['mlx_whisper'] = MagicMock()

from server.main import handle_audio_frame, handle_vad_end, StreamingSession
from server.backends.base import TranscriptResult, Segment


@pytest.fixture
def mock_strategy():
    strategy = MagicMock()
    strategy.transcribe.return_value = TranscriptResult(
        text="hello",
        segments=[Segment(start=0.0, end=1.0, text="hello")],
        language="en",
        processing_time_ms=50.0
    )
    return strategy


def test_handle_audio_frame_adds_to_buffer(mock_strategy):
    session = StreamingSession(strategy=mock_strategy, strategy_name="prompt")
    audio = np.zeros(480, dtype=np.float32)
    audio_b64 = base64.b64encode(audio.tobytes()).decode()

    message = {"type": "audio_frame", "audio": audio_b64, "sample_rate": 16000}
    handle_audio_frame(message, session)

    assert len(session.audio_buffer) == 1


def test_handle_vad_end_returns_final(mock_strategy):
    session = StreamingSession(strategy=mock_strategy, strategy_name="prompt")
    audio = np.zeros(16000, dtype=np.float32)
    session.add_audio(audio)

    result = handle_vad_end(session)

    assert result["type"] == "final"
    assert result["text"] == "hello"
    assert session.audio_buffer == []
```

**Step 2: Run all tests**

Run: `whisper-streaming/venv/bin/pytest whisper-streaming/tests/ -v`
Expected: All tests PASS

---

### Task 6: Integration Test for Streaming

**Files:**
- Modify: `whisper-streaming/tests/test_integration.py`

**Step 1: Add streaming integration test**

Add to `test_integration.py`:

```python
@pytest.mark.asyncio
async def test_streaming_session_full_cycle(mock_backend):
    """Test full streaming cycle: audio frames -> partial -> vad_end -> final."""
    from server.main import StreamingSession
    from server.transcriber import PromptStrategy

    strategy = PromptStrategy(mock_backend)
    session = StreamingSession(strategy=strategy, strategy_name="prompt")

    # Simulate streaming: add multiple audio frames
    for _ in range(10):
        audio = np.zeros(480, dtype=np.float32)  # 30ms chunks
        session.add_audio(audio)

    # Get partial
    partial = session.get_partial()
    assert partial["type"] == "partial"
    assert session.audio_buffer != []  # buffer not cleared

    # Get final
    final = session.get_final()
    assert final["type"] == "final"
    assert session.audio_buffer == []  # buffer cleared
    assert session.previous_transcript != ""  # transcript saved
```

**Step 2: Run integration tests**

Run: `whisper-streaming/venv/bin/pytest whisper-streaming/tests/test_integration.py -v`
Expected: PASS

---

### Task 7: Final Verification

**Step 1: Run all tests**

```bash
whisper-streaming/venv/bin/pytest whisper-streaming/tests/ -v
```

**Step 2: Manual test**

Terminal 1 (server):
```bash
WHISPER_BACKEND=mlx WHISPER_MODEL=small PARTIAL_INTERVAL_MS=500 whisper-streaming/venv/bin/python -m server.main
```

Terminal 2 (client):
```bash
VAD_BACKEND=webrtc STRATEGY=prompt whisper-streaming/venv/bin/python -m client.main
```

Expected behavior:
- Client sends audio frames continuously
- Server logs partial transcriptions every 500ms
- Client displays partials (updating in place)
- On silence, client sends vad_end
- Server returns final, client displays it on new line
