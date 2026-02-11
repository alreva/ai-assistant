#!/bin/bash
# Run voice assistant containers on Raspberry Pi (4 services: client, stt, agent, tts)
# Includes Hailo NPU acceleration and audio passthrough
#
# Usage:
#   ./scripts/run-rpi.sh          # start all services
#   ./scripts/run-rpi.sh up       # start all services
#   ./scripts/run-rpi.sh down     # stop services
#   ./scripts/run-rpi.sh logs     # view all logs
#   ./scripts/run-rpi.sh logs agent  # view specific service logs
#   ./scripts/run-rpi.sh build    # rebuild images
#   ./scripts/run-rpi.sh ps       # show running containers

set -e
cd "$(dirname "$0")/.."

COMPOSE="podman compose -f podman-compose.yml -f podman-compose.rpi.yml"

# Check for TimeReportingMcpSdk symlink (required for agent build)
if [ ! -d "TimeReportingMcpSdk" ]; then
    echo "Error: TimeReportingMcpSdk not found."
    echo "Create symlink or copy the directory:"
    echo "  ln -s /path/to/TimeReportingMcpSdk TimeReportingMcpSdk"
    exit 1
fi

# Check for Hailo device
if [ ! -e "/dev/hailo0" ]; then
    echo "Warning: /dev/hailo0 not found. Hailo NPU acceleration will not work."
    echo "Make sure AI HAT+ 2 is connected and drivers are loaded."
fi

case "${1:-up}" in
    up)
        echo "Starting all services (client, stt-server, agent, tts)..."
        $COMPOSE up -d
        echo ""
        echo "All services started."
        echo "View logs: ./scripts/run-rpi.sh logs"
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
