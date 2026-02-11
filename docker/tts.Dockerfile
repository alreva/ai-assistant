# TTS Service Dockerfile (Azure Speech SDK)
# Multi-stage build for Text-to-Speech service
# Works on ARM64 (M1 Mac, Raspberry Pi) and x86_64

# ==============================================================================
# Stage 1: Restore dependencies
# ==============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore

WORKDIR /src

# Copy project file for dependency restore
COPY SpeechService/SpeechService/SpeechService.csproj SpeechService/SpeechService/

# Restore dependencies
RUN dotnet restore SpeechService/SpeechService/SpeechService.csproj

# ==============================================================================
# Stage 2: Build
# ==============================================================================
FROM restore AS build

WORKDIR /src

# Copy source code
COPY SpeechService/SpeechService/ SpeechService/SpeechService/

# Build the project
RUN dotnet build SpeechService/SpeechService/SpeechService.csproj \
    -c Release \
    --no-restore

# ==============================================================================
# Stage 3: Publish
# ==============================================================================
FROM build AS publish

WORKDIR /src

# Publish the application
RUN dotnet publish SpeechService/SpeechService/SpeechService.csproj \
    -c Release \
    --no-build \
    -o /app/publish

# ==============================================================================
# Stage 4: Runtime
# ==============================================================================
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# Install Azure Speech SDK dependencies
# The SDK requires OpenSSL, ALSA (audio), and other native libraries
# Install Azure Speech SDK dependencies
# libasound2t64 is the new name in Debian trixie/sid, fallback to libasound2 for older
RUN apt-get update && \
    (apt-get install -y --no-install-recommends libasound2t64 || \
     apt-get install -y --no-install-recommends libasound2) && \
    apt-get install -y --no-install-recommends libssl3 ca-certificates && \
    rm -rf /var/lib/apt/lists/*

# Create non-root user for security
RUN useradd --create-home --shell /bin/bash tts
USER tts
WORKDIR /home/tts/app

# Copy published application
COPY --from=publish --chown=tts:tts /app/publish ./

# Environment variables
ENV SPEECH_PORT=8767
ENV AzureSpeech__Region=
ENV AzureSpeech__ApiKey=

EXPOSE 8767

# Health check - verify the process is running
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD pgrep -f SpeechService || exit 1

# Run the TTS service
CMD ["./SpeechService"]
