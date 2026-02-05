#!/usr/bin/env python3
"""Benchmark a single model with memory tracking."""
import sys
import time
import gc
import psutil
import os
from scipy.io import wavfile

audio_path = sys.argv[1]
model_size = sys.argv[2]

# Get audio duration
sample_rate, data = wavfile.read(audio_path)
audio_duration = len(data) / sample_rate

gc.collect()
mem_before = psutil.Process(os.getpid()).memory_info().rss / 1024**2

import mlx_whisper
model_name = f"mlx-community/whisper-{model_size}-mlx"

start = time.perf_counter()
result = mlx_whisper.transcribe(audio_path, path_or_hf_repo=model_name)
elapsed = time.perf_counter() - start

mem_after = psutil.Process(os.getpid()).memory_info().rss / 1024**2

speed = audio_duration / elapsed
rtf = elapsed / audio_duration
realtime = "YES" if rtf < 1.0 else "NO"

print(f"{model_size},{speed:.1f},{rtf:.3f},{mem_after - mem_before:.0f},{mem_after:.0f},{realtime}")
