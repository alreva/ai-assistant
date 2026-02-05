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
