using Flattiverse.Connector.Events;
using Flattiverse.Gateway.Infrastructure.Connector;

namespace Flattiverse.Gateway.Infrastructure.Runtime;

internal interface IConnectorEventPipeline
{
    ValueTask ProcessAsync(ConnectorPlayerSessionLease lease, FlattiverseEvent @event, CancellationToken cancellationToken);
}