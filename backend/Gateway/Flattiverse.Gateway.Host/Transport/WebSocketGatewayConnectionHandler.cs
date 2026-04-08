using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using Flattiverse.Gateway.Application.Abstractions;
using Flattiverse.Gateway.Application.Exceptions;
using Flattiverse.Gateway.Application.Services;
using Flattiverse.Gateway.Contracts.Messages.Server;
using Microsoft.AspNetCore.Http;

namespace Flattiverse.Gateway.Host.Transport;

public sealed class WebSocketGatewayConnectionHandler
{
    private readonly IGatewayConnectionMessageQueue connectionMessageQueue;
    private readonly IGatewayMessageSerializer messageSerializer;
    private readonly GatewaySessionOrchestrator sessionOrchestrator;
    private readonly ILogger<WebSocketGatewayConnectionHandler> logger;

    public WebSocketGatewayConnectionHandler(
        IGatewayConnectionMessageQueue connectionMessageQueue,
        IGatewayMessageSerializer messageSerializer,
        GatewaySessionOrchestrator sessionOrchestrator,
        ILogger<WebSocketGatewayConnectionHandler> logger)
    {
        this.connectionMessageQueue = connectionMessageQueue;
        this.messageSerializer = messageSerializer;
        this.sessionOrchestrator = sessionOrchestrator;
        this.logger = logger;
    }

    public async Task HandleAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
        var openedConnection = await sessionOrchestrator.OpenConnectionAsync(cancellationToken);
        await connectionMessageQueue.RegisterConnectionAsync(openedConnection.ConnectionId, cancellationToken);
        using var handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<string?>? receiveTask = null;
        Task<IReadOnlyList<GatewayMessage>>? queuedMessagesTask = null;

        try
        {
            if (!await SendMessagesAsync(webSocket, openedConnection.Messages, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            receiveTask = ReceiveTextMessageAsync(webSocket, handlerCancellation.Token);
            queuedMessagesTask = connectionMessageQueue.DequeueAsync(openedConnection.ConnectionId, handlerCancellation.Token).AsTask();

            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var completedTask = await Task.WhenAny(receiveTask, queuedMessagesTask).ConfigureAwait(false);

                if (completedTask == queuedMessagesTask)
                {
                    IReadOnlyList<GatewayMessage> queuedMessages;

                    try
                    {
                        queuedMessages = await queuedMessagesTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (handlerCancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    if (queuedMessages.Count > 0)
                    {
                        if (!await SendMessagesAsync(webSocket, queuedMessages, cancellationToken).ConfigureAwait(false))
                        {
                            break;
                        }
                    }

                    queuedMessagesTask = connectionMessageQueue.DequeueAsync(openedConnection.ConnectionId, handlerCancellation.Token).AsTask();

                    continue;
                }

                string? payload;

                try
                {
                    payload = await receiveTask.ConfigureAwait(false);
                }
                catch (GatewayClientMessageException exception)
                {
                    if (!await SendStatusAsync(webSocket, exception.Code, exception.Message, exception.Recoverable, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    receiveTask = ReceiveTextMessageAsync(webSocket, handlerCancellation.Token);

                    continue;
                }
                catch (OperationCanceledException) when (handlerCancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (WebSocketException exception)
                {
                    logger.LogDebug(
                        exception,
                        "WebSocket connection {ConnectionId} terminated while receiving.",
                        openedConnection.ConnectionId);
                    break;
                }
                catch (ObjectDisposedException exception)
                {
                    logger.LogDebug(
                        exception,
                        "WebSocket connection {ConnectionId} was disposed while receiving.",
                        openedConnection.ConnectionId);
                    break;
                }

                if (payload is null)
                {
                    break;
                }

                try
                {
                    var clientMessage = messageSerializer.DeserializeClientMessage(payload);
                    var responses = await sessionOrchestrator.HandleClientMessageAsync(openedConnection.ConnectionId, clientMessage, cancellationToken);
                    if (!await SendMessagesAsync(webSocket, responses, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (GatewayClientMessageException exception)
                {
                    if (!await SendStatusAsync(webSocket, exception.Code, exception.Message, exception.Recoverable, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }

                receiveTask = ReceiveTextMessageAsync(webSocket, handlerCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("WebSocket connection {ConnectionId} cancelled.", openedConnection.ConnectionId);
        }
        catch (WebSocketException exception)
        {
            logger.LogDebug(
                exception,
                "WebSocket connection {ConnectionId} aborted.",
                openedConnection.ConnectionId);
        }
        catch (ObjectDisposedException exception)
        {
            logger.LogDebug(
                exception,
                "WebSocket connection {ConnectionId} disposed during shutdown.",
                openedConnection.ConnectionId);
        }
        finally
        {
            handlerCancellation.Cancel();
            await IgnoreTerminationAsync(receiveTask).ConfigureAwait(false);
            await IgnoreTerminationAsync(queuedMessagesTask).ConfigureAwait(false);
            await connectionMessageQueue.CompleteConnectionAsync(openedConnection.ConnectionId, CancellationToken.None);
            await sessionOrchestrator.DisconnectAsync(openedConnection.ConnectionId, CancellationToken.None);

            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed.", CancellationToken.None);
            }
        }
    }

    private async Task<bool> SendMessagesAsync(WebSocket webSocket, IReadOnlyList<GatewayMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return false;
            }

            var bytes = messageSerializer.SerializeServerMessage(message);

            try
            {
                await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (WebSocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        return true;
    }

    private Task<bool> SendStatusAsync(WebSocket webSocket, string code, string message, bool recoverable, CancellationToken cancellationToken)
    {
        var status = new StatusMessage(GatewayStatusKind.Warning, code, message, recoverable);
        return SendMessagesAsync(webSocket, [status], cancellationToken);
    }

    private static async Task IgnoreTerminationAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (GatewayClientMessageException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<string?> ReceiveTextMessageAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);

        try
        {
            using var stream = new MemoryStream();

            while (true)
            {
                var receiveResult = await webSocket.ReceiveAsync(buffer.AsMemory(), cancellationToken);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (receiveResult.MessageType != WebSocketMessageType.Text)
                {
                    throw new GatewayClientMessageException(
                        "unsupported_frame_type",
                        "The gateway only accepts UTF-8 JSON text frames.",
                        true);
                }

                await stream.WriteAsync(buffer.AsMemory(0, receiveResult.Count), cancellationToken);

                if (receiveResult.EndOfMessage)
                {
                    break;
                }
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}