#!/bin/bash
# check-status.sh - Check device pairing status

set -e

# Configuration
CONFIG_DIR="${CONFIG_DIR:-$HOME/.config/voice-assistant}"

# Load config if exists
if [ -f "$CONFIG_DIR/device.env" ]; then
    source "$CONFIG_DIR/device.env"
else
    echo "ERROR: Device not configured. Run setup-device.sh first."
    exit 1
fi

FUNCTION_URL="${PAIRING_FUNCTION_URL:-https://your-func.azurewebsites.net}"
DEVICE_ID="${DEVICE_ID:-rpi-$(hostname)}"

echo "Checking status for device: $DEVICE_ID"
echo ""

# Get device status
RESPONSE=$(curl -s "$FUNCTION_URL/api/device/$DEVICE_ID/status")

STATUS=$(echo "$RESPONSE" | grep -o '"status":"[^"]*"' | cut -d'"' -f4)

case "$STATUS" in
    "linked")
        echo "✓ Device is linked to a user account"
        LINKED_AT=$(echo "$RESPONSE" | grep -o '"linkedAt":"[^"]*"' | cut -d'"' -f4)
        echo "  Linked at: $LINKED_AT"
        ;;
    "registered")
        echo "⚠ Device is registered but NOT linked to a user"
        echo "  Scan the QR code to link your account"
        ;;
    "not_registered")
        echo "✗ Device is not registered"
        echo "  Run setup-device.sh first"
        ;;
    *)
        echo "Unknown status: $STATUS"
        echo "Response: $RESPONSE"
        ;;
esac

echo ""
