using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Flattiverse.Gateway.Protocol;
using Flattiverse.Gateway.Protocol.ServerMessages;
using Flattiverse.Gateway.Sessions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var galaxyUrl = config["Gateway:FlattiverseGalaxyUrl"] ?? "wss://www.flattiverse.com/api/universes/0/";
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    return new PlayerSessionPool(galaxyUrl, loggerFactory);
});

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

    // Send initial session.ready
    connection.EnqueueMessage(connection.BuildSessionReady());
    connection.EnqueueMessage(new ServerStatusMessage
    {
        Kind = "info",
        Code = "attach_required",
        Message = "Please attach a player session using connection.attach.",
        Recoverable = true
    });

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
