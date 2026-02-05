# tests/test_audio.py
import pytest
import numpy as np
from unittest.mock import patch, MagicMock
from client.audio import AudioCapture


def test_audio_capture_init():
    with patch("client.audio.sd"):
        capture = AudioCapture(sample_rate=16000, chunk_ms=30)
        assert capture.sample_rate == 16000
        assert capture.chunk_size == 480  # 16000 * 0.030


def test_audio_capture_chunk_size_calculation():
    with patch("client.audio.sd"):
        capture = AudioCapture(sample_rate=16000, chunk_ms=20)
        assert capture.chunk_size == 320  # 16000 * 0.020
