# server/main.py
import os
import re
import json
import base64
import signal
import asyncio
import logging
import numpy as np
from websockets.asyncio.server import serve
from websockets.exceptions import ConnectionClosed

from opentelemetry import trace

from .backends import create_backend

# Conditional telemetry setup
_connection_string = os.environ.get("APPLICATIONINSIGHTS_CONNECTION_STRING")
if _connection_string:
    try:
        # Prevent Azure resource detector from hanging on non-Azure machines
        os.environ.setdefault("OTEL_EXPERIMENTAL_RESOURCE_DETECTORS", "otel")
        os.environ.setdefault("OTEL_SERVICE_NAME", "stt-server")

        # Suppress Azure SDK HTTP noise before distro sends any requests
        logging.getLogger("azure").setLevel(logging.WARNING)

        # Save console handlers before distro reconfigures logging
        _root = logging.getLogger()
        _saved_handlers = list(_root.handlers)

        from azure.monitor.opentelemetry import configure_azure_monitor
        configure_azure_monitor(
            connection_string=_connection_string,
            instrumentation_options={
                "flask": {"enabled": False},
                "django": {"enabled": False},
                "fastapi": {"enabled": False},
                "psycopg2": {"enabled": False},
                "requests": {"enabled": False},
                "urllib": {"enabled": False},
                "urllib3": {"enabled": False},
            },
        )

        # Restore console handlers removed by the distro
        for _h in _saved_handlers:
            if _h not in _root.handlers:
                _root.addHandler(_h)
    except Exception as _e:
        logging.getLogger(__name__).warning(f"Azure Monitor telemetry unavailable: {_e}")

tracer = trace.get_tracer("stt-server")

# Configure logging
logging.basicConfig(
    level=os.environ.get("LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S"
)
logger = logging.getLogger(__name__)


def clean_hallucination(text: str) -> str | None:
    """Clean text by removing hallucinations. Returns None if entirely noise."""
    if not text or len(text.strip()) < 2:
        return None

    # Repeated character patterns (like లిలిలిలి or ༼ ༼ ༼)
    if re.search(r'(.)\1{5,}', text):
        return None

    # Repeated short patterns (like "लिलि" or "chool" repeated 4+ times)
    match = re.search(r'(.{2,8})\1{3,}', text)
    if match:
        # Truncate before repetition, keep if enough valid content
        truncated = text[:match.start()].strip()
        if len(truncated) >= 10:
            return truncated
        return None

    # Repeated words - find where repetition starts
    # Look for pattern: word word word word word (5+ times)
    match = re.search(r'(\b\w+\b)(?:\s+\1){4,}', text, re.IGNORECASE)
    if match:
        truncated = text[:match.start()].strip()
        if len(truncated) >= 10:
            return truncated
        return None

    # Repeated phrases like "to make to make to make"
    match = re.search(r'(\b\w+\s+\w+\b)(?:\s+\1){3,}', text, re.IGNORECASE)
    if match:
        truncated = text[:match.start()].strip()
        if len(truncated) >= 10:
            return truncated
        return None

    # Repeated sentences
    sentences = re.split(r'[.!?]+', text)
    sentences = [s.strip().lower() for s in sentences if len(s.strip()) > 10]
    if len(sentences) >= 3:
        from collections import Counter
        counts = Counter(sentences)
        most_common_count = counts.most_common(1)[0][1] if counts else 0
        if most_common_count >= 3:
            return None

    # Very high ratio of non-ASCII to ASCII (likely wrong language detection)
    ascii_chars = sum(1 for c in text if ord(c) < 128)
    if len(text) > 10 and ascii_chars / len(text) < 0.1:
        return None

    return text


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

        raw_text = result.text.strip()

        # Clean hallucinations - may truncate or return None
        text = clean_hallucination(raw_text)

        if text is None:
            logger.info(f"Filtered hallucination: {raw_text[:80]}")
            sample = raw_text[:50] + "..." if len(raw_text) > 50 else raw_text
            return {"type": "noise", "sample": sample}

        # Only update prompt with valid transcriptions
        self.previous_transcript = text

        return {
            "type": "result",
            "text": text,
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
                try:
                    message = json.loads(raw_message)
                except json.JSONDecodeError as e:
                    logger.error(f"Invalid JSON: {e}")
                    continue

                msg_type = message.get("type")
                traceparent_str = message.get("traceparent")
                session_id = message.get("session_id")

                # Extract parent context from traceparent
                parent_context = None
                if traceparent_str:
                    try:
                        parts = traceparent_str.split("-")
                        span_ctx = trace.SpanContext(
                            trace_id=int(parts[1], 16),
                            span_id=int(parts[2], 16),
                            is_remote=True,
                            trace_flags=trace.TraceFlags(int(parts[3], 16)),
                        )
                        parent_context = trace.set_span_in_context(
                            trace.NonRecordingSpan(span_ctx)
                        )
                    except (IndexError, ValueError):
                        logger.warning(f"Invalid traceparent: {traceparent_str}")

                if msg_type == "transcribe":
                    with tracer.start_as_current_span(
                        "stt-transcribe",
                        context=parent_context,
                        kind=trace.SpanKind.SERVER,
                    ) as span:
                        if session_id:
                            span.set_attribute("session.id", session_id)

                        audio_b64 = message.get("audio", "")
                        sample_rate = message.get("sample_rate", 16000)
                        audio_bytes = base64.b64decode(audio_b64)
                        audio = np.frombuffer(audio_bytes, dtype=np.float32)

                        duration_ms = len(audio) / sample_rate * 1000
                        span.set_attribute("audio.duration_ms", duration_ms)
                        span.set_attribute("audio.sample_rate", sample_rate)

                        logger.info(f"Transcribing {duration_ms:.0f}ms audio")

                        session.sample_rate = sample_rate
                        result = await loop.run_in_executor(None, session.transcribe, audio)

                        if result["type"] == "noise":
                            span.set_attribute("result.type", "noise")
                            span.add_event("hallucination-filtered")
                            logger.info("Detected noise/hallucination")
                        else:
                            span.set_attribute("result.type", "result")
                            span.set_attribute("result.text", result["text"][:200])
                            span.set_attribute("result.language", result.get("language", ""))
                            span.set_attribute("result.processing_time_ms", result.get("processing_time_ms", 0))
                            logger.info(f"Result ({result['processing_time_ms']:.0f}ms): {result['text'][:80]}...")

                        # Include traceparent in response
                        current_span = trace.get_current_span()
                        ctx = current_span.get_span_context()
                        if ctx.is_valid:
                            result["traceparent"] = f"00-{format(ctx.trace_id, '032x')}-{format(ctx.span_id, '016x')}-01"

                        try:
                            await websocket.send(json.dumps(result))
                        except ConnectionClosed:
                            break

                else:
                    logger.warning(f"Unknown message type: {msg_type}")

        except ConnectionClosed:
            pass
        finally:
            logger.info(f"Client disconnected: {client_addr}")

    return handler


async def main():
    host = os.environ.get("HOST", "0.0.0.0")
    port = int(os.environ.get("PORT", "8765"))

    handler = create_app()

    logger.info(f"Starting WebSocket server on ws://{host}:{port}")
    logger.info("Batch transcription with prompt conditioning")

    stop = asyncio.Event()

    loop = asyncio.get_event_loop()
    loop.add_signal_handler(signal.SIGINT, stop.set)
    loop.add_signal_handler(signal.SIGTERM, stop.set)

    async with serve(handler, host, port, max_size=10 * 1024 * 1024):
        await stop.wait()

    logger.info("Server stopped")


if __name__ == "__main__":
    asyncio.run(main())
