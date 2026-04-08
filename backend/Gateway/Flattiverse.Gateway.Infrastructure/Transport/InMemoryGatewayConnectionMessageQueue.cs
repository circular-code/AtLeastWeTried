using System.Collections.Concurrent;
using System.Threading.Channels;
using Flattiverse.Gateway.Application.Abstractions;
using Flattiverse.Gateway.Contracts.Messages.Server;

namespace Flattiverse.Gateway.Infrastructure.Transport;

public sealed class InMemoryGatewayConnectionMessageQueue : IGatewayConnectionMessageQueue
{
    private readonly ConcurrentDictionary<string, Channel<GatewayMessage>> channels = new(StringComparer.Ordinal);

    public ValueTask RegisterConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        channels.GetOrAdd(connectionId, static _ => Channel.CreateUnbounded<GatewayMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        }));

        return ValueTask.CompletedTask;
    }

    public async ValueTask EnqueueAsync(string connectionId, IReadOnlyList<GatewayMessage> messages, CancellationToken cancellationToken)
    {
        if (messages.Count == 0 || !channels.TryGetValue(connectionId, out var channel))
        {
            return;
        }

        foreach (var message in messages)
        {
            await channel.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<IReadOnlyList<GatewayMessage>> DequeueAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (!channels.TryGetValue(connectionId, out var channel))
        {
            return [];
        }

        while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var messages = new List<GatewayMessage>();
            while (channel.Reader.TryRead(out var message))
            {
                messages.Add(message);
            }

            if (messages.Count > 0)
            {
                return messages;
            }
        }

        return [];
    }

    public ValueTask CompleteConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (channels.TryRemove(connectionId, out var channel))
        {
            channel.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }
}