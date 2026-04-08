using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Flattiverse.Gateway.Application.Abstractions;
using Flattiverse.Gateway.Domain.Sessions;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Gateway.Infrastructure.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flattiverse.Gateway.Infrastructure.Connector;

internal sealed class ConnectorPlayerSessionPool : IPlayerSessionPool, IConnectorPlayerSessionStore, IAsyncDisposable
{
    private const int RuntimeDisclosureHexLength = 10;
    private const int BuildDisclosureHexLength = 12;
    private readonly ConnectorOptions connectorOptions;
    private readonly IConnectorEventPipeline eventPipeline;
    private readonly ILogger<ConnectorPlayerSessionPool> logger;
    private readonly ConcurrentDictionary<string, ConnectorPlayerSessionLease> leasesByCacheKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConnectorPlayerSessionLease> leasesByPlayerSessionId = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim attachGate = new(1, 1);
    private int disposed;

    public ConnectorPlayerSessionPool(
        IOptions<ConnectorOptions> connectorOptions,
        IConnectorEventPipeline eventPipeline,
        ILogger<ConnectorPlayerSessionPool> logger)
    {
        this.connectorOptions = connectorOptions.Value;
        this.eventPipeline = eventPipeline;
        this.logger = logger;
    }

    public async ValueTask<AttachedPlayerSession> AttachAsync(
        string connectionId,
        string apiKey,
        string? teamName,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildCacheKey(apiKey, teamName);

        await attachGate.WaitAsync(cancellationToken);

        try
        {
            if (leasesByCacheKey.TryGetValue(cacheKey, out var existingLease))
            {
                existingLease.AddHolder(connectionId);
                return ToAttachedPlayerSession(existingLease);
            }

            var galaxy = await Galaxy.Connect(
                GetRequiredEndpoint(),
                apiKey,
                teamName,
                ParseRuntimeDisclosure(connectorOptions.RuntimeDisclosure),
                ParseBuildDisclosure(connectorOptions.BuildDisclosure)).ConfigureAwait(false);

            var playerSessionId = $"player-{Guid.NewGuid():N}"[..21];
            var lease = new ConnectorPlayerSessionLease(cacheKey, playerSessionId, galaxy, eventPipeline, logger);
            lease.AddHolder(connectionId);
            lease.StartEventPump();

            leasesByCacheKey[cacheKey] = lease;
            leasesByPlayerSessionId[playerSessionId] = lease;

            return ToAttachedPlayerSession(lease);
        }
        finally
        {
            attachGate.Release();
        }
    }

    public async ValueTask ReleaseAsync(string connectionId, string playerSessionId, CancellationToken cancellationToken)
    {
        await attachGate.WaitAsync(cancellationToken);

        try
        {
            if (!leasesByPlayerSessionId.TryGetValue(playerSessionId, out var lease))
            {
                return;
            }

            if (!lease.RemoveHolder(connectionId) || lease.HolderCount > 0)
            {
                return;
            }

            leasesByPlayerSessionId.TryRemove(playerSessionId, out _);
            leasesByCacheKey.TryRemove(lease.SessionKey, out _);
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            attachGate.Release();
        }
    }

    public bool TryGetLease(string playerSessionId, [NotNullWhen(true)] out ConnectorPlayerSessionLease? lease)
    {
        return leasesByPlayerSessionId.TryGetValue(playerSessionId, out lease);
    }

    public IReadOnlyCollection<ConnectorPlayerSessionLease> SnapshotLeases()
    {
        return leasesByPlayerSessionId.Values.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        await attachGate.WaitAsync();

        try
        {
            foreach (var lease in leasesByPlayerSessionId.Values.DistinctBy(static entry => entry.PlayerSessionId).ToArray())
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            leasesByPlayerSessionId.Clear();
            leasesByCacheKey.Clear();
        }
        finally
        {
            attachGate.Release();
        }
    }

    private AttachedPlayerSession ToAttachedPlayerSession(ConnectorPlayerSessionLease lease)
    {
        return new AttachedPlayerSession(
            lease.PlayerSessionId,
            lease.Galaxy.Player.Name,
            lease.Galaxy.Player.Team.Name,
            lease.Galaxy.Active);
    }

    private string BuildCacheKey(string apiKey, string? teamName)
    {
        return $"{GetRequiredEndpoint()}|{apiKey}|{teamName?.Trim() ?? string.Empty}";
    }

    private string GetRequiredEndpoint()
    {
        if (string.IsNullOrWhiteSpace(connectorOptions.GalaxyEndpoint))
        {
            throw new InvalidOperationException("Connector:GalaxyEndpoint must be configured before attaching a live player session.");
        }

        return connectorOptions.GalaxyEndpoint.Trim();
    }

    private static RuntimeDisclosure? ParseRuntimeDisclosure(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length != RuntimeDisclosureHexLength || !trimmed.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"Connector:RuntimeDisclosure must be {RuntimeDisclosureHexLength} hexadecimal characters.");
        }

        return new RuntimeDisclosure(
            ParseRuntimeLevel(trimmed[0]),
            ParseRuntimeLevel(trimmed[1]),
            ParseRuntimeLevel(trimmed[2]),
            ParseRuntimeLevel(trimmed[3]),
            ParseRuntimeLevel(trimmed[4]),
            ParseRuntimeLevel(trimmed[5]),
            ParseRuntimeLevel(trimmed[6]),
            ParseRuntimeLevel(trimmed[7]),
            ParseRuntimeLevel(trimmed[8]),
            ParseRuntimeLevel(trimmed[9]));
    }

    private static BuildDisclosure? ParseBuildDisclosure(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length != BuildDisclosureHexLength || !trimmed.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"Connector:BuildDisclosure must be {BuildDisclosureHexLength} hexadecimal characters.");
        }

        return new BuildDisclosure(
            ParseBuildLevel(trimmed[0]),
            ParseBuildLevel(trimmed[1]),
            ParseBuildLevel(trimmed[2]),
            ParseBuildLevel(trimmed[3]),
            ParseBuildLevel(trimmed[4]),
            ParseBuildLevel(trimmed[5]),
            ParseBuildLevel(trimmed[6]),
            ParseBuildLevel(trimmed[7]),
            ParseBuildLevel(trimmed[8]),
            ParseBuildLevel(trimmed[9]),
            ParseBuildLevel(trimmed[10]),
            ParseBuildLevel(trimmed[11]));
    }

    private static RuntimeDisclosureLevel ParseRuntimeLevel(char value)
    {
        var parsed = Convert.ToInt32(value.ToString(), 16);
        if (parsed < 0 || parsed > (int)RuntimeDisclosureLevel.AiControlled)
        {
            throw new InvalidOperationException("Connector:RuntimeDisclosure contains an out-of-range disclosure level.");
        }

        return (RuntimeDisclosureLevel)parsed;
    }

    private static BuildDisclosureLevel ParseBuildLevel(char value)
    {
        var parsed = Convert.ToInt32(value.ToString(), 16);
        if (parsed < 0 || parsed > (int)BuildDisclosureLevel.AgenticTool)
        {
            throw new InvalidOperationException("Connector:BuildDisclosure contains an out-of-range disclosure level.");
        }

        return (BuildDisclosureLevel)parsed;
    }
}