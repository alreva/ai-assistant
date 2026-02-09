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
        from hailo_apps.python.standalone_apps.speech_recognition.app.hailo_whisper_pipeline import (
            HailoWhisperPipeline
        )
        from hailo_apps.python.standalone_apps.speech_recognition.common.preprocessing import (
            preprocess
        )
        from hailo_apps.python.standalone_apps.speech_recognition.common.postprocessing import (
            clean_transcription
        )
        from hailo_apps.python.standalone_apps.speech_recognition.app.whisper_hef_registry import (
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

        # Get HEF file paths from registry
        encoder_hef = _HEF_REGISTRY[variant][self.HW_ARCH]["encoder"]
        decoder_hef = _HEF_REGISTRY[variant][self.HW_ARCH]["decoder"]

        # Initialize pipeline with encoder and decoder
        self.pipeline = _HailoWhisperPipeline(
            encoder_model_path=encoder_hef,
            decoder_model_path=decoder_hef,
            variant=variant
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
            initial_prompt: Optional prompt for context (not supported by Hailo)

        Returns:
            TranscriptResult with transcription text and timing info
        """
        if self.pipeline is None:
            raise RuntimeError("Model not loaded. Call load_model() first.")

        start_time = time.perf_counter()

        # Ensure 16kHz sample rate
        if sample_rate != 16000:
            # Simple linear interpolation resampling
            audio_duration = len(audio) / sample_rate
            target_samples = int(audio_duration * 16000)
            indices = np.linspace(0, len(audio) - 1, target_samples)
            audio = np.interp(indices, np.arange(len(audio)), audio)

        # Ensure float32
        audio = audio.astype(np.float32)

        # Get audio input length from pipeline (typically 10 seconds)
        chunk_length = self.pipeline.get_model_input_audio_length()

        # Preprocess audio to mel spectrograms
        # is_nhwc=True is required for Hailo pipeline
        mel_spectrograms = _preprocess(audio, is_nhwc=True, chunk_length=chunk_length)

        # Run inference on Hailo-10H for each chunk
        transcriptions = []
        for mel in mel_spectrograms:
            self.pipeline.send_data(mel)
            raw_text = self.pipeline.get_transcription()
            cleaned = _clean_transcription(raw_text)
            if cleaned:
                transcriptions.append(cleaned)

        text = " ".join(transcriptions)
        processing_time_ms = (time.perf_counter() - start_time) * 1000

        # Hailo pipeline doesn't provide word-level timestamps
        # Return single segment covering full audio
        audio_duration = len(audio) / 16000
        segments = [Segment(start=0.0, end=audio_duration, text=text)]

        return TranscriptResult(
            text=text,
            segments=segments,
            language="en",  # Hailo Whisper currently supports English only
            processing_time_ms=processing_time_ms
        )
