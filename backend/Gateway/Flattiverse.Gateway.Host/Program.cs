using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Flattiverse.Gateway.Options;
using Flattiverse.Gateway.Protocol;
using Microsoft.Extensions.Options;
using Flattiverse.Gateway.Protocol.ServerMessages;
using Flattiverse.Gateway.Services;
using Flattiverse.Gateway.Sessions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<PathfindingOptions>(builder.Configuration.GetSection(PathfindingOptions.SectionPath));
builder.Services.Configure<GatewayConnectionOptions>(builder.Configuration.GetSection(GatewayConnectionOptions.SectionPath));
var configuredWorldStatePath = builder.Configuration["Gateway:WorldStateFilePath"];
if (!string.IsNullOrWhiteSpace(configuredWorldStatePath) && !Path.IsPathRooted(configuredWorldStatePath))
{
    configuredWorldStatePath = Path.Combine(builder.Environment.ContentRootPath, configuredWorldStatePath);
}
MappingService.ConfigurePersistence(configuredWorldStatePath);

builder.Services.AddSingleton(sp =>
{
    var connectionOptions = sp.GetRequiredService<IOptions<GatewayConnectionOptions>>().Value;
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var pathfindingOptions = sp.GetRequiredService<IOptions<PathfindingOptions>>();
    return new PlayerSessionPool(
        connectionOptions.FlattiverseGalaxyUrl,
        connectionOptions.CreateRuntimeDisclosure(),
        connectionOptions.CreateBuildDisclosure(),
        loggerFactory,
        pathfindingOptions);
});

builder.Services.AddHostedService<TickService>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    protocolVersion = "0.2.0",
    serverTimeUtc = DateTime.UtcNow.ToString("O")
}));

app.Map("/ws", async (HttpContext context, PlayerSessionPool sessionPool, ILoggerFactory loggerFactory) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var logger = loggerFactory.CreateLogger("WebSocket");
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    using var connection = new BrowserConnection(loggerFactory.CreateLogger<BrowserConnection>());

    logger.LogInformation("WebSocket connected: {ConnectionId}", connection.ConnectionId);

    // Send initial session.ready without an attach warning. The browser may
    // already be preparing queued connection.attach messages as soon as the
    // socket opens, so an eager attach_required status creates noisy false alarms.
    connection.EnqueueMessage(connection.BuildSessionReady());

    // Start ping task
    using var cts = new CancellationTokenSource();
    var pingTask = Task.Run(async () =>
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(cts.Token))
                connection.EnqueueMessage(new PingMessage());
        }
        catch (OperationCanceledException) { }
    });

    // Start writer task
    var writerTask = Task.Run(async () =>
    {
        try
        {
            await foreach (var message in connection.OutboundReader.ReadAllAsync(cts.Token))
            {
                if (ws.State != WebSocketState.Open) break;

                var json = JsonSerializer.Serialize<object>(message, JsonDefaults.Options);
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    });

    // Read loop
    var buffer = new byte[8192];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

                // Handle large messages that span multiple frames
                if (!result.EndOfMessage)
                {
                    var sb = new StringBuilder(json);
                    while (!result.EndOfMessage)
                    {
                        result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    json = sb.ToString();
                }

                await connection.HandleMessageAsync(json, sessionPool);
            }
        }
    }
    catch (WebSocketException ex)
    {
        logger.LogWarning(ex, "WebSocket error for {ConnectionId}", connection.ConnectionId);
    }

    logger.LogInformation("WebSocket disconnected: {ConnectionId}", connection.ConnectionId);
    cts.Cancel();
    await Task.WhenAll(writerTask, pingTask);

    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
    {
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Goodbye", CancellationToken.None); }
        catch { }
    }
});

app.Run();
