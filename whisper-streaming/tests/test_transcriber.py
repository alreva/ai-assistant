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
