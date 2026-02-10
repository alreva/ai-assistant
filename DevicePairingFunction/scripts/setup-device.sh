#!/bin/bash
# setup-device.sh - Register device with pairing backend
# Run this once per RPi device to generate credentials

set -e

# Configuration
FUNCTION_URL="${PAIRING_FUNCTION_URL:-https://your-func.azurewebsites.net}"
DEVICE_ID="${DEVICE_ID:-rpi-$(hostname)}"
CONFIG_DIR="${CONFIG_DIR:-$HOME/.config/voice-assistant}"

echo "==========================================="
echo "Device Pairing Setup"
echo "==========================================="
echo ""
echo "Function URL: $FUNCTION_URL"
echo "Device ID:    $DEVICE_ID"
echo ""

# Check if already configured
if [ -f "$CONFIG_DIR/device.env" ]; then
    echo "WARNING: Device already configured!"
    echo "Existing config: $CONFIG_DIR/device.env"
    read -p "Do you want to overwrite? (y/N): " confirm
    if [ "$confirm" != "y" ] && [ "$confirm" != "Y" ]; then
        echo "Aborted."
        exit 0
    fi
fi

# Check dependencies
if ! command -v curl &> /dev/null; then
    echo "ERROR: curl is required but not installed."
    exit 1
fi

if ! command -v openssl &> /dev/null; then
    echo "ERROR: openssl is required but not installed."
    exit 1
fi

# Generate device secret (44 chars base64 = 32 bytes of entropy)
DEVICE_SECRET=$(openssl rand -base64 32)

echo "Registering device with backend..."

# Register device
RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "$FUNCTION_URL/api/device/register" \
    -H "Content-Type: application/json" \
    -d "{\"deviceId\": \"$DEVICE_ID\", \"deviceSecret\": \"$DEVICE_SECRET\"}")

# Parse response
HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | sed '$d')

if [ "$HTTP_CODE" = "200" ]; then
    echo "✓ Device registered successfully"
elif [ "$HTTP_CODE" = "409" ]; then
    echo "ERROR: Device '$DEVICE_ID' is already registered."
    echo "Use a different DEVICE_ID or contact the administrator to remove the existing registration."
    exit 1
else
    echo "ERROR: Registration failed (HTTP $HTTP_CODE)"
    echo "Response: $BODY"
    exit 1
fi

# Create config directory
mkdir -p "$CONFIG_DIR"

# Save device credentials
cat > "$CONFIG_DIR/device.env" << EOF
# Device Pairing Configuration
# Generated: $(date -Iseconds)
# DO NOT SHARE THIS FILE - contains device secret

AUTH_METHOD=device-pairing
DEVICE_ID=$DEVICE_ID
DEVICE_SECRET=$DEVICE_SECRET
PAIRING_FUNCTION_URL=$FUNCTION_URL
EOF

# Secure the file
chmod 600 "$CONFIG_DIR/device.env"

echo "✓ Credentials saved to $CONFIG_DIR/device.env"
echo ""
echo "==========================================="
echo "Setup Complete!"
echo "==========================================="
echo ""
echo "Next steps:"
echo "1. Run ./generate-qr.sh to create a QR code"
echo "2. Print the QR code and stick it to your RPi"
echo "3. Scan the QR code with your phone to link your account"
echo ""
echo "To use with MCP server, source the env file:"
echo "  source $CONFIG_DIR/device.env"
echo ""
