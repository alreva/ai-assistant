# server/backends/faster_backend.py
import time
import numpy as np
from faster_whisper import WhisperModel
from .base import WhisperBackend, TranscriptResult, Segment


class FasterBackend(WhisperBackend):
    def __init__(self):
        self.model: WhisperModel | None = None

    def load_model(self, model_name: str) -> None:
        self.model = WhisperModel(model_name, device="auto", compute_type="auto")

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        initial_prompt: str | None = None
    ) -> TranscriptResult:
        if self.model is None:
            raise RuntimeError("Model not loaded. Call load_model() first.")

        start_time = time.perf_counter()

        segments_gen, info = self.model.transcribe(
            audio,
            initial_prompt=initial_prompt
        )

        segments = [
            Segment(start=s.start, end=s.end, text=s.text.strip())
            for s in segments_gen
        ]

        processing_time_ms = (time.perf_counter() - start_time) * 1000

        full_text = " ".join(s.text for s in segments)

        return TranscriptResult(
            text=full_text,
            segments=segments,
            language=info.language,
            processing_time_ms=processing_time_ms
        )
