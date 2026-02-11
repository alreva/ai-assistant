# client/tts.py
import json
import asyncio
import queue
import threading
import time
import logging
import numpy as np
import sounddevice as sd
import websockets

logger = logging.getLogger("client.tts")


class TtsClient:
    """Client for streaming TTS audio from the SpeechService."""

    def __init__(
        self,
        tts_url: str,
        voice: str = "en-US-JennyNeural",
        sample_rate: int = 24000,
    ):
        self.tts_url = tts_url
        self.voice = voice
        self.sample_rate = sample_rate
        self._playing = False

    async def speak(self, text: str, ssml: str | None = None,
                    traceparent: str | None = None,
                    session_id: str | None = None) -> float:
        """
        Send text to TTS service and stream audio playback.
        If ssml is provided, it will be used instead of building SSML from text.
        Returns the duration of the audio in seconds.
        """
        if not text.strip():
            return 0.0

        text_preview = text[:50] + "..." if len(text) > 50 else text
        logger.info(f"[tts] Sending: {text_preview}")

        # Queue for streaming audio to playback thread
        audio_queue: queue.Queue[bytes | None] = queue.Queue()
        total_bytes = 0
        playback_started = threading.Event()
        playback_done = threading.Event()

        def playback_thread():
            """Play audio chunks as they arrive."""
            stream = None
            try:
                stream = sd.OutputStream(
                    samplerate=self.sample_rate,
                    channels=1,
                    dtype=np.float32,
                )
                stream.start()
                playback_started.set()
                logger.info("[tts] Playback thread started")

                while True:
                    chunk = audio_queue.get()
                    if chunk is None:  # End signal
                        break
                    # Convert to float32
                    audio_data = np.frombuffer(chunk, dtype=np.int16)
                    audio_float = audio_data.astype(np.float32) / 32768.0
                    stream.write(audio_float)
            except Exception as e:
                logger.error(f"[tts] Playback error: {e}")
            finally:
                if stream:
                    stream.stop()
                    stream.close()
                logger.info("[tts] Playback thread done")
                playback_done.set()

        try:
            async with websockets.connect(self.tts_url, close_timeout=0.1) as ws:
                request_data = {
                    "text": text,
                    "voice": self.voice,
                    "output_format": "raw-24khz-16bit-mono-pcm"
                }
                if ssml:
                    request_data["ssml"] = ssml
                if traceparent:
                    request_data["traceparent"] = traceparent
                if session_id:
                    request_data["session_id"] = session_id
                request = json.dumps(request_data)
                await ws.send(request)

                # Start playback thread
                self._playing = True
                thread = threading.Thread(target=playback_thread, daemon=True)
                thread.start()

                # Wait for playback to initialize
                playback_started.wait(timeout=5)

                chunk_count = 0
                first_chunk_time = None
                import time
                start_time = time.perf_counter()

                async for message in ws:
                    if isinstance(message, bytes):
                        if len(message) == 0:
                            break
                        if chunk_count == 0:
                            first_chunk_time = time.perf_counter() - start_time
                            logger.info(f"[tts] First chunk in {first_chunk_time*1000:.0f}ms, streaming...")
                        chunk_count += 1
                        total_bytes += len(message)
                        audio_queue.put(message)
                    else:
                        try:
                            data = json.loads(message)
                            if "error" in data:
                                logger.error(f"[tts] Error: {data['error']}")
                                audio_queue.put(None)
                                return 0.0
                        except json.JSONDecodeError:
                            pass

                # Signal end of audio
                audio_queue.put(None)

                # Wait for playback to complete
                playback_done.wait(timeout=60)

                duration = total_bytes / 2 / self.sample_rate  # 16-bit = 2 bytes per sample
                logger.info(f"[tts] Done: {chunk_count} chunks, {duration:.1f}s audio")
                return duration

        except (OSError, websockets.exceptions.WebSocketException) as e:
            logger.error(f"[tts] Connection error: {e}")
            audio_queue.put(None)
            return 0.0
        except asyncio.TimeoutError:
            logger.error("[tts] Connection timeout")
            audio_queue.put(None)
            return 0.0
        finally:
            self._playing = False

    @property
    def is_playing(self) -> bool:
        return self._playing
