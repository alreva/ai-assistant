# server/transcriber.py
from abc import ABC, abstractmethod
import numpy as np
from .backends.base import WhisperBackend, TranscriptResult, Segment


class TranscriptionStrategy(ABC):
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
    """Prepend context audio, trim result to new portion."""

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        context_audio: np.ndarray | None = None,
        **kwargs
    ) -> TranscriptResult:
        if context_audio is not None:
            combined = np.concatenate([context_audio, audio])
            context_duration = len(context_audio) / sample_rate
        else:
            combined = audio
            context_duration = 0.0

        result = self.backend.transcribe(combined, sample_rate)

        # Filter segments to only include those after context
        new_segments = [
            Segment(
                start=s.start - context_duration,
                end=s.end - context_duration,
                text=s.text
            )
            for s in result.segments
            if s.end > context_duration
        ]

        new_text = " ".join(s.text for s in new_segments)

        return TranscriptResult(
            text=new_text,
            segments=new_segments,
            language=result.language,
            processing_time_ms=result.processing_time_ms
        )


class HybridStrategy(TranscriptionStrategy):
    """Combine context audio and prompt conditioning."""

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        context_audio: np.ndarray | None = None,
        previous_transcript: str | None = None,
        **kwargs
    ) -> TranscriptResult:
        if context_audio is not None:
            combined = np.concatenate([context_audio, audio])
            context_duration = len(context_audio) / sample_rate
        else:
            combined = audio
            context_duration = 0.0

        result = self.backend.transcribe(
            combined,
            sample_rate,
            initial_prompt=previous_transcript
        )

        # Filter segments to only include those after context
        new_segments = [
            Segment(
                start=s.start - context_duration,
                end=s.end - context_duration,
                text=s.text
            )
            for s in result.segments
            if s.end > context_duration
        ]

        new_text = " ".join(s.text for s in new_segments)

        return TranscriptResult(
            text=new_text,
            segments=new_segments,
            language=result.language,
            processing_time_ms=result.processing_time_ms
        )
