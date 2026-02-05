# tests/test_webrtc_vad.py
import sys
import pytest
from unittest.mock import patch, MagicMock

# Mock webrtcvad before importing the module
mock_webrtcvad = MagicMock()
sys.modules["webrtcvad"] = mock_webrtcvad

from client.vad.webrtc_vad import WebRTCVAD
from client.vad.base import VADBackend


def test_webrtc_vad_inherits_base():
    vad = WebRTCVAD()
    assert isinstance(vad, VADBackend)


def test_webrtc_vad_is_speech():
    mock_vad = MagicMock()
    mock_vad.is_speech.return_value = True
    mock_webrtcvad.Vad.return_value = mock_vad

    vad = WebRTCVAD(aggressiveness=2)
    result = vad.is_speech(b"\x00" * 640, 16000)

    assert result is True
    mock_vad.is_speech.assert_called_once()


def test_webrtc_vad_is_speech_returns_false():
    mock_vad = MagicMock()
    mock_vad.is_speech.return_value = False
    mock_webrtcvad.Vad.return_value = mock_vad

    vad = WebRTCVAD()
    result = vad.is_speech(b"\x00" * 320, 16000)

    assert result is False


def test_webrtc_vad_default_aggressiveness():
    mock_webrtcvad.Vad.reset_mock()
    vad = WebRTCVAD()
    mock_webrtcvad.Vad.assert_called_once_with(2)


def test_webrtc_vad_reset():
    mock_vad = MagicMock()
    mock_webrtcvad.Vad.return_value = mock_vad

    vad = WebRTCVAD()
    # reset() should not raise any errors (it's a no-op for WebRTC VAD)
    vad.reset()
    # Verify it doesn't call anything on the underlying vad
    mock_vad.reset.assert_not_called()
