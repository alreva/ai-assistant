# tests/test_backend_factory.py
import sys
import pytest
from unittest.mock import patch, MagicMock

# Mock the external dependencies before any backend imports
mock_mlx_whisper = MagicMock()
mock_faster_whisper = MagicMock()
sys.modules["mlx_whisper"] = mock_mlx_whisper
sys.modules["faster_whisper"] = mock_faster_whisper

from server.backends import create_backend
from server.backends.base import WhisperBackend


def test_create_mlx_backend():
    with patch.dict("os.environ", {"WHISPER_BACKEND": "mlx"}):
        backend = create_backend()
        assert isinstance(backend, WhisperBackend)


def test_create_faster_backend():
    with patch.dict("os.environ", {"WHISPER_BACKEND": "faster"}):
        backend = create_backend()
        assert isinstance(backend, WhisperBackend)


def test_invalid_backend_raises():
    with patch.dict("os.environ", {"WHISPER_BACKEND": "invalid"}):
        with pytest.raises(ValueError, match="Unknown backend"):
            create_backend()


def test_default_backend_is_mlx():
    """Test that mlx is the default backend when WHISPER_BACKEND is not set."""
    with patch.dict("os.environ", {}, clear=True):
        # Ensure WHISPER_BACKEND is not set
        import os
        os.environ.pop("WHISPER_BACKEND", None)
        backend = create_backend()
        assert isinstance(backend, WhisperBackend)
        # Verify it's actually an MLXBackend by checking its type name
        assert backend.__class__.__name__ == "MLXBackend"
