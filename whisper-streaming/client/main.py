# client/main.py
import os
import sys
import json
import time
import base64
import asyncio
import numpy as np
import websockets
from dataclasses import dataclass, field

from .audio import AudioCapture
from .vad import create_vad


class LatencyStats:
    """Track latency statistics."""

    def __init__(self):
        self.server_times: list[float] = []

    def record(self, server_ms: float):
        self.server_times.append(server_ms)

    def summary(self) -> str:
        if not self.server_times:
            return "No data"
        n = len(self.server_times)
        avg_srv = sum(self.server_times) / n
        return f"Transcriptions: {n} | Avg server time: {avg_srv:.0f}ms"


class UtteranceAccumulator:
    """Accumulates transcriptions into complete utterances for LLM."""

    def __init__(self, utterance_silence_ms: int = 5000):
        self.utterance_silence_ms = utterance_silence_ms
        self.transcriptions: list[str] = []
        self.last_transcription_time: float = 0

    def add(self, text: str) -> str | None:
        """Add transcription. Returns complete utterance if silence threshold reached."""
        now = time.perf_counter()

        # Check if we should emit previous utterance first
        utterance = None
        if self.transcriptions and self.last_transcription_time > 0:
            elapsed_ms = (now - self.last_transcription_time) * 1000
            if elapsed_ms >= self.utterance_silence_ms:
                utterance = self._emit()

        # Add new transcription
        if text:
            self.transcriptions.append(text)
            self.last_transcription_time = now

        return utterance

    def check_timeout(self) -> str | None:
        """Check if utterance should be emitted due to silence. Call periodically."""
        if not self.transcriptions or self.last_transcription_time == 0:
            return None

        elapsed_ms = (time.perf_counter() - self.last_transcription_time) * 1000
        if elapsed_ms >= self.utterance_silence_ms:
            return self._emit()
        return None

    def _emit(self) -> str:
        """Emit accumulated utterance and reset."""
        utterance = " ".join(self.transcriptions)
        self.transcriptions = []
        self.last_transcription_time = 0
        return utterance


@dataclass
class SpeechState:
    """Tracks speech detection state."""
    is_speaking: bool = False
    silence_count: int = 0
    onset_count: int = 0
    audio_chunks: list = field(default_factory=list)
    energy_sum: float = 0.0
    energy_count: int = 0
    speech_start_time: float = 0.0

    def reset(self):
        """Reset all state after finalization."""
        self.is_speaking = False
        self.silence_count = 0
        self.onset_count = 0
        self.audio_chunks = []
        self.energy_sum = 0.0
        self.energy_count = 0
        self.speech_start_time = 0.0

    def start_speaking(self):
        """Transition to speaking state."""
        self.is_speaking = True
        self.speech_start_time = time.perf_counter()

    def add_chunk(self, chunk: np.ndarray, energy: float):
        """Add audio chunk to buffer."""
        self.audio_chunks.append(chunk)
        self.energy_sum += energy
        self.energy_count += 1

    def get_audio(self) -> np.ndarray:
        """Get concatenated audio."""
        if not self.audio_chunks:
            return np.array([], dtype=np.float32)
        return np.concatenate(self.audio_chunks)

    def avg_energy(self) -> float:
        """Get average energy of speech chunks."""
        return self.energy_sum / max(self.energy_count, 1)

    def duration_ms(self, sample_rate: int) -> float:
        """Get speech duration in milliseconds."""
        total_samples = sum(len(c) for c in self.audio_chunks)
        return total_samples / sample_rate * 1000


class BatchClient:
    def __init__(
        self,
        server_url: str,
        sample_rate: int = 16000,
        chunk_ms: int = 30,
        silence_threshold_ms: int = 1000,
        max_speech_ms: int = 5000,
        min_energy: float = 0.01,
        onset_threshold: int = 3,
        reconnect_interval: float = 5.0,
        min_speech_ms: int = 200,
        utterance_silence_ms: int = 5000,
    ):
        self.server_url = f"{server_url}/ws/transcribe"
        self.sample_rate = sample_rate
        self.chunk_ms = chunk_ms
        self.silence_threshold_ms = silence_threshold_ms
        self.max_speech_ms = max_speech_ms
        self.min_energy = min_energy
        self.onset_threshold = onset_threshold
        self.silence_chunks = int(silence_threshold_ms / chunk_ms)
        self.reconnect_interval = reconnect_interval
        self.min_speech_ms = min_speech_ms
        self.utterance_silence_ms = utterance_silence_ms

        self.vad = create_vad()
        self.audio_capture = AudioCapture(sample_rate=sample_rate, chunk_ms=chunk_ms)
        self.latency_stats = LatencyStats()
        self.accumulator = UtteranceAccumulator(utterance_silence_ms=utterance_silence_ms)

        self._ws = None
        self._connected = False

    def _build_transcribe_message(self, audio: np.ndarray) -> str:
        """Build transcribe message with audio batch."""
        audio_b64 = base64.b64encode(audio.astype(np.float32).tobytes()).decode()
        return json.dumps({
            "type": "transcribe",
            "audio": audio_b64,
            "sample_rate": self.sample_rate
        })

    def _detect_speech(self, chunk: np.ndarray) -> tuple[bool, float]:
        """Detect speech in chunk using VAD and energy. Returns (is_speech, energy)."""
        chunk_bytes = (chunk * 32768).astype(np.int16).tobytes()
        vad_speech = self.vad.is_speech(chunk_bytes, self.sample_rate)
        energy = float(np.sqrt(np.mean(chunk ** 2)))
        return vad_speech and energy >= self.min_energy, energy

    def _should_finalize(self, state: SpeechState, speech_detected: bool) -> bool:
        """Determine if we should finalize the current speech segment."""
        if not state.is_speaking:
            return False

        # Silence timeout
        if not speech_detected:
            state.silence_count += 1
            if state.silence_count >= self.silence_chunks:
                return True

        # Max duration reached
        if state.duration_ms(self.sample_rate) >= self.max_speech_ms:
            return True

        return False

    async def _connect(self) -> bool:
        """Try to connect to server. Returns True on success."""
        try:
            self._ws = await websockets.connect(
                self.server_url,
                max_size=10 * 1024 * 1024,
                close_timeout=2
            )
            self._connected = True
            print("[connected] Server connected")
            return True
        except (OSError, websockets.exceptions.WebSocketException):
            self._connected = False
            self._ws = None
            return False

    async def _send_and_receive(self, audio: np.ndarray) -> tuple[dict | None, float]:
        """Send audio batch and receive result. Returns (result, e2e_ms)."""
        if not self._connected or self._ws is None:
            return None, 0

        try:
            start = time.perf_counter()
            message = self._build_transcribe_message(audio)
            await self._ws.send(message)
            response = await self._ws.recv()
            e2e_ms = (time.perf_counter() - start) * 1000
            return json.loads(response), e2e_ms
        except websockets.exceptions.ConnectionClosed:
            self._connected = False
            print("\n[disconnected] Server connection lost")
            return None, 0

    async def _reconnect_loop(self):
        """Background task to reconnect when disconnected."""
        while True:
            if not self._connected:
                await self._connect()
                if not self._connected:
                    await asyncio.sleep(self.reconnect_interval)
            else:
                await asyncio.sleep(1)

    async def _utterance_check_loop(self):
        """Background task to check for utterance timeout."""
        while True:
            await asyncio.sleep(0.5)
            utterance = self.accumulator.check_timeout()
            if utterance:
                print(f"\n[utterance] {utterance}\n")

    async def run(self):
        """Main client loop."""
        print(f"Server: {self.server_url}")
        print(f"VAD: {os.environ.get('VAD_BACKEND', 'webrtc')}")
        print(f"Min energy: {self.min_energy}")
        print(f"Silence: {self.silence_threshold_ms}ms | Utterance: {self.utterance_silence_ms}ms")
        print("Press Ctrl+C to stop\n")

        # Try initial connection
        if not await self._connect():
            print(f"[offline] Server not available, will retry every {self.reconnect_interval}s")
            print("[offline] Audio capture active, speech detection running\n")

        with self.audio_capture:
            state = SpeechState()

            # Start background tasks
            reconnect_task = asyncio.create_task(self._reconnect_loop())
            utterance_task = asyncio.create_task(self._utterance_check_loop())
            loop = asyncio.get_event_loop()

            try:
                while True:
                    chunk = await loop.run_in_executor(
                        None, self.audio_capture.get_chunk, 0.1
                    )
                    if chunk is None:
                        continue

                    speech_detected, energy = self._detect_speech(chunk)

                    # Handle speech onset (debounce)
                    if speech_detected:
                        state.silence_count = 0
                        if not state.is_speaking:
                            state.onset_count += 1
                            if state.onset_count >= self.onset_threshold:
                                state.start_speaking()
                    else:
                        state.onset_count = 0

                    # Collect audio during speech
                    if state.is_speaking:
                        state.add_chunk(chunk, energy)

                    # Check for finalization
                    if self._should_finalize(state, speech_detected):
                        audio = state.get_audio()
                        duration_ms = state.duration_ms(self.sample_rate)
                        avg_energy = state.avg_energy()

                        # Skip if too short or too quiet (likely noise)
                        if duration_ms < self.min_speech_ms or avg_energy < self.min_energy:
                            state.reset()
                            continue

                        if self._connected:
                            result, rtt_ms = await self._send_and_receive(audio)
                            if result:
                                total_ms = (time.perf_counter() - state.speech_start_time) * 1000
                                if result.get("type") == "noise":
                                    sample = result.get("sample", "")
                                    print(f"[noise] {sample}")
                                else:
                                    text = result.get("text", "").strip()
                                    self.latency_stats.record(total_ms)
                                    if text:
                                        print(f"[e2e:{total_ms:.0f}ms rtt:{rtt_ms:.0f}ms] {text}")
                                        # Add to utterance accumulator
                                        prev_utterance = self.accumulator.add(text)
                                        if prev_utterance:
                                            print(f"\n[utterance] {prev_utterance}\n")
                        else:
                            print(f"[offline] Speech detected ({duration_ms:.0f}ms) - server unavailable")

                        state.reset()

            finally:
                reconnect_task.cancel()
                utterance_task.cancel()
                try:
                    await reconnect_task
                except asyncio.CancelledError:
                    pass
                try:
                    await utterance_task
                except asyncio.CancelledError:
                    pass
                if self._ws:
                    await self._ws.close()


async def main():
    server = os.environ.get("SERVER_URL", "ws://localhost:8765")
    min_energy = float(os.environ.get("MIN_ENERGY", "0.01"))
    silence_ms = int(os.environ.get("SILENCE_MS", "1000"))
    utterance_ms = int(os.environ.get("UTTERANCE_MS", "5000"))

    client = BatchClient(
        server_url=server,
        min_energy=min_energy,
        silence_threshold_ms=silence_ms,
        utterance_silence_ms=utterance_ms
    )
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
