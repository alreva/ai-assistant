// VoiceAgent/Program.cs
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoiceAgent.Handlers;
using VoiceAgent.Services;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
var port = int.Parse(Environment.GetEnvironmentVariable("AGENT_PORT") ?? "8766");
var sessionTimeoutHours = int.Parse(Environment.GetEnvironmentVariable("SESSION_TIMEOUT_HOURS") ?? "4");
var confirmationTimeoutMinutes = int.Parse(Environment.GetEnvironmentVariable("CONFIRMATION_TIMEOUT_MINUTES") ?? "2");

// Register services
builder.Services.AddSingleton(new SessionManager(
    TimeSpan.FromHours(sessionTimeoutHours),
    TimeSpan.FromMinutes(confirmationTimeoutMinutes)));

builder.Services.AddSingleton<IntentClassifier>();

builder.Services.AddSingleton(new McpClientConfig
{
    Command = Environment.GetEnvironmentVariable("MCP_COMMAND") ?? "dotnet",
    Arguments = Environment.GetEnvironmentVariable("MCP_ARGS") ?? "run --project TimeReportingMcpSdk"
});

builder.Services.AddSingleton<IMcpClientService, McpClientService>();
builder.Services.AddSingleton<IAgentService, AgentService>();
builder.Services.AddSingleton<WebSocketHandler>();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var handler = app.Services.GetRequiredService<WebSocketHandler>();

// Start WebSocket server
var listener = new HttpListener();
listener.Prefixes.Add($"http://+:{port}/");
listener.Start();

logger.LogInformation("Voice Agent WebSocket server started on port {Port}", port);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var context = await listener.GetContextAsync();

        if (context.Request.IsWebSocketRequest)
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            _ = HandleWebSocketAsync(wsContext.WebSocket, handler, logger, cts.Token);
        }
        else
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
        }
    }
}
finally
{
    listener.Stop();
}

static async Task HandleWebSocketAsync(
    WebSocket webSocket,
    WebSocketHandler handler,
    ILogger logger,
    CancellationToken cancellationToken)
{
    var buffer = new byte[4096];

    try
    {
        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", cancellationToken);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                logger.LogDebug("Received: {Message}", message);

                var response = await handler.HandleMessageAsync(message);
                var responseBytes = Encoding.UTF8.GetBytes(response);

                await webSocket.SendAsync(responseBytes, WebSocketMessageType.Text, true, cancellationToken);
                logger.LogDebug("Sent: {Response}", response);
            }
        }
    }
    catch (WebSocketException ex)
    {
        logger.LogWarning("WebSocket error: {Error}", ex.Message);
    }
}
