# client/vad/__init__.py
import os
from .base import VADBackend


def create_vad() -> VADBackend:
    vad_type = os.environ.get("VAD_BACKEND", "webrtc")

    if vad_type == "webrtc":
        from .webrtc_vad import WebRTCVAD
        return WebRTCVAD()
    elif vad_type == "silero":
        from .silero_vad import SileroVAD
        return SileroVAD()
    else:
        raise ValueError(f"Unknown VAD backend: {vad_type}")


__all__ = ["create_vad", "VADBackend"]
