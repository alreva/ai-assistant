# server/main.py
import os
import json
import base64
import asyncio
import logging
import numpy as np
from websockets.asyncio.server import serve

from .backends import create_backend
from .backends.base import Segment
from .transcriber import PromptStrategy, ContextStrategy, HybridStrategy

# Configure logging
logging.basicConfig(
    level=os.environ.get("LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S"
)
logger = logging.getLogger(__name__)


class StreamingSession:
    """Manages per-connection streaming state."""

    def __init__(self, strategy, strategy_name: str, sample_rate: int = 16000):
        self.strategy = strategy
        self.strategy_name = strategy_name
        self.sample_rate = sample_rate
        self.audio_buffer: list[np.ndarray] = []
        self.previous_transcript = ""
        self.context_audio: np.ndarray | None = None
        self.context_overlap_ms = 1000
        self.partial_max_ms = 3000  # max audio for partial transcription

    def add_audio(self, audio: np.ndarray):
        """Add audio frame to buffer."""
        self.audio_buffer.append(audio)

    def get_buffer_duration_ms(self) -> float:
        """Get total duration of buffered audio in milliseconds."""
        if not self.audio_buffer:
            return 0
        total_samples = sum(len(chunk) for chunk in self.audio_buffer)
        return (total_samples / self.sample_rate) * 1000

    def _get_audio_for_transcription(self) -> np.ndarray:
        """Get audio array, prepending context if using context/hybrid strategy."""
        audio = np.concatenate(self.audio_buffer).flatten()

        if self.strategy_name in ("context", "hybrid") and self.context_audio is not None:
            audio = np.concatenate([self.context_audio, audio])

        return audio

    def _get_context_duration(self) -> float:
        """Get context audio duration in seconds."""
        if self.strategy_name in ("context", "hybrid") and self.context_audio is not None:
            return len(self.context_audio) / self.sample_rate
        return 0.0

    def _get_tail_audio(self, max_ms: float) -> np.ndarray:
        """Get the last max_ms of audio from the buffer."""
        max_samples = int(self.sample_rate * max_ms / 1000)
        # Walk backwards through chunks to collect enough samples
        chunks = []
        total = 0
        for chunk in reversed(self.audio_buffer):
            chunks.append(chunk)
            total += len(chunk)
            if total >= max_samples:
                break
        chunks.reverse()
        audio = np.concatenate(chunks).flatten()
        if len(audio) > max_samples:
            audio = audio[-max_samples:]
        return audio

    @staticmethod
    def _dedup_text(text: str, max_repeats: int = 3) -> str:
        """Remove repeated words/phrases caused by Whisper hallucination."""
        words = text.split()
        if len(words) <= max_repeats:
            return text
        # Scan for repetition starting at any position
        for start in range(len(words)):
            for phrase_len in range(1, min(4, (len(words) - start) // 2) + 1):
                phrase = words[start:start + phrase_len]
                count = 0
                i = start
                while i + phrase_len <= len(words):
                    if words[i:i + phrase_len] == phrase:
                        count += 1
                        i += phrase_len
                    else:
                        break
                if count > max_repeats:
                    # Truncate: keep everything before the repetition + one instance
                    return " ".join(words[:start + phrase_len])
        return text

    def get_partial(self) -> dict:
        """Transcribe current buffer and return partial result."""
        if not self.audio_buffer:
            return {"type": "partial", "text": "", "processing_time_ms": 0}

        # Use sliding window for partials to keep transcription fast
        audio = self._get_tail_audio(self.partial_max_ms)

        kwargs = {}
        if self.strategy_name in ("prompt", "hybrid") and self.previous_transcript:
            kwargs["previous_transcript"] = self.previous_transcript

        result = self.strategy.transcribe(audio, self.sample_rate, **kwargs)

        text = self._dedup_text(result.text)

        return {
            "type": "partial",
            "text": text,
            "processing_time_ms": result.processing_time_ms
        }

    def get_final(self) -> dict:
        """Transcribe, finalize segment, update state, return final result."""
        if not self.audio_buffer:
            return {"type": "final", "text": "", "segments": [], "language": "unknown", "processing_time_ms": 0}

        audio = self._get_audio_for_transcription()

        kwargs = {}
        if self.strategy_name in ("prompt", "hybrid") and self.previous_transcript:
            kwargs["previous_transcript"] = self.previous_transcript

        result = self.strategy.transcribe(audio, self.sample_rate, **kwargs)

        # For context/hybrid, filter segments after context
        segments = result.segments
        text = result.text
        if self.strategy_name in ("context", "hybrid") and self.context_audio is not None:
            context_duration = self._get_context_duration()
            segments = [
                Segment(start=s.start - context_duration, end=s.end - context_duration, text=s.text)
                for s in result.segments if s.end > context_duration
            ]
            text = " ".join(s.text for s in segments)

        # Update state for next segment
        raw_audio = np.concatenate(self.audio_buffer).flatten()
        overlap_samples = int(self.sample_rate * self.context_overlap_ms / 1000)
        if len(raw_audio) > overlap_samples:
            self.context_audio = raw_audio[-overlap_samples:]
        else:
            self.context_audio = raw_audio

        self.previous_transcript = text
        self.audio_buffer = []

        return {
            "type": "final",
            "text": text,
            "segments": [{"start": s.start, "end": s.end, "text": s.text} for s in segments],
            "language": result.language,
            "processing_time_ms": result.processing_time_ms
        }


def handle_audio_frame(message: dict, session: StreamingSession):
    """Handle incoming audio frame, add to session buffer."""
    audio_b64 = message.get("audio", "")
    audio_bytes = base64.b64decode(audio_b64)
    audio = np.frombuffer(audio_bytes, dtype=np.float32)
    session.add_audio(audio)


def handle_vad_end(session: StreamingSession) -> dict:
    """Handle VAD end signal, return final result."""
    return session.get_final()


def create_app():
    """Create and configure the WebSocket server."""
    backend_type = os.environ.get("WHISPER_BACKEND", "mlx")
    model_name = os.environ.get("WHISPER_MODEL", "small")

    logger.info(f"Loading {backend_type} backend with model '{model_name}'...")
    backend = create_backend()
    backend.load_model(model_name)

    # Warmup: run a dummy transcription to force model download and loading
    logger.info("Warming up model...")
    warmup_audio = np.zeros(16000, dtype=np.float32)  # 1 second of silence
    backend.transcribe(warmup_audio, 16000)
    logger.info("Model ready")

    strategies = {
        "prompt": PromptStrategy(backend),
        "context": ContextStrategy(backend),
        "hybrid": HybridStrategy(backend),
    }

    async def handler(websocket):
        path = websocket.request.path
        parts = path.strip("/").split("/")
        if len(parts) >= 3 and parts[0] == "ws" and parts[1] == "transcribe":
            strategy_name = parts[2]
        else:
            logger.warning(f"Invalid path: {path}")
            await websocket.close(1008, "Invalid path")
            return

        if strategy_name not in strategies:
            logger.warning(f"Unknown strategy: {strategy_name}")
            await websocket.close(1008, f"Unknown strategy: {strategy_name}")
            return

        client_addr = websocket.remote_address
        logger.info(f"Client connected: {client_addr} -> /{strategy_name}")

        strategy = strategies[strategy_name]
        session = StreamingSession(strategy=strategy, strategy_name=strategy_name)

        partial_interval_ms = int(os.environ.get("PARTIAL_INTERVAL_MS", "500"))
        last_partial_time = asyncio.get_event_loop().time()
        loop = asyncio.get_event_loop()

        try:
            async for raw_message in websocket:
                message = json.loads(raw_message)
                msg_type = message.get("type")

                if msg_type == "audio_frame":
                    handle_audio_frame(message, session)

                    # Check if we should send a partial
                    current_time = loop.time()
                    elapsed_ms = (current_time - last_partial_time) * 1000

                    if elapsed_ms >= partial_interval_ms and session.get_buffer_duration_ms() > 0:
                        logger.debug(f"[{strategy_name}] Sending partial ({session.get_buffer_duration_ms():.0f}ms audio)")
                        partial = await loop.run_in_executor(None, session.get_partial)
                        await websocket.send(json.dumps(partial))
                        last_partial_time = loop.time()

                elif msg_type == "vad_end":
                    if session.get_buffer_duration_ms() > 0:
                        logger.info(f"[{strategy_name}] VAD end, finalizing ({session.get_buffer_duration_ms():.0f}ms audio)")
                        final = await loop.run_in_executor(None, handle_vad_end, session)
                        logger.info(f"[{strategy_name}] Final ({final['processing_time_ms']:.0f}ms): {final['text'][:80]}...")
                        await websocket.send(json.dumps(final))
                    last_partial_time = loop.time()

                else:
                    logger.warning(f"Unknown message type: {msg_type}")

        except json.JSONDecodeError as e:
            logger.error(f"Invalid JSON: {e}")
        finally:
            logger.info(f"Client disconnected: {client_addr}")

    return handler, strategies


async def main():
    host = os.environ.get("HOST", "0.0.0.0")
    port = int(os.environ.get("PORT", "8765"))

    handler, _ = create_app()

    logger.info(f"Starting WebSocket server on ws://{host}:{port}")
    logger.info("Endpoints:")
    logger.info("  /ws/transcribe/prompt  - Prompt conditioning")
    logger.info("  /ws/transcribe/context - Context audio + trim")
    logger.info("  /ws/transcribe/hybrid  - Combined strategy")

    # Increase max message size to 10MB for longer audio chunks
    async with serve(handler, host, port, max_size=10 * 1024 * 1024):
        await asyncio.Future()  # run forever


if __name__ == "__main__":
    asyncio.run(main())
