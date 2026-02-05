# server/backends/__init__.py
import os
from .base import WhisperBackend, TranscriptResult, Segment


def create_backend() -> WhisperBackend:
    backend_type = os.environ.get("WHISPER_BACKEND", "mlx")

    if backend_type == "mlx":
        from .mlx_backend import MLXBackend
        return MLXBackend()
    elif backend_type == "faster":
        from .faster_backend import FasterBackend
        return FasterBackend()
    else:
        raise ValueError(f"Unknown backend: {backend_type}")


__all__ = ["create_backend", "WhisperBackend", "TranscriptResult", "Segment"]
