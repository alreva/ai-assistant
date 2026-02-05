# server/backends/base.py
from abc import ABC, abstractmethod
from dataclasses import dataclass
import numpy as np


@dataclass
class Segment:
    start: float
    end: float
    text: str


@dataclass
class TranscriptResult:
    text: str
    segments: list[Segment]
    language: str
    processing_time_ms: float


class WhisperBackend(ABC):
    @abstractmethod
    def load_model(self, model_name: str) -> None:
        """Load the specified model."""
        pass

    @abstractmethod
    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        initial_prompt: str | None = None
    ) -> TranscriptResult:
        """Transcribe audio and return normalized result."""
        pass
