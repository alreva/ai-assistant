# tests/test_client_streaming.py
import pytest
import json
import base64
import numpy as np
import sys
from unittest.mock import MagicMock

# Mock dependencies before importing
sys.modules['sounddevice'] = MagicMock()
sys.modules['webrtcvad'] = MagicMock()

from client.main import StreamingClient, SpeechState, LatencyStats


class TestSpeechState:
    def test_initial_state(self):
        state = SpeechState()
        assert state.is_speaking is False
        assert state.silence_count == 0
        assert state.onset_count == 0
        assert state.energy_sum == 0.0
        assert state.chunk_count == 0

    def test_reset(self):
        state = SpeechState(is_speaking=True, silence_count=5, onset_count=3,
                           energy_sum=1.5, chunk_count=10)
        state.reset()
        assert state.is_speaking is False
        assert state.silence_count == 0
        assert state.onset_count == 0
        assert state.energy_sum == 0.0
        assert state.chunk_count == 0

    def test_start_speaking(self):
        state = SpeechState()
        state.start_speaking()
        assert state.is_speaking is True

    def test_add_chunk(self):
        state = SpeechState()
        state.add_chunk(0.1)
        state.add_chunk(0.2)
        assert state.chunk_count == 2
        assert state.energy_sum == pytest.approx(0.3)

    def test_avg_energy(self):
        state = SpeechState()
        state.add_chunk(0.1)
        state.add_chunk(0.3)
        assert state.avg_energy() == pytest.approx(0.2)

    def test_avg_energy_empty(self):
        state = SpeechState()
        assert state.avg_energy() == 0.0

    def test_duration_ms(self):
        state = SpeechState(chunk_count=10)
        assert state.duration_ms(chunk_ms=30) == 300


class TestLatencyStats:
    def test_empty_summary(self):
        stats = LatencyStats()
        assert stats.summary() == "No data"

    def test_record_and_summary(self):
        stats = LatencyStats()
        stats.record(100.0)
        stats.record(200.0)
        summary = stats.summary()
        assert "Transcriptions: 2" in summary
        assert "150ms" in summary


class TestStreamingClient:
    def test_build_audio_frame_message(self):
        client = StreamingClient.__new__(StreamingClient)
        client.sample_rate = 16000

        audio = np.zeros(480, dtype=np.float32)
        message = client._build_audio_frame(audio)

        data = json.loads(message)
        assert data["type"] == "audio_frame"
        assert data["sample_rate"] == 16000
        assert "audio" in data

    def test_build_vad_end_message(self):
        client = StreamingClient.__new__(StreamingClient)
        message = client._build_vad_end()

        data = json.loads(message)
        assert data["type"] == "vad_end"

    def test_should_finalize_not_speaking(self):
        client = StreamingClient.__new__(StreamingClient)
        client.silence_chunks = 10
        client.max_speech_ms = 5000
        client.chunk_ms = 30

        state = SpeechState(is_speaking=False)
        assert client._should_finalize(state, speech_detected=False) is False

    def test_should_finalize_silence_timeout(self):
        client = StreamingClient.__new__(StreamingClient)
        client.silence_chunks = 3
        client.max_speech_ms = 5000
        client.chunk_ms = 30

        state = SpeechState(is_speaking=True, silence_count=2)
        # Third silence increment triggers finalization
        assert client._should_finalize(state, speech_detected=False) is True

    def test_should_finalize_max_duration(self):
        client = StreamingClient.__new__(StreamingClient)
        client.silence_chunks = 10
        client.max_speech_ms = 300
        client.chunk_ms = 30

        state = SpeechState(is_speaking=True, chunk_count=10)  # 300ms
        assert client._should_finalize(state, speech_detected=True) is True

    def test_should_finalize_not_yet(self):
        client = StreamingClient.__new__(StreamingClient)
        client.silence_chunks = 10
        client.max_speech_ms = 5000
        client.chunk_ms = 30

        state = SpeechState(is_speaking=True, chunk_count=5, silence_count=2)
        assert client._should_finalize(state, speech_detected=True) is False
