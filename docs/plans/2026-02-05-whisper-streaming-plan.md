# Whisper Streaming PoC Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a WebSocket-based streaming transcription service with swappable Whisper backends and VAD options.

**Architecture:** Client captures mic audio, runs VAD locally, sends PCM chunks over WebSocket. Server transcribes using configurable backend (mlx_whisper or faster-whisper) with three strategy endpoints for boundary artifact handling.

**Tech Stack:** Python, websockets, numpy, sounddevice, mlx-whisper/faster-whisper, py-webrtcvad/silero-vad

**Design doc:** `docs/plans/2026-02-05-whisper-streaming-design.md`

---

### Task 1: Project Scaffolding

**Files:**
- Create: `whisper-streaming/pyproject.toml`
- Create: `whisper-streaming/README.md`
- Create: `whisper-streaming/server/__init__.py`
- Create: `whisper-streaming/server/backends/__init__.py`
- Create: `whisper-streaming/client/__init__.py`
- Create: `whisper-streaming/client/vad/__init__.py`

**Step 1: Create folder structure**

```bash
mkdir -p whisper-streaming/server/backends
mkdir -p whisper-streaming/client/vad
mkdir -p whisper-streaming/tests
```

**Step 2: Create pyproject.toml**

```toml
[project]
name = "whisper-streaming"
version = "0.1.0"
description = "Real-time streaming transcription with Whisper"
requires-python = ">=3.10"
dependencies = [
    "websockets>=12.0",
    "numpy>=1.24.0",
]

[project.optional-dependencies]
server-mlx = ["mlx-whisper>=0.1.0"]
server-faster = ["faster-whisper>=1.0.0"]
client = [
    "sounddevice>=0.4.6",
    "py-webrtcvad>=2.0.10",
]
client-silero = ["silero-vad>=4.0.0"]
dev = [
    "pytest>=7.0.0",
    "ruff>=0.1.0",
]

[tool.ruff]
line-length = 100
target-version = "py310"
```

**Step 3: Create README.md**

```markdown
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
```

**Step 4: Create __init__.py files**

Create empty `__init__.py` in:
- `whisper-streaming/server/__init__.py`
- `whisper-streaming/server/backends/__init__.py`
- `whisper-streaming/client/__init__.py`
- `whisper-streaming/client/vad/__init__.py`

**Step 5: Create venv and install deps**

```bash
cd whisper-streaming
python3 -m venv venv
source venv/bin/activate
pip install -e ".[dev]"
```

---

### Task 2: Backend Abstraction

**Files:**
- Create: `whisper-streaming/server/backends/base.py`
- Create: `whisper-streaming/tests/test_backends_base.py`

**Step 1: Write the failing test**

```python
# tests/test_backends_base.py
import pytest
import numpy as np
from server.backends.base import WhisperBackend, TranscriptResult, Segment


def test_transcript_result_dataclass():
    segment = Segment(start=0.0, end=1.0, text="hello")
    result = TranscriptResult(
        text="hello",
        segments=[segment],
        language="en",
        processing_time_ms=100.0
    )
    assert result.text == "hello"
    assert result.language == "en"
    assert len(result.segments) == 1


def test_whisper_backend_is_abstract():
    with pytest.raises(TypeError):
        WhisperBackend()
```

**Step 2: Run test to verify it fails**

Run: `cd whisper-streaming && pytest tests/test_backends_base.py -v`
Expected: FAIL with ModuleNotFoundError

**Step 3: Write minimal implementation**

```python
# server/backends/base.py
from abc import ABC, abstractmethod
from dataclasses import dataclass
import numpy as np


@dataclass
class Segment:
    start: float
    end: float
    text: str


@dataclass
class TranscriptResult:
    text: str
    segments: list[Segment]
    language: str
    processing_time_ms: float


class WhisperBackend(ABC):
    @abstractmethod
    def load_model(self, model_name: str) -> None:
        """Load the specified model."""
        pass

    @abstractmethod
    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        initial_prompt: str | None = None
    ) -> TranscriptResult:
        """Transcribe audio and return normalized result."""
        pass
```

**Step 4: Run test to verify it passes**

Run: `cd whisper-streaming && pytest tests/test_backends_base.py -v`
Expected: PASS

---

### Task 3: MLX Backend Implementation

**Files:**
- Create: `whisper-streaming/server/backends/mlx_backend.py`
- Create: `whisper-streaming/tests/test_mlx_backend.py`

**Step 1: Write the failing test**

```python
# tests/test_mlx_backend.py
import pytest
import numpy as np
from unittest.mock import patch, MagicMock
from server.backends.mlx_backend import MLXBackend
from server.backends.base import TranscriptResult


def test_mlx_backend_inherits_base():
    backend = MLXBackend()
    assert hasattr(backend, "transcribe")
    assert hasattr(backend, "load_model")


@patch("server.backends.mlx_backend.mlx_whisper")
def test_mlx_backend_transcribe(mock_mlx):
    mock_mlx.transcribe.return_value = {
        "text": "hello world",
        "language": "en",
        "segments": [{"start": 0.0, "end": 1.0, "text": "hello world"}]
    }

    backend = MLXBackend()
    backend.load_model("small")

    audio = np.zeros(16000, dtype=np.float32)
    result = backend.transcribe(audio, 16000)

    assert isinstance(result, TranscriptResult)
    assert result.text == "hello world"
    assert result.language == "en"
```

**Step 2: Run test to verify it fails**

Run: `cd whisper-streaming && pytest tests/test_mlx_backend.py -v`
Expected: FAIL with ModuleNotFoundError

**Step 3: Write minimal implementation**

```python
# server/backends/mlx_backend.py
import time
import numpy as np
import mlx_whisper
from .base import WhisperBackend, TranscriptResult, Segment


class MLXBackend(WhisperBackend):
    def __init__(self):
        self.model_path: str | None = None

    def load_model(self, model_name: str) -> None:
        self.model_path = f"mlx-community/whisper-{model_name}-mlx"

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        initial_prompt: str | None = None
    ) -> TranscriptResult:
        if self.model_path is None:
            raise RuntimeError("Model not loaded. Call load_model() first.")

        start_time = time.perf_counter()

        result = mlx_whisper.transcribe(
            audio,
            path_or_hf_repo=self.model_path,
            initial_prompt=initial_prompt
        )

        processing_time_ms = (time.perf_counter() - start_time) * 1000

        segments = [
            Segment(start=s["start"], end=s["end"], text=s["text"].strip())
            for s in result.get("segments", [])
        ]

        return TranscriptResult(
            text=result.get("text", "").strip(),
            segments=segments,
            language=result.get("language", "unknown"),
            processing_time_ms=processing_time_ms
        )
```

**Step 4: Run test to verify it passes**

Run: `cd whisper-streaming && pytest tests/test_mlx_backend.py -v`
Expected: PASS

---

### Task 4: Faster-Whisper Backend Implementation

**Files:**
- Create: `whisper-streaming/server/backends/faster_backend.py`
- Create: `whisper-streaming/tests/test_faster_backend.py`

**Step 1: Write the failing test**

```python
# tests/test_faster_backend.py
import pytest
import numpy as np
from unittest.mock import patch, MagicMock
from server.backends.faster_backend import FasterBackend
from server.backends.base import TranscriptResult


def test_faster_backend_inherits_base():
    with patch("server.backends.faster_backend.WhisperModel"):
        backend = FasterBackend()
        assert hasattr(backend, "transcribe")
        assert hasattr(backend, "load_model")


@patch("server.backends.faster_backend.WhisperModel")
def test_faster_backend_transcribe(mock_model_class):
    mock_segment = MagicMock()
    mock_segment.start = 0.0
    mock_segment.end = 1.0
    mock_segment.text = "hello world"

    mock_info = MagicMock()
    mock_info.language = "en"

    mock_model = MagicMock()
    mock_model.transcribe.return_value = ([mock_segment], mock_info)
    mock_model_class.return_value = mock_model

    backend = FasterBackend()
    backend.load_model("small")

    audio = np.zeros(16000, dtype=np.float32)
    result = backend.transcribe(audio, 16000)

    assert isinstance(result, TranscriptResult)
    assert result.text == "hello world"
    assert result.language == "en"
```

**Step 2: Run test to verify it fails**

Run: `cd whisper-streaming && pytest tests/test_faster_backend.py -v`
Expected: FAIL with ModuleNotFoundError

**Step 3: Write minimal implementation**

```python
# server/backends/faster_backend.py
import time
import numpy as np
from faster_whisper import WhisperModel
from .base import WhisperBackend, TranscriptResult, Segment


class FasterBackend(WhisperBackend):
    def __init__(self):
        self.model: WhisperModel | None = None

    def load_model(self, model_name: str) -> None:
        self.model = WhisperModel(model_name, device="auto", compute_type="auto")

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        initial_prompt: str | None = None
    ) -> TranscriptResult:
        if self.model is None:
            raise RuntimeError("Model not loaded. Call load_model() first.")

        start_time = time.perf_counter()

        segments_gen, info = self.model.transcribe(
            audio,
            initial_prompt=initial_prompt
        )

        segments = [
            Segment(start=s.start, end=s.end, text=s.text.strip())
            for s in segments_gen
        ]

        processing_time_ms = (time.perf_counter() - start_time) * 1000

        full_text = " ".join(s.text for s in segments)

        return TranscriptResult(
            text=full_text,
            segments=segments,
            language=info.language,
            processing_time_ms=processing_time_ms
        )
```

**Step 4: Run test to verify it passes**

Run: `cd whisper-streaming && pytest tests/test_faster_backend.py -v`
Expected: PASS

---

### Task 5: Backend Factory

**Files:**
- Modify: `whisper-streaming/server/backends/__init__.py`
- Create: `whisper-streaming/tests/test_backend_factory.py`

**Step 1: Write the failing test**

```python
# tests/test_backend_factory.py
import pytest
from unittest.mock import patch
from server.backends import create_backend
from server.backends.base import WhisperBackend


def test_create_mlx_backend():
    with patch.dict("os.environ", {"WHISPER_BACKEND": "mlx"}):
        backend = create_backend()
        assert isinstance(backend, WhisperBackend)


def test_create_faster_backend():
    with patch.dict("os.environ", {"WHISPER_BACKEND": "faster"}):
        with patch("server.backends.faster_backend.WhisperModel"):
            backend = create_backend()
            assert isinstance(backend, WhisperBackend)


def test_invalid_backend_raises():
    with patch.dict("os.environ", {"WHISPER_BACKEND": "invalid"}):
        with pytest.raises(ValueError, match="Unknown backend"):
            create_backend()
```

**Step 2: Run test to verify it fails**

Run: `cd whisper-streaming && pytest tests/test_backend_factory.py -v`
Expected: FAIL with ImportError

**Step 3: Write minimal implementation**

```python
# server/backends/__init__.py
import os
from .base import WhisperBackend, TranscriptResult, Segment


def create_backend() -> WhisperBackend:
    backend_type = os.environ.get("WHISPER_BACKEND", "mlx")

    if backend_type == "mlx":
        from .mlx_backend import MLXBackend
        return MLXBackend()
    elif backend_type == "faster":
        from .faster_backend import FasterBackend
        return FasterBackend()
    else:
        raise ValueError(f"Unknown backend: {backend_type}")


__all__ = ["create_backend", "WhisperBackend", "TranscriptResult", "Segment"]
```

**Step 4: Run test to verify it passes**

Run: `cd whisper-streaming && pytest tests/test_backend_factory.py -v`
Expected: PASS

---

### Task 6: VAD Abstraction

**Files:**
- Create: `whisper-streaming/client/vad/base.py`
- Create: `whisper-streaming/tests/test_vad_base.py`

**Step 1: Write the failing test**

```python
# tests/test_vad_base.py
import pytest
from client.vad.base import VADBackend


def test_vad_backend_is_abstract():
    with pytest.raises(TypeError):
        VADBackend()
```

**Step 2: Run test to verify it fails**

Run: `cd whisper-streaming && pytest tests/test_vad_base.py -v`
Expected: FAIL with ModuleNotFoundError

**Step 3: Write minimal implementation**

```python
# client/vad/base.py
from abc import ABC, abstractmethod


class VADBackend(ABC):
    @abstractmethod
    def is_speech(self, audio_chunk: bytes, sample_rate: int) -> bool:
        """Check if audio chunk contains speech."""
        pass

    @abstractmethod
    def reset(self) -> None:
        """Reset internal state."""
        pass
```

**Step 4: Run test to verify it passes**

Run: `cd whisper-streaming && pytest tests/test_vad_base.py -v`
Expected: PASS

---

### Task 7: WebRTC VAD Implementation

**Files:**
- Create: `whisper-streaming/client/vad/webrtc_vad.py`
- Create: `whisper-streaming/tests/test_webrtc_vad.py`

**Step 1: Write the failing test**

```python
# tests/test_webrtc_vad.py
import pytest
from unittest.mock import patch, MagicMock
from client.vad.webrtc_vad import WebRTCVAD
from client.vad.base import VADBackend


def test_webrtc_vad_inherits_base():
    with patch("client.vad.webrtc_vad.webrtcvad"):
        vad = WebRTCVAD()
        assert isinstance(vad, VADBackend)


@patch("client.vad.webrtc_vad.webrtcvad")
def test_webrtc_vad_is_speech(mock_webrtcvad):
    mock_vad = MagicMock()
    mock_vad.is_speech.return_value = True
    mock_webrtcvad.Vad.return_value = mock_vad

    vad = WebRTCVAD(aggressiveness=2)
    result = vad.is_speech(b"\x00" * 640, 16000)

    assert result is True
    mock_vad.is_speech.assert_called_once()
```

**Step 2: Run test to verify it fails**

Run: `cd whisper-streaming && pytest tests/test_webrtc_vad.py -v`
Expected: FAIL with ModuleNotFoundError

**Step 3: Write minimal implementation**

```python
# client/vad/webrtc_vad.py
import webrtcvad
from .base import VADBackend


class WebRTCVAD(VADBackend):
    def __init__(self, aggressiveness: int = 2):
        """
        Args:
            aggressiveness: 0-3, higher = more aggressive filtering
        """
        self.vad = webrtcvad.Vad(aggressiveness)

    def is_speech(self, audio_chunk: bytes, sample_rate: int) -> bool:
        """
        Check if audio contains speech.
        Audio must be 16-bit PCM, 10/20/30ms frames.
        """
        return self.vad.is_speech(audio_chunk, sample_rate)

    def reset(self) -> None:
        """WebRTC VAD is stateless, nothing to reset."""
        pass
```

**Step 4: Run test to verify it passes**

Run: `cd whisper-streaming && pytest tests/test_webrtc_vad.py -v`
Expected: PASS

---

### Task 8: Silero VAD Implementation

**Files:**
- Create: `whisper-streaming/client/vad/silero_vad.py`
- Create: `whisper-streaming/tests/test_silero_vad.py`

**Step 1: Write the failing test**

```python
# tests/test_silero_vad.py
import pytest
import numpy as np
from unittest.mock import patch, MagicMock
from client.vad.silero_vad import SileroVAD
from client.vad.base import VADBackend


def test_silero_vad_inherits_base():
    with patch("client.vad.silero_vad.torch"):
        vad = SileroVAD.__new__(SileroVAD)
        vad.model = MagicMock()
        vad.get_speech_timestamps = MagicMock()
        assert isinstance(vad, VADBackend)


@patch("client.vad.silero_vad.torch")
def test_silero_vad_is_speech(mock_torch):
    mock_model = MagicMock()
    mock_model.return_value = MagicMock(item=lambda: 0.8)

    with patch.object(SileroVAD, "__init__", lambda self: None):
        vad = SileroVAD.__new__(SileroVAD)
        vad.model = mock_model
        vad.threshold = 0.5

        audio = np.zeros(512, dtype=np.float32)
        result = vad.is_speech(audio.tobytes(), 16000)

        assert result is True
```

**Step 2: Run test to verify it fails**

Run: `cd whisper-streaming && pytest tests/test_silero_vad.py -v`
Expected: FAIL with ModuleNotFoundError

**Step 3: Write minimal implementation**

```python
# client/vad/silero_vad.py
import numpy as np
import torch
from .base import VADBackend


class SileroVAD(VADBackend):
    def __init__(self, threshold: float = 0.5):
        self.threshold = threshold
        self.model, utils = torch.hub.load(
            repo_or_dir="snakers4/silero-vad",
            model="silero_vad",
            force_reload=False
        )
        self.model.eval()

    def is_speech(self, audio_chunk: bytes, sample_rate: int) -> bool:
        """Check if audio contains speech using Silero VAD."""
        audio = np.frombuffer(audio_chunk, dtype=np.int16).astype(np.float32) / 32768.0
        tensor = torch.from_numpy(audio)

        with torch.no_grad():
            speech_prob = self.model(tensor, sample_rate).item()

        return speech_prob > self.threshold

    def reset(self) -> None:
        """Reset model state."""
        self.model.reset_states()
```

**Step 4: Run test to verify it passes**

Run: `cd whisper-streaming && pytest tests/test_silero_vad.py -v`
Expected: PASS

---

### Task 9: VAD Factory

**Files:**
- Modify: `whisper-streaming/client/vad/__init__.py`
- Create: `whisper-streaming/tests/test_vad_factory.py`

**Step 1: Write the failing test**

```python
# tests/test_vad_factory.py
import pytest
from unittest.mock import patch
from client.vad import create_vad
from client.vad.base import VADBackend


def test_create_webrtc_vad():
    with patch.dict("os.environ", {"VAD_BACKEND": "webrtc"}):
        with patch("client.vad.webrtc_vad.webrtcvad"):
            vad = create_vad()
            assert isinstance(vad, VADBackend)


def test_create_silero_vad():
    with patch.dict("os.environ", {"VAD_BACKEND": "silero"}):
        with patch("client.vad.silero_vad.torch"):
            with patch.object(
                __import__("client.vad.silero_vad", fromlist=["SileroVAD"]).SileroVAD,
                "__init__",
                lambda self: None
            ):
                vad = create_vad()
                assert isinstance(vad, VADBackend)


def test_invalid_vad_raises():
    with patch.dict("os.environ", {"VAD_BACKEND": "invalid"}):
        with pytest.raises(ValueError, match="Unknown VAD"):
            create_vad()
```

**Step 2: Run test to verify it fails**

Run: `cd whisper-streaming && pytest tests/test_vad_factory.py -v`
Expected: FAIL with ImportError

**Step 3: Write minimal implementation**

```python
# client/vad/__init__.py
import os
from .base import VADBackend


def create_vad() -> VADBackend:
    vad_type = os.environ.get("VAD_BACKEND", "webrtc")

    if vad_type == "webrtc":
        from .webrtc_vad import WebRTCVAD
        return WebRTCVAD()
    elif vad_type == "silero":
        from .silero_vad import SileroVAD
        return SileroVAD()
    else:
        raise ValueError(f"Unknown VAD backend: {vad_type}")


__all__ = ["create_vad", "VADBackend"]
```

**Step 4: Run test to verify it passes**

Run: `cd whisper-streaming && pytest tests/test_vad_factory.py -v`
Expected: PASS

---

### Task 10: Transcription Strategies

**Files:**
- Create: `whisper-streaming/server/transcriber.py`
- Create: `whisper-streaming/tests/test_transcriber.py`

**Step 1: Write the failing test**

```python
# tests/test_transcriber.py
import pytest
import numpy as np
from unittest.mock import MagicMock
from server.transcriber import (
    PromptStrategy,
    ContextStrategy,
    HybridStrategy,
)
from server.backends.base import TranscriptResult, Segment


@pytest.fixture
def mock_backend():
    backend = MagicMock()
    backend.transcribe.return_value = TranscriptResult(
        text="hello world",
        segments=[Segment(start=0.0, end=1.0, text="hello world")],
        language="en",
        processing_time_ms=100.0
    )
    return backend


def test_prompt_strategy(mock_backend):
    strategy = PromptStrategy(mock_backend)
    audio = np.zeros(16000, dtype=np.float32)

    result = strategy.transcribe(audio, 16000, previous_transcript="hi")

    assert result.text == "hello world"
    mock_backend.transcribe.assert_called_once()
    call_args = mock_backend.transcribe.call_args
    assert call_args.kwargs["initial_prompt"] == "hi"


def test_context_strategy(mock_backend):
    mock_backend.transcribe.return_value = TranscriptResult(
        text="context hello world",
        segments=[
            Segment(start=0.0, end=0.5, text="context"),
            Segment(start=0.5, end=1.5, text="hello world"),
        ],
        language="en",
        processing_time_ms=100.0
    )

    strategy = ContextStrategy(mock_backend)
    audio = np.zeros(16000, dtype=np.float32)
    context = np.zeros(8000, dtype=np.float32)

    result = strategy.transcribe(audio, 16000, context_audio=context)

    # Should trim context portion based on timestamps
    assert "hello world" in result.text


def test_hybrid_strategy(mock_backend):
    strategy = HybridStrategy(mock_backend)
    audio = np.zeros(16000, dtype=np.float32)
    context = np.zeros(8000, dtype=np.float32)

    result = strategy.transcribe(
        audio, 16000,
        context_audio=context,
        previous_transcript="hi"
    )

    assert result.text == "hello world"
```

**Step 2: Run test to verify it fails**

Run: `cd whisper-streaming && pytest tests/test_transcriber.py -v`
Expected: FAIL with ModuleNotFoundError

**Step 3: Write minimal implementation**

```python
# server/transcriber.py
from abc import ABC, abstractmethod
import numpy as np
from .backends.base import WhisperBackend, TranscriptResult, Segment


class TranscriptionStrategy(ABC):
    def __init__(self, backend: WhisperBackend):
        self.backend = backend

    @abstractmethod
    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        **kwargs
    ) -> TranscriptResult:
        pass


class PromptStrategy(TranscriptionStrategy):
    """Use previous transcript as initial prompt."""

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        previous_transcript: str | None = None,
        **kwargs
    ) -> TranscriptResult:
        return self.backend.transcribe(
            audio,
            sample_rate,
            initial_prompt=previous_transcript
        )


class ContextStrategy(TranscriptionStrategy):
    """Prepend context audio, trim result to new portion."""

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        context_audio: np.ndarray | None = None,
        **kwargs
    ) -> TranscriptResult:
        if context_audio is not None:
            combined = np.concatenate([context_audio, audio])
            context_duration = len(context_audio) / sample_rate
        else:
            combined = audio
            context_duration = 0.0

        result = self.backend.transcribe(combined, sample_rate)

        # Filter segments to only include those after context
        new_segments = [
            Segment(
                start=s.start - context_duration,
                end=s.end - context_duration,
                text=s.text
            )
            for s in result.segments
            if s.end > context_duration
        ]

        new_text = " ".join(s.text for s in new_segments)

        return TranscriptResult(
            text=new_text,
            segments=new_segments,
            language=result.language,
            processing_time_ms=result.processing_time_ms
        )


class HybridStrategy(TranscriptionStrategy):
    """Combine context audio and prompt conditioning."""

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        context_audio: np.ndarray | None = None,
        previous_transcript: str | None = None,
        **kwargs
    ) -> TranscriptResult:
        if context_audio is not None:
            combined = np.concatenate([context_audio, audio])
            context_duration = len(context_audio) / sample_rate
        else:
            combined = audio
            context_duration = 0.0

        result = self.backend.transcribe(
            combined,
            sample_rate,
            initial_prompt=previous_transcript
        )

        # Filter segments to only include those after context
        new_segments = [
            Segment(
                start=s.start - context_duration,
                end=s.end - context_duration,
                text=s.text
            )
            for s in result.segments
            if s.end > context_duration
        ]

        new_text = " ".join(s.text for s in new_segments)

        return TranscriptResult(
            text=new_text,
            segments=new_segments,
            language=result.language,
            processing_time_ms=result.processing_time_ms
        )
```

**Step 4: Run test to verify it passes**

Run: `cd whisper-streaming && pytest tests/test_transcriber.py -v`
Expected: PASS

---

### Task 11: WebSocket Server

**Files:**
- Create: `whisper-streaming/server/main.py`
- Create: `whisper-streaming/tests/test_server.py`

**Step 1: Write the failing test**

```python
# tests/test_server.py
import pytest
import json
import base64
import numpy as np
from unittest.mock import patch, MagicMock, AsyncMock
from server.main import handle_transcribe, create_app
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


@pytest.mark.asyncio
async def test_handle_transcribe_prompt(mock_strategy):
    audio = np.zeros(16000, dtype=np.float32)
    audio_b64 = base64.b64encode(audio.tobytes()).decode()

    message = json.dumps({
        "audio": audio_b64,
        "sample_rate": 16000,
        "previous_transcript": "hi"
    })

    result = await handle_transcribe(message, mock_strategy, "prompt")
    response = json.loads(result)

    assert response["text"] == "hello"
    assert response["language"] == "en"
    assert "processing_time_ms" in response


@pytest.mark.asyncio
async def test_handle_transcribe_invalid_json(mock_strategy):
    result = await handle_transcribe("not json", mock_strategy, "prompt")
    response = json.loads(result)

    assert "error" in response
```

**Step 2: Run test to verify it fails**

Run: `cd whisper-streaming && pip install pytest-asyncio && pytest tests/test_server.py -v`
Expected: FAIL with ModuleNotFoundError

**Step 3: Write minimal implementation**

```python
# server/main.py
import os
import json
import base64
import asyncio
import numpy as np
import websockets
from websockets.server import serve

from .backends import create_backend
from .transcriber import PromptStrategy, ContextStrategy, HybridStrategy


async def handle_transcribe(message: str, strategy, strategy_name: str) -> str:
    """Handle a transcription request and return JSON response."""
    try:
        data = json.loads(message)
    except json.JSONDecodeError as e:
        return json.dumps({"error": f"Invalid JSON: {e}", "code": "INVALID_JSON"})

    try:
        audio_b64 = data.get("audio")
        if not audio_b64:
            return json.dumps({"error": "Missing audio", "code": "MISSING_AUDIO"})

        audio_bytes = base64.b64decode(audio_b64)
        audio = np.frombuffer(audio_bytes, dtype=np.float32)
        sample_rate = data.get("sample_rate", 16000)

        kwargs = {}
        if "previous_transcript" in data:
            kwargs["previous_transcript"] = data["previous_transcript"]
        if "context_audio" in data:
            context_bytes = base64.b64decode(data["context_audio"])
            kwargs["context_audio"] = np.frombuffer(context_bytes, dtype=np.float32)

        result = strategy.transcribe(audio, sample_rate, **kwargs)

        return json.dumps({
            "text": result.text,
            "segments": [
                {"start": s.start, "end": s.end, "text": s.text}
                for s in result.segments
            ],
            "language": result.language,
            "processing_time_ms": result.processing_time_ms
        })

    except Exception as e:
        return json.dumps({"error": str(e), "code": "TRANSCRIBE_ERROR"})


def create_app():
    """Create and configure the WebSocket server."""
    model_name = os.environ.get("WHISPER_MODEL", "small")

    backend = create_backend()
    backend.load_model(model_name)

    strategies = {
        "prompt": PromptStrategy(backend),
        "context": ContextStrategy(backend),
        "hybrid": HybridStrategy(backend),
    }

    async def handler(websocket, path: str):
        # Extract strategy from path: /ws/transcribe/{strategy}
        parts = path.strip("/").split("/")
        if len(parts) >= 3 and parts[0] == "ws" and parts[1] == "transcribe":
            strategy_name = parts[2]
        else:
            await websocket.close(1008, "Invalid path")
            return

        if strategy_name not in strategies:
            await websocket.close(1008, f"Unknown strategy: {strategy_name}")
            return

        strategy = strategies[strategy_name]

        async for message in websocket:
            response = await handle_transcribe(message, strategy, strategy_name)
            await websocket.send(response)

    return handler, strategies


async def main():
    host = os.environ.get("HOST", "0.0.0.0")
    port = int(os.environ.get("PORT", "8765"))

    handler, _ = create_app()

    print(f"Starting WebSocket server on ws://{host}:{port}")
    print("Endpoints:")
    print("  /ws/transcribe/prompt  - Prompt conditioning")
    print("  /ws/transcribe/context - Context audio + trim")
    print("  /ws/transcribe/hybrid  - Combined strategy")

    async with serve(handler, host, port):
        await asyncio.Future()  # run forever


if __name__ == "__main__":
    asyncio.run(main())
```

**Step 4: Run test to verify it passes**

Run: `cd whisper-streaming && pytest tests/test_server.py -v`
Expected: PASS

---

### Task 12: Client Audio Capture

**Files:**
- Create: `whisper-streaming/client/audio.py`
- Create: `whisper-streaming/tests/test_audio.py`

**Step 1: Write the failing test**

```python
# tests/test_audio.py
import pytest
import numpy as np
from unittest.mock import patch, MagicMock
from client.audio import AudioCapture


def test_audio_capture_init():
    with patch("client.audio.sd"):
        capture = AudioCapture(sample_rate=16000, chunk_ms=30)
        assert capture.sample_rate == 16000
        assert capture.chunk_size == 480  # 16000 * 0.030


def test_audio_capture_chunk_size_calculation():
    with patch("client.audio.sd"):
        capture = AudioCapture(sample_rate=16000, chunk_ms=20)
        assert capture.chunk_size == 320  # 16000 * 0.020
```

**Step 2: Run test to verify it fails**

Run: `cd whisper-streaming && pytest tests/test_audio.py -v`
Expected: FAIL with ModuleNotFoundError

**Step 3: Write minimal implementation**

```python
# client/audio.py
import queue
import numpy as np
import sounddevice as sd


class AudioCapture:
    def __init__(self, sample_rate: int = 16000, chunk_ms: int = 30):
        self.sample_rate = sample_rate
        self.chunk_ms = chunk_ms
        self.chunk_size = int(sample_rate * chunk_ms / 1000)
        self.audio_queue: queue.Queue[np.ndarray] = queue.Queue()
        self.stream: sd.InputStream | None = None

    def _callback(self, indata, frames, time, status):
        """Called by sounddevice for each audio chunk."""
        if status:
            print(f"Audio status: {status}")
        self.audio_queue.put(indata.copy())

    def start(self):
        """Start capturing audio from microphone."""
        self.stream = sd.InputStream(
            samplerate=self.sample_rate,
            channels=1,
            dtype=np.float32,
            blocksize=self.chunk_size,
            callback=self._callback
        )
        self.stream.start()

    def stop(self):
        """Stop capturing audio."""
        if self.stream:
            self.stream.stop()
            self.stream.close()
            self.stream = None

    def get_chunk(self, timeout: float = 1.0) -> np.ndarray | None:
        """Get next audio chunk from queue."""
        try:
            return self.audio_queue.get(timeout=timeout)
        except queue.Empty:
            return None

    def __enter__(self):
        self.start()
        return self

    def __exit__(self, *args):
        self.stop()
```

**Step 4: Run test to verify it passes**

Run: `cd whisper-streaming && pytest tests/test_audio.py -v`
Expected: PASS

---

### Task 13: Client Main

**Files:**
- Create: `whisper-streaming/client/main.py`

**Step 1: Write the client implementation**

```python
# client/main.py
import os
import json
import base64
import asyncio
import numpy as np
import websockets

from .audio import AudioCapture
from .vad import create_vad


class StreamingClient:
    def __init__(
        self,
        server_url: str,
        strategy: str = "prompt",
        sample_rate: int = 16000,
        min_chunk_ms: int = 500,
        max_chunk_ms: int = 5000,
        silence_threshold_ms: int = 300,
        context_overlap_ms: int = 1000,
    ):
        self.server_url = f"{server_url}/ws/transcribe/{strategy}"
        self.strategy = strategy
        self.sample_rate = sample_rate
        self.min_chunk_ms = min_chunk_ms
        self.max_chunk_ms = max_chunk_ms
        self.silence_threshold_ms = silence_threshold_ms
        self.context_overlap_ms = context_overlap_ms

        self.vad = create_vad()
        self.audio_capture = AudioCapture(sample_rate=sample_rate, chunk_ms=30)

        self.buffer: list[np.ndarray] = []
        self.previous_transcript = ""
        self.context_audio: np.ndarray | None = None

    def _buffer_duration_ms(self) -> float:
        if not self.buffer:
            return 0
        total_samples = sum(len(chunk) for chunk in self.buffer)
        return (total_samples / self.sample_rate) * 1000

    def _build_message(self) -> str:
        audio = np.concatenate(self.buffer).flatten()
        audio_b64 = base64.b64encode(audio.astype(np.float32).tobytes()).decode()

        message = {
            "audio": audio_b64,
            "sample_rate": self.sample_rate,
        }

        if self.strategy in ("prompt", "hybrid") and self.previous_transcript:
            message["previous_transcript"] = self.previous_transcript

        if self.strategy in ("context", "hybrid") and self.context_audio is not None:
            context_b64 = base64.b64encode(
                self.context_audio.astype(np.float32).tobytes()
            ).decode()
            message["context_audio"] = context_b64

        return json.dumps(message)

    def _update_context(self):
        """Store tail of current buffer as context for next chunk."""
        if not self.buffer:
            return

        audio = np.concatenate(self.buffer).flatten()
        overlap_samples = int(self.sample_rate * self.context_overlap_ms / 1000)

        if len(audio) > overlap_samples:
            self.context_audio = audio[-overlap_samples:]
        else:
            self.context_audio = audio

    async def run(self):
        """Main client loop."""
        print(f"Connecting to {self.server_url}")
        print(f"Strategy: {self.strategy}")
        print(f"VAD: {os.environ.get('VAD_BACKEND', 'webrtc')}")
        print("Press Ctrl+C to stop\n")

        async with websockets.connect(self.server_url) as ws:
            with self.audio_capture:
                silence_count = 0
                silence_chunks = int(self.silence_threshold_ms / 30)
                is_speaking = False

                while True:
                    chunk = self.audio_capture.get_chunk(timeout=0.1)
                    if chunk is None:
                        continue

                    chunk_bytes = (chunk * 32768).astype(np.int16).tobytes()
                    speech_detected = self.vad.is_speech(chunk_bytes, self.sample_rate)

                    if speech_detected:
                        silence_count = 0
                        is_speaking = True
                        self.buffer.append(chunk)
                    elif is_speaking:
                        silence_count += 1
                        self.buffer.append(chunk)

                        # Check if we should send
                        should_send = (
                            silence_count >= silence_chunks
                            and self._buffer_duration_ms() >= self.min_chunk_ms
                        ) or self._buffer_duration_ms() >= self.max_chunk_ms

                        if should_send:
                            message = self._build_message()
                            await ws.send(message)

                            response = await ws.recv()
                            result = json.loads(response)

                            if "error" in result:
                                print(f"Error: {result['error']}")
                            else:
                                text = result.get("text", "").strip()
                                if text:
                                    print(f"[{result['processing_time_ms']:.0f}ms] {text}")
                                    self.previous_transcript = text

                            self._update_context()
                            self.buffer = []
                            is_speaking = False
                            silence_count = 0


async def main():
    server = os.environ.get("SERVER_URL", "ws://localhost:8765")
    strategy = os.environ.get("STRATEGY", "prompt")

    client = StreamingClient(server_url=server, strategy=strategy)
    await client.run()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nStopped.")
```

**Step 2: Verify client imports work**

Run: `cd whisper-streaming && python -c "from client.main import StreamingClient; print('OK')"`
Expected: OK (may need mocked deps)

---

### Task 14: Integration Test

**Files:**
- Create: `whisper-streaming/tests/test_integration.py`

**Step 1: Write integration test**

```python
# tests/test_integration.py
import pytest
import json
import base64
import asyncio
import numpy as np
from unittest.mock import patch, MagicMock

from server.main import create_app, handle_transcribe
from server.backends.base import TranscriptResult, Segment


@pytest.fixture
def mock_backend():
    """Mock backend that returns predictable results."""
    backend = MagicMock()
    backend.transcribe.return_value = TranscriptResult(
        text="test transcription",
        segments=[Segment(start=0.0, end=1.0, text="test transcription")],
        language="en",
        processing_time_ms=42.0
    )
    return backend


@pytest.mark.asyncio
async def test_full_request_response_cycle(mock_backend):
    """Test complete request/response with mocked backend."""
    from server.transcriber import PromptStrategy

    strategy = PromptStrategy(mock_backend)

    # Create test audio (1 second of silence)
    audio = np.zeros(16000, dtype=np.float32)
    audio_b64 = base64.b64encode(audio.tobytes()).decode()

    message = json.dumps({
        "audio": audio_b64,
        "sample_rate": 16000,
        "previous_transcript": "hello"
    })

    response = await handle_transcribe(message, strategy, "prompt")
    result = json.loads(response)

    assert result["text"] == "test transcription"
    assert result["language"] == "en"
    assert len(result["segments"]) == 1
    assert result["processing_time_ms"] == 42.0

    # Verify backend was called with correct args
    mock_backend.transcribe.assert_called_once()
    call_kwargs = mock_backend.transcribe.call_args.kwargs
    assert call_kwargs["initial_prompt"] == "hello"


@pytest.mark.asyncio
async def test_context_strategy_trims_correctly(mock_backend):
    """Test that context strategy correctly trims overlapping audio."""
    from server.transcriber import ContextStrategy

    # Return segments that span context and new audio
    mock_backend.transcribe.return_value = TranscriptResult(
        text="old new words",
        segments=[
            Segment(start=0.0, end=0.5, text="old"),
            Segment(start=0.5, end=1.0, text="new"),
            Segment(start=1.0, end=1.5, text="words"),
        ],
        language="en",
        processing_time_ms=50.0
    )

    strategy = ContextStrategy(mock_backend)

    # 1 second new audio, 0.5 second context
    audio = np.zeros(16000, dtype=np.float32)
    context = np.zeros(8000, dtype=np.float32)  # 0.5 seconds

    result = strategy.transcribe(audio, 16000, context_audio=context)

    # Should only include segments after 0.5s context
    assert "new" in result.text
    assert "words" in result.text
```

**Step 2: Run integration tests**

Run: `cd whisper-streaming && pytest tests/test_integration.py -v`
Expected: PASS

---

### Task 15: Final Verification & README Update

**Step 1: Run all tests**

```bash
cd whisper-streaming && pytest tests/ -v
```

**Step 2: Update README with usage instructions**

Add to `whisper-streaming/README.md`:

```markdown
## Quick Start

### Server

```bash
cd whisper-streaming
source venv/bin/activate

# For Apple Silicon
pip install -e ".[server-mlx,dev]"
WHISPER_BACKEND=mlx WHISPER_MODEL=small python -m server.main

# For CPU/CUDA
pip install -e ".[server-faster,dev]"
WHISPER_BACKEND=faster WHISPER_MODEL=small python -m server.main
```

### Client

```bash
# In another terminal
cd whisper-streaming
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
```

**Step 3: Verify server starts**

```bash
cd whisper-streaming
WHISPER_BACKEND=mlx python -m server.main
```

**Step 4: Verify client connects**

```bash
# In another terminal
cd whisper-streaming
VAD_BACKEND=webrtc STRATEGY=prompt python -m client.main
```
