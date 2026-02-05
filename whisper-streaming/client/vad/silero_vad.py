# client/vad/silero_vad.py
import numpy as np
import torch
from .base import VADBackend


class SileroVAD(VADBackend):
    def __init__(self, threshold: float = 0.5):
        self.threshold = threshold
        self.model, utils = torch.hub.load(
            repo_or_dir="snakers4/silero-vad",
            model="silero_vad",
            force_reload=False
        )
        self.model.eval()

    def is_speech(self, audio_chunk: bytes, sample_rate: int) -> bool:
        """Check if audio contains speech using Silero VAD."""
        audio = np.frombuffer(audio_chunk, dtype=np.int16).astype(np.float32) / 32768.0
        tensor = torch.from_numpy(audio)

        with torch.no_grad():
            speech_prob = self.model(tensor, sample_rate).item()

        return speech_prob > self.threshold

    def reset(self) -> None:
        """Reset model state."""
        self.model.reset_states()
