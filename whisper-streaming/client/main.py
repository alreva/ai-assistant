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
        self.e2e_times: list[float] = []
        self.first_times: list[float] = []

    def record(self, e2e_ms: float, first_ms: float = 0.0):
        self.e2e_times.append(e2e_ms)
        if first_ms > 0:
            self.first_times.append(first_ms)

    def summary(self) -> str:
        if not self.e2e_times:
            return "No data"
        n = len(self.e2e_times)
        avg_e2e = sum(self.e2e_times) / n
        result = f"Utterances: {n} | Avg e2e: {avg_e2e:.0f}ms"
        if self.first_times:
            avg_first = sum(self.first_times) / len(self.first_times)
            result += f" | Avg first result: {avg_first:.0f}ms"
        return result


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
        max_speech_ms: int = 60000,
        min_energy: float = 0.01,
        onset_threshold: int = 3,
        reconnect_interval: float = 5.0,
        min_speech_ms: int = 200,
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

        self.vad = create_vad()
        self.audio_capture = AudioCapture(sample_rate=sample_rate, chunk_ms=chunk_ms)
        self.latency_stats = LatencyStats()

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
        """Send audio batch and receive result. Returns (result, rtt_ms)."""
        if not self._connected or self._ws is None:
            return None, 0

        try:
            start = time.perf_counter()
            message = self._build_transcribe_message(audio)
            await self._ws.send(message)
            response = await self._ws.recv()
            rtt_ms = (time.perf_counter() - start) * 1000
            return json.loads(response), rtt_ms
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

    async def run(self):
        """Main client loop."""
        print(f"Server: {self.server_url}")
        print(f"VAD: {os.environ.get('VAD_BACKEND', 'webrtc')}")
        print(f"Min energy: {self.min_energy}")
        print(f"Silence: {self.silence_threshold_ms}ms | Max speech: {self.max_speech_ms}ms")
        print("Press Ctrl+C to stop\n")

        # Try initial connection
        if not await self._connect():
            print(f"[offline] Server not available, will retry every {self.reconnect_interval}s")
            print("[offline] Audio capture active, speech detection running\n")

        with self.audio_capture:
            state = SpeechState()

            # Start background reconnect
            reconnect_task = asyncio.create_task(self._reconnect_loop())
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
                        else:
                            print(f"[offline] Speech detected ({duration_ms:.0f}ms) - server unavailable")

                        state.reset()

            finally:
                reconnect_task.cancel()
                try:
                    await reconnect_task
                except asyncio.CancelledError:
                    pass
                if self._ws:
                    await self._ws.close()


class StreamingClient:
    """
    Streaming client that sends chunks on short pauses for faster perceived latency.

    - Short pause (pause_ms): triggers sending current chunk to server
    - Long silence (silence_ms): marks end of utterance, combines all transcriptions
    """

    def __init__(
        self,
        server_url: str,
        sample_rate: int = 16000,
        chunk_ms: int = 30,
        pause_ms: int = 400,
        silence_ms: int = 1000,
        max_speech_ms: int = 60000,
        min_energy: float = 0.01,
        onset_threshold: int = 3,
        reconnect_interval: float = 5.0,
        min_chunk_ms: int = 200,
    ):
        self.server_url = f"{server_url}/ws/transcribe"
        self.sample_rate = sample_rate
        self.chunk_ms = chunk_ms
        self.pause_ms = pause_ms
        self.silence_ms = silence_ms
        self.max_speech_ms = max_speech_ms
        self.min_energy = min_energy
        self.onset_threshold = onset_threshold
        self.reconnect_interval = reconnect_interval
        self.min_chunk_ms = min_chunk_ms

        self.pause_chunks = int(pause_ms / chunk_ms)
        self.silence_chunks = int(silence_ms / chunk_ms)

        self.vad = create_vad()
        self.audio_capture = AudioCapture(sample_rate=sample_rate, chunk_ms=chunk_ms)
        self.latency_stats = LatencyStats()

        self._ws = None
        self._connected = False

    def _build_transcribe_message(self, audio: np.ndarray) -> str:
        """Build transcribe message with audio."""
        audio_b64 = base64.b64encode(audio.astype(np.float32).tobytes()).decode()
        return json.dumps({
            "type": "transcribe",
            "audio": audio_b64,
            "sample_rate": self.sample_rate
        })

    def _detect_speech(self, chunk: np.ndarray) -> tuple[bool, float]:
        """Detect speech in chunk using VAD and energy."""
        chunk_bytes = (chunk * 32768).astype(np.int16).tobytes()
        vad_speech = self.vad.is_speech(chunk_bytes, self.sample_rate)
        energy = float(np.sqrt(np.mean(chunk ** 2)))
        return vad_speech and energy >= self.min_energy, energy

    async def _connect(self) -> bool:
        """Try to connect to server."""
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
        """Send audio and receive result."""
        if not self._connected or self._ws is None:
            return None, 0

        try:
            start = time.perf_counter()
            message = self._build_transcribe_message(audio)
            await self._ws.send(message)
            response = await self._ws.recv()
            rtt_ms = (time.perf_counter() - start) * 1000
            return json.loads(response), rtt_ms
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

    async def run(self):
        """Main client loop with streaming chunks."""
        print(f"Server: {self.server_url}")
        print(f"Mode: streaming (pause={self.pause_ms}ms, silence={self.silence_ms}ms)")
        print(f"Min energy: {self.min_energy}")
        print("Press Ctrl+C to stop\n")

        if not await self._connect():
            print(f"[offline] Server not available, will retry every {self.reconnect_interval}s\n")

        with self.audio_capture:
            # State tracking
            is_speaking = False
            onset_count = 0
            silence_count = 0
            audio_chunks: list[np.ndarray] = []
            utterance_transcripts: list[str] = []
            utterance_start_time = 0.0
            chunk_start_time = 0.0
            first_result_time = 0.0
            last_result_time = 0.0

            reconnect_task = asyncio.create_task(self._reconnect_loop())
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
                        silence_count = 0
                        if not is_speaking:
                            onset_count += 1
                            if onset_count >= self.onset_threshold:
                                is_speaking = True
                                utterance_start_time = time.perf_counter()
                                chunk_start_time = time.perf_counter()
                                utterance_transcripts = []
                    else:
                        onset_count = 0
                        if is_speaking:
                            silence_count += 1

                    # Collect audio during speech
                    if is_speaking:
                        audio_chunks.append(chunk)

                    # Check for pause (short silence) - send chunk
                    if is_speaking and silence_count >= self.pause_chunks and audio_chunks:
                        audio = np.concatenate(audio_chunks)
                        duration_ms = len(audio) / self.sample_rate * 1000

                        if duration_ms >= self.min_chunk_ms and self._connected:
                            result, rtt_ms = await self._send_and_receive(audio)
                            if result and result.get("type") != "noise":
                                text = result.get("text", "").strip()
                                if text:
                                    now = time.perf_counter()
                                    chunk_time = (now - chunk_start_time) * 1000
                                    if first_result_time == 0.0:
                                        first_result_time = now
                                    last_result_time = now
                                    utterance_transcripts.append(text)
                                    print(f"  [{chunk_time:.0f}ms] {text}")

                        audio_chunks = []
                        chunk_start_time = time.perf_counter()

                    # Check for long silence - end of utterance
                    if is_speaking and silence_count >= self.silence_chunks:
                        # Send any remaining audio
                        if audio_chunks:
                            audio = np.concatenate(audio_chunks)
                            duration_ms = len(audio) / self.sample_rate * 1000

                            if duration_ms >= self.min_chunk_ms and self._connected:
                                result, rtt_ms = await self._send_and_receive(audio)
                                if result and result.get("type") != "noise":
                                    text = result.get("text", "").strip()
                                    if text:
                                        now = time.perf_counter()
                                        chunk_time = (now - chunk_start_time) * 1000
                                        if first_result_time == 0.0:
                                            first_result_time = now
                                        last_result_time = now
                                        utterance_transcripts.append(text)
                                        print(f"  [{chunk_time:.0f}ms] {text}")

                        # Print complete utterance
                        if utterance_transcripts:
                            # e2e = speech start to last transcription received (excludes silence detection)
                            e2e_ms = (last_result_time - utterance_start_time) * 1000
                            first_ms = (first_result_time - utterance_start_time) * 1000
                            full_text = " ".join(utterance_transcripts)
                            self.latency_stats.record(e2e_ms, first_ms)
                            print(f">>> [first:{first_ms:.0f}ms e2e:{e2e_ms:.0f}ms] {full_text}\n")

                        # Reset state
                        is_speaking = False
                        onset_count = 0
                        silence_count = 0
                        audio_chunks = []
                        utterance_transcripts = []
                        first_result_time = 0.0
                        last_result_time = 0.0

                    # Max duration check
                    if is_speaking:
                        total_samples = sum(len(c) for c in audio_chunks)
                        if total_samples / self.sample_rate * 1000 >= self.max_speech_ms:
                            # Force send current chunk
                            if audio_chunks and self._connected:
                                audio = np.concatenate(audio_chunks)
                                result, rtt_ms = await self._send_and_receive(audio)
                                if result and result.get("type") != "noise":
                                    text = result.get("text", "").strip()
                                    if text:
                                        utterance_transcripts.append(text)
                                        print(f"  [max] {text}")
                            audio_chunks = []
                            chunk_start_time = time.perf_counter()

            finally:
                reconnect_task.cancel()
                try:
                    await reconnect_task
                except asyncio.CancelledError:
                    pass
                if self._ws:
                    await self._ws.close()


async def main():
    server = os.environ.get("SERVER_URL", "ws://localhost:8765")
    min_energy = float(os.environ.get("MIN_ENERGY", "0.01"))
    silence_ms = int(os.environ.get("SILENCE_MS", "1000"))
    max_speech_ms = int(os.environ.get("MAX_SPEECH_MS", "60000"))
    mode = os.environ.get("CLIENT_MODE", "batch")
    pause_ms = int(os.environ.get("PAUSE_MS", "400"))

    if mode == "streaming":
        client = StreamingClient(
            server_url=server,
            min_energy=min_energy,
            pause_ms=pause_ms,
            silence_ms=silence_ms,
            max_speech_ms=max_speech_ms,
        )
    else:
        client = BatchClient(
            server_url=server,
            min_energy=min_energy,
            silence_threshold_ms=silence_ms,
            max_speech_ms=max_speech_ms,
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
