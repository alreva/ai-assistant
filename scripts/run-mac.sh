#!/bin/bash
# Run voice assistant containers on Mac (3 services: stt, agent, tts)
# Client runs natively on Mac for audio access
#
# Usage:
#   ./scripts/run-mac.sh          # start services
#   ./scripts/run-mac.sh up       # start services
#   ./scripts/run-mac.sh down     # stop services
#   ./scripts/run-mac.sh logs     # view all logs
#   ./scripts/run-mac.sh logs agent  # view specific service logs
#   ./scripts/run-mac.sh build    # rebuild images
#   ./scripts/run-mac.sh ps       # show running containers

set -e
cd "$(dirname "$0")/.."

# Check for TimeReportingMcpSdk symlink (required for agent build)
if [ ! -d "TimeReportingMcpSdk" ]; then
    echo "Creating symlink to TimeReportingMcpSdk..."
    ln -s /Users/oleksandrreva/Documents/git/time-reporting-agent/claude-code-time-reporting/TimeReportingMcpSdk TimeReportingMcpSdk
fi

COMPOSE="podman compose -f podman-compose.yml"

case "${1:-up}" in
    up)
        echo "Starting services (stt-server, agent, tts)..."
        $COMPOSE up -d
        echo ""
        echo "Services started. Run native client with:"
        echo "  ./whisper-streaming/rpi-client.sh"
        echo ""
        echo "View logs: ./scripts/run-mac.sh logs"
        ;;
    down)
        echo "Stopping services..."
        $COMPOSE down
        ;;
    logs)
        shift
        $COMPOSE logs -f ${1:-}
        ;;
    build)
        echo "Building images..."
        $COMPOSE build
        ;;
    ps)
        $COMPOSE ps
        ;;
    restart)
        echo "Restarting services..."
        $COMPOSE restart
        ;;
    *)
        echo "Usage: $0 {up|down|logs|build|ps|restart}"
        exit 1
        ;;
esac
