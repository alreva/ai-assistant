#!/usr/bin/env python3
"""Transcribe audio files using mlx-whisper (Metal GPU accelerated)."""

import sys
import time
from pathlib import Path
import mlx_whisper

def get_audio_duration(audio_path: str) -> float:
    """Get audio duration using scipy."""
    from scipy.io import wavfile
    sample_rate, data = wavfile.read(audio_path)
    return len(data) / sample_rate

def transcribe(audio_path: str, model_size: str = "base"):
    """Transcribe an audio file and print results with performance metrics."""
    model_name = f"mlx-community/whisper-{model_size}-mlx"

    # Get audio duration
    audio_duration = get_audio_duration(audio_path)

    print(f"Model: {model_size} (Metal GPU accelerated)")
    print(f"File: {audio_path}")
    print(f"Audio duration: {audio_duration:.2f}s\n")

    # Time the transcription
    start_time = time.perf_counter()
    result = mlx_whisper.transcribe(audio_path, path_or_hf_repo=model_name)
    transcribe_time = time.perf_counter() - start_time

    # Calculate real-time factor (RTF)
    rtf = transcribe_time / audio_duration
    speed_multiplier = audio_duration / transcribe_time

    print(f"\nDetected language: {result.get('language', 'unknown')}")
    print("=" * 60)

    for segment in result.get("segments", []):
        start = segment["start"]
        end = segment["end"]
        text = segment["text"].strip()
        print(f"[{start:6.2f}s -> {end:6.2f}s] {text}")

    print("=" * 60)
    print("\nFull transcript:")
    print(result.get("text", "").strip())

    print("\n" + "=" * 60)
    print("PERFORMANCE METRICS")
    print("=" * 60)
    print(f"Audio duration:    {audio_duration:.2f}s")
    print(f"Transcribe time:   {transcribe_time:.2f}s")
    print(f"Speed:             {speed_multiplier:.1f}x realtime")
    print(f"RTF (lower=better): {rtf:.3f}")
    print(f"Realtime capable:  {'YES' if rtf < 1.0 else 'NO'}")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python transcribe.py <audio_file> [model_size]")
        print("Model sizes: tiny, base, small, medium, large-v3")
        sys.exit(1)

    audio_file = sys.argv[1]
    model = sys.argv[2] if len(sys.argv) > 2 else "base"

    if not Path(audio_file).exists():
        print(f"Error: File not found: {audio_file}")
        sys.exit(1)

    transcribe(audio_file, model)
