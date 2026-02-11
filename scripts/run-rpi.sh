#!/bin/bash
# Run voice assistant containers on Raspberry Pi (4 services: client, stt, agent, tts)
# Includes Hailo NPU acceleration and audio passthrough
#
# Usage:
#   ./scripts/run-rpi.sh          # start all services (uses pre-built images)
#   ./scripts/run-rpi.sh up       # start all services
#   ./scripts/run-rpi.sh down     # stop services
#   ./scripts/run-rpi.sh logs     # view all logs
#   ./scripts/run-rpi.sh logs agent  # view specific service logs
#   ./scripts/run-rpi.sh build    # rebuild images locally (slow on RPi)
#   ./scripts/run-rpi.sh ps       # show running containers
#
# Pre-built images workflow (recommended):
#   1. Build on Mac: podman build -t ai-assistant-stt:hailo -f docker/stt-server-hailo.Dockerfile .
#   2. Transfer: podman save ai-assistant-stt:hailo | ssh alreva@rpi.local "podman load"
#   3. Run: ./scripts/run-rpi.sh up

set -e
cd "$(dirname "$0")/.."

# Use pre-built images by default (faster), fall back to build if not available
COMPOSE="podman compose -f podman-compose.yml -f podman-compose.rpi.yml -f podman-compose.images.yml"
COMPOSE_BUILD="podman compose -f podman-compose.yml -f podman-compose.rpi.yml"

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
        echo "Building images locally (this is slow on RPi, consider building on Mac)..."
        $COMPOSE_BUILD build
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
