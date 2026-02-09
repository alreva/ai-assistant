#!/bin/bash
# Run the TTS service with required environment variables
#
# Prerequisites:
#   - Set Azure Speech credentials (or source env.sh)
#
# Usage:
#   ./run-tts.sh                    # run in foreground
#   ./run-tts.sh --background       # run in background

set -e
cd "$(dirname "$0")"

# Defaults
export SPEECH_PORT="${SPEECH_PORT:-8767}"

# Check required vars
if [ -z "$AzureSpeech__ApiKey" ]; then
    echo "Error: AzureSpeech__ApiKey not set"
    echo ""
    echo "Set these environment variables:"
    echo "  export AzureSpeech__Region=eastus"
    echo "  export AzureSpeech__ApiKey=your-key"
    exit 1
fi

if [ -z "$AzureSpeech__Region" ]; then
    echo "Error: AzureSpeech__Region not set"
    echo ""
    echo "Set these environment variables:"
    echo "  export AzureSpeech__Region=eastus"
    echo "  export AzureSpeech__ApiKey=your-key"
    exit 1
fi

echo "Starting TTS Service..."
echo "  Port: $SPEECH_PORT"
echo "  Azure Speech Region: $AzureSpeech__Region"
echo ""

if [ "$1" = "--background" ]; then
    dotnet run --project SpeechService > /tmp/tts-service.log 2>&1 &
    echo "Started in background. Logs: /tmp/tts-service.log"
    echo "Stop with: pkill -f SpeechService"
else
    dotnet run --project SpeechService
fi
