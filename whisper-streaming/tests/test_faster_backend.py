# tests/test_faster_backend.py
import sys
import pytest
import numpy as np
from unittest.mock import patch, MagicMock

# Mock faster_whisper before importing FasterBackend
mock_faster_whisper = MagicMock()
sys.modules["faster_whisper"] = mock_faster_whisper

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


@patch("server.backends.faster_backend.WhisperModel")
def test_faster_backend_transcribe_without_model_raises(mock_model_class):
    backend = FasterBackend()
    audio = np.zeros(16000, dtype=np.float32)

    with pytest.raises(RuntimeError, match="Model not loaded"):
        backend.transcribe(audio, 16000)
