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

from client.main import StreamingClient, BatchClient, SpeechState, LatencyStats


class TestSpeechState:
    def test_initial_state(self):
        state = SpeechState()
        assert state.is_speaking is False
        assert state.silence_count == 0
        assert state.onset_count == 0
        assert state.energy_sum == 0.0
        assert state.energy_count == 0
        assert state.audio_chunks == []

    def test_reset(self):
        state = SpeechState()
        state.is_speaking = True
        state.silence_count = 5
        state.onset_count = 3
        state.energy_sum = 1.5
        state.energy_count = 10
        state.audio_chunks = [np.zeros(480, dtype=np.float32)]
        state.reset()
        assert state.is_speaking is False
        assert state.silence_count == 0
        assert state.onset_count == 0
        assert state.energy_sum == 0.0
        assert state.energy_count == 0
        assert state.audio_chunks == []

    def test_start_speaking(self):
        state = SpeechState()
        state.start_speaking()
        assert state.is_speaking is True

    def test_add_chunk(self):
        state = SpeechState()
        chunk1 = np.zeros(480, dtype=np.float32)
        chunk2 = np.ones(480, dtype=np.float32)
        state.add_chunk(chunk1, 0.1)
        state.add_chunk(chunk2, 0.2)
        assert state.energy_count == 2
        assert len(state.audio_chunks) == 2
        assert state.energy_sum == pytest.approx(0.3)

    def test_avg_energy(self):
        state = SpeechState()
        state.add_chunk(np.zeros(480, dtype=np.float32), 0.1)
        state.add_chunk(np.zeros(480, dtype=np.float32), 0.3)
        assert state.avg_energy() == pytest.approx(0.2)

    def test_avg_energy_empty(self):
        state = SpeechState()
        assert state.avg_energy() == 0.0

    def test_duration_ms(self):
        state = SpeechState()
        # 10 chunks of 480 samples at 16000 Hz = 300ms
        for _ in range(10):
            state.add_chunk(np.zeros(480, dtype=np.float32), 0.1)
        assert state.duration_ms(sample_rate=16000) == pytest.approx(300.0)

    def test_get_audio(self):
        state = SpeechState()
        state.add_chunk(np.ones(480, dtype=np.float32), 0.1)
        state.add_chunk(np.ones(480, dtype=np.float32) * 2, 0.2)
        audio = state.get_audio()
        assert len(audio) == 960

    def test_get_audio_empty(self):
        state = SpeechState()
        audio = state.get_audio()
        assert len(audio) == 0


class TestLatencyStats:
    def test_empty_summary(self):
        stats = LatencyStats()
        assert stats.summary() == "No data"

    def test_record_and_summary(self):
        stats = LatencyStats()
        stats.record(100.0)
        stats.record(200.0)
        summary = stats.summary()
        assert "Utterances: 2" in summary
        assert "150" in summary

    def test_record_with_first_time(self):
        stats = LatencyStats()
        stats.record(100.0, first_ms=50.0)
        stats.record(200.0, first_ms=80.0)
        summary = stats.summary()
        assert "Avg first result" in summary


class TestBatchClient:
    def test_build_transcribe_message(self):
        client = BatchClient.__new__(BatchClient)
        client.sample_rate = 16000
        client.agent_client = None

        audio = np.zeros(480, dtype=np.float32)
        message = client._build_transcribe_message(audio)

        data = json.loads(message)
        assert data["type"] == "transcribe"
        assert data["sample_rate"] == 16000
        assert "audio" in data

    def test_should_finalize_not_speaking(self):
        client = BatchClient.__new__(BatchClient)
        client.silence_chunks = 10
        client.max_speech_ms = 5000

        state = SpeechState()
        assert client._should_finalize(state, speech_detected=False) is False

    def test_should_finalize_silence_timeout(self):
        client = BatchClient.__new__(BatchClient)
        client.silence_chunks = 3
        client.max_speech_ms = 5000

        state = SpeechState()
        state.is_speaking = True
        state.silence_count = 2
        # Third silence increment happens inside _should_finalize
        assert client._should_finalize(state, speech_detected=False) is True

    def test_should_finalize_max_duration(self):
        client = BatchClient.__new__(BatchClient)
        client.silence_chunks = 10
        client.max_speech_ms = 300
        client.sample_rate = 16000

        state = SpeechState()
        state.is_speaking = True
        # 10 chunks of 480 samples at 16000 Hz = 300ms
        for _ in range(10):
            state.add_chunk(np.zeros(480, dtype=np.float32), 0.1)
        assert client._should_finalize(state, speech_detected=True) is True

    def test_should_finalize_not_yet(self):
        client = BatchClient.__new__(BatchClient)
        client.silence_chunks = 10
        client.max_speech_ms = 5000
        client.sample_rate = 16000

        state = SpeechState()
        state.is_speaking = True
        state.silence_count = 2
        for _ in range(5):
            state.add_chunk(np.zeros(480, dtype=np.float32), 0.1)
        assert client._should_finalize(state, speech_detected=True) is False


class TestStreamingClient:
    def test_build_transcribe_message(self):
        client = StreamingClient.__new__(StreamingClient)
        client.sample_rate = 16000
        client.agent_client = None

        audio = np.zeros(480, dtype=np.float32)
        message = client._build_transcribe_message(audio)

        data = json.loads(message)
        assert data["type"] == "transcribe"
        assert data["sample_rate"] == 16000
        assert "audio" in data
