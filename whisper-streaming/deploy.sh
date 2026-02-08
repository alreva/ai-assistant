#!/bin/bash
# Deploy whisper-streaming to Raspberry Pi
#
# Usage:
#   ./deploy.sh                      # deploy to default (pi@rpi.local)
#   ./deploy.sh user@host            # deploy to specific host
#   ./deploy.sh user@host dest_dir   # deploy to specific host and dir
#
# Environment:
#   RPI_HOST - default: pi@rpi.local
#   DEST_DIR - default: ~/whisper-streaming

set -e

RPI_HOST="${1:-${RPI_HOST:-alreva@rpi.local}}"
DEST_DIR="${2:-${DEST_DIR:-~/whisper-streaming}}"

echo "==> Deploying to $RPI_HOST:$DEST_DIR"

# Files to copy
FILES=(
    "pyproject.toml"
    "server/"
    "client/"
    "rpi-server.sh"
    "rpi-client.sh"
)

# Create destination directory
ssh "$RPI_HOST" "mkdir -p $DEST_DIR"

# Copy files
echo "==> Copying files..."
for f in "${FILES[@]}"; do
    if [ -e "$f" ]; then
        scp -r "$f" "$RPI_HOST:$DEST_DIR/"
    fi
done

# Setup on RPi
echo "==> Setting up on RPi..."
ssh "$RPI_HOST" << EOF
    cd $DEST_DIR

    # Create venv if not exists
    if [ ! -d "venv" ]; then
        echo "Creating virtual environment..."
        python3 -m venv venv
    fi

    # Install dependencies
    echo "Installing dependencies..."
    venv/bin/pip install -q --upgrade pip
    venv/bin/pip install -q -e ".[client]"

    # Check for server backend
    if venv/bin/pip show faster-whisper > /dev/null 2>&1; then
        echo "faster-whisper already installed"
    else
        echo "Installing faster-whisper (this may take a while)..."
        venv/bin/pip install -q faster-whisper
    fi

    # Make scripts executable
    chmod +x rpi-server.sh rpi-client.sh 2>/dev/null || true

    echo "==> Setup complete!"
    echo ""
    echo "To start server: ssh $RPI_HOST 'cd $DEST_DIR && ./rpi-server.sh'"
    echo "To run client:   ssh -t $RPI_HOST 'cd $DEST_DIR && ./rpi-client.sh'"
    echo "To view server:  ssh $RPI_HOST 'tmux attach -t whisper-server'"
EOF

echo ""
echo "==> Deploy complete!"
