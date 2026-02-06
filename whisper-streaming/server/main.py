# server/main.py
import os
import json
import base64
import asyncio
import logging
import numpy as np
from websockets.asyncio.server import serve

from .backends import create_backend

# Configure logging
logging.basicConfig(
    level=os.environ.get("LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S"
)
logger = logging.getLogger(__name__)


class TranscriptionSession:
    """Manages per-connection state for prompt conditioning."""

    def __init__(self, backend, sample_rate: int = 16000):
        self.backend = backend
        self.sample_rate = sample_rate
        self.previous_transcript = ""

    def transcribe(self, audio: np.ndarray) -> dict:
        """Transcribe audio batch using prompt conditioning."""
        result = self.backend.transcribe(
            audio,
            self.sample_rate,
            initial_prompt=self.previous_transcript or None
        )

        # Update prompt for next transcription
        self.previous_transcript = result.text

        return {
            "type": "result",
            "text": result.text,
            "segments": [{"start": s.start, "end": s.end, "text": s.text} for s in result.segments],
            "language": result.language,
            "processing_time_ms": result.processing_time_ms
        }


def create_app():
    """Create and configure the WebSocket server."""
    backend_type = os.environ.get("WHISPER_BACKEND", "mlx")
    model_name = os.environ.get("WHISPER_MODEL", "small")

    logger.info(f"Loading {backend_type} backend with model '{model_name}'...")
    backend = create_backend()
    backend.load_model(model_name)

    logger.info("Warming up model...")
    warmup_audio = np.zeros(16000, dtype=np.float32)
    backend.transcribe(warmup_audio, 16000)
    logger.info("Model ready")

    async def handler(websocket):
        client_addr = websocket.remote_address
        logger.info(f"Client connected: {client_addr}")

        session = TranscriptionSession(backend=backend)
        loop = asyncio.get_event_loop()

        try:
            async for raw_message in websocket:
                message = json.loads(raw_message)
                msg_type = message.get("type")

                if msg_type == "transcribe":
                    # Receive complete audio batch
                    audio_b64 = message.get("audio", "")
                    sample_rate = message.get("sample_rate", 16000)
                    audio_bytes = base64.b64decode(audio_b64)
                    audio = np.frombuffer(audio_bytes, dtype=np.float32)

                    duration_ms = len(audio) / sample_rate * 1000
                    logger.info(f"Transcribing {duration_ms:.0f}ms audio")

                    session.sample_rate = sample_rate
                    result = await loop.run_in_executor(None, session.transcribe, audio)

                    logger.info(f"Result ({result['processing_time_ms']:.0f}ms): {result['text'][:80]}...")
                    await websocket.send(json.dumps(result))

                else:
                    logger.warning(f"Unknown message type: {msg_type}")

        except json.JSONDecodeError as e:
            logger.error(f"Invalid JSON: {e}")
        finally:
            logger.info(f"Client disconnected: {client_addr}")

    return handler


async def main():
    host = os.environ.get("HOST", "0.0.0.0")
    port = int(os.environ.get("PORT", "8765"))

    handler = create_app()

    logger.info(f"Starting WebSocket server on ws://{host}:{port}")
    logger.info("Batch transcription with prompt conditioning")

    async with serve(handler, host, port, max_size=10 * 1024 * 1024):
        await asyncio.Future()


if __name__ == "__main__":
    asyncio.run(main())
