# tests/test_backends_base.py
import pytest
from server.backends.base import WhisperBackend, TranscriptResult, Segment


def test_transcript_result_dataclass():
    segment = Segment(start=0.0, end=1.0, text="hello")
    result = TranscriptResult(
        text="hello",
        segments=[segment],
        language="en",
        processing_time_ms=100.0
    )
    assert result.text == "hello"
    assert result.language == "en"
    assert len(result.segments) == 1


def test_whisper_backend_is_abstract():
    with pytest.raises(TypeError):
        WhisperBackend()
