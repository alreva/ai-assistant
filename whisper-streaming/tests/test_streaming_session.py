# tests/test_streaming_session.py
import pytest
import numpy as np
from unittest.mock import MagicMock
from server.main import StreamingSession
from server.backends.base import TranscriptResult, Segment


@pytest.fixture
def mock_strategy():
    strategy = MagicMock()
    strategy.uses_context = False
    strategy.uses_prompt = True
    strategy.transcribe.return_value = TranscriptResult(
        text="hello world",
        segments=[Segment(start=0.0, end=1.0, text="hello world")],
        language="en",
        processing_time_ms=100.0
    )
    return strategy


def test_streaming_session_init(mock_strategy):
    session = StreamingSession(strategy=mock_strategy)
    assert session.audio_buffer == []
    assert session.previous_transcript == ""
    assert session.context_audio is None


def test_streaming_session_add_audio(mock_strategy):
    session = StreamingSession(strategy=mock_strategy)
    audio = np.zeros(480, dtype=np.float32)  # 30ms at 16kHz
    session.add_audio(audio)
    assert len(session.audio_buffer) == 1


def test_streaming_session_get_partial(mock_strategy):
    session = StreamingSession(strategy=mock_strategy)
    audio = np.zeros(16000, dtype=np.float32)  # 1 second
    session.add_audio(audio)

    result = session.get_partial()

    assert result["type"] == "partial"
    assert result["text"] == "hello world"
    assert "processing_time_ms" in result


def test_streaming_session_get_final(mock_strategy):
    session = StreamingSession(strategy=mock_strategy)
    audio = np.zeros(16000, dtype=np.float32)
    session.add_audio(audio)

    result = session.get_final()

    assert result["type"] == "final"
    assert result["text"] == "hello world"
    assert "segments" in result
    assert session.audio_buffer == []  # cleared after final
    assert session.previous_transcript == "hello world"


def test_streaming_session_uses_prompt_when_strategy_supports_it(mock_strategy):
    """Verify previous_transcript is passed when strategy.uses_prompt is True."""
    session = StreamingSession(strategy=mock_strategy)
    session.previous_transcript = "previous text"
    audio = np.zeros(16000, dtype=np.float32)
    session.add_audio(audio)

    session.get_partial()

    call_kwargs = mock_strategy.transcribe.call_args.kwargs
    assert call_kwargs["previous_transcript"] == "previous text"


def test_streaming_session_context_handling():
    """Verify context audio is prepended when strategy.uses_context is True."""
    strategy = MagicMock()
    strategy.uses_context = True
    strategy.uses_prompt = False
    strategy.transcribe.return_value = TranscriptResult(
        text="hello world",
        segments=[Segment(start=0.0, end=1.0, text="hello world")],
        language="en",
        processing_time_ms=100.0
    )

    session = StreamingSession(strategy=strategy)

    # First utterance
    audio1 = np.ones(16000, dtype=np.float32)
    session.add_audio(audio1)
    session.get_final()

    # Context should be saved
    assert session.context_audio is not None

    # Second utterance
    audio2 = np.ones(16000, dtype=np.float32) * 2
    session.add_audio(audio2)
    session.get_final()

    # Backend should receive audio longer than just the new chunk (context prepended)
    call_args = strategy.transcribe.call_args[0]
    transcribed_audio = call_args[0]
    assert len(transcribed_audio) > 16000
