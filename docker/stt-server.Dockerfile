# STT Server Dockerfile (CPU/faster-whisper)
# For ARM64 (M1 Mac, Raspberry Pi) and x86_64
FROM python:3.11-slim AS builder

# Install build dependencies for faster-whisper
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    && rm -rf /var/lib/apt/lists/*

# Create virtual environment
RUN python -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

# Install Python dependencies
# faster-whisper includes ctranslate2 which provides CPU inference
RUN pip install --no-cache-dir \
    websockets>=12.0 \
    numpy>=1.24.0 \
    faster-whisper>=1.0.0

# Production stage
FROM python:3.11-slim

# Install runtime dependencies for faster-whisper/ctranslate2
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgomp1 \
    && rm -rf /var/lib/apt/lists/*

# Copy virtual environment from builder
COPY --from=builder /opt/venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

# Create non-root user for security
RUN useradd --create-home --shell /bin/bash stt
USER stt
WORKDIR /home/stt/app

# Copy server source code
COPY --chown=stt:stt whisper-streaming/server ./server
COPY --chown=stt:stt whisper-streaming/pyproject.toml ./

# Environment variables with sensible defaults
ENV WHISPER_BACKEND=faster \
    WHISPER_MODEL=small \
    HOST=0.0.0.0 \
    PORT=8765 \
    LOG_LEVEL=INFO \
    PYTHONUNBUFFERED=1

EXPOSE 8765

# Health check - verify WebSocket server is responding
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD python -c "import asyncio; from websockets.sync.client import connect; connect('ws://localhost:8765')" || exit 1

# Run the STT server
CMD ["python", "-m", "server.main"]
