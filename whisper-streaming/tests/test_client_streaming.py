# tests/test_client_streaming.py
import pytest
import json
import base64
import numpy as np
from client.main import StreamingClient


def test_build_audio_frame_message():
    # Mock dependencies
    import sys
    from unittest.mock import MagicMock
    sys.modules['sounddevice'] = MagicMock()
    sys.modules['webrtcvad'] = MagicMock()

    from client.main import StreamingClient

    client = StreamingClient.__new__(StreamingClient)
    client.sample_rate = 16000

    audio = np.zeros(480, dtype=np.float32)
    message = client._build_audio_frame(audio)

    data = json.loads(message)
    assert data["type"] == "audio_frame"
    assert data["sample_rate"] == 16000
    assert "audio" in data


def test_build_vad_end_message():
    import sys
    from unittest.mock import MagicMock
    sys.modules['sounddevice'] = MagicMock()
    sys.modules['webrtcvad'] = MagicMock()

    from client.main import StreamingClient

    client = StreamingClient.__new__(StreamingClient)
    message = client._build_vad_end()

    data = json.loads(message)
    assert data["type"] == "vad_end"
