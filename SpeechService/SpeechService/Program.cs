using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Azure.Monitor.OpenTelemetry.Exporter;
using SpeechService.Handlers;
using SpeechService.Services;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
var port = int.Parse(Environment.GetEnvironmentVariable("SPEECH_PORT") ?? "8767");

// Register services
builder.Services.AddSingleton(new AzureSpeechConfig
{
    Region = Environment.GetEnvironmentVariable("AzureSpeech__Region") ?? "",
    ApiKey = Environment.GetEnvironmentVariable("AzureSpeech__ApiKey") ?? ""
});

builder.Services.AddSingleton<ITtsService, TtsService>();
builder.Services.AddSingleton<WebSocketHandler>();

// OpenTelemetry
var connectionString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("speech-service"))
        .WithTracing(t =>
        {
            t.AddSource("SpeechService");
            t.AddAzureMonitorTraceExporter(o => o.ConnectionString = connectionString);
        });
}

var app = builder.Build();
await app.StartAsync();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var handler = app.Services.GetRequiredService<WebSocketHandler>();

// Start WebSocket server
var listener = new HttpListener();
listener.Prefixes.Add($"http://+:{port}/");
listener.Start();

logger.LogInformation("TTS Service listening on port {Port}", port);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.LogInformation("Shutting down...");
    cts.Cancel();
};

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var getContextTask = listener.GetContextAsync();

        // Race between getting a connection and cancellation
        var tcs = new TaskCompletionSource<bool>();
        await using (cts.Token.Register(() => tcs.TrySetResult(true)))
        {
            var completedTask = await Task.WhenAny(getContextTask, tcs.Task);

            if (completedTask == tcs.Task)
            {
                // Cancellation requested - exit cleanly
                break;
            }
        }

        var context = await getContextTask;

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
    logger.LogInformation("Server stopped");
}

static async Task HandleWebSocketAsync(
    WebSocket webSocket,
    WebSocketHandler handler,
    ILogger logger,
    CancellationToken cancellationToken)
{
    try
    {
        await handler.HandleConnectionAsync(webSocket, cancellationToken);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "WebSocket connection error");
    }
}
