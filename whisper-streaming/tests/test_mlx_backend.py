# tests/test_mlx_backend.py
import sys
import pytest
import numpy as np
from unittest.mock import patch, MagicMock

# Mock mlx_whisper before importing MLXBackend
mock_mlx_whisper = MagicMock()
sys.modules["mlx_whisper"] = mock_mlx_whisper

from server.backends.mlx_backend import MLXBackend
from server.backends.base import TranscriptResult


def test_mlx_backend_inherits_base():
    backend = MLXBackend()
    assert hasattr(backend, "transcribe")
    assert hasattr(backend, "load_model")


def test_mlx_backend_transcribe():
    mock_mlx_whisper.transcribe.return_value = {
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


def test_mlx_backend_transcribe_without_model_raises():
    backend = MLXBackend()
    audio = np.zeros(16000, dtype=np.float32)

    with pytest.raises(RuntimeError, match="Model not loaded"):
        backend.transcribe(audio, 16000)
