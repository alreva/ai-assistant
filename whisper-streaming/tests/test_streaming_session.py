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
