# server/backends/mlx_backend.py
import time
import numpy as np
import mlx_whisper
from .base import WhisperBackend, TranscriptResult, Segment


class MLXBackend(WhisperBackend):
    def __init__(self):
        self.model_path: str | None = None

    def load_model(self, model_name: str) -> None:
        self.model_path = f"mlx-community/whisper-{model_name}-mlx"

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        initial_prompt: str | None = None
    ) -> TranscriptResult:
        if self.model_path is None:
            raise RuntimeError("Model not loaded. Call load_model() first.")

        start_time = time.perf_counter()

        result = mlx_whisper.transcribe(
            audio,
            path_or_hf_repo=self.model_path,
            initial_prompt=initial_prompt
        )

        processing_time_ms = (time.perf_counter() - start_time) * 1000

        segments = [
            Segment(start=s["start"], end=s["end"], text=s["text"].strip())
            for s in result.get("segments", [])
        ]

        return TranscriptResult(
            text=result.get("text", "").strip(),
            segments=segments,
            language=result.get("language", "unknown"),
            processing_time_ms=processing_time_ms
        )
