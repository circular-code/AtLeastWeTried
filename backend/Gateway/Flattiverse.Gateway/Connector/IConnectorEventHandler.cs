using Flattiverse.Connector.Events;

namespace Flattiverse.Gateway.Connector;

/// <summary>
/// Interface for services that receive Connector events from the event loop.
/// Handlers are called synchronously on the event-loop task, so they can
/// safely mutate their own state without locking.
/// </summary>
public interface IConnectorEventHandler
{
    void Handle(FlattiverseEvent @event);
}
