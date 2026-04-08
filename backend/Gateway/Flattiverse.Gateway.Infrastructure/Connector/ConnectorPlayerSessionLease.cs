using System.Collections.Generic;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Network;
using Flattiverse.Gateway.Infrastructure.Runtime;
using Microsoft.Extensions.Logging;

namespace Flattiverse.Gateway.Infrastructure.Connector;

internal sealed class ConnectorPlayerSessionLease : IAsyncDisposable
{
    private readonly IConnectorEventPipeline eventPipeline;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> holders = new(StringComparer.Ordinal);
    private readonly ILogger logger;
    private Task? eventPumpTask;

    public ConnectorPlayerSessionLease(string sessionKey, string playerSessionId, Galaxy galaxy, IConnectorEventPipeline eventPipeline, ILogger logger)
    {
        SessionKey = sessionKey;
        PlayerSessionId = playerSessionId;
        Galaxy = galaxy;
        this.eventPipeline = eventPipeline;
        this.logger = logger;
        CommandGate = new SemaphoreSlim(1, 1);
    }

    public string SessionKey { get; }

    public string PlayerSessionId { get; }

    public Galaxy Galaxy { get; }

    public SemaphoreSlim CommandGate { get; }

    public int HolderCount => holders.Count;

    public void AddHolder(string connectionId)
    {
        holders.Add(connectionId);
    }

    public bool RemoveHolder(string connectionId)
    {
        return holders.Remove(connectionId);
    }

    public void StartEventPump()
    {
        eventPumpTask = Task.Run(() => PumpEventsAsync(lifetime.Token));
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();

        try
        {
            Galaxy.Dispose();
        }
        catch
        {
        }

        if (eventPumpTask is not null)
        {
            try
            {
                await eventPumpTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        CommandGate.Dispose();
        lifetime.Dispose();
    }

    private async Task PumpEventsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && Galaxy.Active)
        {
            try
            {
                var @event = await Galaxy.NextEvent().ConfigureAwait(false);
                try
                {
                    await eventPipeline.ProcessAsync(this, @event, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Failed to process connector event {EventKind} for player session {PlayerSessionId}.",
                        @event.Kind,
                        PlayerSessionId);
                }

                logger.LogDebug(
                    "Received connector event {EventKind} for player session {PlayerSessionId}.",
                    @event.Kind,
                    PlayerSessionId);
            }
            catch (ConnectionTerminatedGameException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }
}