# tests/test_silero_vad.py
import sys
import pytest
import numpy as np
from unittest.mock import patch, MagicMock

# Mock torch before importing the module
mock_torch = MagicMock()
sys.modules["torch"] = mock_torch

from client.vad.silero_vad import SileroVAD
from client.vad.base import VADBackend


def test_silero_vad_inherits_base():
    with patch("client.vad.silero_vad.torch"):
        vad = SileroVAD.__new__(SileroVAD)
        vad.model = MagicMock()
        vad.get_speech_timestamps = MagicMock()
        assert isinstance(vad, VADBackend)


@patch("client.vad.silero_vad.torch")
def test_silero_vad_is_speech(mock_torch):
    mock_model = MagicMock()
    mock_model.return_value = MagicMock(item=lambda: 0.8)

    with patch.object(SileroVAD, "__init__", lambda self: None):
        vad = SileroVAD.__new__(SileroVAD)
        vad.model = mock_model
        vad.threshold = 0.5

        audio = np.zeros(512, dtype=np.float32)
        result = vad.is_speech(audio.tobytes(), 16000)

        assert result is True
