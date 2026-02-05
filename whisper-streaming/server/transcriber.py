# server/transcriber.py
from abc import ABC, abstractmethod
import numpy as np
from .backends.base import WhisperBackend, TranscriptResult


class TranscriptionStrategy(ABC):
    uses_context: bool = False
    uses_prompt: bool = False

    def __init__(self, backend: WhisperBackend):
        self.backend = backend

    @abstractmethod
    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        **kwargs
    ) -> TranscriptResult:
        pass


class PromptStrategy(TranscriptionStrategy):
    """Use previous transcript as initial prompt."""
    uses_prompt = True

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        previous_transcript: str | None = None,
        **kwargs
    ) -> TranscriptResult:
        return self.backend.transcribe(
            audio,
            sample_rate,
            initial_prompt=previous_transcript
        )


class ContextStrategy(TranscriptionStrategy):
    """Context audio overlap strategy. Session handles prepending/trimming."""
    uses_context = True

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        **kwargs
    ) -> TranscriptResult:
        return self.backend.transcribe(audio, sample_rate)


class HybridStrategy(TranscriptionStrategy):
    """Combine context audio and prompt conditioning. Session handles context."""
    uses_context = True
    uses_prompt = True

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        previous_transcript: str | None = None,
        **kwargs
    ) -> TranscriptResult:
        return self.backend.transcribe(
            audio,
            sample_rate,
            initial_prompt=previous_transcript
        )
