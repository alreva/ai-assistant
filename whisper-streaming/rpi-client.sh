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
#   SILENCE_MS    - Silence before utterance ends (default: 1000)
#   MAX_SPEECH_MS - Max continuous speech (default: 60000)
#   VAD_BACKEND   - VAD backend: webrtc or silero (default: webrtc)
#   CLIENT_MODE   - batch or streaming (default: batch)
#   PAUSE_MS      - Pause before sending chunk in streaming mode (default: 400)
#   AGENT_URL     - Voice agent WebSocket URL (optional, e.g., ws://localhost:8766)
#   TTS_URL       - TTS service WebSocket URL (optional, e.g., ws://localhost:8767)
#   TTS_VOICE     - Azure TTS voice name (default: en-US-JennyNeural)

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
export CLIENT_MODE="${CLIENT_MODE:-batch}"
export PAUSE_MS="${PAUSE_MS:-400}"
export AGENT_URL="${AGENT_URL:-}"  # Optional: ws://localhost:8766
export TTS_URL="${TTS_URL:-}"      # Optional: ws://localhost:8767
export TTS_VOICE="${TTS_VOICE:-en-US-JennyNeural}"

echo "Starting client..."
echo "  Server: $SERVER_URL"
echo "  Mode: $CLIENT_MODE"
echo "  Min energy: $MIN_ENERGY"
echo "  Silence: ${SILENCE_MS}ms"
if [ "$CLIENT_MODE" = "streaming" ]; then
    echo "  Pause: ${PAUSE_MS}ms"
fi
echo "  Max speech: ${MAX_SPEECH_MS}ms"
if [ -n "$AGENT_URL" ]; then
    echo "  Agent: $AGENT_URL"
fi
if [ -n "$TTS_URL" ]; then
    echo "  TTS: $TTS_URL ($TTS_VOICE)"
fi
echo ""

exec venv/bin/python -m client.main
