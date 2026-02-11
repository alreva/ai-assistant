# STT Server Dockerfile with Hailo-10H NPU Acceleration
# For Raspberry Pi 5 with AI HAT+ 2 (Hailo-10H, 40 TOPS)
# ARM64 only
#
# Prerequisites on host:
#   - Hailo packages installed (h10-hailort, python3-h10-hailort)
#   - /dev/hailo0 device available
#
# Runtime requirements:
#   --device /dev/hailo0:/dev/hailo0
#   -v /usr/lib/python3/dist-packages/hailo_platform:/usr/lib/python3/dist-packages/hailo_platform:ro
#   -v /usr/lib/python3/dist-packages/hailort:/usr/lib/python3/dist-packages/hailort:ro

# ==============================================================================
# Stage 1: Builder - Install Python dependencies
# ==============================================================================
# Use trixie (Debian 13) to match RPi host GLIBC version for Hailo SDK compatibility
FROM python:3.13-slim-trixie AS builder

WORKDIR /build

# Install build dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    git \
    && rm -rf /var/lib/apt/lists/*

# Create virtual environment
RUN python -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

# Install Python dependencies for STT server
RUN pip install --no-cache-dir \
    websockets>=12.0 \
    numpy>=1.24.0

# Clone and install hailo-apps (provides Whisper pipeline for Hailo)
RUN git clone --depth 1 https://github.com/hailo-ai/hailo-apps.git /opt/hailo-apps \
    && cd /opt/hailo-apps \
    && pip install --no-cache-dir -e .

# ==============================================================================
# Stage 2: Production runtime
# ==============================================================================
# Use trixie (Debian 13) to match RPi host GLIBC version for Hailo SDK compatibility
FROM python:3.13-slim-trixie AS runtime

# Install runtime dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgomp1 \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN useradd --create-home --shell /bin/bash --groups video stt

# Copy virtual environment from builder
COPY --from=builder /opt/venv /opt/venv
COPY --from=builder /opt/hailo-apps /opt/hailo-apps

# Set up Python environment
ENV PATH="/opt/venv/bin:$PATH"
ENV PYTHONPATH="/opt/hailo-apps:/usr/lib/python3/dist-packages:$PYTHONPATH"
ENV PYTHONUNBUFFERED=1

# Copy application code
WORKDIR /home/stt/app
COPY --chown=stt:stt whisper-streaming/server ./server
COPY --chown=stt:stt whisper-streaming/pyproject.toml ./

USER stt

# Environment variables
ENV WHISPER_BACKEND=hailo
ENV WHISPER_MODEL=base
ENV HOST=0.0.0.0
ENV PORT=8765
ENV LOG_LEVEL=INFO

EXPOSE 8765

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD python -c "import socket; s=socket.socket(); s.connect(('localhost', 8765)); s.close()" || exit 1

# Run the server
CMD ["python", "-m", "server.main"]
