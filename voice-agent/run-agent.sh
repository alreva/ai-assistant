#!/bin/bash
# Run the voice agent with required environment variables
#
# Prerequisites:
#   - podman start time-reporting-db time-reporting-api
#   - Set Azure OpenAI credentials (or source env.sh)
#
# Usage:
#   ./run-agent.sh                    # run in foreground
#   ./run-agent.sh --background       # run in background

set -e
cd "$(dirname "$0")"

# Defaults
export AGENT_PORT="${AGENT_PORT:-8766}"
export GRAPHQL_API_URL="${GRAPHQL_API_URL:-http://localhost:5001/graphql}"
export MCP_COMMAND="${MCP_COMMAND:-dotnet}"
export MCP_ARGS="${MCP_ARGS:-run --project /Users/oleksandrreva/Documents/git/time-reporting-agent/claude-code-time-reporting/TimeReportingMcp/TimeReportingMcp.csproj}"

# Check required vars
if [ -z "$AzureOpenAI__ApiKey" ]; then
    echo "Error: AzureOpenAI__ApiKey not set"
    echo ""
    echo "Set these environment variables:"
    echo "  export AzureOpenAI__Endpoint=https://your-resource.openai.azure.com/"
    echo "  export AzureOpenAI__ApiKey=your-key"
    echo "  export AzureOpenAI__DeploymentName=gpt-4o"
    exit 1
fi

echo "Starting Voice Agent..."
echo "  Port: $AGENT_PORT"
echo "  GraphQL: $GRAPHQL_API_URL"
echo "  Azure OpenAI: $AzureOpenAI__Endpoint ($AzureOpenAI__DeploymentName)"
echo ""

if [ "$1" = "--background" ]; then
    dotnet run --project VoiceAgent > /tmp/voice-agent.log 2>&1 &
    echo "Started in background. Logs: /tmp/voice-agent.log"
    echo "Stop with: pkill -f VoiceAgent"
else
    dotnet run --project VoiceAgent
fi
