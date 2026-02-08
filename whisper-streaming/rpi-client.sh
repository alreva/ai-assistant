#!/bin/bash
# Run whisper client interactively
#
# Usage:
#   ./rpi-client.sh                    # connect to local server
#   ./rpi-client.sh ws://host:8765     # connect to remote server
#
# Environment variables:
#   SERVER_URL    - WebSocket server URL (default: ws://localhost:8765)
#   MIN_ENERGY    - Minimum energy threshold (default: 0.01)
#   SILENCE_MS    - Silence before segment ends (default: 1000)
#   MAX_SPEECH_MS - Max continuous speech (default: 60000)
#   VAD_BACKEND   - VAD backend: webrtc or silero (default: webrtc)

set -e
cd "$(dirname "$0")"

# Allow passing server URL as argument
if [ -n "$1" ]; then
    export SERVER_URL="$1"
fi

# Defaults
export SERVER_URL="${SERVER_URL:-ws://localhost:8765}"
export MIN_ENERGY="${MIN_ENERGY:-0.01}"
export SILENCE_MS="${SILENCE_MS:-1000}"
export MAX_SPEECH_MS="${MAX_SPEECH_MS:-60000}"
export VAD_BACKEND="${VAD_BACKEND:-webrtc}"

echo "Starting client..."
echo "  Server: $SERVER_URL"
echo "  Min energy: $MIN_ENERGY"
echo "  Silence: ${SILENCE_MS}ms"
echo "  Max speech: ${MAX_SPEECH_MS}ms"
echo ""

exec venv/bin/python -m client.main
