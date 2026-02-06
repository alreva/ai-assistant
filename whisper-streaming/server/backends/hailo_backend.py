# server/backends/hailo_backend.py
"""
Hailo-10H Whisper backend for Raspberry Pi AI HAT+ 2.

Requirements:
- Raspberry Pi 5 with AI HAT+ 2 (40 TOPS, Hailo-10H)
- HailoRT >= 4.20
- Hailo DFC 5.x (for Hailo-10H)
- hailo-apps package: https://github.com/hailo-ai/hailo-apps

Setup:
1. Install HailoRT from Hailo Developer Zone
2. Clone and install hailo-apps:
   git clone https://github.com/hailo-ai/hailo-apps
   cd hailo-apps && pip install -e .
3. Download Whisper HEF files (will be auto-downloaded on first run)

Supported models: tiny, tiny.en, base, base.en
"""
import time
import numpy as np
from .base import WhisperBackend, TranscriptResult, Segment

# Lazy imports - only load when actually using Hailo
_hailo_imported = False
_HailoWhisperPipeline = None
_preprocess = None
_clean_transcription = None
_HEF_REGISTRY = None


def _import_hailo():
    """Lazy import Hailo dependencies."""
    global _hailo_imported, _HailoWhisperPipeline, _preprocess, _clean_transcription, _HEF_REGISTRY

    if _hailo_imported:
        return

    try:
        from hailo_apps.python.standalone_apps.speech_recognition.common.hailo_whisper_pipeline import (
            HailoWhisperPipeline
        )
        from hailo_apps.python.standalone_apps.speech_recognition.common.audio_utils import (
            preprocess
        )
        from hailo_apps.python.standalone_apps.speech_recognition.common.text_utils import (
            clean_transcription
        )
        from hailo_apps.python.standalone_apps.speech_recognition.common.hef_registry import (
            HEF_REGISTRY
        )

        _HailoWhisperPipeline = HailoWhisperPipeline
        _preprocess = preprocess
        _clean_transcription = clean_transcription
        _HEF_REGISTRY = HEF_REGISTRY
        _hailo_imported = True

    except ImportError as e:
        raise ImportError(
            f"Hailo dependencies not found: {e}\n"
            "Install hailo-apps: https://github.com/hailo-ai/hailo-apps\n"
            "Ensure HailoRT is installed and AI HAT+ 2 is connected."
        )


class HailoBackend(WhisperBackend):
    """Whisper backend using Hailo-10H NPU acceleration."""

    SUPPORTED_MODELS = ["tiny", "tiny.en", "base", "base.en"]
    HW_ARCH = "hailo10h"  # AI HAT+ 2 uses Hailo-10H

    def __init__(self):
        self.pipeline = None
        self.model_variant: str | None = None

    def load_model(self, model_name: str) -> None:
        """Load Whisper model onto Hailo-10H.

        Args:
            model_name: One of 'tiny', 'tiny.en', 'base', 'base.en'
        """
        _import_hailo()

        # Map common names to Hailo variants
        variant = model_name.lower().replace("whisper-", "").replace("whisper_", "")

        if variant not in self.SUPPORTED_MODELS:
            raise ValueError(
                f"Unsupported model '{model_name}'. "
                f"Hailo-10H supports: {self.SUPPORTED_MODELS}"
            )

        self.model_variant = variant

        # Get HEF file paths from registry (downloads if needed)
        encoder_hef = _HEF_REGISTRY.get_hef_path(
            f"whisper_{variant}_encoder",
            self.HW_ARCH
        )
        decoder_hef = _HEF_REGISTRY.get_hef_path(
            f"whisper_{variant}_decoder",
            self.HW_ARCH
        )

        # Initialize pipeline with encoder and decoder
        self.pipeline = _HailoWhisperPipeline(
            encoder_hef_path=encoder_hef,
            decoder_hef_path=decoder_hef,
            hw_arch=self.HW_ARCH
        )

    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        initial_prompt: str | None = None
    ) -> TranscriptResult:
        """Transcribe audio using Hailo-10H.

        Args:
            audio: Audio samples as float32 numpy array
            sample_rate: Sample rate (will be resampled to 16kHz if needed)
            initial_prompt: Optional prompt for context (may not be supported)

        Returns:
            TranscriptResult with transcription text and timing info
        """
        if self.pipeline is None:
            raise RuntimeError("Model not loaded. Call load_model() first.")

        start_time = time.perf_counter()

        # Ensure 16kHz sample rate
        if sample_rate != 16000:
            # Simple linear interpolation resampling
            duration = len(audio) / sample_rate
            target_samples = int(duration * 16000)
            indices = np.linspace(0, len(audio) - 1, target_samples)
            audio = np.interp(indices, np.arange(len(audio)), audio)

        # Ensure float32
        audio = audio.astype(np.float32)

        # Preprocess audio to mel spectrogram
        mel_spectrogram = _preprocess(audio)

        # Run inference on Hailo-10H
        self.pipeline.send_data(mel_spectrogram)
        raw_text = self.pipeline.get_transcription()

        # Clean up transcription
        text = _clean_transcription(raw_text)

        processing_time_ms = (time.perf_counter() - start_time) * 1000

        # Hailo pipeline doesn't provide word-level timestamps
        # Return single segment covering full audio
        duration = len(audio) / 16000
        segments = [Segment(start=0.0, end=duration, text=text)]

        return TranscriptResult(
            text=text,
            segments=segments,
            language="en",  # Hailo Whisper currently supports English only
            processing_time_ms=processing_time_ms
        )
