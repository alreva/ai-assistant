# client/audio.py
import queue
import logging
import numpy as np
import sounddevice as sd

logger = logging.getLogger("client.audio")


class AudioCapture:
    def __init__(self, sample_rate: int = 16000, chunk_ms: int = 30):
        self.sample_rate = sample_rate
        self.chunk_ms = chunk_ms
        self.chunk_size = int(sample_rate * chunk_ms / 1000)
        self.audio_queue: queue.Queue[np.ndarray] = queue.Queue()
        self.stream: sd.InputStream | None = None

    def _callback(self, indata, frames, time, status):
        """Called by sounddevice for each audio chunk."""
        if status:
            logger.warning(f"Audio status: {status}")
        self.audio_queue.put(indata.copy())

    def start(self):
        """Start capturing audio from microphone."""
        self.stream = sd.InputStream(
            samplerate=self.sample_rate,
            channels=1,
            dtype=np.float32,
            blocksize=self.chunk_size,
            callback=self._callback
        )
        self.stream.start()

    def stop(self):
        """Stop capturing audio."""
        if self.stream:
            self.stream.stop()
            self.stream.close()
            self.stream = None

    def get_chunk(self, timeout: float = 1.0) -> np.ndarray | None:
        """Get next audio chunk from queue."""
        try:
            return self.audio_queue.get(timeout=timeout)
        except queue.Empty:
            return None

    def __enter__(self):
        self.start()
        return self

    def __exit__(self, *args):
        self.stop()
