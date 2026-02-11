# Voice Agent + MCP SDK Dockerfile
# Multi-stage build for Voice Agent with Time Reporting MCP integration
# Works on ARM64 (M1 Mac, Raspberry Pi) and x86_64

# ==============================================================================
# Stage 1: Restore dependencies
# ==============================================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore

WORKDIR /src

# Copy Central Package Management files for MCP SDK (from external repo)
COPY TimeReportingMcpSdk/Directory.Build.props TimeReportingMcpSdk/
COPY TimeReportingMcpSdk/Directory.Packages.props TimeReportingMcpSdk/

# Copy project files for dependency restore
COPY voice-agent/VoiceAgent/VoiceAgent.csproj voice-agent/VoiceAgent/
COPY TimeReportingMcpSdk/TimeReportingMcpSdk.csproj TimeReportingMcpSdk/

# Restore VoiceAgent dependencies
RUN dotnet restore voice-agent/VoiceAgent/VoiceAgent.csproj

# Restore MCP SDK dependencies (skip auto-generation targets)
RUN dotnet restore TimeReportingMcpSdk/TimeReportingMcpSdk.csproj \
    -p:AutoGenerateFragments=false

# ==============================================================================
# Stage 2: Build VoiceAgent
# ==============================================================================
FROM restore AS build-agent

WORKDIR /src

# Copy VoiceAgent source code
COPY voice-agent/VoiceAgent/ voice-agent/VoiceAgent/

# Build VoiceAgent
RUN dotnet build voice-agent/VoiceAgent/VoiceAgent.csproj \
    -c Release \
    --no-restore

# ==============================================================================
# Stage 3: Build MCP SDK
# ==============================================================================
FROM restore AS build-mcp

WORKDIR /src

# Copy MCP SDK source code
COPY TimeReportingMcpSdk/ TimeReportingMcpSdk/

# Build MCP SDK (disable schema auto-generation - use pre-generated files)
RUN dotnet build TimeReportingMcpSdk/TimeReportingMcpSdk.csproj \
    -c Release \
    --no-restore \
    -p:AutoGenerateFragments=false

# ==============================================================================
# Stage 4: Publish VoiceAgent
# ==============================================================================
FROM build-agent AS publish-agent

WORKDIR /src

# Publish VoiceAgent
RUN dotnet publish voice-agent/VoiceAgent/VoiceAgent.csproj \
    -c Release \
    --no-build \
    -o /app/agent

# ==============================================================================
# Stage 5: Publish MCP SDK
# ==============================================================================
FROM build-mcp AS publish-mcp

WORKDIR /src

# Publish MCP SDK
RUN dotnet publish TimeReportingMcpSdk/TimeReportingMcpSdk.csproj \
    -c Release \
    --no-build \
    -o /app/mcp \
    -p:AutoGenerateFragments=false

# ==============================================================================
# Stage 6: Runtime
# ==============================================================================
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

# Create non-root user for security
RUN useradd --create-home --shell /bin/bash agent
USER agent
WORKDIR /home/agent/app

# Copy published applications
COPY --from=publish-agent --chown=agent:agent /app/agent ./agent/
COPY --from=publish-mcp --chown=agent:agent /app/mcp ./mcp/

# Environment variables
# Agent configuration
ENV AGENT_PORT=8766

# MCP configuration - spawns MCP SDK as subprocess
ENV MCP_COMMAND=/home/agent/app/mcp/TimeReportingMcpSdk
ENV MCP_ARGS=
ENV GRAPHQL_API_URL=http://localhost:5001/graphql

# Azure OpenAI configuration (must be provided at runtime)
ENV AzureOpenAI__Endpoint=
ENV AzureOpenAI__ApiKey=
ENV AzureOpenAI__DeploymentName=gpt-4o

# Device Pairing configuration (optional)
ENV AUTH_METHOD=
ENV DEVICE_ID=
ENV DEVICE_SECRET=
ENV PAIRING_FUNCTION_URL=

# Session configuration
ENV SESSION_TIMEOUT_HOURS=4
ENV CONFIRMATION_TIMEOUT_MINUTES=2

# Agent character selection
ENV AGENT_CHARACTER=

EXPOSE 8766

# Health check - verify the process is running
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD pgrep -f VoiceAgent || exit 1

# Run the Voice Agent
CMD ["./agent/VoiceAgent"]
