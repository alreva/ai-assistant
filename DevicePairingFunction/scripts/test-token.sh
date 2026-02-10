#!/bin/bash
# test-token.sh - Test retrieving an access token from the pairing backend

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

echo "Testing token retrieval for device: $DEVICE_ID"
echo ""

# Request token
RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "$PAIRING_FUNCTION_URL/api/device/token" \
    -H "Content-Type: application/json" \
    -d "{\"deviceId\": \"$DEVICE_ID\", \"deviceSecret\": \"$DEVICE_SECRET\"}")

# Parse response
HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | sed '$d')

case "$HTTP_CODE" in
    "200")
        echo "✓ Token retrieved successfully!"
        EXPIRES_IN=$(echo "$BODY" | grep -o '"expiresIn":[0-9]*' | cut -d':' -f2)
        echo "  Expires in: ${EXPIRES_IN}s"

        # Optionally show token preview
        if [ "$1" = "-v" ]; then
            ACCESS_TOKEN=$(echo "$BODY" | grep -o '"accessToken":"[^"]*"' | cut -d'"' -f4)
            echo "  Token (first 50 chars): ${ACCESS_TOKEN:0:50}..."
        fi
        ;;
    "401")
        echo "✗ Authentication failed"
        echo "  The refresh token may have expired. Scan the QR code to re-authenticate."
        ;;
    "403")
        echo "✗ Device not linked to a user"
        echo "  Scan the QR code to link your account."
        ;;
    *)
        echo "✗ Error (HTTP $HTTP_CODE)"
        echo "Response: $BODY"
        ;;
esac

echo ""
