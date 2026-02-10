#!/bin/bash
# generate-qr.sh - Generate permanent QR code for device pairing
# The QR code URL is PERMANENT - print once, use forever

set -e

# Configuration
CONFIG_DIR="${CONFIG_DIR:-$HOME/.config/voice-assistant}"
OUTPUT_DIR="${OUTPUT_DIR:-$HOME}"

# Load config if exists
if [ -f "$CONFIG_DIR/device.env" ]; then
    source "$CONFIG_DIR/device.env"
fi

FUNCTION_URL="${PAIRING_FUNCTION_URL:-https://your-func.azurewebsites.net}"
DEVICE_ID="${DEVICE_ID:-rpi-$(hostname)}"

# The permanent pairing URL
PAIRING_URL="$FUNCTION_URL/api/device/$DEVICE_ID/pair"

echo "==========================================="
echo "PERMANENT QR CODE FOR DEVICE"
echo "==========================================="
echo ""
echo "Device ID: $DEVICE_ID"
echo "URL:       $PAIRING_URL"
echo ""

# Check for qrencode
if command -v qrencode &> /dev/null; then
    echo "Terminal QR Code:"
    echo ""
    qrencode -t ANSIUTF8 "$PAIRING_URL"
    echo ""

    # Generate PNG for printing
    OUTPUT_FILE="$OUTPUT_DIR/qr-$DEVICE_ID.png"
    qrencode -o "$OUTPUT_FILE" -s 10 -l H "$PAIRING_URL"
    echo "PNG saved to: $OUTPUT_FILE"
    echo "Print this QR code and stick it to your RPi!"
else
    echo "NOTE: Install qrencode for graphical QR code"
    echo "  macOS: brew install qrencode"
    echo "  Linux: sudo apt-get install qrencode"
    echo ""
    echo "Or visit this URL to generate a QR code online:"
    echo "https://api.qrserver.com/v1/create-qr-code/?size=300x300&data=$(echo -n "$PAIRING_URL" | jq -sRr @uri)"
fi

echo ""
echo "==========================================="
echo "How to use:"
echo "==========================================="
echo ""
echo "1. Print the QR code and stick it to your RPi"
echo ""
echo "2. Scan the QR code with your phone to:"
echo "   - Link your account to this device"
echo "   - Re-authenticate if token expired"
echo "   - Switch to a different user account"
echo ""
echo "The QR code never changes - scan anytime you need to (re)link."
echo ""
