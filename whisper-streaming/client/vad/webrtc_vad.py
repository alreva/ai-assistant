# client/vad/webrtc_vad.py
import webrtcvad
from .base import VADBackend


class WebRTCVAD(VADBackend):
    def __init__(self, aggressiveness: int = 2):
        """
        Args:
            aggressiveness: 0-3, higher = more aggressive filtering
        """
        self.vad = webrtcvad.Vad(aggressiveness)

    def is_speech(self, audio_chunk: bytes, sample_rate: int) -> bool:
        """
        Check if audio contains speech.
        Audio must be 16-bit PCM, 10/20/30ms frames.
        """
        return self.vad.is_speech(audio_chunk, sample_rate)

    def reset(self) -> None:
        """WebRTC VAD is stateless, nothing to reset."""
        pass
