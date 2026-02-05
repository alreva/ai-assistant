# client/main.py
import os
import sys
import json
import base64
import asyncio
import logging
import numpy as np
import websockets

logger = logging.getLogger(__name__)

from .audio import AudioCapture
from .vad import create_vad


class LatencyStats:
    """Track latency statistics."""

    def __init__(self):
        self.round_trips: list[float] = []
        self.server_times: list[float] = []

    def record(self, server_ms: float):
        self.server_times.append(server_ms)

    def summary(self) -> str:
        if not self.server_times:
            return "No data"
        n = len(self.server_times)
        avg_srv = sum(self.server_times) / n
        return f"Transcriptions: {n} | Avg server time: {avg_srv:.0f}ms"


class StreamingClient:
    def __init__(
        self,
        server_url: str,
        strategy: str = "prompt",
        sample_rate: int = 16000,
        silence_threshold_ms: int = 300,
        min_energy: float = 0.01,
    ):
        self.server_url = f"{server_url}/ws/transcribe/{strategy}"
        self.strategy = strategy
        self.sample_rate = sample_rate
        self.silence_threshold_ms = silence_threshold_ms
        self.min_energy = min_energy

        self.vad = create_vad()
        self.audio_capture = AudioCapture(sample_rate=sample_rate, chunk_ms=30)
        self.latency_stats = LatencyStats()

        self._current_partial = ""

    def _build_audio_frame(self, audio: np.ndarray) -> str:
        """Build audio_frame message."""
        audio_b64 = base64.b64encode(audio.astype(np.float32).tobytes()).decode()
        return json.dumps({
            "type": "audio_frame",
            "audio": audio_b64,
            "sample_rate": self.sample_rate
        })

    def _build_vad_end(self) -> str:
        """Build vad_end message."""
        return json.dumps({"type": "vad_end"})

    def _display_partial(self, text: str):
        """Display partial result, overwriting previous."""
        # Clear previous line and write new
        clear = "\r" + " " * (len(self._current_partial) + 20) + "\r"
        sys.stdout.write(clear)
        sys.stdout.write(f"[partial] {text}")
        sys.stdout.flush()
        self._current_partial = text

    def _display_final(self, text: str, processing_ms: float):
        """Display final result on new line."""
        # Clear partial line first
        clear = "\r" + " " * (len(self._current_partial) + 20) + "\r"
        sys.stdout.write(clear)
        print(f"[final {processing_ms:.0f}ms] {text}")
        self._current_partial = ""

    async def _handle_response(self, response: str):
        """Handle server response (partial or final)."""
        data = json.loads(response)
        msg_type = data.get("type")

        if msg_type == "partial":
            text = data.get("text", "").strip()
            if text:
                self._display_partial(text)

        elif msg_type == "final":
            text = data.get("text", "").strip()
            processing_ms = data.get("processing_time_ms", 0)
            self.latency_stats.record(processing_ms)
            if text:
                self._display_final(text, processing_ms)

    async def run(self):
        """Main client loop."""
        print(f"Connecting to {self.server_url}")
        print(f"Strategy: {self.strategy}")
        print(f"VAD: {os.environ.get('VAD_BACKEND', 'webrtc')}")
        print(f"Min energy: {self.min_energy}")
        print("Press Ctrl+C to stop\n")

        async with websockets.connect(self.server_url, max_size=10 * 1024 * 1024) as ws:
            with self.audio_capture:
                silence_count = 0
                silence_chunks = int(self.silence_threshold_ms / 30)
                is_speaking = False
                speech_onset_count = 0
                speech_onset_threshold = 3  # require 3 consecutive speech chunks (~90ms)

                async def receive_responses():
                    """Background task to receive and handle responses."""
                    try:
                        async for response in ws:
                            await self._handle_response(response)
                    except websockets.exceptions.ConnectionClosed:
                        pass

                # Start response handler
                response_task = asyncio.create_task(receive_responses())

                loop = asyncio.get_event_loop()

                speech_energy_sum = 0.0
                speech_chunk_count = 0

                try:
                    while True:
                        # Run blocking queue.get() in thread to not block event loop
                        chunk = await loop.run_in_executor(
                            None, self.audio_capture.get_chunk, 0.1
                        )
                        if chunk is None:
                            continue

                        # VAD + energy detection
                        chunk_bytes = (chunk * 32768).astype(np.int16).tobytes()
                        vad_speech = self.vad.is_speech(chunk_bytes, self.sample_rate)
                        chunk_energy = float(np.sqrt(np.mean(chunk ** 2)))
                        # Require both VAD and energy to count as speech
                        speech_detected = vad_speech and chunk_energy >= self.min_energy

                        if speech_detected:
                            silence_count = 0
                            if not is_speaking:
                                speech_onset_count += 1
                                if speech_onset_count >= speech_onset_threshold:
                                    is_speaking = True
                        else:
                            speech_onset_count = 0

                        # Only send audio frames during speech
                        if is_speaking:
                            message = self._build_audio_frame(chunk)
                            await ws.send(message)
                            speech_energy_sum += chunk_energy
                            speech_chunk_count += 1

                        if not speech_detected and is_speaking:
                            silence_count += 1
                            if silence_count >= silence_chunks:
                                # Check energy before finalizing
                                avg_energy = speech_energy_sum / max(speech_chunk_count, 1)
                                if avg_energy >= self.min_energy:
                                    await ws.send(self._build_vad_end())
                                else:
                                    logger.debug(f"[skip] Energy too low ({avg_energy:.4f} < {self.min_energy})")
                                is_speaking = False
                                silence_count = 0
                                speech_onset_count = 0
                                speech_energy_sum = 0.0
                                speech_chunk_count = 0

                finally:
                    response_task.cancel()
                    try:
                        await response_task
                    except asyncio.CancelledError:
                        pass


async def main():
    server = os.environ.get("SERVER_URL", "ws://localhost:8765")
    strategy = os.environ.get("STRATEGY", "prompt")

    min_energy = float(os.environ.get("MIN_ENERGY", "0.01"))

    client = StreamingClient(server_url=server, strategy=strategy, min_energy=min_energy)
    try:
        await client.run()
    except KeyboardInterrupt:
        pass
    finally:
        print(f"\n--- Latency Summary ---")
        print(client.latency_stats.summary())


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
