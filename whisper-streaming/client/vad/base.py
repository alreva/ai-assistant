# client/vad/base.py
from abc import ABC, abstractmethod


class VADBackend(ABC):
    @abstractmethod
    def is_speech(self, audio_chunk: bytes, sample_rate: int) -> bool:
        """Check if audio chunk contains speech."""
        pass

    @abstractmethod
    def reset(self) -> None:
        """Reset internal state."""
        pass
