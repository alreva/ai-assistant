#!/usr/bin/env python3
"""Benchmark whisper models with memory tracking."""

import sys
import time
import gc
import psutil
import os
from scipy.io import wavfile

def get_memory_mb():
    """Get current process memory in MB."""
    process = psutil.Process(os.getpid())
    return process.memory_info().rss / 1024 / 1024

def benchmark_model(audio_path: str, model_size: str):
    """Benchmark a single model."""
    import mlx_whisper

    gc.collect()
    mem_before = get_memory_mb()

    model_name = f"mlx-community/whisper-{model_size}-mlx"

    # Time transcription
    start = time.perf_counter()
    result = mlx_whisper.transcribe(audio_path, path_or_hf_repo=model_name)
    elapsed = time.perf_counter() - start

    mem_after = get_memory_mb()
    mem_used = mem_after - mem_before

    return elapsed, mem_before, mem_after, mem_used

def main():
    audio_path = sys.argv[1] if len(sys.argv) > 1 else "test.wav"
    models = sys.argv[2].split(",") if len(sys.argv) > 2 else ["tiny", "base", "small", "medium"]

    # Get audio duration
    sample_rate, data = wavfile.read(audio_path)
    audio_duration = len(data) / sample_rate

    print(f"Audio: {audio_path} ({audio_duration:.2f}s)")
    print(f"System RAM: {psutil.virtual_memory().total / 1024**3:.1f} GB")
    print()
    print(f"{'Model':<10} {'Speed':>8} {'RTF':>8} {'Mem Used':>10} {'Peak Mem':>10} {'Realtime':>10}")
    print("=" * 66)

    for model in models:
        try:
            elapsed, mem_before, mem_after, mem_used = benchmark_model(audio_path, model)
            speed = audio_duration / elapsed
            rtf = elapsed / audio_duration
            realtime = "YES" if rtf < 1.0 else "NO"

            print(f"{model:<10} {speed:>7.1f}x {rtf:>8.3f} {mem_used:>9.0f}MB {mem_after:>9.0f}MB {realtime:>10}")
        except Exception as e:
            print(f"{model:<10} ERROR: {e}")

    print("=" * 66)
    print("\nNote: Memory shows process RSS. Actual GPU/unified memory may differ.")
    print("Run each model in separate process for accurate peak measurement.")

if __name__ == "__main__":
    main()
