using Flattiverse.Gateway.Protocol.Dtos;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Shares owner-overlay snapshots between sessions that operate in the same
/// galaxy/team scope, so teammates can consume each other's full controllable state.
/// </summary>
public static class TeamOverlaySyncService
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Dictionary<string, Dictionary<string, OwnerOverlayDeltaDto>>> SnapshotsByScope = new(StringComparer.Ordinal);

    public static void Publish(string scopeKey, string sessionId, IReadOnlyList<OwnerOverlayDeltaDto> snapshots)
    {
        if (string.IsNullOrWhiteSpace(scopeKey) || string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (Sync)
        {
            if (!SnapshotsByScope.TryGetValue(scopeKey, out var sessions))
            {
                sessions = new Dictionary<string, Dictionary<string, OwnerOverlayDeltaDto>>(StringComparer.Ordinal);
                SnapshotsByScope[scopeKey] = sessions;
            }

            if (!sessions.TryGetValue(sessionId, out var sessionSnapshots))
            {
                sessionSnapshots = new Dictionary<string, OwnerOverlayDeltaDto>(StringComparer.Ordinal);
                sessions[sessionId] = sessionSnapshots;
            }
            else
            {
                sessionSnapshots.Clear();
            }

            foreach (var snapshot in snapshots)
            {
                if (!string.Equals(snapshot.EventType, "overlay.snapshot", StringComparison.Ordinal) || snapshot.Changes is null)
                    continue;

                sessionSnapshots[snapshot.ControllableId] = Clone(snapshot);
            }
        }
    }

    public static List<OwnerOverlayDeltaDto> CollectTeammateSnapshots(string scopeKey, string requestingSessionId)
    {
        var result = new List<OwnerOverlayDeltaDto>();
        if (string.IsNullOrWhiteSpace(scopeKey))
            return result;

        lock (Sync)
        {
            if (!SnapshotsByScope.TryGetValue(scopeKey, out var sessions))
                return result;

            foreach (var (sessionId, sessionSnapshots) in sessions)
            {
                if (string.Equals(sessionId, requestingSessionId, StringComparison.Ordinal))
                    continue;

                foreach (var snapshot in sessionSnapshots.Values)
                    result.Add(Clone(snapshot));
            }
        }

        return result;
    }

    public static void RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (Sync)
        {
            var emptyScopes = new List<string>();
            foreach (var (scopeKey, sessions) in SnapshotsByScope)
            {
                sessions.Remove(sessionId);
                if (sessions.Count == 0)
                    emptyScopes.Add(scopeKey);
            }

            foreach (var scopeKey in emptyScopes)
                SnapshotsByScope.Remove(scopeKey);
        }
    }

    private static OwnerOverlayDeltaDto Clone(OwnerOverlayDeltaDto source)
    {
        var clonedChanges = source.Changes is null
            ? null
            : new Dictionary<string, object?>(source.Changes, StringComparer.Ordinal);

        return new OwnerOverlayDeltaDto
        {
            EventType = source.EventType,
            ControllableId = source.ControllableId,
            Changes = clonedChanges
        };
    }
}
