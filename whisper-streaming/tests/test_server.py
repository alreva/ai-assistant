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
    strategy.uses_context = False
    strategy.uses_prompt = True
    strategy.transcribe.return_value = TranscriptResult(
        text="hello",
        segments=[Segment(start=0.0, end=1.0, text="hello")],
        language="en",
        processing_time_ms=50.0
    )
    return strategy


def test_handle_audio_frame_adds_to_buffer(mock_strategy):
    session = StreamingSession(strategy=mock_strategy)
    audio = np.zeros(480, dtype=np.float32)
    audio_b64 = base64.b64encode(audio.tobytes()).decode()

    message = {"type": "audio_frame", "audio": audio_b64, "sample_rate": 16000}
    handle_audio_frame(message, session)

    assert len(session.audio_buffer) == 1


def test_handle_vad_end_returns_final(mock_strategy):
    session = StreamingSession(strategy=mock_strategy)
    audio = np.zeros(16000, dtype=np.float32)
    session.add_audio(audio)

    result = handle_vad_end(session)

    assert result["type"] == "final"
    assert result["text"] == "hello"
    assert session.audio_buffer == []
