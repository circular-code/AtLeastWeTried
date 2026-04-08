using Flattiverse.Gateway.Contracts.Protocol;

namespace Flattiverse.Gateway.Application.Options;

public sealed class GatewayOptions
{
    public int TickIntervalMs { get; set; } = 40;

    public string ProtocolVersion { get; set; } = ProtocolConstants.CurrentVersion;
}