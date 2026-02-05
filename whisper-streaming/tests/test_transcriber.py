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


def test_prompt_strategy_attributes():
    """PromptStrategy uses prompt but not context."""
    strategy = PromptStrategy(MagicMock())
    assert strategy.uses_prompt is True
    assert strategy.uses_context is False


def test_context_strategy_attributes():
    """ContextStrategy uses context but not prompt."""
    strategy = ContextStrategy(MagicMock())
    assert strategy.uses_prompt is False
    assert strategy.uses_context is True


def test_hybrid_strategy_attributes():
    """HybridStrategy uses both context and prompt."""
    strategy = HybridStrategy(MagicMock())
    assert strategy.uses_prompt is True
    assert strategy.uses_context is True


def test_prompt_strategy_passes_initial_prompt(mock_backend):
    strategy = PromptStrategy(mock_backend)
    audio = np.zeros(16000, dtype=np.float32)

    result = strategy.transcribe(audio, 16000, previous_transcript="hi")

    assert result.text == "hello world"
    mock_backend.transcribe.assert_called_once()
    call_args = mock_backend.transcribe.call_args
    assert call_args.kwargs["initial_prompt"] == "hi"


def test_context_strategy_just_transcribes(mock_backend):
    """ContextStrategy just transcribes; StreamingSession handles context prepending/trimming."""
    strategy = ContextStrategy(mock_backend)
    audio = np.zeros(16000, dtype=np.float32)

    result = strategy.transcribe(audio, 16000)

    assert result.text == "hello world"
    mock_backend.transcribe.assert_called_once()


def test_hybrid_strategy_passes_initial_prompt(mock_backend):
    """HybridStrategy passes prompt; StreamingSession handles context."""
    strategy = HybridStrategy(mock_backend)
    audio = np.zeros(16000, dtype=np.float32)

    result = strategy.transcribe(audio, 16000, previous_transcript="hi")

    assert result.text == "hello world"
    mock_backend.transcribe.assert_called_once()
    call_args = mock_backend.transcribe.call_args
    assert call_args.kwargs["initial_prompt"] == "hi"
