# tests/test_integration.py
import pytest
import json
import base64
import asyncio
import numpy as np
from unittest.mock import patch, MagicMock

from server.main import create_app, handle_transcribe, StreamingSession
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


@pytest.mark.asyncio
async def test_streaming_session_full_cycle(mock_backend):
    """Test full streaming cycle: audio frames -> partial -> vad_end -> final."""
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
