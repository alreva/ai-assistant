# Client Dockerfile (Audio capture/playback for RPi)
# For ARM64 (Raspberry Pi 4/5, M1 Mac)
FROM python:3.11-slim AS builder

# Install build dependencies for audio libraries
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    libasound2-dev \
    libportaudio2 \
    libportaudiocpp0 \
    portaudio19-dev \
    && rm -rf /var/lib/apt/lists/*

# Create virtual environment
RUN python -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

# Install Python dependencies
# sounddevice for audio I/O, webrtcvad for voice activity detection
RUN pip install --no-cache-dir \
    websockets>=12.0 \
    numpy>=1.24.0 \
    sounddevice>=0.4.6 \
    webrtcvad>=2.0.10 \
    opentelemetry-api>=1.29.0 \
    opentelemetry-sdk>=1.29.0 \
    azure-monitor-opentelemetry>=1.6.4

# Production stage
FROM python:3.11-slim

# Install runtime dependencies for audio
# - libasound2: ALSA library for audio device access
# - libportaudio2: PortAudio for cross-platform audio
# - alsa-utils: Optional debugging tools (aplay, arecord)
# - pulseaudio-utils: PulseAudio client tools (pactl, paplay)
RUN apt-get update && apt-get install -y --no-install-recommends \
    libasound2 \
    libportaudio2 \
    alsa-utils \
    pulseaudio-utils \
    && rm -rf /var/lib/apt/lists/*

# Copy virtual environment from builder
COPY --from=builder /opt/venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

# Create non-root user for security
# Add to audio group for device access
RUN useradd --create-home --shell /bin/bash --groups audio client
USER client
WORKDIR /home/client/app

# Copy client source code
COPY --chown=client:client whisper-streaming/client ./client
COPY --chown=client:client whisper-streaming/pyproject.toml ./

# Environment variables with sensible defaults
# Server connections (these should be overridden at runtime)
ENV SERVER_URL=ws://stt-server:8765 \
    AGENT_URL=ws://agent:8766 \
    TTS_URL=ws://tts:8767 \
    # Audio/VAD settings
    MIN_ENERGY=0.01 \
    SILENCE_MS=1000 \
    MAX_SPEECH_MS=60000 \
    VAD_BACKEND=webrtc \
    # Client mode: batch or streaming
    CLIENT_MODE=batch \
    PAUSE_MS=400 \
    # Agent settings
    AGENT_CHARACTER= \
    AGENT_COOLDOWN_MS=1000 \
    # TTS settings
    TTS_VOICE=en-US-JennyNeural \
    # Python settings
    PYTHONUNBUFFERED=1

# Note: Audio device access requires runtime mounts:
#   --device /dev/snd (for ALSA)
#   -v /run/user/1000/pulse:/run/user/1000/pulse (for PulseAudio)
#   -e PULSE_SERVER=unix:/run/user/1000/pulse/native

# Run the client
CMD ["python", "-m", "client.main"]
