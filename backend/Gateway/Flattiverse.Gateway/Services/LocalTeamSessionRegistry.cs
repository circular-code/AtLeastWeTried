namespace Flattiverse.Gateway.Services;

/// <summary>
/// Tracks which team members are currently represented by connected player
/// sessions inside this backend process. This lets collaborative sync skip
/// teammates that are already sharing state locally through the gateway.
/// </summary>
public static class LocalTeamSessionRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Dictionary<string, SessionEntry>> SessionsByScope = new(StringComparer.Ordinal);

    public static void Register(string scopeKey, string sessionId, int playerId)
    {
        if (string.IsNullOrWhiteSpace(scopeKey) || string.IsNullOrWhiteSpace(sessionId) || playerId <= 0)
            return;

        lock (Sync)
        {
            RemoveSessionUnsafe(sessionId);

            if (!SessionsByScope.TryGetValue(scopeKey, out var sessions))
            {
                sessions = new Dictionary<string, SessionEntry>(StringComparer.Ordinal);
                SessionsByScope[scopeKey] = sessions;
            }

            sessions[sessionId] = new SessionEntry(playerId);
        }
    }

    public static bool IsLocallyManagedPlayer(string scopeKey, int playerId)
    {
        if (string.IsNullOrWhiteSpace(scopeKey) || playerId <= 0)
            return false;

        lock (Sync)
        {
            return SessionsByScope.TryGetValue(scopeKey, out var sessions)
                && sessions.Values.Any(entry => entry.PlayerId == playerId);
        }
    }

    public static HashSet<int> GetLocallyManagedPlayerIds(string scopeKey)
    {
        lock (Sync)
        {
            if (!SessionsByScope.TryGetValue(scopeKey, out var sessions))
                return new HashSet<int>();

            return sessions.Values
                .Select(entry => entry.PlayerId)
                .ToHashSet();
        }
    }

    public static bool IsLeader(string scopeKey, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(scopeKey) || string.IsNullOrWhiteSpace(sessionId))
            return false;

        lock (Sync)
        {
            if (!SessionsByScope.TryGetValue(scopeKey, out var sessions) ||
                !sessions.TryGetValue(sessionId, out var current))
            {
                return false;
            }

            foreach (var (candidateSessionId, candidate) in sessions)
            {
                if (candidate.PlayerId < current.PlayerId)
                    return false;

                if (candidate.PlayerId == current.PlayerId &&
                    string.CompareOrdinal(candidateSessionId, sessionId) < 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public static void RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (Sync)
            RemoveSessionUnsafe(sessionId);
    }

    private static void RemoveSessionUnsafe(string sessionId)
    {
        var emptyScopes = new List<string>();
        foreach (var (scopeKey, sessions) in SessionsByScope)
        {
            sessions.Remove(sessionId);
            if (sessions.Count == 0)
                emptyScopes.Add(scopeKey);
        }

        foreach (var scopeKey in emptyScopes)
            SessionsByScope.Remove(scopeKey);
    }

    private sealed record SessionEntry(int PlayerId);
}
