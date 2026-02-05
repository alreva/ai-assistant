# tests/test_vad_factory.py
import sys
import pytest
from unittest.mock import patch, MagicMock

# Mock the external modules before any imports
sys.modules['webrtcvad'] = MagicMock()
sys.modules['torch'] = MagicMock()

from client.vad import create_vad
from client.vad.base import VADBackend


def test_create_webrtc_vad():
    with patch.dict("os.environ", {"VAD_BACKEND": "webrtc"}):
        vad = create_vad()
        assert isinstance(vad, VADBackend)


def test_create_silero_vad():
    with patch.dict("os.environ", {"VAD_BACKEND": "silero"}):
        with patch("client.vad.silero_vad.torch") as mock_torch:
            mock_torch.hub.load.return_value = (MagicMock(), None)
            vad = create_vad()
            assert isinstance(vad, VADBackend)


def test_invalid_vad_raises():
    with patch.dict("os.environ", {"VAD_BACKEND": "invalid"}):
        with pytest.raises(ValueError, match="Unknown VAD"):
            create_vad()
