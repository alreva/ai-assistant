#!/bin/bash
# Start whisper server in tmux session
#
# Usage:
#   ./rpi-server.sh          # start in tmux
#   ./rpi-server.sh --attach # start and attach
#   ./rpi-server.sh --stop   # stop server
#
# To view logs: tmux attach -t whisper-server
# To detach: Ctrl+B, then D

set -e
cd "$(dirname "$0")"

SESSION="whisper-server"

case "$1" in
    --stop)
        echo "Stopping server..."
        tmux kill-session -t "$SESSION" 2>/dev/null && echo "Server stopped" || echo "Server not running"
        exit 0
        ;;
    --attach)
        ATTACH=1
        ;;
esac

# Check if already running
if tmux has-session -t "$SESSION" 2>/dev/null; then
    echo "Server already running. Use: tmux attach -t $SESSION"
    exit 0
fi

# Environment
export WHISPER_BACKEND="${WHISPER_BACKEND:-faster}"
export WHISPER_MODEL="${WHISPER_MODEL:-small}"
export HOST="${HOST:-0.0.0.0}"
export PORT="${PORT:-8765}"

echo "Starting server..."
echo "  Backend: $WHISPER_BACKEND"
echo "  Model: $WHISPER_MODEL"
echo "  Address: ws://$HOST:$PORT"

# Start in tmux
tmux new-session -d -s "$SESSION" "venv/bin/python -m server.main; echo 'Server exited. Press Enter to close.'; read"

echo "Server started in tmux session '$SESSION'"
echo ""
echo "Commands:"
echo "  View logs:  tmux attach -t $SESSION"
echo "  Stop:       ./rpi-server.sh --stop"
echo "  Detach:     Ctrl+B, then D"

if [ "$ATTACH" = "1" ]; then
    tmux attach -t "$SESSION"
fi
