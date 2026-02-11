# Voice Assistant Containerization Design

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Containerize all voice assistant components for consistent deployment on RPi and Mac.

**Architecture:** Four containers (client, stt-server, agent, tts) communicating via WebSockets. Client runs natively on Mac (audio passthrough issues with Podman VM), containerized on RPi.

**Tech Stack:** Podman, Python 3.11, .NET 8, Whisper, Hailo SDK (RPi), Azure Speech/OpenAI

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         Host (RPi / Mac)                        │
├─────────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌─────────┐ │
│  │   client    │  │  stt-server │  │    agent    │  │   tts   │ │
│  │  (Python)   │  │  (Python)   │  │ (.NET+MCP)  │  │ (.NET)  │ │
│  │             │  │  Whisper    │  │             │  │  Azure  │ │
│  │  Audio I/O  │  │             │  │ OpenAI+MCP  │  │ Speech  │ │
│  └──────┬──────┘  └──────┬──────┘  └──────┬──────┘  └────┬────┘ │
│         │                │                │               │      │
│     RPi: container   ws://8765        ws://8766       ws://8767  │
│     Mac: native                                                  │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
                    ┌───────────────────┐
                    │  External APIs    │
                    │  - Device Pairing │
                    │  - Time Reporting │
                    │  - Azure OpenAI   │
                    │  - Azure Speech   │
                    └───────────────────┘
```

## Platform Differences

| Component | Mac | RPi |
|-----------|-----|-----|
| **client** | Native (mlx audio) | Container (ALSA/Pulse passthrough) |
| **stt-server** | Container (CPU Whisper) | Container (Hailo NPU) |
| **agent** | Container | Container |
| **tts** | Container | Container |

Both platforms are ARM64 (M1 Mac + RPi 5), no multi-arch builds needed.

## Container Images

| Image | Base | Purpose |
|-------|------|---------|
| `ai-assistant/client` | `python:3.11-slim` | Audio capture/playback, orchestration |
| `ai-assistant/stt-server` | `python:3.11-slim` | Whisper STT (CPU) |
| `ai-assistant/stt-server:hailo` | `hailo-ai/base` | Whisper STT (Hailo NPU) |
| `ai-assistant/agent` | `mcr.microsoft.com/dotnet/sdk:8.0` | Voice agent + MCP SDK |
| `ai-assistant/tts` | `mcr.microsoft.com/dotnet/sdk:8.0` | Azure Speech TTS |

## File Structure

```
ai-assistant/
├── docker/
│   ├── client.Dockerfile
│   ├── stt-server.Dockerfile
│   ├── stt-server-hailo.Dockerfile
│   ├── agent.Dockerfile
│   └── tts.Dockerfile
├── scripts/
│   ├── run-mac.sh
│   └── run-rpi.sh
├── podman-compose.yml          # Base (Mac: 3 containers)
├── podman-compose.rpi.yml      # Override (RPi: +client, +Hailo)
├── .env.sample
└── .env                        # gitignored
```

## Compose Files

### podman-compose.yml (base)

```yaml
version: "3.8"

services:
  stt-server:
    build:
      context: .
      dockerfile: docker/stt-server.Dockerfile
    ports:
      - "8765:8765"
    environment:
      - WHISPER_BACKEND=faster-whisper
      - WHISPER_MODEL=base
      - HOST=0.0.0.0
      - PORT=8765

  agent:
    build:
      context: .
      dockerfile: docker/agent.Dockerfile
    ports:
      - "8766:8766"
    env_file: .env
    environment:
      - AGENT_PORT=8766

  tts:
    build:
      context: .
      dockerfile: docker/tts.Dockerfile
    ports:
      - "8767:8767"
    env_file: .env
    environment:
      - SPEECH_PORT=8767
```

### podman-compose.rpi.yml (override)

```yaml
version: "3.8"

services:
  client:
    build:
      context: .
      dockerfile: docker/client.Dockerfile
    depends_on:
      - stt-server
      - agent
      - tts
    environment:
      - SERVER_URL=ws://stt-server:8765
      - AGENT_URL=ws://agent:8766
      - TTS_URL=ws://tts:8767
    devices:
      - /dev/snd:/dev/snd
    volumes:
      - /run/user/1000/pulse:/run/user/1000/pulse
    environment:
      - PULSE_SERVER=unix:/run/user/1000/pulse/native
    group_add:
      - audio

  stt-server:
    build:
      context: .
      dockerfile: docker/stt-server-hailo.Dockerfile
    devices:
      - /dev/hailo0:/dev/hailo0
    environment:
      - WHISPER_BACKEND=hailo
```

## Environment Variables

### .env.sample

```bash
# Azure OpenAI (Agent)
AzureOpenAI__Endpoint=https://your-resource.openai.azure.com/
AzureOpenAI__ApiKey=your-key
AzureOpenAI__DeploymentName=gpt-4o

# Azure Speech (TTS)
AzureSpeech__Region=eastus
AzureSpeech__ApiKey=your-key

# Device Pairing (Agent/MCP)
AUTH_METHOD=device-pairing
DEVICE_ID=rpi-living-room
DEVICE_SECRET=your-secret
PAIRING_FUNCTION_URL=https://func-device-pairing.azurewebsites.net

# Time Reporting API
GRAPHQL_API_URL=http://su-macbook-1dfa.local:5001/graphql
```

## Wrapper Scripts

### scripts/run-mac.sh

```bash
#!/bin/bash
set -e
cd "$(dirname "$0")/.."

case "${1:-up}" in
  up)
    podman-compose up -d
    echo "Services started. Run client with:"
    echo "  ./whisper-streaming/rpi-client.sh"
    ;;
  down)
    podman-compose down
    ;;
  logs)
    podman-compose logs -f ${2:-}
    ;;
  build)
    podman-compose build
    ;;
esac
```

### scripts/run-rpi.sh

```bash
#!/bin/bash
set -e
cd "$(dirname "$0")/.."

COMPOSE="podman-compose -f podman-compose.yml -f podman-compose.rpi.yml"

case "${1:-up}" in
  up)
    $COMPOSE up -d
    ;;
  down)
    $COMPOSE down
    ;;
  logs)
    $COMPOSE logs -f ${2:-}
    ;;
  build)
    $COMPOSE build
    ;;
esac
```

## Audio Passthrough (RPi)

Client container needs access to host audio:

```yaml
devices:
  - /dev/snd:/dev/snd           # ALSA devices
volumes:
  - /run/user/1000/pulse:/run/user/1000/pulse  # PulseAudio socket
environment:
  - PULSE_SERVER=unix:/run/user/1000/pulse/native
group_add:
  - audio                        # Audio group membership
```

RPi user `alreva` has UID 1000, so hardcoded path works.

## Usage

### Mac Development

```bash
# Build
./scripts/run-mac.sh build

# Start services
./scripts/run-mac.sh up

# Run native client
./whisper-streaming/rpi-client.sh

# View logs
./scripts/run-mac.sh logs agent

# Stop
./scripts/run-mac.sh down
```

### RPi Deployment

```bash
# Build (includes Hailo image)
./scripts/run-rpi.sh build

# Start all 4 containers
./scripts/run-rpi.sh up

# View logs
./scripts/run-rpi.sh logs

# Stop
./scripts/run-rpi.sh down
```

## Network Access

- RPi accesses Mac's Time Reporting API via mDNS: `http://su-macbook-1dfa.local:5001/graphql`
- Mac's API already binds to `0.0.0.0:5001` (Podman container)

## Implementation Order

1. Create Dockerfiles (stt-server, agent, tts)
2. Create client Dockerfile
3. Create stt-server-hailo Dockerfile
4. Create podman-compose.yml
5. Create podman-compose.rpi.yml
6. Create wrapper scripts
7. Create .env.sample
8. Test on Mac
9. Test on RPi
