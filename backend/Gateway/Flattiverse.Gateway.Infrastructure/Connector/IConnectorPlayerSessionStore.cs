using System.Diagnostics.CodeAnalysis;

namespace Flattiverse.Gateway.Infrastructure.Connector;

internal interface IConnectorPlayerSessionStore
{
    bool TryGetLease(string playerSessionId, [NotNullWhen(true)] out ConnectorPlayerSessionLease? lease);

    IReadOnlyCollection<ConnectorPlayerSessionLease> SnapshotLeases();
}