# tests/test_integration.py
import pytest
import numpy as np
from unittest.mock import MagicMock

from server.main import StreamingSession
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
async def test_streaming_session_full_cycle(mock_backend):
    """Test full streaming cycle: audio frames -> partial -> vad_end -> final."""
    from server.transcriber import PromptStrategy

    strategy = PromptStrategy(mock_backend)
    session = StreamingSession(strategy=strategy)

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


@pytest.mark.asyncio
async def test_streaming_session_context_handling(mock_backend):
    """Test that StreamingSession handles context prepending and trimming for context strategy."""
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
    session = StreamingSession(strategy=strategy)

    # First utterance
    audio = np.zeros(16000, dtype=np.float32)  # 1 second
    session.add_audio(audio)
    final1 = session.get_final()

    # Context should be saved
    assert session.context_audio is not None

    # Second utterance - session should prepend context and trim result
    audio2 = np.zeros(16000, dtype=np.float32)
    session.add_audio(audio2)
    final2 = session.get_final()

    # Verify context was used (backend received more audio than just the new chunk)
    call_args = mock_backend.transcribe.call_args
    transcribed_audio = call_args[0][0]
    assert len(transcribed_audio) > 16000  # context was prepended
